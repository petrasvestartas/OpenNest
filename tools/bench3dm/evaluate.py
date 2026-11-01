"""Evaluate a bench3dm placement against its instance (geometry checks on the UN-offset rings).

python -I tools/bench3dm/evaluate.py --instance <instance.json> --placements <placements.json> --out <dir> [--3dm]
python -I tools/bench3dm/evaluate.py --instance <instance.json> --identity [--3dm] --out <dir>

Frame (placements.json "frame"):
    p_world = sheet_world_origin[sid] + (inset, inset) + R(angle_rad) * [M] * p_local + (tx, ty)
M = diag(1,-1) only when an instance entry carries "mirrored": true (bench3dm extension; engines never
set it). Sheet world origins: instance.sheet.world_origin + (0, k*pitch_y) when pitch_y is set, else side
by side in x with a 200 mm gap -- unless placements.json carries an optional "sheet_world_origins"
list (reference layouts do), which then wins for result.3dm.

Public API: evaluate_files(instance_path, placements_path, out_dir, write_3dm=False, write_svg=True) -> dict
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

import numpy as np
import shapely
from shapely.geometry import Polygon, box

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench_geom as bg  # noqa: E402

OVERLAP_AREA_TOL = 1.0      # mm^2
CLEARANCE_SLACK = 0.05      # mm
OOB_TOL = 1e-6              # mm
SHEET_GAP = 200.0           # mm, side-by-side layout


# ----------------------------------------------------------------------------------------------
# frames
# ----------------------------------------------------------------------------------------------
def affine(angle: float, tx: float, ty: float, mirrored: bool = False) -> np.ndarray:
    c, s = math.cos(angle), math.sin(angle)
    R = np.array([[c, -s, tx], [s, c, ty], [0.0, 0.0, 1.0]])
    if mirrored:
        R = R @ np.diag([1.0, -1.0, 1.0])
    return R


def decompose(A: np.ndarray):
    """3x3 rigid(+mirror) affine -> (angle, tx, ty, mirrored) with A = T * R(angle) * [M]."""
    det = A[0, 0] * A[1, 1] - A[0, 1] * A[1, 0]
    return math.atan2(A[1, 0], A[0, 0]), float(A[0, 2]), float(A[1, 2]), bool(det < 0)


def sheet_world_origins(inst: dict, n: int):
    sh = inst["sheet"]
    o = sh.get("world_origin") or [0.0, 0.0]
    py = sh.get("pitch_y")
    if py:
        return [(float(o[0]), float(o[1]) + k * float(py)) for k in range(n)]
    return [(float(o[0]) + k * (float(sh["w"]) + SHEET_GAP), float(o[1])) for k in range(n)]


def copy_to_local(inst_part: dict, copy: int) -> np.ndarray:
    """Matrix mapping copy `copy`'s ORIGINAL world curves onto the part's local frame."""
    cps = inst_part.get("copies")
    if cps and copy < len(cps):
        t = cps[copy]["to_local"]
        return affine(t["angle_rad"], t["tx"], t["ty"], t.get("mirrored", False))
    o = inst_part["local_origin"]
    return affine(0.0, -o[0], -o[1])


def copy_guids(inst_part: dict, copy: int):
    cps = inst_part.get("copies")
    if cps and copy < len(cps):
        return list(cps[copy]["guids"])
    g = inst_part.get("guids") or []
    return [g[copy]] if copy < len(g) else []


# ----------------------------------------------------------------------------------------------
# evaluation
# ----------------------------------------------------------------------------------------------
def _xf_ring(A, ring):
    a = np.asarray(ring, dtype=float)
    out = a @ A[:2, :2].T + A[:2, 2]
    r = [tuple(p) for p in out]
    return r


def part_rings(part, copy, rings="true"):
    """(outer, holes, net area) used for the geometry checks. rings="true": the copy's near-exact curve
    rings (copies[k].eval, sagitta <= 0.005 mm) when the instance has them; "nesting": the un-offset
    nesting rings (role allowance included) that the engines' solve rings were offset from."""
    cps = part.get("copies")
    if rings == "true" and cps and copy < len(cps) and cps[copy].get("eval"):
        e = cps[copy]["eval"]
        return e["outer"], e["holes"], float(cps[copy].get("area_true", part["area_net"]))
    return part["outer"], part["holes"], float(part["area_net"])


