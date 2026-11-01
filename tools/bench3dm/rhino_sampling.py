"""Rhino curve -> nesting ring conversion, mirroring the OpenNest GH plugin (rhino3dm + shapely).

Mirrors (see src/opennest_2/nest_rhino_lib):
  curve_explode.MakeCurveSegments (recursive): PolyCurve -> RemoveNesting + Explode, PolylineCurve -> one
      LineCurve per span, other polyline-able curves -> spans, Arc/Line as is, anything else whole
      (rhino3dm cannot split a NURBS at C1 kinks; the input jobs contain none).
  nest_geo.curve_to_polyline (keep_all=True, the identify_groups path): linear sub-segments (IsLinear at
      10*mtol) contribute their START point, curved ones are sampled with the turn seed + measured sagitta
      refinement (END excluded) against max(mtol, min(frac*segment_diag, CHORD_FRACTION_OF_RING*ref_diag))
      with ref_diag = the BATCH's largest ring bbox diagonal (curves_to_groups) or the ring's own,
      pts<3 falls back to sampling the whole curve; coarse (Simplify < 0, keep_all=False) then runs
      rdp_closed at that same cap (the plugin's simplify_rdp), fine keeps every vertex.
      Not mirrored: the hull option, and Rhino's IsLinear/explode details (see curve_explode).
  nest_geo.role_allowance: outer GROWS / holes SHRINK by the measured escape x1.05 (mitre offset),
      re-measured, up to 3 passes; skipped below the offsetter's grid step (polylines: exactly zero).
  nest_geo.identify_groups via bench_geom.classify_rings.

rhino3dm lacks DivideByCount/ClosestPoint, so arc-length division is analytic for lines and arcs (their
parameter is proportional to arc length) and table-based (chord lengths) for anything else, and the escape
scan probes each ring chord's own stretch of curve at a fixed 48 probes/chord instead of the adaptive
bisection (equivalent within the 1.05 pad, and re-measured after the offset like the plugin does).
"""
from __future__ import annotations

import math
import sys
from dataclasses import dataclass, field
from typing import List, Optional, Sequence

import numpy as np
import rhino3dm as r3
import shapely
from shapely.geometry import Polygon

import os
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench_geom as bg  # noqa: E402

# curve_sampling.cs constants
STEP_FINE = math.radians(12.0)
STEP_COARSE = math.radians(24.0)
SAG_FINE = 0.004
SAG_COARSE = 0.012
# chord cap on the per-segment budget (curve_sampling.chord_fraction_of_ring_fine / _coarse):
# budget = max(mtol, min(frac * segment_diag, CHORD_FRACTION_OF_RING * ref_diag)), ref_diag = the batch's
# largest ring bbox diagonal; the same cap is the RDP epsilon of the coarse (non-keep_all) path.
CHORD_FRACTION_OF_RING = 3.5e-6
CHORD_FRACTION_OF_RING_COARSE = 3.5e-6
CAP_FINE = 256
CAP_COARSE = 128
SAGITTA_PROBES = 3
ALLOWANCE_SAFETY = 1.05
ESCAPE_PROBES_PER_CHORD = 48


@dataclass
class CurveRec:
    guid: str
    name: str
    layer: str
    curve: object
    bbox: tuple  # (x0, y0, x1, y1)
    layer_index: int = -1


@dataclass
class PartGroup:
    outer: list                      # local frame, CCW, no closing duplicate
    holes: list                      # local frame, CW
    guids: list                      # [outer guid, hole guids...]
    layer: str
    names: list
    local_origin: tuple              # world position of the local origin (first outer vertex)
    outer_world: list = field(default_factory=list)
    holes_world: list = field(default_factory=list)
    curves: list = field(default_factory=list)   # rhino curves, same order as guids
    allowance: list = field(default_factory=list)  # applied allowance per ring (outer first)


