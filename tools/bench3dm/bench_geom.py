"""Pure 2D geometry helpers for the bench3dm harness (shapely + numpy only, no Rhino).

Ring = list[tuple[float, float]], NO closing duplicate. Outer rings are CCW, holes CW, unless a
function says otherwise.

Conventions mirror src/opennest_2/nest_rhino_lib (nest_geo.identify_groups, polygon_offset.offset_ring:
round joins for the part clearance offset, mitre for the sheet inset / role allowance).
"""
from __future__ import annotations

import math
from typing import Iterable, Optional, Sequence

import numpy as np
import shapely
from shapely.geometry import LinearRing, MultiPolygon, Point, Polygon
from shapely.geometry.base import BaseGeometry

Ring = list  # list[tuple[float, float]]


# ----------------------------------------------------------------------------------------------
# basic ring utilities
# ----------------------------------------------------------------------------------------------
def signed_area(ring: Sequence[Sequence[float]]) -> float:
    """Shoelace area, positive for CCW."""
    if len(ring) < 3:
        return 0.0
    a = np.asarray(ring, dtype=float)
    x, y = a[:, 0], a[:, 1]
    # subtract the first vertex: keeps precision when the ring sits far from the origin
    x = x - x[0]
    y = y - y[0]
    return 0.5 * float(np.dot(x, np.roll(y, -1)) - np.dot(np.roll(x, -1), y))


def ensure_ccw(ring: Ring) -> Ring:
    ring = [tuple(map(float, p)) for p in ring]
    return ring if signed_area(ring) >= 0 else ring[::-1]


def ensure_cw(ring: Ring) -> Ring:
    ring = [tuple(map(float, p)) for p in ring]
    return ring if signed_area(ring) <= 0 else ring[::-1]


def drop_closing_duplicate(ring: Ring, tol: float = 1e-9) -> Ring:
    ring = [tuple(map(float, p)) for p in ring]
    while len(ring) > 1 and math.hypot(ring[0][0] - ring[-1][0], ring[0][1] - ring[-1][1]) <= tol:
        ring.pop()
    return ring


def dedupe_consecutive(ring: Ring, tol: float = 1e-6) -> Ring:
    """Remove consecutive vertices closer than tol (cyclically). Never shrinks below 1 vertex."""
    out: list = []
    for p in ring:
        p = (float(p[0]), float(p[1]))
        if out and math.hypot(p[0] - out[-1][0], p[1] - out[-1][1]) <= tol:
            continue
        out.append(p)
    while len(out) > 1 and math.hypot(out[0][0] - out[-1][0], out[0][1] - out[-1][1]) <= tol:
        out.pop()
    return out


def ring_bbox(ring: Ring):
    a = np.asarray(ring, dtype=float)
    return float(a[:, 0].min()), float(a[:, 1].min()), float(a[:, 0].max()), float(a[:, 1].max())


def bbox_diag(ring: Ring) -> float:
    x0, y0, x1, y1 = ring_bbox(ring)
    return math.hypot(x1 - x0, y1 - y0)


def translate(ring: Ring, dx: float, dy: float) -> Ring:
    return [(p[0] + dx, p[1] + dy) for p in ring]


def place(ring: Ring, angle_rad: float, tx: float, ty: float, mirrored: bool = False) -> Ring:
    """p' = R(angle) * M * p + (tx, ty) where M = diag(1,-1) when mirrored (applied first)."""
    c, s = math.cos(angle_rad), math.sin(angle_rad)
    out = []
    for x, y in ring:
        if mirrored:
            y = -y
        out.append((c * x - s * y + tx, s * x + c * y + ty))
    if mirrored:
        out.reverse()  # keep the winding of the input
    return out


def solid(outer: Ring, holes: Iterable[Ring] = ()) -> Polygon:
    """shapely Polygon; made valid (largest piece kept) if the rings are not."""
    p = Polygon(outer, [h for h in holes if len(h) >= 3])
    if not p.is_valid:
        q = shapely.make_valid(p)
        p = _largest_polygon(q) or p
    return p