def _placed_solid(rings_outer, rings_holes, A):
    outer = _xf_ring(A, rings_outer)
    holes = [_xf_ring(A, h) for h in rings_holes]
    if np.linalg.det(A[:2, :2]) < 0:
        outer, holes = outer[::-1], [h[::-1] for h in holes]
    return outer, holes, bg.solid(outer, holes)


def evaluate(inst: dict, plc: dict, rings: str = "true") -> dict:
    W, H = float(inst["sheet"]["w"]), float(inst["sheet"]["h"])
    inset = float(inst.get("solve_sheet", {}).get("inset", 0.0))
    clearance = float(inst.get("clearance", 0.0))
    parts = {p["id"]: p for p in inst["parts"]}
    total = sum(int(p["qty"]) for p in inst["parts"])
    items, unplaced = [], []
    seen = set()
    for e in plc.get("instances", []):
        key = (int(e["part"]), int(e.get("copy", 0)))
        if key in seen:
            raise ValueError(f"duplicate placement for part {key[0]} copy {key[1]}")
        seen.add(key)
        p = parts[key[0]]
        sid = int(e.get("sheet", -1))
        if sid < 0:
            unplaced.append({"part": key[0], "copy": key[1], "name": p["name"]})
            continue
        A = affine(float(e["angle_rad"]), float(e["tx"]) + inset, float(e["ty"]) + inset,
                   bool(e.get("mirrored", False)))
        ro, rh, area = part_rings(p, key[1], rings)
        outer, holes, sol = _placed_solid(ro, rh, A)
        items.append({"part": key[0], "copy": key[1], "name": p["name"], "sheet": sid, "A": A,
                      "outer": outer, "holes": holes, "solid": sol, "area": area})
    # copies never mentioned count as unplaced
    for p in inst["parts"]:
        for c in range(int(p["qty"])):
            if (p["id"], c) not in seen:
                unplaced.append({"part": p["id"], "copy": c, "name": p["name"], "missing": True})

    sheets = sorted({it["sheet"] for it in items})
    rect = box(0, 0, W, H)
    per_sheet = []
    oob, min_margin, max_protrusion = [], math.inf, 0.0
    for it in items:
        a = np.asarray(it["outer"])
        x0, y0 = a.min(axis=0)
        x1, y1 = a.max(axis=0)
        m = min(x0, y0, W - x1, H - y1)
        it["margin"] = float(m)
        min_margin = min(min_margin, m)
        if m < -OOB_TOL:
            prot = float(it["solid"].difference(rect).area)
            max_protrusion = max(max_protrusion, -m)
            oob.append({"part": it["part"], "copy": it["copy"], "name": it["name"], "sheet": it["sheet"],
                        "protrusion": -float(m), "area_outside": prot})

    # pairs
    overlap_pairs, overlap_area, clear_viol = 0, 0.0, 0
    min_pair, min_hole = math.inf, math.inf
    in_hole = set()
    pairs = []
    for sid in sheets:
        its = [i for i, it in enumerate(items) if it["sheet"] == sid]
        for ai in range(len(its)):
            for bi in range(ai + 1, len(its)):
                A_, B_ = items[its[ai]], items[its[bi]]
                hole_rel = None
                for (X, Y, xi) in ((A_, B_, its[ai]), (B_, A_, its[bi])):
                    if not X["solid"].bounds or not Y["holes"]:
                        continue
                    for h in Y["holes"]:
                        hp = Polygon(h)
                        if hp.is_valid and hp.covers(X["solid"]):
                            hole_rel = xi
                            in_hole.add(xi)
                            break
                    if hole_rel is not None:
                        break
                ia = A_["solid"].intersection(B_["solid"]).area if A_["solid"].intersects(B_["solid"]) else 0.0
                d = float(A_["solid"].distance(B_["solid"]))
                rec = {"a": [A_["part"], A_["copy"], A_["name"]], "b": [B_["part"], B_["copy"], B_["name"]],
                       "sheet": sid, "distance": d, "overlap_area": float(ia), "hole_pair": hole_rel is not None}
                pairs.append(rec)
                if ia > OVERLAP_AREA_TOL:
                    overlap_pairs += 1
                    overlap_area += ia
                    rec["overlap"] = True
                if hole_rel is not None:
                    min_hole = min(min_hole, d)
                else:
                    min_pair = min(min_pair, d)
                    if d < clearance - CLEARANCE_SLACK:
                        clear_viol += 1

    for sid in sheets:
        its = [it for it in items if it["sheet"] == sid]
        area = sum(it["area"] for it in its)
        used_w = max(float(np.asarray(it["outer"])[:, 0].max()) for it in its)
        used_h = max(float(np.asarray(it["outer"])[:, 1].max()) for it in its)
        per_sheet.append({"sheet": sid, "parts": len(its), "names": [f"{it['name']}#{it['copy']}" for it in its],
                          "area": area, "util": 100.0 * area / (W * H), "used_width": used_w,
                          "used_height": used_h, "bbox_area": used_w * used_h,
                          "bbox_frac": used_w * used_h / (W * H)})
    n_sheets = len(sheets)
    tot_area = sum(ps["area"] for ps in per_sheet)
    widths = [ps["used_width"] for ps in per_sheet]
    remnant = min(widths) if widths else 0.0
    total_w = sum(widths)
    # remnant sheet = least-filled sheet; its used bounding box decides the reusable rectangular offcut
    rem_sheet = min(per_sheet, key=lambda ps: ps["area"]) if per_sheet else None
    remnant_bbox = (rem_sheet["used_width"], rem_sheet["used_height"]) if rem_sheet else (0.0, 0.0)
    worst = sorted(pairs, key=lambda r: (-r["overlap_area"], r["distance"]))[:5]
    res = {
        "engine": plc.get("engine"), "config": plc.get("config"), "seed": plc.get("seed"),
        "n_sheets": n_sheets,
        "util_per_sheet": [round(ps["util"], 4) for ps in per_sheet],
        "total_util": (100.0 * tot_area / (n_sheets * W * H)) if n_sheets else 0.0,
        "remnant_used_width": remnant, "total_used_width": total_w,
        "remnant_sheet": (rem_sheet["sheet"] if rem_sheet else None),
        "remnant_bbox": [remnant_bbox[0], remnant_bbox[1]],
        "remnant_bbox_frac": (remnant_bbox[0] * remnant_bbox[1] / (W * H)),
        "bbox_frac_per_sheet": [round(ps["bbox_frac"], 4) for ps in per_sheet],
        "overlap_pairs": overlap_pairs, "overlap_area": overlap_area,
        "min_pair_distance": (min_pair if min_pair < math.inf else None),
        "min_in_hole_distance": (min_hole if min_hole < math.inf else None),
        "clearance": clearance,
        "clearance_violations": clear_viol,
        "oob_parts": len(oob), "oob": oob, "max_protrusion": max_protrusion,
        "min_margin": (min_margin if min_margin < math.inf else None),
        "parts_in_holes": len(in_hole),
        "parts_in_holes_list": [f"{items[i]['name']}#{items[i]['copy']}" for i in sorted(in_hole)],
        "placed": len(items), "total": total, "unplaced": len(unplaced), "unplaced_list": unplaced,
        "score": (n_sheets, remnant, total_w),
        "per_sheet": per_sheet, "worst_pairs": worst,
        "sheet_w": W, "sheet_h": H, "inset": inset, "rings": rings,
        "wall_s": plc.get("wall_s"), "cancelled": plc.get("cancelled"),
    }
    swo = plc.get("sheet_world_origins")
    res["_sheet_origins"] = [tuple(map(float, o)) for o in swo] if swo else None
    res["_items"] = items
    res["_pairs"] = pairs
    res["_unplaced"] = unplaced
    return res


