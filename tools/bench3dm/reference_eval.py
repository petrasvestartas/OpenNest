"""Evaluate a reference (e.g. NestFab) layout .3dm against a bench3dm instance.

python -I tools/bench3dm/reference_eval.py --instance <instance.json> --layout <layout.3dm> --out <dir>
    [--cut-layers Cut] [--sheet-layer Sheet] [--sheet-grid X0 Y0 W H PITCH N] [--infer-sheets]
    [--pitch 2200] [--3dm]

Steps: read the closed curves on the cut layers; drop exactly stacked duplicates (|area| equal within 1e-3
relative and bbox IoU >= 0.98); group them into parts with the same classifier as the converter (no role
allowance, no offset, world frame); match every layout part to one COPY of the instance by shape signature
and rigid_match against that copy's raw source rings (re-read from source.parts_3dm), so the placement is
    A = G o L_k^-1   (G: source copy -> layout, L_k: the copy's to_local),
resolve sheets (Sheet-layer rectangles, else --sheet-grid, else --infer-sheets), write placements.json
(engine "nestfab", tx/ty in the SOLVE frame) and run evaluate.evaluate_files on it.
"""
from __future__ import annotations

import argparse
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench_geom as bg  # noqa: E402
import evaluate as ev  # noqa: E402
import rhino_sampling as rs  # noqa: E402