# ----------------------------------------------------------------------------------------------
# reading
# ----------------------------------------------------------------------------------------------
def read_closed_curves(path: str, layer_exclude: Sequence[str] = (), z_tol: float = 0.01,
                       layer_include: Optional[Sequence[str]] = None, log=print) -> List[CurveRec]:
    model = r3.File3dm.Read(path)
    if model is None:
        raise RuntimeError("cannot read " + path)
    layers = {l.Index: l.Name for l in model.Layers}
    excl = {s.lower() for s in layer_exclude}
    incl = {s.lower() for s in layer_include} if layer_include else None
    out = []
    n_open = n_skip = 0
    for o in model.Objects:
        g = o.Geometry
        if not isinstance(g, r3.Curve):
            n_skip += 1
            continue
        lname = layers.get(o.Attributes.LayerIndex, "")
        if lname.lower() in excl or (incl is not None and lname.lower() not in incl):
            continue
        if not g.IsClosed:
            n_open += 1
            continue
        bb = g.GetBoundingBox()
        if max(abs(bb.Min.Z), abs(bb.Max.Z)) > z_tol:
            log(f"WARNING: curve {o.Attributes.Id} has |z| up to {max(abs(bb.Min.Z), abs(bb.Max.Z)):.4f}"
                f" > {z_tol}; it is projected to z=0 (only x,y are used)")
        out.append(CurveRec(str(o.Attributes.Id), o.Attributes.Name or "", lname, g,
                            (bb.Min.X, bb.Min.Y, bb.Max.X, bb.Max.Y), o.Attributes.LayerIndex))
    if n_open:
        log(f"WARNING: skipped {n_open} open curve(s) in {os.path.basename(path)}")
    read_closed_curves.last_stats = {"non_curves": n_skip, "open": n_open, "closed": len(out),
                                     "model_abs_tol": model.Settings.ModelAbsoluteTolerance,
                                     "units": str(model.Settings.ModelUnitSystem)}
    return out


read_closed_curves.last_stats = {}


# ----------------------------------------------------------------------------------------------
# explode
# ----------------------------------------------------------------------------------------------
def explode(curve) -> list:
    """Recursive MakeCurveSegments."""
    out: list = []
    _explode(curve, out)
    return out if out else [curve]


def _line(a, b):
    return r3.LineCurve(r3.Point3d(a.X, a.Y, a.Z), r3.Point3d(b.X, b.Y, b.Z))


def _explode(c, out):
    if isinstance(c, r3.PolyCurve):
        d = c.Duplicate()
        try:
            d.RemoveNesting()
        except Exception:
            pass
        segs = d.Explode()
        if not segs:
            out.append(c)
            return
        for s in segs:
            _explode(s, out)
        return
    if isinstance(c, r3.PolylineCurve):
        for i in range(c.PointCount - 1):
            out.append(_line(c.Point(i), c.Point(i + 1)))
        return
    if isinstance(c, (r3.LineCurve, r3.ArcCurve)):
        out.append(c)
        return
    pl = None
    try:
        pl = c.TryGetPolyline()
    except Exception:
        pl = None
    if pl is not None and pl.Count >= 2:
        for i in range(pl.Count - 1):
            out.append(_line(pl[i], pl[i + 1]))
        return
    out.append(c)


# ----------------------------------------------------------------------------------------------
# per-segment sampling
# ----------------------------------------------------------------------------------------------
def _xy(c, t):
    p = c.PointAt(t)
    return (p.X, p.Y)


class _Param:
    """Arc-length parameterisation of one curve (analytic for lines/arcs, chord table otherwise)."""

    def __init__(self, c):
        self.c = c
        d = c.Domain
        self.t0, self.t1 = d.T0, d.T1
        self.uniform = isinstance(c, (r3.LineCurve, r3.ArcCurve))
        if not self.uniform:
            ts = np.linspace(self.t0, self.t1, 2049)
            P = np.array([_xy(c, t) for t in ts])
            s = np.concatenate([[0.0], np.cumsum(np.hypot(*np.diff(P, axis=0).T))])
            self.ts, self.s = ts, s

    def at_fraction(self, f):
        """Parameter at arc-length fraction f (array)."""
        f = np.asarray(f, dtype=float)
        if self.uniform:
            return self.t0 + f * (self.t1 - self.t0)
        L = self.s[-1]
        if L <= 0:
            return self.t0 + f * (self.t1 - self.t0)
        return np.interp(f * L, self.s, self.ts)

    def divisions(self, n):
        """START + n-1 interior divisions (END excluded) - sample_parameters()."""
        return list(self.at_fraction(np.arange(n) / n))