# ----------------------------------------------------------------------------------------------
# outputs
# ----------------------------------------------------------------------------------------------
def write_svg(path, inst, res):
    W, H = res["sheet_w"], res["sheet_h"]
    items = res["_items"]
    sheets = sorted({it["sheet"] for it in items}) or [0]
    slot = {s: k for k, s in enumerate(sheets)}
    gap = 0.05 * W
    parts = {p["id"]: p for p in inst["parts"]}
    unpl = res["_unplaced"]
    col_w = 0.0
    for u in unpl:
        x0, y0, x1, y1 = bg.ring_bbox(parts[u["part"]]["outer"])
        col_w = max(col_w, x1 - x0)
    totW = len(sheets) * (W + gap) + (col_w + gap if unpl else 0)
    unpl_h = 0.0
    for u in unpl:
        x0, y0, x1, y1 = bg.ring_bbox(parts[u["part"]]["outer"])
        unpl_h += (y1 - y0) + 50
    totH = max(H, unpl_h) + 0.20 * H
    s = max(1800.0 / totW, 900.0 / W)   # at least 900 px per sheet
    PX, PY = totW * s, totH * s
    top = 0.18 * H

    def T(x, y, ox):
        return (ox + x) * s, (top + max(H, unpl_h) - y) * s

    bad = set()
    for pr in res["_pairs"]:
        if pr.get("overlap") or (not pr["hole_pair"] and pr["distance"] < res["clearance"] - CLEARANCE_SLACK):
            bad.add((pr["a"][0], pr["a"][1]))
            bad.add((pr["b"][0], pr["b"][1]))
    oobk = {(o["part"], o["copy"]) for o in res["oob"]}

    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{PX:.0f}" height="{PY:.0f}" viewBox="0 0 {PX:.1f} {PY:.1f}">',
           '<rect width="100%" height="100%" fill="white"/>']
    fs = max(10.0, 0.035 * H * s)
    title = (f'{res.get("engine") or ""} {res.get("config") or ""}  sheets {res["n_sheets"]}  util '
             f'{res["total_util"]:.2f}%  placed {res["placed"]}/{res["total"]}  overlaps {res["overlap_pairs"]}  '
             f'clearance viol {res["clearance_violations"]}  oob {res["oob_parts"]}')
    out.append(f'<text x="4" y="{fs:.1f}" font-size="{fs:.1f}" font-family="sans-serif">{title}</text>')
    for sid in sheets:
        ox = slot[sid] * (W + gap)
        x0, y0 = T(0, H, ox)
        out.append(f'<rect x="{x0:.2f}" y="{y0:.2f}" width="{W * s:.2f}" height="{H * s:.2f}" fill="#f4f4f4" '
                   f'stroke="#000" stroke-width="1"/>')
        ps = next(p for p in res["per_sheet"] if p["sheet"] == sid)
        lx, ly = T(0, H, ox)
        out.append(f'<text x="{lx:.1f}" y="{ly - 4:.1f}" font-size="{fs * 0.8:.1f}" font-family="sans-serif">'
                   f'sheet {sid}: {ps["parts"]} parts, {ps["util"]:.2f}%, used {ps["used_width"]:.0f} x '
                   f'{ps["used_height"]:.0f} ({100 * ps["bbox_frac"]:.1f}% bbox)</text>')
        # used bounding box (dashed): everything outside it is the reusable offcut
        bx0, by0 = T(0, ps["used_height"], ox)
        out.append(f'<rect x="{bx0:.2f}" y="{by0:.2f}" width="{ps["used_width"] * s:.2f}" '
                   f'height="{ps["used_height"] * s:.2f}" fill="none" stroke="#c00" stroke-width="1" '
                   f'stroke-dasharray="6,4"/>')
    for it in items:
        ox = slot[it["sheet"]] * (W + gap)
        d = []
        for ring in [it["outer"]] + it["holes"]:
            d.append("M" + " L".join("%.2f,%.2f" % T(x, y, ox) for x, y in ring) + " Z")
        hue = (it["part"] * 137.508) % 360
        k = (it["part"], it["copy"])
        stroke = "#d00000" if k in bad else ("#ff7f00" if k in oobk else "#333")
        sw = 2.0 if stroke != "#333" else 0.6
        out.append(f'<path d="{" ".join(d)}" fill="hsl({hue:.0f},55%,70%)" fill-rule="evenodd" '
                   f'stroke="{stroke}" stroke-width="{sw}"/>')
        c = it["solid"].representative_point()
        cx, cy = T(c.x, c.y, ox)
        out.append(f'<text x="{cx:.1f}" y="{cy:.1f}" font-size="{fs * 0.45:.1f}" text-anchor="middle" '
                   f'font-family="monospace">{it["part"]}</text>')
    if unpl:
        ox = len(sheets) * (W + gap)
        yc = max(H, unpl_h)
        lx, ly = T(0, yc, ox)
        out.append(f'<text x="{lx:.1f}" y="{ly - 4:.1f}" font-size="{fs * 0.8:.1f}" font-family="sans-serif">'
                   f'unplaced ({len(unpl)})</text>')
        for u in unpl:
            p = parts[u["part"]]
            x0, y0, x1, y1 = bg.ring_bbox(p["outer"])
            dy = yc - y1
            yc -= (y1 - y0) + 50
            d = []
            for ring in [p["outer"]] + p["holes"]:
                d.append("M" + " L".join("%.2f,%.2f" % T(x - x0, y + dy, ox) for x, y in ring) + " Z")
            out.append(f'<path d="{" ".join(d)}" fill="#ccc" fill-rule="evenodd" stroke="#900" stroke-width="0.6"/>')
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