def _largest_polygon(g: BaseGeometry) -> Optional[Polygon]:
    if g is None or g.is_empty:
        return None
    if isinstance(g, Polygon):
        return g
    polys = [x for x in getattr(g, "geoms", []) if isinstance(x, (Polygon, MultiPolygon))]
    flat = []
    for x in polys:
        flat.extend(x.geoms if isinstance(x, MultiPolygon) else [x])
    if not flat:
        return None
    return max(flat, key=lambda q: q.area)


# ----------------------------------------------------------------------------------------------
# classification
# ----------------------------------------------------------------------------------------------
def _strictly_inside(poly: Polygon, pt, tol: float) -> bool:
    """Rhino Curve.Contains(.., tol) == Inside: inside AND farther than tol from the boundary."""
    p = Point(pt)
    return poly.contains(p) and poly.exterior.distance(p) > tol


def classify_rings(rings: Sequence[Ring], mode: str = "identify_groups", probe_tol: float = 0.01) -> list:
    """Sort rings into part groups. Returns [dict(outer=i, holes=[j, ...]), ...] in group order.

    identify_groups: mirror of nest_geo.identify_groups - rings sorted by bbox diagonal (largest first);
      the largest unvisited ring is an outer; every LATER ring (index in the sorted order > current,
      visited or not, exactly like the RTree callback) whose bbox overlaps and one of whose first 3
      vertices is strictly inside the outer polygon becomes its hole. Islands therefore become holes of
      the enclosing outer, like in the plugin.
    parity: containment depth; even depth = outer, odd depth = hole of its immediate (smallest) parent.
    """
    n = len(rings)
    polys = [Polygon(r) if len(r) >= 3 else None for r in rings]
    if mode == "identify_groups":
        diag = [bbox_diag(r) if len(r) else 0.0 for r in rings]
        # stable sort descending (Array.Sort + Reverse in C# is not stable; ties are measure-zero here)
        order = sorted(range(n), key=lambda i: -diag[i])
        bbs = [ring_bbox(r) if len(r) else (0, 0, 0, 0) for r in rings]
        visited = set()
        groups = []
        for a, i in enumerate(order):
            if a in visited:
                continue
            visited.add(a)
            g = {"outer": i, "holes": []}
            pi = polys[i]
            if pi is None or not pi.is_valid:
                pi = shapely.make_valid(pi) if pi is not None else None
            bi = bbs[i]
            for b in range(a + 1, n):
                j = order[b]
                bj = bbs[j]
                if bj[0] > bi[2] or bj[2] < bi[0] or bj[1] > bi[3] or bj[3] < bi[1]:
                    continue
                for pt in rings[j][:3]:
                    if pi is not None and _strictly_inside(pi, pt, probe_tol):
                        visited.add(b)
                        g["holes"].append(j)
                        break
            groups.append(g)
        return groups
    if mode == "parity":
        areas = [abs(signed_area(r)) for r in rings]
        parents: list = [[] for _ in range(n)]
        for j in range(n):
            if polys[j] is None:
                continue
            probe = rings[j][0]
            for i in range(n):
                if i == j or polys[i] is None or areas[i] <= areas[j]:
                    continue
                if _strictly_inside(polys[i], probe, probe_tol) or polys[i].contains(polys[j]):
                    parents[j].append(i)
        depth = [len(p) for p in parents]
        groups = {}
        order = sorted(range(n), key=lambda i: -bbox_diag(rings[i]) if len(rings[i]) else 0)
        for i in order:
            if depth[i] % 2 == 0:
                groups[i] = {"outer": i, "holes": []}
        for j in order:
            if depth[j] % 2 == 1:
                par = [i for i in parents[j] if depth[i] == depth[j] - 1]
                if par:
                    p = min(par, key=lambda i: areas[i])
                    groups[p]["holes"].append(j)
        return [groups[i] for i in order if i in groups]
    raise ValueError("unknown mode: " + mode)


