"""Convert a Rhino parts .3dm + sheet .3dm into a bench3dm nesting instance.

python -I tools/bench3dm/rhino3dm_to_instance.py --parts <abs>.3dm --sheet <abs>.3dm --out <dir>
    [--margin 15] [--clearance 10] [--offset-join round|mitre] [--sheets 10] [--sampling fine|coarse]
    [--no-role-allowance]
    [--sagitta-abs MM | --plugin-legacy-sampling]
    [--merge-duplicates | --no-merge-duplicates] [--sheet-origin X Y] [--sheet-pitch 2200]
    [--z-tol 0.01] [--exclude-layers L1 L2 ...]

Writes instance.json (schema opennest.bench3dm.instance/1), instance.nfp.txt (bench/datasets.h loadFile
grammar), preview.svg and report.txt.
"""
from __future__ import annotations

import argparse
import datetime as _dt
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bench_geom as bg  # noqa: E402
import shapely  # noqa: E402
import rhino_sampling as rs  # noqa: E402

SCHEMA = "opennest.bench3dm.instance/1"


def _r(v, nd=9):
    return round(float(v), nd)


def _ring_json(ring):
    return [[_r(x), _r(y)] for x, y in ring]


def read_sheet(path, z_tol, mtol, log):
    recs = rs.read_closed_curves(path, z_tol=z_tol, log=log)
    if not recs:
        raise SystemExit("no closed curve in sheet file " + path)
    rings = [rs.curve_to_ring(r.curve, mtol)[0] for r in recs]
    k = max(range(len(rings)), key=lambda i: abs(bg.signed_area(rings[i])))
    x0, y0, x1, y1 = bg.ring_bbox(rings[k])
    w, h = x1 - x0, y1 - y0
    a = abs(bg.signed_area(rings[k]))
    if abs(a - w * h) > 1e-6 * w * h:
        log(f"WARNING: sheet curve is not a rectangle (area {a:.3f} vs bbox {w * h:.3f}); using its bbox")
    if len(recs) > 1:
        log(f"WARNING: sheet file has {len(recs)} closed curves; using the largest")
    return w, h, (x0, y0)


def merge_duplicates(groups, rel=1e-3, log=print, merge_mirrored=True, hausdorff_tol=0.5):
    """-> list of parts: dict(group=g, copies=[(group, to_local(angle,tx,ty,mirrored), info)]).
    to_local maps the copy's WORLD rings onto the part's local rings: p_local = R(angle)*M*p_world + t,
    M = diag(1,-1) when mirrored."""
    sigs = [bg.shape_signature(g.outer, g.holes) for g in groups]
    used = [False] * len(groups)
    parts = []
    for i, g in enumerate(groups):
        if used[i]:
            continue
        used[i] = True
        o = g.local_origin
        copies = [(g, (0.0, -o[0], -o[1], False), {"rel_symdiff": 0.0})]
        for j in range(i + 1, len(groups)):
            if used[j] or not bg.signatures_equal(sigs[i], sigs[j], rel):
                continue
            h = groups[j]
            m = bg.rigid_match(h.outer_world, g.outer, src_holes=h.holes_world, ref_holes=g.holes,
                               allow_mirror=merge_mirrored)
            if m is None:
                log(f"note: group {j} has the signature of group {i} but no rigid "
                    f"{'(or mirrored) ' if merge_mirrored else '(unmirrored) '}match - kept separate")
                continue
            info = dict(bg.rigid_match.last_info)
            if info.get("hausdorff", 0.0) > hausdorff_tol:
                log(f"note: group {j} matches group {i} only to Hausdorff {info['hausdorff']:.3f} mm "
                    f"> --dup-tol {hausdorff_tol} - kept separate")
                continue
            used[j] = True
            copies.append((h, (m[0], m[1], m[2], bool(m[3])), info))
        parts.append({"group": g, "copies": copies})
    return parts