def _dist_to_segment(p, a, b):
    dx, dy = b[0] - a[0], b[1] - a[1]
    l2 = dx * dx + dy * dy
    t = ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / l2 if l2 > 0 else 0.0
    t = min(1.0, max(0.0, t))
    return math.hypot(a[0] + dx * t - p[0], a[1] + dy * t - p[1])


def _worst_sagitta(c, ts, t_end, probes=SAGITTA_PROBES):
    worst = 0.0
    for d in range(len(ts)):
        a = ts[d]
        b = ts[d + 1] if d + 1 < len(ts) else t_end
        if not abs(b - a) > 0:
            continue
        A, B = _xy(c, a), _xy(c, b)
        for q in range(1, probes + 1):
            h = _dist_to_segment(_xy(c, a + (b - a) * q / (probes + 1)), A, B)
            worst = max(worst, h)
    return worst


def _divisions_for_turn(turn, step, cap):
    cap = max(1, cap)
    if not (turn > 0) or not (step > 0):
        return 1
    raw = math.ceil(turn / step)
    if not raw >= 1:
        return 1
    return cap if raw >= cap else int(raw)


def _divisions_for_sagitta(c, par, n0, cap, dev_tol, t_end, probes=SAGITTA_PROBES):
    cap = max(1, cap)
    n = 1 if n0 < 1 else min(n0, cap)
    if not dev_tol > 0:
        return n
    for _ in range(8):
        if n >= cap:
            break
        ts = par.divisions(n)
        worst = _worst_sagitta(c, ts, t_end, probes)
        if not worst > dev_tol:
            break
        bumped = int(math.ceil(n * math.sqrt(worst / dev_tol)))
        if bumped <= n:
            break
        n = min(bumped, cap)
    return n


def _segment_turn(c, mtol):
    if isinstance(c, r3.ArcCurve):
        return abs(c.AngleRadians)
    try:
        a = c.TryGetArc(mtol)
        if a is not None and a.IsValid:
            return abs(a.AngleRadians)
    except Exception:
        pass
    probes = 128
    d = c.Domain
    turn, prev = 0.0, None
    for i in range(probes + 1):
        try:
            t = c.TangentAt(d.T0 + (d.T1 - d.T0) * i / probes)
        except Exception:
            continue
        v = (t.X, t.Y)
        if math.hypot(*v) == 0:
            continue
        if prev is not None:
            dot = max(-1.0, min(1.0, (prev[0] * v[0] + prev[1] * v[1]) / (math.hypot(*prev) * math.hypot(*v))))
            turn += math.acos(dot)
        prev = v
    return turn


def _sample_bbox(c):
    d = c.Domain
    P = np.array([_xy(c, d.T0 + (d.T1 - d.T0) * i / 256) for i in range(257)])
    return P.min(axis=0), P.max(axis=0)


def _ring_diagonal(segs, curve=None):
    """Bounding-box diagonal of the WHOLE closed curve (nest_geo.bbox_diagonal(curve))."""
    los, his = [], []
    for s in (segs or [curve]):
        if s is None:
            continue
        lo, hi = _sample_bbox(s)
        los.append(lo)
        his.append(hi)
    if not los:
        return 0.0
    lo, hi = np.min(los, axis=0), np.max(his, axis=0)
    return float(np.hypot(*(hi - lo)))


def _sagitta_budget(c, frac, mtol, cap_abs=None):
    """curve_sampling.sagitta_budget: max(mtol, min(frac * segment_diag, cap_abs)); cap_abs None = no cap
    (the legacy relative-only rule). cap_abs is CHORD_FRACTION_OF_RING * ring_diag by default, or the
    explicit --sagitta-abs override (see chord_cap)."""
    lo, hi = _sample_bbox(c)
    diag = float(np.hypot(*(hi - lo)))
    rel = frac * diag
    if cap_abs is not None and cap_abs > 0:
        rel = min(rel, cap_abs)
    return max(mtol, rel)