def classification_disagreements(rings: Sequence[Ring], probe_tol: float = 0.01) -> list:
    """Run both modes; return a list of human-readable differences (empty = agree)."""
    a = classify_rings(rings, "identify_groups", probe_tol)
    b = classify_rings(rings, "parity", probe_tol)
    ka = {g["outer"]: sorted(g["holes"]) for g in a}
    kb = {g["outer"]: sorted(g["holes"]) for g in b}
    diffs = []
    for o in sorted(set(ka) | set(kb)):
        if o not in ka:
            diffs.append(f"ring {o}: outer in parity only (holes {kb[o]})")
        elif o not in kb:
            diffs.append(f"ring {o}: outer in identify_groups only (holes {ka[o]})")
        elif ka[o] != kb[o]:
            diffs.append(f"ring {o}: holes identify_groups={ka[o]} parity={kb[o]}")
    return diffs


# ----------------------------------------------------------------------------------------------
# offsets
# ----------------------------------------------------------------------------------------------
# Worst-chord / nominal-step sagitta ratio of the fillet chording in BOTH offsetters (GEOS
# addDirectedFillet and the vendored Clipper DoRound round the per-fillet step count, so one chord can
# span up to 1.5 nominal steps; sagitta grows with the angle squared). Mirrors
# polygon_offset.round_last_chord_factor.
ROUND_LAST_CHORD_FACTOR = 2.25


def round_sagitta_target(d: float, units_per_mm: float = 1.0) -> float:
    """Chord budget for the round fillets of a clearance offset by |d|: 0.4% of the offset clamped to
    [0.01 mm, 0.05 mm] (mirrors polygon_offset.round_sagitta_target; 0.02 at d = 5 mm)."""
    if not (units_per_mm > 0) or math.isinf(units_per_mm):
        units_per_mm = 1.0
    lo, hi = 0.01 * units_per_mm, 0.05 * units_per_mm
    return min(hi, max(lo, 0.004 * abs(d)))


def round_quad_segs(d: float, sagitta: Optional[float] = None) -> int:
    """shapely/GEOS quad_segs (chords per quarter circle) so that the WORST fillet chord of an offset by
    |d| stays within `sagitta` (default round_sagitta_target(d)) of the true arc."""
    d = abs(d)
    if d <= 0:
        return 1
    s = round_sagitta_target(d) if sagitta is None else sagitta
    y = min(s / ROUND_LAST_CHORD_FACTOR, 0.25 * d)  # same 0.25*d cap as Clipper's ArcTolerance
    step = 2.0 * math.acos(1.0 - y / d)               # chord angle with sagitta y
    return max(1, int(math.ceil((0.5 * math.pi) / step)))


def offset_ring(ring: Ring, delta: float, join: str = "round", mitre_limit: float = 2.0,
                sagitta: Optional[float] = None, quad_segs: Optional[int] = None) -> Optional[Ring]:
    """Offset the REGION bounded by ring by delta (>0 grows, <0 shrinks). Keeps the largest resulting
    loop's exterior, restores the input winding. None when consumed.

    join="round" (default): the exact clearance region (Minkowski sum / difference with a disk of radius
    |delta|), fillets chorded with round_quad_segs(delta, sagitta) unless quad_segs is given; vertices sit
    ON the arc, chords at most `sagitta` inside it. Mirrors polygon_offset.offset_ring(..., round_sagitta).
    join="mitre": mitre joins with `mitre_limit` (the plugin's sheet inset and role allowance)."""
    if join not in ("round", "mitre"):
        raise ValueError(f"join must be 'round' or 'mitre', not {join!r}")
    if len(ring) < 3:
        return None
    ccw = signed_area(ring) >= 0
    if delta == 0:
        return [tuple(p) for p in ring]
    # translate to the ring's bbox centre so the computation is size-, not position-, dependent
    x0, y0, x1, y1 = ring_bbox(ring)
    cx, cy = 0.5 * (x0 + x1), 0.5 * (y0 + y1)
    poly = Polygon(translate(ring, -cx, -cy))
    if not poly.is_valid:
        poly = _largest_polygon(shapely.make_valid(poly))
        if poly is None:
            return None
    if join == "round":
        qs = quad_segs if quad_segs is not None else round_quad_segs(delta, sagitta)
        g = poly.buffer(delta, join_style="round", quad_segs=int(qs))
    else:
        g = poly.buffer(delta, join_style="mitre", mitre_limit=mitre_limit)
    big = _largest_polygon(g)
    if big is None or big.is_empty or big.area <= 0:
        return None
    out = drop_closing_duplicate(list(big.exterior.coords))
    out = dedupe_consecutive(out, 1e-9)
    if len(out) < 3:
        return None
    out = translate(out, cx, cy)
    return ensure_ccw(out) if ccw else ensure_cw(out)