def envelope_near_duplicates(part, exact_tol=1e-3, exact_rel_symdiff=1e-7, log=print):
    """When a merged copy is only a NEAR duplicate, replace the part's rings by the union of every copy's
    solid (local frame) so the nesting rings contain each real copy. Re-anchors the local frame on the
    union's first vertex and shifts every copy's to_local accordingly. Returns the max Hausdorff."""
    copies = part["copies"]
    hmax = max((c[2].get("hausdorff", 0.0) for c in copies), default=0.0)
    smax = max((c[2].get("rel_symdiff", 0.0) for c in copies), default=0.0)
    if hmax <= exact_tol and smax <= exact_rel_symdiff:
        return hmax
    g = part["group"]
    sols = []
    for (cg, (a, tx, ty, mir), _) in copies:
        o = bg.place(cg.outer_world, a, tx, ty, mir)
        hs = [bg.place(h, a, tx, ty, mir) for h in cg.holes_world]
        sols.append(bg.solid(o, hs))
    u = bg._largest_polygon(shapely.union_all(sols))
    outer = bg.ensure_ccw(bg.dedupe_consecutive(bg.drop_closing_duplicate(list(u.exterior.coords)), 1e-9))
    holes = [bg.ensure_cw(bg.dedupe_consecutive(bg.drop_closing_duplicate(list(r.coords)), 1e-9))
             for r in u.interiors]
    holes = [h for h in holes if len(h) >= 3]
    v = outer[0]
    g.outer = bg.translate(outer, -v[0], -v[1])
    g.holes = [bg.translate(h, -v[0], -v[1]) for h in holes]
    # to_local' = T(-v) o to_local ; copy 0's world position of the local origin moves by R^-1 v
    part["copies"] = [(cg, (a, tx - v[0], ty - v[1], mir), info) for (cg, (a, tx, ty, mir), info) in copies]
    a0, tx0, ty0, _ = part["copies"][0][1]
    g.local_origin = (-tx0, -ty0) if abs(a0) < 1e-15 else g.local_origin
    log(f"note: near-duplicate merge {g.guids[0]} (outer Hausdorff {hmax:.3f} mm, solid rel symdiff {smax:.2e}):"
        f" part rings = union of the copies")
    return hmax


def write_nfp_txt(path, solve_w, solve_h, parts_json):
    lines = ["# opennest bench3dm instance (solve rings, local frame); grammar: bench/datasets.h loadFile",
             f"sheet {solve_w:.9f} {solve_h:.9f}", ""]
    for p in parts_json:
        lines.append(f"part {p['qty']} 0")
        for x, y in p["solve"]["outer"]:
            lines.append(f"{x:.9f} {y:.9f}")
        lines.append("")
        for hole in p["solve"]["holes"]:
            for x, y in hole:
                lines.append(f"{x:.9f} {y:.9f}")
            lines.append("")
    with open(path, "w", newline="\n", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")


def _svg_path(rings, sx, sy, ox, oy, H):
    d = []
    for ring in rings:
        pts = [f"{(x - ox) * sx:.2f},{(H - (y - oy)) * sy:.2f}" for x, y in ring]
        d.append("M" + " L".join(pts) + " Z")
    return " ".join(d)


def write_preview_svg(path, parts_json):
    # grid layout, each cell sized by the largest part
    cells = []
    for p in parts_json:
        x0, y0, x1, y1 = bg.ring_bbox(p["outer"])
        cells.append((x0, y0, x1, y1))
    cw = max(c[2] - c[0] for c in cells) + 40
    ch = max(c[3] - c[1] for c in cells) + 80
    ncol = max(1, int(math.ceil(math.sqrt(len(cells) * ch / cw))))
    nrow = int(math.ceil(len(cells) / ncol))
    s = 1200.0 / (ncol * cw)
    W, H = ncol * cw * s, nrow * ch * s
    out = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W:.0f}" height="{H:.0f}" '
           f'viewBox="0 0 {W:.1f} {H:.1f}"><rect width="100%" height="100%" fill="white"/>']
    for k, (p, c) in enumerate(zip(parts_json, cells)):
        col, row = k % ncol, k // ncol
        ox, oy = col * cw * s, row * ch * s
        rings = [p["outer"]] + p["holes"]
        d = []
        for ring in rings:
            pts = [f"{ox + (x - c[0] + 20) * s:.2f},{oy + (ch - 60 - (y - c[1])) * s:.2f}" for x, y in ring]
            d.append("M" + " L".join(pts) + " Z")
        hue = (k * 137.508) % 360
        out.append(f'<path d="{" ".join(d)}" fill="hsl({hue:.0f},55%,70%)" fill-rule="evenodd" '
                   f'stroke="#333" stroke-width="0.6"/>')
        out.append(f'<text x="{ox + 20 * s:.1f}" y="{oy + (ch - 20) * s:.1f}" font-size="11" '
                   f'font-family="monospace">{p["id"]} {p["name"]} x{p["qty"]}</text>')
    out.append("</svg>")
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(out))