def _add_curved(pts, seeds, c, step, cap, frac, mtol, seg_index, cap_abs=None):
    dev_tol = _sagitta_budget(c, frac, mtol, cap_abs)
    n = _divisions_for_turn(_segment_turn(c, mtol), step, cap)
    n = max(n, 2)
    n = min(n, cap)
    par = _Param(c)
    n = _divisions_for_sagitta(c, par, n, cap, dev_tol, c.Domain.T1)
    ts = par.divisions(n)
    for t in ts:
        pts.append(_xy(c, t))
    seeds.append((seg_index, list(ts) + [c.Domain.T1]))


def _is_linear(c, tol):
    if isinstance(c, r3.LineCurve):
        return True
    try:
        return bool(c.IsLinear(tol))
    except Exception:
        return False


def chord_cap(curve, segs, coarse: bool = False, sagitta_abs=None, legacy: bool = False, ring_diag_ref=None):
    """Absolute cap on the per-segment chord budget: the explicit sagitta_abs override if given, None for
    the legacy relative-only rule, else CHORD_FRACTION_OF_RING(_COARSE) * ref diagonal (ring_diag_ref =
    the batch's largest ring diagonal; None -> this ring's own)."""
    if sagitta_abs is not None and sagitta_abs > 0:
        return float(sagitta_abs)
    if legacy:
        return None
    rf = CHORD_FRACTION_OF_RING_COARSE if coarse else CHORD_FRACTION_OF_RING
    d = ring_diag_ref if (ring_diag_ref is not None and ring_diag_ref > 0) else _ring_diagonal(segs, curve)
    return rf * d


def batch_diagonal(curves) -> Optional[float]:
    """nest_geo.batch_diagonal: the largest ring bbox diagonal among the curves of one batch."""
    best = 0.0
    for c in curves:
        if c is None:
            continue
        best = max(best, _ring_diagonal(explode(c), c))
    return best if best > 0 else None


def rdp_closed(ring, eps, locked=None):
    """curve_sampling.rdp_closed, line for line: indices kept by Ramer-Douglas-Peucker on a CLOSED ring.
    Anchors (vertex 0, the vertex farthest from it (first max), every locked vertex) are always kept and
    RDP runs on each span between consecutive anchors; < 3 survivors -> all."""
    n = len(ring)
    allidx = list(range(n))
    if n <= 3 or not eps > 0:
        return allidx
    keep = [False] * n
    keep[0] = True
    x0, y0 = ring[0]
    far, fd = 0, -1.0
    for i in range(1, n):
        dx, dy = ring[i][0] - x0, ring[i][1] - y0
        d = dx * dx + dy * dy
        if d > fd:
            fd, far = d, i
    keep[far] = True
    if locked is not None:
        for i in range(min(n, len(locked))):
            if locked[i]:
                keep[i] = True
    anchors = [i for i in range(n) if keep[i]]
    stack = [(anchors[k], anchors[k + 1] if k + 1 < len(anchors) else n) for k in range(len(anchors))][::-1]
    while stack:
        a, b = stack.pop()
        if b - a < 2:
            continue
        A, B = ring[a], ring[b % n]
        worst, at = -1.0, -1
        for i in range(a + 1, b):
            d = _dist_to_segment(ring[i], A, B)
            if d > worst:
                worst, at = d, i
        if worst > eps:
            keep[at] = True
            stack.append((at, b))
            stack.append((a, at))
    res = [i for i in range(n) if keep[i]]
    return res if len(res) >= 3 else allidx