def offset_part(outer: Ring, holes: Sequence[Ring], d: float, join: str = "round"):
    """Grow outer by d, shrink each hole by d (holes consumed by the shrink are dropped), with `join`
    ("round" = exact clearance region, the plugin's nest_geo.offset_nesting_boundary; "mitre" = the old
    behaviour, kept for A/B). Returns (outer2 CCW, holes2 CW, n_dropped). outer2 is None if the outer
    offset failed."""
    o2 = offset_ring(ensure_ccw(outer), d, join=join)
    hs, dropped = [], 0
    for h in holes:
        h2 = offset_ring(ensure_cw(h), -d, join=join)
        if h2 is None or len(h2) < 3:
            dropped += 1
            continue
        hs.append(ensure_cw(h2))
    return (ensure_ccw(o2) if o2 else None), hs, dropped


def inset_sheet(w: float, h: float, inset: float) -> Ring:
    """Solve-sheet rectangle in its OWN frame (origin at the inset corner), CCW."""
    W, H = w - 2 * inset, h - 2 * inset
    if W <= 0 or H <= 0:
        raise ValueError("inset consumes the sheet")
    return [(0.0, 0.0), (W, 0.0), (W, H), (0.0, H)]


# ----------------------------------------------------------------------------------------------
# signatures / orientation helpers
# ----------------------------------------------------------------------------------------------
def mrr_dims(poly: Polygon):
    r = poly.minimum_rotated_rectangle
    c = list(r.exterior.coords)[:4]
    if len(c) < 4:
        return (0.0, 0.0)
    a = math.dist(c[0], c[1])
    b = math.dist(c[1], c[2])
    return (max(a, b), min(a, b))


def mrr_angle(poly: Polygon) -> float:
    """Angle (rad, in [0, pi)) of the long side of the minimum rotated rectangle."""
    r = poly.minimum_rotated_rectangle
    c = list(r.exterior.coords)[:4]
    if len(c) < 4:
        return 0.0
    e1 = (c[1][0] - c[0][0], c[1][1] - c[0][1])
    e2 = (c[2][0] - c[1][0], c[2][1] - c[1][1])
    e = e1 if math.hypot(*e1) >= math.hypot(*e2) else e2
    return math.atan2(e[1], e[0]) % math.pi


def longest_edge_angle(poly) -> float:
    """Angle (rad, in [0, pi)) of the longest exterior edge. Accepts a Polygon or a Ring."""
    ring = drop_closing_duplicate(list(poly.exterior.coords)) if isinstance(poly, Polygon) else list(poly)
    best, ang = -1.0, 0.0
    for i in range(len(ring)):
        a, b = ring[i], ring[(i + 1) % len(ring)]
        L = math.dist(a, b)
        if L > best:
            best, ang = L, math.atan2(b[1] - a[1], b[0] - a[0])
    return ang % math.pi


def perimeter(ring: Ring) -> float:
    return sum(math.dist(ring[i], ring[(i + 1) % len(ring)]) for i in range(len(ring)))


def shape_signature(outer: Ring, holes: Sequence[Ring] = ()) -> tuple:
    """(net area, outer perimeter, hole count, sorted hole areas, mrr long, mrr short), rounded to 1e-3 mm."""
    ao = abs(signed_area(outer))
    ha = sorted(abs(signed_area(h)) for h in holes)
    L, S = mrr_dims(Polygon(outer))
    r = lambda v: round(v, 3)
    return (r(ao - sum(ha)), r(perimeter(outer)), len(holes), tuple(r(a) for a in ha), r(L), r(S))


def signatures_equal(a: tuple, b: tuple, rel: float = 1e-3) -> bool:
    if a[2] != b[2]:
        return False

    def close(x, y):
        return abs(x - y) <= rel * max(abs(x), abs(y), 1.0)

    if not all(close(a[i], b[i]) for i in (0, 1, 4, 5)):
        return False
    return all(close(x, y) for x, y in zip(a[3], b[3]))