def _bbox_iou(a, b):
    ix = max(0.0, min(a[2], b[2]) - max(a[0], b[0]))
    iy = max(0.0, min(a[3], b[3]) - max(a[1], b[1]))
    inter = ix * iy
    ua = (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - inter
    return inter / ua if ua > 0 else 0.0


def dedupe_stacked(recs, rel=1e-3, iou=0.98):
    areas = [abs(rs.curve_area(r.curve)) for r in recs]
    keep, dropped = [], []
    for i, r in enumerate(recs):
        dup = None
        for j in keep:
            if abs(areas[i] - areas[j]) <= rel * max(areas[i], areas[j]) and _bbox_iou(r.bbox, recs[j].bbox) >= iou:
                dup = j
                break
        if dup is None:
            keep.append(i)
        else:
            dropped.append((i, dup))
    return [recs[i] for i in keep], dropped


def source_copies(inst, log):
    """Raw (no allowance) world groups of every instance copy: list of dict(part, copy, group, sig, L)."""
    recs = rs.read_closed_curves(inst["source"]["parts_3dm"], log=log)
    byg = {r.guid: r for r in recs}
    out = []
    for p in inst["parts"]:
        for c in range(int(p["qty"])):
            guids = ev.copy_guids(p, c)
            sub = [byg[g] for g in guids if g in byg]
            gs = rs.curves_to_groups(sub, inst["source"].get("model_abs_tol") or 0.001, with_allowance=False,
                                     local_frame=False)
            if len(gs) != 1:
                log(f"WARNING: part {p['name']} copy {c}: source curves regroup into {len(gs)} groups")
            g = gs[0]
            out.append({"part": p["id"], "copy": c, "name": p["name"], "group": g,
                        "sig": bg.shape_signature(g.outer_world, g.holes_world), "L": ev.copy_to_local(p, c)})
    return out


def match_groups(groups, srcs, log, tol=0.05):
    used = set()
    matches, unmatched = [], []
    for gi, g in enumerate(groups):
        sig = bg.shape_signature(g.outer_world, g.holes_world)
        cands = [k for k, s in enumerate(srcs) if bg.signatures_equal(sig, s["sig"], 1e-3)]
        if not cands:  # looser fallback: area + hole count within 1 %
            cands = [k for k, s in enumerate(srcs) if s["sig"][2] == sig[2]
                     and abs(s["sig"][0] - sig[0]) <= 0.01 * max(sig[0], 1.0)]
        best = None
        for k in sorted(cands, key=lambda k: (k in used, k)):
            s = srcs[k]["group"]
            m = bg.rigid_match(s.outer_world, g.outer_world, tol=tol, src_holes=s.holes_world,
                               ref_holes=g.holes_world)
            if m is None:
                continue
            info = dict(bg.rigid_match.last_info)
            key = (k in used, info["mirrored"], not info["within_tol"], info["hausdorff"])
            if best is None or key < best[0]:
                best = (key, k, m, info)
            if not key[0] and not key[1] and not key[2]:
                break
        if best is None:
            unmatched.append(gi)
            continue
        _, k, m, info = best
        if k in used:
            log(f"WARNING: layout group {gi} re-uses {srcs[k]['name']} copy {srcs[k]['copy']} "
                f"(more layout copies than instance copies)")
            unmatched.append(gi)
            continue
        used.add(k)
        G = ev.affine(m[0], m[1], m[2], m[3])
        A = G @ np.linalg.inv(srcs[k]["L"])
        matches.append({"group": gi, "src": k, "A": A, "info": info, "mirrored": bool(m[3])})
    missing = [k for k in range(len(srcs)) if k not in used]
    return matches, unmatched, missing


def sheets_from_layer(path, layer, log):
    recs = rs.read_closed_curves(path, layer_include=[layer], log=log)
    rects = []
    for r in recs:
        x0, y0, x1, y1 = r.bbox
        rects.append((x0, y0, x1 - x0, y1 - y0))
    rects.sort(key=lambda t: (round(t[1], 3), round(t[0], 3)))
    return rects


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--layout", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--cut-layers", nargs="+", default=["Cut"])
    ap.add_argument("--sheet-layer", default="Sheet")
    ap.add_argument("--sheet-grid", type=float, nargs=6, metavar=("X0", "Y0", "W", "H", "PITCH", "N"))
    ap.add_argument("--infer-sheets", action="store_true")
    ap.add_argument("--pitch", type=float, default=2200.0, help="band pitch for --infer-sheets")
    ap.add_argument("--3dm", dest="w3dm", action="store_true")
    ap.add_argument("--rings", choices=["true", "nesting"], default="true")
    a = ap.parse_args(argv)
    log = print
    with open(a.instance, encoding="utf-8") as f:
        inst = json.load(f)
    W, H = float(inst["sheet"]["w"]), float(inst["sheet"]["h"])
    margin = float(inst.get("margin", 0.0))
    inset = float(inst["solve_sheet"]["inset"])
    mtol = inst["source"].get("model_abs_tol") or 0.001

    recs = rs.read_closed_curves(a.layout, layer_include=a.cut_layers, log=log)
    st = dict(rs.read_closed_curves.last_stats)
    n_read = len(recs)
    recs, dropped = dedupe_stacked(recs)
    log(f"layout          : {os.path.abspath(a.layout)}")
    log(f"cut curves      : {n_read} closed on {a.cut_layers} ({st.get('open', 0)} open, "
        f"{st.get('non_curves', 0)} non-curves skipped); {len(dropped)} stacked duplicates removed -> {len(recs)}")
    stats = {}
    # parity, not identify_groups: a layout has parts seated inside other parts' holes, which
    # identify_groups would swallow as extra holes of the host
    groups = rs.curves_to_groups(recs, mtol, with_allowance=False, local_frame=False, stats=stats,
                                 classification="parity")
    log(f"layout parts    : {len(groups)} by parity classification ({stats['n_holes']} holes); identify_groups "
        f"would differ on {len(stats['disagreements'])} rings (parts seated in holes)")
    srcs = source_copies(inst, log)
    matches, unmatched, missing = match_groups(groups, srcs, log)
    n_mir = sum(1 for m in matches if m["mirrored"])
    haus = max((m["info"]["hausdorff"] for m in matches), default=0.0)
    log(f"matched         : {len(matches)}/{len(groups)} layout parts to {len(srcs)} instance copies "
        f"(max Hausdorff {haus:.4f} mm, {n_mir} placed MIRRORED); unmatched layout parts {len(unmatched)}; "
        f"instance copies absent from the layout {len(missing)}")
    for gi in unmatched:
        g = groups[gi]
        x0, y0, x1, y1 = bg.ring_bbox(g.outer_world)
        log(f"  unmatched layout part {gi}: bbox {x1 - x0:.1f} x {y1 - y0:.1f} at ({x0:.1f},{y0:.1f}), "
            f"net area {bg.shape_signature(g.outer_world, g.holes_world)[0]:.1f}, holes {len(g.holes_world)}, "
            f"outer guid {g.guids[0]}")
    for k in missing:
        log(f"  absent: {srcs[k]['name']} copy {srcs[k]['copy']}")

    # ---- sheets
    pts = np.vstack([np.asarray(groups[m["group"]].outer_world) for m in matches]) if matches else np.zeros((1, 2))
    if not a.sheet_grid and not a.infer_sheets:
        rects = sheets_from_layer(a.layout, a.sheet_layer, log)
        source = f"layer '{a.sheet_layer}' ({len(rects)} rectangles)"
    else:
        rects = []
    if not rects and a.sheet_grid:
        x0, y0, w, h, pitch, n = a.sheet_grid
        rects = [(x0, y0 + k * pitch, w, h) for k in range(int(n))]
        source = f"--sheet-grid {a.sheet_grid}"
    if not rects:
        if not a.infer_sheets:
            log("no Sheet layer and no --sheet-grid: falling back to --infer-sheets")
        x0 = float(pts[:, 0].min()) - margin
        y0 = float(pts[:, 1].min()) - margin
        n = int(math.floor((float(pts[:, 1].max()) - y0) / a.pitch)) + 1
        rects = [(x0, y0 + k * a.pitch, W, H) for k in range(n)]
        source = f"inferred {n} bands of pitch {a.pitch} from origin ({x0:.3f}, {y0:.3f})"
    for r in rects:
        if abs(r[2] - W) > 1e-3 or abs(r[3] - H) > 1e-3:
            log(f"WARNING: sheet rect {r[2]:.3f} x {r[3]:.3f} differs from the instance sheet {W} x {H}")
    log(f"sheets          : {source}")

    # ---- assign each matched part to a sheet (max bbox overlap)
    sheet_of = {}
    for m in matches:
        bb = bg.ring_bbox(groups[m["group"]].outer_world)
        best, bo = -1, -1.0
        for s, (x, y, w, h) in enumerate(rects):
            ov = max(0.0, min(bb[2], x + w) - max(bb[0], x)) * max(0.0, min(bb[3], y + h) - max(bb[1], y))
            if ov > bo:
                best, bo = s, ov
        if bo <= 0:  # outside every rect: nearest band by centre y
            cy = 0.5 * (bb[1] + bb[3])
            best = min(range(len(rects)), key=lambda s: abs(rects[s][1] + 0.5 * rects[s][3] - cy))
        sheet_of[m["group"]] = best
    used = sorted(set(sheet_of.values()))
    remap = {s: k for k, s in enumerate(used)}

    log("per-band extents (relative to each sheet origin):")
    for s in used:
        x, y, w, h = rects[s]
        gl = [groups[m["group"]] for m in matches if sheet_of[m["group"]] == s]
        P = np.vstack([np.asarray(g.outer_world) for g in gl])
        log(f"  sheet {remap[s]} (rect {s}) origin ({x:.3f}, {y:.3f}): {len(gl)} parts, x [{P[:, 0].min() - x:.3f}, "
            f"{P[:, 0].max() - x:.3f}], y [{P[:, 1].min() - y:.3f}, {P[:, 1].max() - y:.3f}]")

    instances = []
    for m in matches:
        s = sheet_of[m["group"]]
        x, y = rects[s][0], rects[s][1]
        A = m["A"]
        ang, tx, ty, mir = ev.decompose(A)
        src = srcs[m["src"]]
        e = {"part": src["part"], "copy": src["copy"], "sheet": remap[s], "tx": tx - x - inset,
             "ty": ty - y - inset, "angle_rad": ang}
        if mir:
            e["mirrored"] = True
        instances.append(e)
    for k in missing:
        instances.append({"part": srcs[k]["part"], "copy": srcs[k]["copy"], "sheet": -1, "tx": 0.0, "ty": 0.0,
                          "angle_rad": 0.0})
    instances.sort(key=lambda e: (e["part"], e["copy"]))
    plc = {"schema": "opennest.bench3dm.placements/1", "instance": os.path.abspath(a.instance),
           "engine": "nestfab", "config": os.path.basename(a.layout), "seed": None,
           "dll": {"path": None, "sha256": None}, "wall_s": None, "cancelled": False,
           "engine_report": {"layout": os.path.abspath(a.layout), "stacked_duplicates_removed": len(dropped),
                             "layout_parts": len(groups), "matched": len(matches),
                             "unmatched_layout_parts": len(unmatched), "absent_copies": len(missing),
                             "mirrored": n_mir, "max_hausdorff": haus, "sheets": source},
           "frame": "p_world = sheet_world_origin[sid] + (inset,inset) + R(angle_rad)*p_local + (tx,ty)",
           "n_sheets": len(used), "instances": instances,
           "sheet_world_origins": [[rects[s][0], rects[s][1]] for s in used]}
    os.makedirs(a.out, exist_ok=True)
    pp = os.path.join(a.out, "placements.json")
    with open(pp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(plc, f, indent=1)
    m = ev.evaluate_files(a.instance, pp, a.out, write_3dm=a.w3dm, write_svg=True, rings=a.rings)
    log(ev.summary(m))
    log(f"written         : placements.json, metrics.json, result.svg{', result.3dm' if a.w3dm else ''} in "
        f"{os.path.abspath(a.out)}")


if __name__ == "__main__":
    main()