def curve_to_ring(curve, mtol: float = 0.001, coarse: bool = False, segments=None, sagitta_abs=None,
                  legacy: bool = False, ring_diag_ref: Optional[float] = None):
    """Returns (ring, seeds, segments). ring has no closing duplicate and the curve's own winding.
    seeds = [(segment_index, [t0, t1, ..., t_end]), ...] - the chord partition of every CURVED segment
    (segment_index -1 = the whole curve, fallback path).
    Budget per curved segment: max(mtol, min(frac * segment_diag, cap)) with cap = chord_cap(...): by
    default CHORD_FRACTION_OF_RING x the ring's bbox diagonal (the plugin rule); sagitta_abs overrides
    the cap; legacy=True drops it (the pre-cap plugin rule)."""
    segs = segments if segments is not None else explode(curve)
    step = STEP_COARSE if coarse else STEP_FINE
    cap = CAP_COARSE if coarse else CAP_FINE
    frac = SAG_COARSE if coarse else SAG_FINE
    cap_abs = chord_cap(curve, segs, coarse, sagitta_abs, legacy, ring_diag_ref)
    pts, seeds = [], []
    locked = set()   # curve samples + the vertex ending a curved segment: never dropped by the RDP pass
    nn = [s for s in segs if s is not None]
    prev_curved = bool(nn) and not _is_linear(nn[-1], mtol * 10)
    for i, s in enumerate(segs):
        if s is None:
            continue
        if _is_linear(s, mtol * 10):
            pts.append(_xy(s, s.Domain.T0))
            if prev_curved:
                locked.add(pts[-1])
            prev_curved = False
        else:
            k0 = len(pts)
            _add_curved(pts, seeds, s, step, cap, frac, mtol, i, cap_abs)
            locked.update(pts[k0:])
            prev_curved = True
    if len(pts) < 3:
        pts, seeds = [], []
        _add_curved(pts, seeds, curve, step, cap, frac, mtol, -1, cap_abs)
        locked = set(pts)
    if not pts:
        pts.append(_xy(curve, curve.Domain.T0))
    ring = bg.drop_closing_duplicate(pts, 1e-9)
    ring = bg.dedupe_consecutive(ring, 1e-9)
    if coarse and cap_abs is not None:
        # nest_geo.simplify_rdp (non-keep_all path) at the same cap. legacy=True would be the old
        # MergeColinearSegments(10 deg), which is NOT mirrored (the ring is left unsimplified).
        keep = rdp_closed(ring, max(mtol, cap_abs), [p in locked for p in ring])
        ring = [ring[i] for i in keep]
    return ring, seeds, segs


# ----------------------------------------------------------------------------------------------
# role allowance
# ----------------------------------------------------------------------------------------------
def dense_curve_points(segments, seeds, curve=None, per_chord: int = ESCAPE_PROBES_PER_CHORD):
    """Probe points on the real curve: every curved chord's stretch at per_chord probes, plus the
    start/mid of every linear segment."""
    pts = []
    curved = {si for si, _ in seeds}
    for si, ts in seeds:
        c = curve if si < 0 else segments[si]
        for d in range(len(ts) - 1):
            a, b = ts[d], ts[d + 1]
            for q in range(per_chord):
                pts.append(_xy(c, a + (b - a) * q / per_chord))
    for i, s in enumerate(segments):
        if i in curved or s is None:
            continue
        d = s.Domain
        pts.append(_xy(s, d.T0))
        pts.append(_xy(s, 0.5 * (d.T0 + d.T1)))
    return np.asarray(pts, dtype=float)


def measure_escape(ring, probe_pts: np.ndarray, circumscribe: bool) -> float:
    """circumscribe: deepest real-curve point OUTSIDE the ring polygon; else deepest real-curve point
    INSIDE it (the ring bridging a stretch the real region does not cover)."""
    if len(probe_pts) == 0 or len(ring) < 3:
        return 0.0
    poly = Polygon(ring)
    if not poly.is_valid:
        poly = shapely.make_valid(poly)
    inside = shapely.contains_xy(poly, probe_pts[:, 0], probe_pts[:, 1])
    sel = probe_pts[~inside] if circumscribe else probe_pts[inside]
    if len(sel) == 0:
        return 0.0
    bnd = shapely.LinearRing(ring) if len(ring) >= 3 else poly.boundary
    d = shapely.distance(bnd, shapely.points(sel))
    return float(np.max(d)) if len(d) else 0.0


def _allowance_floor(ring):
    x0, y0, x1, y1 = bg.ring_bbox(ring)
    ext = max(0.5 * (x1 - x0), 0.5 * (y1 - y0))
    scale = min(1e6, 1e8 / max(1.0, ext))
    scale = max(scale, 1.0)
    step = 1.0 / scale
    a = np.abs(np.asarray(ring, dtype=float)).max()
    return max(step, max(1e-12, a * 1e-12))