# ----------------------------------------------------------------------------------------------
# rigid matching
# ----------------------------------------------------------------------------------------------
def _moments(ring: Ring):
    """Area centroid and principal axis angle (rad) of the region bounded by ring."""
    a = np.asarray(ring, dtype=float)
    ox, oy = a[0]
    x, y = a[:, 0] - ox, a[:, 1] - oy
    xn, yn = np.roll(x, -1), np.roll(y, -1)
    cr = x * yn - xn * y
    A = 0.5 * cr.sum()
    if abs(A) < 1e-15:
        return (float(a[:, 0].mean()), float(a[:, 1].mean())), 0.0, False
    cx = ((x + xn) * cr).sum() / (6 * A)
    cy = ((y + yn) * cr).sum() / (6 * A)
    Ixx = ((y * y + y * yn + yn * yn) * cr).sum() / 12.0 - A * cy * cy
    Iyy = ((x * x + x * xn + xn * xn) * cr).sum() / 12.0 - A * cx * cx
    Ixy = ((x * yn + 2 * x * y + 2 * xn * yn + xn * y) * cr).sum() / 24.0 - A * cx * cy
    ang = 0.5 * math.atan2(-2 * Ixy, Iyy - Ixx)
    aniso = abs(Iyy - Ixx) + abs(Ixy)
    well_defined = aniso > 1e-3 * (abs(Ixx) + abs(Iyy))
    return (float(cx + ox), float(cy + oy)), float(ang), well_defined


def _edge_angles(ring: Ring, k: int = 4):
    e = []
    for i in range(len(ring)):
        a, b = ring[i], ring[(i + 1) % len(ring)]
        e.append((math.dist(a, b), math.atan2(b[1] - a[1], b[0] - a[0])))
    e.sort(key=lambda t: -t[0])
    return [t[1] for t in e[:k]]


def _kabsch_candidates(src: Ring, ref: Ring, keep: int = 3):
    """Exact vertex-correspondence candidates when both rings have the same vertex count: for every
    cyclic shift, the least-squares proper rotation (2D Kabsch). Returns [(rms, angle)]."""
    n = len(src)
    if n != len(ref) or n < 3 or n > 3000:
        return []
    A = np.asarray(src, dtype=float)
    B = np.asarray(ref, dtype=float)
    A = A - A.mean(axis=0)
    B = B - B.mean(axis=0)
    out = []
    for k in range(n):
        Bk = np.roll(B, -k, axis=0)
        sxx = float(np.sum(A[:, 0] * Bk[:, 0] + A[:, 1] * Bk[:, 1]))
        sxy = float(np.sum(A[:, 0] * Bk[:, 1] - A[:, 1] * Bk[:, 0]))
        th = math.atan2(sxy, sxx)
        c, s_ = math.cos(th), math.sin(th)
        R = np.array([[c, -s_], [s_, c]])
        rms = float(np.sqrt(np.mean(np.sum((A @ R.T - Bk) ** 2, axis=1))))
        out.append((rms, th))
    out.sort()
    return out[:keep]