def _rh_xform(A):
    import rhino3dm as r3
    t = r3.Transform(1.0)
    t.M00, t.M01, t.M03 = float(A[0, 0]), float(A[0, 1]), float(A[0, 2])
    t.M10, t.M11, t.M13 = float(A[1, 0]), float(A[1, 1]), float(A[1, 2])
    return t


def write_3dm(path, inst, res):
    import rhino3dm as r3
    src = r3.File3dm.Read(inst["source"]["parts_3dm"])
    by_guid = {str(o.Attributes.Id): o.Geometry for o in src.Objects}
    model = r3.File3dm()
    model.Settings.ModelUnitSystem = r3.UnitSystem.Millimeters
    model.Settings.ModelAbsoluteTolerance = float(inst["source"].get("model_abs_tol") or 0.001)
    W, H, inset = res["sheet_w"], res["sheet_h"], res["inset"]
    items = res["_items"]
    nmax = max([it["sheet"] for it in items] + [0]) + 1
    origins = res.get("_sheet_origins") or sheet_world_origins(inst, nmax)
    if len(origins) < nmax:
        origins = list(origins) + sheet_world_origins(inst, nmax)[len(origins):]
    parts = {p["id"]: p for p in inst["parts"]}
    layer_sheet, layer_parts = {}, {}
    missing = 0
    for sid in sorted({it["sheet"] for it in items}):
        layer_sheet[sid] = model.Layers.AddLayer(f"Sheet_{sid + 1:02d}", (0, 0, 0, 255))
        layer_parts[sid] = model.Layers.AddLayer(f"Parts_{sid + 1:02d}", (40, 90, 200, 255))
        ox, oy = origins[sid]
        pts = [r3.Point3d(ox, oy, 0), r3.Point3d(ox + W, oy, 0), r3.Point3d(ox + W, oy + H, 0),
               r3.Point3d(ox, oy + H, 0), r3.Point3d(ox, oy, 0)]
        at = r3.ObjectAttributes()
        at.LayerIndex = layer_sheet[sid]
        at.Name = f"sheet {sid}"
        model.Objects.AddCurve(r3.PolylineCurve(pts), at)

    def add_copy(A_world, part, copy, layer):
        nonlocal missing
        L = copy_to_local(part, copy)
        X = _rh_xform(A_world @ L)
        for g in copy_guids(part, copy):
            geo = by_guid.get(g)
            if geo is None:
                missing += 1
                continue
            c = geo.Duplicate()
            c.Transform(X)
            at = r3.ObjectAttributes()
            at.LayerIndex = layer
            at.Name = f"{part['name']}#{copy}"
            at.SetUserString("src_guid", g)
            model.Objects.AddCurve(c, at)

    for it in items:
        ox, oy = origins[it["sheet"]]
        A_world = np.array([[1, 0, ox], [0, 1, oy], [0, 0, 1.0]]) @ it["A"]
        add_copy(A_world, parts[it["part"]], it["copy"], layer_parts[it["sheet"]])
    if res["_unplaced"]:
        lu = model.Layers.AddLayer("Unplaced", (200, 0, 0, 255))
        x = max(o[0] for o in origins) + W + 500.0
        y = origins[0][1]
        for u in res["_unplaced"]:
            p = parts[u["part"]]
            x0, y0, x1, y1 = bg.ring_bbox(p["outer"])
            add_copy(affine(0, x - x0, y - y0), p, u["copy"], lu)
            y += (y1 - y0) + 50
    model.Write(path, 8)
    return missing