def role_allowance(ring, segments, seeds, circumscribe: bool, curve=None, info: Optional[dict] = None):
    """Grow (circumscribe) / shrink (inscribe) the ring by the measured escape x1.05, re-measure, up to
    3 passes. Returns the ring unchanged when there is nothing curved or the escape is below the grid."""
    if info is not None:
        info.update({"escape": 0.0, "applied": 0.0, "residual": 0.0, "passes": 0})
    if len(ring) < 3 or not seeds:
        return ring
    probe = dense_curve_points(segments, seeds, curve)
    dev = measure_escape(ring, probe, circumscribe)
    eps = _allowance_floor(ring)
    if info is not None:
        info["escape"] = dev
    if not dev > eps:
        return ring
    d = dev * ALLOWANCE_SAFETY
    best = ring
    for p in range(3):
        off = bg.offset_ring(ring, d if circumscribe else -d, join="mitre")  # plugin: role_allowance is mitre
        if off is None or len(off) < 3:
            break
        best = off
        res = measure_escape(off, probe, circumscribe)
        if info is not None:
            info.update({"applied": d, "residual": res, "passes": p + 1})
        if not res > eps:
            return off
        d += res * ALLOWANCE_SAFETY
    return best


# ----------------------------------------------------------------------------------------------
# grouping
# ----------------------------------------------------------------------------------------------
def curves_to_groups(recs: Sequence[CurveRec], mtol: float = 0.001, with_allowance: bool = True,
                     coarse: bool = False, classification: str = "identify_groups", stats: Optional[dict] = None,
                     local_frame: bool = True, sagitta_abs: Optional[float] = None,
                     legacy: bool = False) -> List[PartGroup]:
    rings, seeds, segs = [], [], []
    # chord-cap reference: the largest ring of the whole batch (nest_geo.identify_groups / geo_to_nest_geo)
    diag_ref = batch_diagonal([r.curve for r in recs])
    if stats is not None:
        stats["ring_diag_ref"] = diag_ref
    for rec in recs:
        rg, sd, sg = curve_to_ring(rec.curve, mtol, coarse, sagitta_abs=sagitta_abs, legacy=legacy,
                                   ring_diag_ref=diag_ref)
        rings.append(rg)
        seeds.append(sd)
        segs.append(sg)
    groups = bg.classify_rings(rings, classification)
    if stats is not None:
        stats["rings"] = rings
        stats["disagreements"] = bg.classification_disagreements(rings)
        stats["n_outer"] = len(groups)
        stats["n_holes"] = sum(len(g["holes"]) for g in groups)
        # islands: holes of a group that lie inside another hole of the same group
        isl = 0
        for g in groups:
            hp = [Polygon(rings[h]) for h in g["holes"]]
            for a in range(len(hp)):
                if any(b != a and hp[b].contains(hp[a]) for b in range(len(hp))):
                    isl += 1
        stats["n_islands"] = isl
        stats["allowance"] = []
    out = []
    for g in groups:
        idx = [g["outer"]] + g["holes"]
        world = []
        allow = []
        for k, i in enumerate(idx):
            rg = bg.ensure_ccw(rings[i]) if k == 0 else bg.ensure_cw(rings[i])
            if with_allowance:
                info = {}
                rg2 = role_allowance(rg, segs[i], seeds[i], circumscribe=(k == 0), curve=recs[i].curve, info=info)
                rg = bg.ensure_ccw(rg2) if k == 0 else bg.ensure_cw(rg2)
                allow.append(info)
                if stats is not None:
                    stats["allowance"].append(info)
            world.append(rg)
        o = world[0][0] if local_frame else (0.0, 0.0)
        local = [bg.translate(w, -o[0], -o[1]) for w in world]
        rec0 = recs[g["outer"]]
        out.append(PartGroup(outer=local[0], holes=local[1:], guids=[recs[i].guid for i in idx],
                             layer=rec0.layer, names=[recs[i].name for i in idx], local_origin=(o[0], o[1]),
                             outer_world=world[0], holes_world=world[1:], curves=[recs[i].curve for i in idx],
                             allowance=allow))
    return out