def rigid_match(ring_src: Ring, ring_ref: Ring, tol: float = 0.05, src_holes: Sequence[Ring] = (),
                ref_holes: Sequence[Ring] = (), max_rel_symdiff: float = 0.005, allow_mirror: bool = True):
    """Find (angle_rad, tx, ty, mirrored) with place(src, angle, tx, ty, mirrored) ~= ref.

    Robust to start-index shifts and resampling: candidate angles come from principal axes (both
    directions + quarter turns), the minimum rotated rectangle and longest-edge pairings; translation
    aligns area centroids; each candidate is verified by the symmetric-difference area of the shapely
    solids (holes included when given) and refined by a short golden search on the angle. Accepted when
    symdiff < max_rel_symdiff * area. A non-mirrored match is preferred over a mirrored one. `tol` is
    the absolute Hausdorff tolerance reported back via rigid_match.last_info (informational).
    Returns None when nothing passes."""
    ref_poly = solid(ensure_ccw(ring_ref), [ensure_cw(h) for h in ref_holes])
    area = ref_poly.area
    if area <= 0:
        return None
    (rcx, rcy), rang, _ = _moments(ring_ref)
    ref_edges = _edge_angles(ring_ref)
    ref_mrr = mrr_angle(Polygon(ring_ref))
    results = []
    for mirrored in ((False, True) if allow_mirror else (False,)):
        src = place(ring_src, 0, 0, 0, mirrored)
        sh = [place(h, 0, 0, 0, mirrored) for h in src_holes]
        (scx, scy), sang, _ = _moments(src)
        src_c = translate(src, -scx, -scy)
        sh_c = [translate(h, -scx, -scy) for h in sh]
        src_poly_c = solid(ensure_ccw(src_c), [ensure_cw(h) for h in sh_c])

        def cost(theta):
            g = shapely.affinity.rotate(src_poly_c, theta, origin=(0, 0), use_radians=True)
            g = shapely.affinity.translate(g, rcx, rcy)
            try:
                return g.symmetric_difference(ref_poly).area
            except Exception:
                return float("inf")

        cands = set()
        for k in range(4):
            cands.add(round((rang - sang + k * math.pi / 2) % (2 * math.pi), 9))
        smrr = mrr_angle(Polygon(src))
        for k in range(4):
            cands.add(round((ref_mrr - smrr + k * math.pi / 2) % (2 * math.pi), 9))
        for se in _edge_angles(src, 2):
            for re in ref_edges:
                cands.add(round((re - se) % (2 * math.pi), 9))
                cands.add(round((re - se + math.pi) % (2 * math.pi), 9))
        for _, th in _kabsch_candidates(src, ring_ref):
            cands.add(round(th % (2 * math.pi), 12))
        scored = sorted(((cost(t), t) for t in cands), key=lambda u: u[0])
        best_c, best_t = scored[0]
        # golden-section refine around the best candidate (+-0.5 deg)
        if best_c > 1e-9 * area:
            lo, hi = best_t - math.radians(0.5), best_t + math.radians(0.5)
            gr = (math.sqrt(5) - 1) / 2
            c1, c2 = hi - gr * (hi - lo), lo + gr * (hi - lo)
            f1, f2 = cost(c1), cost(c2)
            for _ in range(30):
                if f1 < f2:
                    hi, c2, f2 = c2, c1, f1
                    c1 = hi - gr * (hi - lo)
                    f1 = cost(c1)
                else:
                    lo, c1, f1 = c1, c2, f2
                    c2 = lo + gr * (hi - lo)
                    f2 = cost(c2)
            t = 0.5 * (lo + hi)
            ct = cost(t)
            if ct < best_c:
                best_c, best_t = ct, t
        # translation: R*(p - c_src) + c_ref  ->  R*p + (c_ref - R*c_src)
        c, s = math.cos(best_t), math.sin(best_t)
        tx = rcx - (c * scx - s * scy)
        ty = rcy - (s * scx + c * scy)
        results.append((best_c, best_t, tx, ty, mirrored))
    ok = [r for r in results if r[0] < max_rel_symdiff * area]
    if not ok:
        rigid_match.last_info = {"rel_symdiff": min(r[0] for r in results) / area, "accepted": False}
        return None
    ref_ext = Polygon(ring_ref).exterior

    def haus(r):
        return Polygon(place(ring_src, r[1], r[2], r[3], r[4])).exterior.hausdorff_distance(ref_ext)

    scored_ok = sorted(((haus(r), r) for r in ok), key=lambda u: (u[0], u[1][0]))
    # unmirrored wins only when it is a genuine fit (Hausdorff within tol); otherwise the tighter fit wins
    nm = [u for u in scored_ok if not u[1][4] and u[0] <= tol]
    h, best = nm[0] if nm else scored_ok[0]
    rigid_match.last_info = {"rel_symdiff": best[0] / area, "hausdorff": h, "within_tol": h <= tol,
                             "accepted": True, "mirrored": best[4]}
    ang = math.atan2(math.sin(best[1]), math.cos(best[1]))
    return (ang, best[2], best[3], best[4])


rigid_match.last_info = {}

import shapely.affinity  # noqa: E402  (used inside rigid_match)
