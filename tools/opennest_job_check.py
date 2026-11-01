"""Independent check of an opennest_job result against its job (shapely, no solver code).

    python -I tools/opennest_job_check.py <job.json> <result.json> [--tol 1e-6]

Places every ORIGINAL part ring with the result transform (rotate about the origin by `angle`, then
translate by (tx, ty), in the sheet's own coordinates) and verifies, per sheet copy:
  * every placed part lies inside the sheet outer ring with at least `margin` to its edge,
  * at least `margin` to every sheet hole,
  * at least `spacing` between any two parts, except a part placed inside another part's hole, which must
    keep `spacing` to that hole's wall.
Exit 0 when the layout passes, 1 otherwise. Prints the measured minima.
"""
from __future__ import annotations

import argparse
import json
import math
import sys

from shapely.geometry import Polygon
from shapely.strtree import STRtree


def ring(pts):
    p = [(float(x), float(y)) for x, y in pts]
    if len(p) > 1 and p[0] == p[-1]:
        p = p[:-1]
    return p


def real_rings(rings):
    """Drop holes that enclose nothing (the worker ignores them too)."""
    out = []
    for h in rings or []:
        r = ring(h)
        if len(set(r)) < 3:
            continue
        a = 0.0
        for i in range(len(r)):
            x1, y1 = r[i - 1]; x2, y2 = r[i]
            a += x1 * y2 - x2 * y1
        w = max(x for x, _ in r) - min(x for x, _ in r); hgt = max(y for _, y in r) - min(y for _, y in r)
        if abs(a) / 2 > 1e-9 * (w * w + hgt * hgt) + 1e-12:
            out.append(r)
    return out


def place(points, angle, tx, ty):
    c, s = math.cos(angle), math.sin(angle)
    return [(c * x - s * y + tx, s * x + c * y + ty) for x, y in points]


def main(argv=None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("job")
    ap.add_argument("result")
    ap.add_argument("--tol", type=float, default=1e-3,
                    help="absolute slack in model units (the solver works in float32; 0.002 is its own floor)")
    a = ap.parse_args(argv)
    job = json.load(open(a.job, encoding="utf-8-sig"))
    res = json.load(open(a.result, encoding="utf-8-sig"))
    spacing, margin, tol = float(job.get("spacing", 0)), float(job.get("margin", 0)), a.tol
    parts = {json.dumps(p.get("id", i)): p for i, p in enumerate(job["parts"])}
    sheet_specs = [s for s in job["sheets"] for _ in range(int(s.get("count", 1)))]
    problems, by_sheet = [], {}
    for pl in res["placements"]:
        if pl["sheet"] < 0:
            continue
        p = parts[json.dumps(pl["part"])]
        outer = place(ring(p["outer"]), pl["angle"], pl["tx"], pl["ty"])
        holes = [place(h, pl["angle"], pl["tx"], pl["ty"]) for h in real_rings(p.get("holes"))]
        by_sheet.setdefault(pl["sheet"], []).append((f"{pl['part']}#{pl['copy']}", Polygon(outer, holes)))
    min_edge = min_hole = min_pair = math.inf
    for sid, items in sorted(by_sheet.items()):
        spec = sheet_specs[sid]
        sheet = Polygon(ring(spec["outer"]))
        sheet_holes = [Polygon(h) for h in real_rings(spec.get("holes"))]
        for name, poly in items:
            if not sheet.buffer(tol).contains(poly):
                problems.append(f"sheet {sid}: {name} not inside the sheet")
            d = sheet.exterior.distance(poly)
            min_edge = min(min_edge, d)
            if d < margin - tol:
                problems.append(f"sheet {sid}: {name} is {d:.4f} from the sheet edge (< margin {margin})")
            for h in sheet_holes:
                dh = h.distance(poly)
                min_hole = min(min_hole, dh)
                if dh < margin - tol:
                    problems.append(f"sheet {sid}: {name} is {dh:.4f} from a sheet hole (< margin {margin})")
        polys = [p for _, p in items]
        tree = STRtree(polys)
        for i, (name, poly) in enumerate(items):
            for j in tree.query(poly.buffer(spacing + 1e-9)):
                j = int(j)
                if j <= i:
                    continue
                other = polys[j]
                # A hole is not part of a shapely Polygon, so a part seated in another part's hole is
                # disjoint from it and distance() is its gap to the hole wall: no special case needed.
                d = poly.distance(other)
                if poly.intersection(other).area > tol * tol:
                    problems.append(f"sheet {sid}: {name} overlaps {items[j][0]}")
                    d = 0.0
                min_pair = min(min_pair, d)
                if d < spacing - tol:
                    problems.append(f"sheet {sid}: {name} and {items[j][0]} are {d:.4f} apart (< spacing {spacing})")
    placed = sum(len(v) for v in by_sheet.values())
    print(f"placed {placed}/{len(res['placements'])} on {len(by_sheet)} sheet(s); spacing {spacing}, margin {margin}")
    print(f"min part-part {min_pair:.4f}  min part-edge {min_edge:.4f}  min part-sheet-hole {min_hole:.4f}")
    for p in problems[:20]:
        print("  FAIL", p)
    print("PASS" if not problems else f"FAIL ({len(problems)} problems)")
    return 0 if not problems else 1


if __name__ == "__main__":
    sys.exit(main())