# ----------------------------------------------------------------------------------------------
# exact-ish curve properties (for round-trip checks)
# ----------------------------------------------------------------------------------------------
def curve_area(curve) -> float:
    """Signed area enclosed by a closed planar curve: exact for line/arc segments (shoelace over the
    segment endpoints plus the circular-segment areas), dense sampling for anything else."""
    segs = explode(curve)
    total = 0.0
    x0 = None
    for s in segs:
        a = s.PointAtStart
        b = s.PointAtEnd
        if x0 is None:
            x0 = (a.X, a.Y)
        if isinstance(s, r3.ArcCurve):
            ax, ay, bx, by = a.X - x0[0], a.Y - x0[1], b.X - x0[0], b.Y - x0[1]
            total += 0.5 * (ax * by - bx * ay)
            R = s.Radius
            th = s.AngleRadians
            seg_area = 0.5 * R * R * (th - math.sin(th))
            # sign: CCW arc (turning left) adds area to a CCW loop
            m = s.PointAt(0.5 * (s.Domain.T0 + s.Domain.T1))
            cross = (b.X - a.X) * (m.Y - a.Y) - (b.Y - a.Y) * (m.X - a.X)
            total += seg_area if cross < 0 else -seg_area
        elif _is_linear(s, 1e-12) or isinstance(s, r3.LineCurve):
            ax, ay, bx, by = a.X - x0[0], a.Y - x0[1], b.X - x0[0], b.Y - x0[1]
            total += 0.5 * (ax * by - bx * ay)
        else:
            par = _Param(s)
            ts = par.at_fraction(np.linspace(0, 1, 4097))
            P = np.array([_xy(s, t) for t in ts]) - np.asarray(x0)
            total += 0.5 * float(np.sum(P[:-1, 0] * P[1:, 1] - P[1:, 0] * P[:-1, 1]))
    return total


def curve_dense(curve, per_unit: float = 0.0, n_arc_deg: float = 0.5):
    """Dense points on a closed curve (lines: ends; arcs: every n_arc_deg; others: 2048 by arc length)."""
    pts = []
    for s in explode(curve):
        d = s.Domain
        if isinstance(s, r3.LineCurve) or _is_linear(s, 1e-9):
            pts.append(_xy(s, d.T0))
            pts.append(_xy(s, 0.5 * (d.T0 + d.T1)))
        elif isinstance(s, r3.ArcCurve):
            n = max(4, int(math.ceil(math.degrees(abs(s.AngleRadians)) / n_arc_deg)))
            for i in range(n):
                pts.append(_xy(s, d.T0 + (d.T1 - d.T0) * i / n))
        else:
            par = _Param(s)
            for t in par.at_fraction(np.arange(2048) / 2048):
                pts.append(_xy(s, t))
    return np.asarray(pts, dtype=float)


def curve_to_exact_ring(curve, sagitta: float = 0.005, max_per_segment: int = 4000):
    """Near-exact ring of a closed curve for EVALUATION (not nesting): lines contribute their start point,
    arcs are divided uniformly so the chord sagitta is <= `sagitta`, other curves 2048 arc-length samples.
    No closing duplicate; the curve's own winding."""
    pts = []
    for s in explode(curve):
        d = s.Domain
        if isinstance(s, r3.LineCurve) or _is_linear(s, 1e-9):
            pts.append(_xy(s, d.T0))
        elif isinstance(s, r3.ArcCurve):
            R, th = s.Radius, abs(s.AngleRadians)
            step = 2.0 * math.acos(max(-1.0, 1.0 - sagitta / R)) if R > sagitta else th
            n = min(max_per_segment, max(1, int(math.ceil(th / step))))
            for i in range(n):
                pts.append(_xy(s, d.T0 + (d.T1 - d.T0) * i / n))
        else:
            par = _Param(s)
            for t in par.at_fraction(np.arange(2048) / 2048):
                pts.append(_xy(s, t))
    return bg.dedupe_consecutive(bg.drop_closing_duplicate(pts, 1e-9), 1e-9)