def build(args, log):
    mtol = 0.001
    excl = args.exclude_layers or []
    recs = rs.read_closed_curves(args.parts, layer_exclude=excl, z_tol=args.z_tol, log=log)
    st0 = dict(rs.read_closed_curves.last_stats)
    mtol = st0.get("model_abs_tol") or 0.001
    if mtol <= 0:
        mtol = 0.001
    if args.sheet:
        w, h, sheet_min = read_sheet(args.sheet, args.z_tol, mtol, log)
    else:
        w, h, sheet_min = None, None, (0.0, 0.0)
    if args.sheet_size:
        if w is not None:
            log(f"sheet size override: {args.sheet_size[0]} x {args.sheet_size[1]} (file says {w:.3f} x {h:.3f})")
        w, h = float(args.sheet_size[0]), float(args.sheet_size[1])
    from collections import Counter
    types = Counter(type(r.curve).__name__ for r in recs)

    stats = {}
    groups = rs.curves_to_groups(recs, mtol, with_allowance=not args.no_role_allowance,
                                 coarse=(args.sampling == "coarse"), stats=stats,
                                 sagitta_abs=args.sagitta_abs, legacy=args.plugin_legacy_sampling)
    if args.merge_duplicates:
        parts = merge_duplicates(groups, log=log, merge_mirrored=args.merge_mirrored, hausdorff_tol=args.dup_tol)
    else:
        parts = [{"group": g, "copies": [(g, (0.0, -g.local_origin[0], -g.local_origin[1], False), {})]}
                 for g in groups]

    half = 0.5 * args.clearance
    inset = args.margin - half
    solve = bg.inset_sheet(w, h, inset)
    solve_w, solve_h = solve[2]

    parts_json = []
    tot_dropped = 0
    for pid, p in enumerate(parts):
        g = p["group"]
        envelope_near_duplicates(p, log=log)
        if half > 0:
            so, sh, nd = bg.offset_part(g.outer, g.holes, half, join=args.offset_join)
            if so is None:
                raise SystemExit(f"part {pid}: outer offset failed")
        else:
            so, sh, nd = g.outer, g.holes, 0
        tot_dropped += nd
        net = abs(bg.signed_area(g.outer)) - sum(abs(bg.signed_area(x)) for x in g.holes)
        copies = []
        for (cg, (a, tx, ty, mir), info) in p["copies"]:
            # near-exact TRUE geometry of this copy in the part's local frame (for evaluation only)
            ev_rings, ev_area = [], 0.0
            for k, c in enumerate(cg.curves):
                rg = rs.curve_to_exact_ring(c)
                rg = bg.ensure_ccw(rg) if k == 0 else bg.ensure_cw(rg)
                ev_rings.append(bg.place(rg, a, tx, ty, mir))
                ar = abs(rs.curve_area(c))
                ev_area += ar if k == 0 else -ar
            copies.append({"guids": cg.guids, "names": cg.names, "layer": cg.layer,
                           "eval": {"outer": _ring_json(ev_rings[0]), "holes": [_ring_json(x) for x in ev_rings[1:]]},
                           "area_true": _r(ev_area, 6),
                           "to_local": {"angle_rad": _r(a, 12), "tx": _r(tx), "ty": _r(ty), "mirrored": mir},
                           "rel_symdiff": _r(info.get("rel_symdiff", 0.0), 12),
                           "hausdorff": _r(info.get("hausdorff", 0.0), 9)})
        parts_json.append({
            "id": pid, "name": f"P{pid:03d}", "guids": [c["guids"][0] for c in copies], "layer": g.layer,
            "qty": len(copies), "rotations": 0,
            "local_origin": [_r(g.local_origin[0]), _r(g.local_origin[1])],
            "outer": _ring_json(g.outer), "holes": [_ring_json(x) for x in g.holes],
            "area_net": copies[0]["area_true"], "area_nesting": _r(net, 6),
            "solve": {"outer": _ring_json(so), "holes": [_ring_json(x) for x in sh], "dropped_holes": nd},
            "copies": copies,
        })

    origin = list(args.sheet_origin) if args.sheet_origin else [sheet_min[0], sheet_min[1]]
    inst = {
        "schema": SCHEMA, "units": "mm",
        "source": {"parts_3dm": os.path.abspath(args.parts), "sheet_3dm": (os.path.abspath(args.sheet) if args.sheet else None),
                   "sheet_override": ({"w": w, "h": h} if args.sheet_size else None),
                   "model_abs_tol": mtol,
                   "created": _dt.datetime.now().astimezone().isoformat(timespec="seconds")},
        "sampling": {"mode": args.sampling,
                     "step_deg": 24 if args.sampling == "coarse" else 12,
                     "sagitta_fraction": rs.SAG_COARSE if args.sampling == "coarse" else rs.SAG_FINE,
                     "max_divisions": rs.CAP_COARSE if args.sampling == "coarse" else rs.CAP_FINE,
                     "merge_colinear_deg": 0, "role_allowance": not args.no_role_allowance,
                     "classification": "identify_groups",
                     "sagitta_abs": args.sagitta_abs,
                     "chord_fraction_of_ring": (None if (args.plugin_legacy_sampling or args.sagitta_abs)
                                                else (rs.CHORD_FRACTION_OF_RING_COARSE if args.sampling == "coarse"
                                                      else rs.CHORD_FRACTION_OF_RING)),
                     "chord_cap_reference": (None if (args.plugin_legacy_sampling or args.sagitta_abs)
                                             else "batch_max_ring_bbox_diagonal"),
                     "ring_diag_ref": stats.get("ring_diag_ref"),
                     "simplify": ("rdp_closed(eps = chord cap)" if args.sampling == "coarse" else "keep_all"),
                     "budget_rule": _budget_rule(args),
                     "offset_join": args.offset_join,
                     "offset_round_sagitta": (_r(bg.round_sagitta_target(half), 6)
                                              if args.offset_join == "round" and half > 0 else None),
                     "offset_round_quad_segs": (bg.round_quad_segs(half)
                                                if args.offset_join == "round" and half > 0 else None)},
        "sheet": {"w": _r(w), "h": _r(h), "count": args.sheets, "world_origin": [_r(origin[0]), _r(origin[1])],
                  "pitch_y": (float(args.sheet_pitch) if args.sheet_pitch is not None else None)},
        "margin": float(args.margin), "clearance": float(args.clearance),
        "solve_sheet": {"w": _r(solve_w), "h": _r(solve_h), "inset": _r(inset)},
        "parts": parts_json,
    }
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "instance.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(inst, f, indent=1)
    write_nfp_txt(os.path.join(args.out, "instance.nfp.txt"), solve_w, solve_h, parts_json)
    write_preview_svg(os.path.join(args.out, "preview.svg"), parts_json)

    # ---- report
    ring_v = [len(p["outer"]) for p in parts_json] + [len(hh) for p in parts_json for hh in p["holes"]]
    solve_v = [len(p["solve"]["outer"]) for p in parts_json] + [len(hh) for p in parts_json for hh in p["solve"]["holes"]]
    raw_v = [len(rg) for rg in stats["rings"]]
    al = stats.get("allowance", [])
    n_dup_groups = sum(1 for p in parts_json if p["qty"] > 1)
    n_mir = sum(1 for p in parts_json for c in p["copies"] if c["to_local"]["mirrored"])
    rep = [
        f"parts file      : {os.path.abspath(args.parts)}",
        f"sheet file      : {os.path.abspath(args.sheet) if args.sheet else '-'}"
        f"{'  (size overridden by --sheet-size)' if args.sheet_size else ''}",
        f"units / abs tol : {st0.get('units')} / {mtol}",
        f"curves read     : {len(recs)} closed ({st0.get('open', 0)} open skipped, {st0.get('non_curves', 0)} non-curves skipped)",
        f"curve types     : {dict(types)}",
        f"classification  : {stats['n_outer']} outers, {stats['n_holes']} holes, {stats['n_islands']} islands",
        f"modes agree     : {'yes' if not stats['disagreements'] else 'NO: ' + '; '.join(stats['disagreements'][:10])}",
        f"duplicates      : {'merged' if args.merge_duplicates else 'not merged'}; {n_dup_groups} groups with qty>1;"
        f" {len(parts_json)} parts, total copies {sum(p['qty'] for p in parts_json)};"
        f" {n_mir} copies are MIRROR images of their part (merge_mirrored={args.merge_mirrored})",
        f"sheet           : {w:.3f} x {h:.3f}, count {args.sheets}, world origin {origin}, pitch_y {args.sheet_pitch}",
        f"margin/clearance: {args.margin} / {args.clearance} -> part offset {half} ({args.offset_join} joins"
        f"{(', chord sagitta <= %.4f, quad_segs %d' % (bg.round_sagitta_target(half), bg.round_quad_segs(half))) if args.offset_join == 'round' and half > 0 else ''}),"
        f" sheet inset {inset}",
        f"solve sheet     : {solve_w:.3f} x {solve_h:.3f}",
        f"sampling        : {args.sampling}, role allowance {'on' if not args.no_role_allowance else 'off'}, "
        f"chord budget {_budget_rule(args)}, ref diag {stats.get('ring_diag_ref')}",
        f"raw rings       : total vertices {sum(raw_v)}, max {max(raw_v)}, min {min(raw_v)}",
        f"nesting rings   : total vertices {sum(ring_v)}, max {max(ring_v)}, min {min(ring_v)} (all >= 3: {min(ring_v) >= 3})",
        f"solve rings     : total vertices {sum(solve_v)}, max {max(solve_v)}, min {min(solve_v)} (all >= 3: {min(solve_v) >= 3})",
        f"dropped holes   : {tot_dropped} (consumed by the clearance/2 shrink)",
    ]
    if al:
        grown = [a for a in al if a.get("applied", 0) > 0]
        worst = max(al, key=lambda a: a.get("escape", 0))
        rep.append(f"role allowance  : applied to {len(grown)} of {len(al)} rings; max measured escape "
                   f"{worst['escape']:.4f} (applied {worst['applied']:.4f}); max residual "
                   f"{max(a.get('residual', 0) for a in al):.2e}")
    for p in parts_json:
        if p["qty"] > 1:
            rep.append(f"  dup {p['name']} x{p['qty']}: max rel symdiff "
                       f"{max(c['rel_symdiff'] for c in p['copies']):.2e}, max Hausdorff "
                       f"{max(c['hausdorff'] for c in p['copies']):.4f} mm, mirrored copies "
                       f"{[k for k, c in enumerate(p['copies']) if c['to_local']['mirrored']]}")
    rep.append(f"written         : instance.json, instance.nfp.txt, preview.svg, report.txt in {os.path.abspath(args.out)}")
    txt = "\n".join(rep)
    with open(os.path.join(args.out, "report.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write(txt + "\n")
    log(txt)
    return inst


def _budget_rule(args) -> str:
    frac = rs.SAG_COARSE if args.sampling == "coarse" else rs.SAG_FINE
    if args.sagitta_abs:
        return f"max(mtol, min({frac}*segment_diag, {args.sagitta_abs}))  [--sagitta-abs override]"
    if args.plugin_legacy_sampling:
        return f"max(mtol, {frac}*segment_diag)  [--plugin-legacy-sampling]"
    rf = rs.CHORD_FRACTION_OF_RING_COARSE if args.sampling == "coarse" else rs.CHORD_FRACTION_OF_RING
    return f"max(mtol, min({frac}*segment_diag, {rf}*batch_max_ring_diag))  [plugin rule]"


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--parts", required=True)
    ap.add_argument("--sheet", default=None, help="sheet .3dm (optional when --sheet-size is given)")
    ap.add_argument("--sheet-size", type=float, nargs=2, metavar=("W", "H"),
                    help="override the sheet dimensions (e.g. 3000 1500)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--margin", type=float, default=15.0)
    ap.add_argument("--clearance", type=float, default=10.0)
    ap.add_argument("--offset-join", choices=["round", "mitre"], default="round",
                    help="join style of the clearance/2 part offset: round (default) = the exact clearance region "
                         "(Minkowski sum with a disk, what the plugin's nest_geo.offset_nesting_boundary does); "
                         "mitre = the old behaviour (mitre limit 2), kept for A/B")
    ap.add_argument("--sheets", type=int, default=10)
    ap.add_argument("--sampling", choices=["fine", "coarse"], default="fine")
    ap.add_argument("--no-role-allowance", action="store_true")
    ap.add_argument("--sagitta-abs", type=float, default=None, metavar="MM",
                    help="explicit override of the chord-deviation cap: max(mtol, min(frac*diag, MM)) instead "
                         "of the plugin's cap (CHORD_FRACTION_OF_RING x the batch's largest ring bbox diagonal)")
    ap.add_argument("--plugin-legacy-sampling", action="store_true",
                    help="reproduce the OLD plugin rule: relative-only budget max(mtol, frac*segment_diag), "
                         "no ring cap")
    ap.add_argument("--merge-duplicates", dest="merge_duplicates", action="store_true", default=True)
    ap.add_argument("--no-merge-duplicates", dest="merge_duplicates", action="store_false")
    ap.add_argument("--merge-mirrored", dest="merge_mirrored", action="store_true", default=True,
                    help="also merge mirror-image duplicates (copy = part turned over); default on")
    ap.add_argument("--no-merge-mirrored", dest="merge_mirrored", action="store_false")
    ap.add_argument("--dup-tol", type=float, default=0.5,
                    help="max Hausdorff distance (mm) between a copy and its part for a duplicate merge")
    ap.add_argument("--sheet-origin", type=float, nargs=2, metavar=("X", "Y"))
    ap.add_argument("--sheet-pitch", type=float, default=None)
    ap.add_argument("--z-tol", type=float, default=0.01)
    ap.add_argument("--exclude-layers", nargs="*", default=[])
    args = ap.parse_args(argv)
    if not args.sheet and not args.sheet_size:
        ap.error("--sheet is required unless --sheet-size is given")
    build(args, print)


if __name__ == "__main__":
    main()