def _json_clean(res):
    return {k: v for k, v in res.items() if not k.startswith("_")}


def evaluate_files(instance_path, placements_path, out_dir, write_3dm=False, write_svg=True,
                   rings: str = "true") -> dict:
    with open(instance_path, encoding="utf-8") as f:
        inst = json.load(f)
    with open(placements_path, encoding="utf-8") as f:
        plc = json.load(f)
    res = evaluate(inst, plc, rings)
    os.makedirs(out_dir, exist_ok=True)
    clean = _json_clean(res)
    clean["instance"] = os.path.abspath(instance_path)
    clean["placements"] = os.path.abspath(placements_path)
    with open(os.path.join(out_dir, "metrics.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(clean, f, indent=1, default=float)
    if write_svg:
        globals()["write_svg"](os.path.join(out_dir, "result.svg"), inst, res)
    if write_3dm:
        clean["missing_source_curves"] = globals()["write_3dm"](os.path.join(out_dir, "result.3dm"), inst, res)
    return clean


def summary(m: dict) -> str:
    f = lambda v, nd=3: "-" if v is None else (f"{v:.{nd}f}" if isinstance(v, float) else str(v))
    lines = [
        f"geometry measured   : {m.get('rings')} rings",
        f"sheets used         : {m['n_sheets']}",
        f"util per sheet [%]  : {', '.join(f'{u:.2f}' for u in m['util_per_sheet'])}",
        f"total util [%]      : {m['total_util']:.2f}",
        f"placed / total      : {m['placed']} / {m['total']} (unplaced {m['unplaced']})",
        f"overlaps            : {m['overlap_pairs']} pairs, {m['overlap_area']:.3f} mm^2",
        f"min pair distance   : {f(m['min_pair_distance'])} (clearance {m['clearance']}, violations {m['clearance_violations']})",
        f"min in-hole distance: {f(m['min_in_hole_distance'])}",
        f"min margin          : {f(m['min_margin'])}  (oob parts {m['oob_parts']}, max protrusion {m['max_protrusion']:.3f})",
        f"parts in holes      : {m['parts_in_holes']} {m['parts_in_holes_list']}",
        f"used widths         : {', '.join(f'{p['used_width']:.1f}' for p in m['per_sheet'])}",
        f"used bbox (w x h)   : {', '.join(f'{p['used_width']:.0f}x{p['used_height']:.0f}' for p in m['per_sheet'])}",
        f"remnant sheet bbox  : {m['remnant_bbox'][0]:.0f} x {m['remnant_bbox'][1]:.0f} = {100 * m['remnant_bbox_frac']:.1f}% of sheet {m['remnant_sheet']}",
        f"score               : ({m['score'][0]}, {m['score'][1]:.3f}, {m['score'][2]:.3f})",
    ]
    for o in m["oob"]:
        lines.append(f"  OOB {o['name']}#{o['copy']} sheet {o['sheet']}: protrudes {o['protrusion']:.3f} mm "
                     f"({o['area_outside']:.2f} mm^2 outside)")
    for w in m["worst_pairs"]:
        lines.append(f"  pair {w['a'][2]}#{w['a'][1]} - {w['b'][2]}#{w['b'][1]} sheet {w['sheet']}: "
                     f"d={w['distance']:.3f} overlap={w['overlap_area']:.3f}{' (hole pair)' if w['hole_pair'] else ''}")
    return "\n".join(lines)


# ----------------------------------------------------------------------------------------------
# identity round trip
# ----------------------------------------------------------------------------------------------
def identity_roundtrip(instance_path, out_dir, write_3dm=True):
    import rhino3dm as r3
    import rhino_sampling as rs
    with open(instance_path, encoding="utf-8") as f:
        inst = json.load(f)
    # virtual sheet: covers every copy's original world position; inset 0
    src = r3.File3dm.Read(inst["source"]["parts_3dm"])
    by_guid = {str(o.Attributes.Id): o.Geometry for o in src.Objects}
    xs, ys = [], []
    for p in inst["parts"]:
        for c in range(p["qty"]):
            for g in copy_guids(p, c):
                bb = by_guid[g].GetBoundingBox()
                xs += [bb.Min.X, bb.Max.X]
                ys += [bb.Min.Y, bb.Max.Y]
    ox, oy = math.floor(min(xs)) - 100.0, math.floor(min(ys)) - 100.0
    vinst = json.loads(json.dumps(inst))
    vinst["sheet"].update({"w": max(xs) - ox + 100.0, "h": max(ys) - oy + 100.0, "count": 1,
                           "world_origin": [ox, oy], "pitch_y": None})
    vinst["solve_sheet"] = {"w": vinst["sheet"]["w"], "h": vinst["sheet"]["h"], "inset": 0.0}
    insts = []
    for p in inst["parts"]:
        for c in range(p["qty"]):
            Ainv = np.linalg.inv(copy_to_local(p, c))
            a, tx, ty, mir = decompose(Ainv)
            e = {"part": p["id"], "copy": c, "sheet": 0, "tx": tx - ox, "ty": ty - oy, "angle_rad": a}
            if mir:
                e["mirrored"] = True
            insts.append(e)
    plc = {"schema": "opennest.bench3dm.placements/1", "instance": os.path.abspath(instance_path),
           "engine": "identity", "config": "identity", "seed": 0, "dll": {"path": None, "sha256": None},
           "wall_s": 0.0, "cancelled": False, "engine_report": {},
           "frame": "p_world = sheet_world_origin[sid] + (inset,inset) + R(angle_rad)*p_local + (tx,ty)",
           "n_sheets": 1, "instances": insts}
    os.makedirs(out_dir, exist_ok=True)
    vpath = os.path.join(out_dir, "instance_identity.json")
    ppath = os.path.join(out_dir, "placements.json")
    with open(vpath, "w", encoding="utf-8", newline="\n") as f:
        json.dump(vinst, f, indent=1)
    with open(ppath, "w", encoding="utf-8", newline="\n") as f:
        json.dump(plc, f, indent=1)
    m = evaluate_files(vpath, ppath, out_dir, write_3dm=write_3dm, write_svg=True)
    print(summary(m))
    if not write_3dm:
        return m
    # ---- compare result.3dm with the source curves
    out = r3.File3dm.Read(os.path.join(out_dir, "result.3dm"))
    curves = [o for o in out.Objects if o.Attributes.GetUserString("src_guid")]
    n_src = sum(1 for o in src.Objects if isinstance(o.Geometry, r3.Curve))
    max_bb, max_area, seen = 0.0, 0.0, set()
    for o in curves:
        g = o.Attributes.GetUserString("src_guid")
        seen.add(g)
        a, b = o.Geometry, by_guid[g]
        ba, bb = a.GetBoundingBox(), b.GetBoundingBox()
        d = max(abs(ba.Min.X - bb.Min.X), abs(ba.Min.Y - bb.Min.Y), abs(ba.Max.X - bb.Max.X), abs(ba.Max.Y - bb.Max.Y))
        max_bb = max(max_bb, d)
        max_area = max(max_area, abs(abs(rs.curve_area(a)) - abs(rs.curve_area(b))))
    # ---- discretisation deviation: placed nesting rings vs the source curves (world)
    dev_out, dev_in, dev_ring = 0.0, 0.0, 0.0
    worst = worst_out = None
    parts = {p["id"]: p for p in inst["parts"]}
    for it in m_items(vpath, ppath):
        p = parts[it["part"]]
        Aw = np.array([[1, 0, ox], [0, 1, oy], [0, 0, 1.0]]) @ it["A"]
        rings = [p["outer"]] + p["holes"]
        for k, g in enumerate(copy_guids(p, it["copy"])):
            if k >= len(rings):
                break
            ring = _xf_ring(Aw, rings[k])
            P = rs.curve_dense(by_guid[g])
            lr = shapely.LinearRing(ring)
            poly = Polygon(ring)
            dist = shapely.distance(lr, shapely.points(P))
            inside = shapely.contains_xy(poly, P[:, 0], P[:, 1])
            # curve points on the WRONG side of the ring: outside an outer / inside a hole
            wrong = ~inside if k == 0 else inside
            if np.any(wrong):
                v = float(dist[wrong].max())
                if v > dev_out:
                    dev_out, worst_out = v, (p["name"], it["copy"], "outer" if k == 0 else f"hole{k}", g)
            right = ~wrong
            if np.any(right):
                v = float(dist[right].max())
                if v > dev_in:
                    dev_in, worst = v, (p["name"], it["copy"], k, g)
            dev_ring = max(dev_ring, float(lr.hausdorff_distance(shapely.LineString(np.vstack([P, P[:1]])))))
    print(f"\nround trip: result.3dm has {len(curves)} part curves, source has {n_src}; "
          f"{'same count' if len(curves) == n_src and len(seen) == n_src else 'COUNT MISMATCH'}")
    print(f"round trip: max bbox delta {max_bb:.3e} mm, max |area| delta {max_area:.3e} mm^2 "
          f"({'OK' if max_bb <= 1e-6 and max_area <= 1e-6 else 'EXCEEDS 1e-6'})")
    print(f"discretisation: max curve->ring distance on the SAFE side (allowance + sagitta) {dev_in:.4f} mm"
          f" at {worst}; on the WRONG side (escape after allowance) {dev_out:.2e} mm at {worst_out};"
          f" max ring<->curve Hausdorff {dev_ring:.4f} mm")
    return m


def m_items(vpath, ppath):
    with open(vpath, encoding="utf-8") as f:
        inst = json.load(f)
    with open(ppath, encoding="utf-8") as f:
        plc = json.load(f)
    return evaluate(inst, plc, "nesting")["_items"]


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--placements")
    ap.add_argument("--out", required=True)
    ap.add_argument("--3dm", dest="w3dm", action="store_true")
    ap.add_argument("--no-svg", action="store_true")
    ap.add_argument("--identity", action="store_true")
    ap.add_argument("--rings", choices=["true", "nesting"], default="true",
                    help="geometry measured: near-exact curve rings (default) or the nesting rings")
    a = ap.parse_args(argv)
    if a.identity:
        identity_roundtrip(a.instance, a.out, write_3dm=a.w3dm)
        return
    if not a.placements:
        ap.error("--placements is required unless --identity")
    m = evaluate_files(a.instance, a.placements, a.out, write_3dm=a.w3dm, write_svg=not a.no_svg, rings=a.rings)
    print(summary(m))


if __name__ == "__main__":
    main()
