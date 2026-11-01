"""Independent shapely validator for np_bench layouts with IRREGULAR sheet outlines and SHEET HOLES.

np_bench (src/nest_physics_cpp/bench/np_bench.cpp) writes `--json=PATH`: every sheet's TRUE outline and TRUE
interior holes (not the engine's keep-out decomposition) and every part's world outer ring + holes. This script
re-checks the layout with shapely, independent of the engine and of np_bench's own convex-clip measurement:

  * no two parts on the same sheet overlap (part MATERIAL = outer minus its holes, so a part seated in another
    part's hole is legitimate),
  * every placed part lies inside its sheet's usable region (outline minus holes),
  * every pair keeps at least `clearance` (0 for np_bench: the parts are already the offset outlines),

and reports sheets used, per-sheet utilisation (material area / usable area), the least-filled sheet and the
bounding box of its used region (the remnant).

    python -I tools/bench3dm/validate_layout.py <layout.json> [--tol-frac 1e-4] [--json-out result.json]

Exit code 0 = valid, 1 = invalid (violations listed), 2 = bad input.
Tolerances are RELATIVE: an overlap / out-of-sheet area counts when it exceeds tol_frac x the smaller part's
area, and an excursion when it exceeds tol_frac x the sheet span; the engine's own contract excuses contact up
to 1e-3 of a part's size, so 1e-4 is stricter than the engine.
"""
from __future__ import annotations

import argparse
import json
import sys

from shapely.geometry import Polygon
from shapely.strtree import STRtree
from shapely.validation import make_valid


def _poly(outer, holes=()):
    p = Polygon(outer, [h for h in holes if len(h) >= 3])
    if not p.is_valid:
        p = make_valid(p)
    return p


def validate(doc: dict, tol_frac: float = 1e-4) -> dict:
    sheets = []
    for s in doc["sheets"]:
        outer = _poly(s["outer"])
        usable = _poly(s["outer"], s.get("holes", []))
        sheets.append({"outline": outer, "usable": usable, "usable_area": usable.area,
                       "span": (outer.bounds[2] - outer.bounds[0]) + (outer.bounds[3] - outer.bounds[1])})
    clearance = float(doc.get("clearance", 0.0) or 0.0)
    placed, unplaced = [], []
    for p in doc["parts"]:
        if p.get("sheet", -1) is None or p.get("sheet", -1) < 0 or "outer" not in p:
            unplaced.append(p["id"])
            continue
        solid = _poly(p["outer"])
        mat = _poly(p["outer"], p.get("holes", []))
        placed.append({"id": p["id"], "sheet": int(p["sheet"]), "solid": solid, "mat": mat})
    viol = []
    per_sheet = {}
    for q in placed:
        per_sheet.setdefault(q["sheet"], []).append(q)
    # inside the usable region
    for q in placed:
        S = sheets[q["sheet"]]
        out_area = q["solid"].difference(S["usable"]).area
        if out_area > tol_frac * max(q["mat"].area, 1e-30):
            viol.append(f"part {q['id']} sheet {q['sheet']}: {out_area:.6g} outside the usable sheet region "
                        f"({100 * out_area / q['solid'].area:.3f}% of the part)")
    # pairwise overlap / clearance per sheet
    n_pairs = 0
    for s, qs in per_sheet.items():
        tree = STRtree([q["solid"] for q in qs])
        for i, q in enumerate(qs):
            geom = q["solid"].buffer(clearance) if clearance > 0 else q["solid"]
            for j in tree.query(geom):
                j = int(j)
                if j <= i:
                    continue
                r = qs[j]
                a = q["mat"].intersection(r["mat"]).area
                if a > tol_frac * min(q["mat"].area, r["mat"].area):
                    viol.append(f"sheet {s}: parts {q['id']} and {r['id']} overlap by {a:.6g}")
                    n_pairs += 1
                elif clearance > 0:
                    d = q["mat"].distance(r["mat"])
                    if d < clearance - 0.05 and a == 0:
                        viol.append(f"sheet {s}: parts {q['id']} and {r['id']} clearance {d:.3f} < {clearance}")
    used = sorted(per_sheet)
    util = {s: sum(q["mat"].area for q in per_sheet[s]) / sheets[s]["usable_area"] for s in used}
    least = min(used, key=lambda s: util[s]) if used else None
    rem = None
    if least is not None:
        xs0 = sheets[least]["outline"].bounds
        b = [1e300, 1e300, -1e300, -1e300]
        for q in per_sheet[least]:
            bb = q["solid"].bounds
            b = [min(b[0], bb[0]), min(b[1], bb[1]), max(b[2], bb[2]), max(b[3], bb[3])]
        rem = (b[2] - xs0[0]) * (b[3] - xs0[1]) / ((xs0[2] - xs0[0]) * (xs0[3] - xs0[1]))
    used_area = sum(sheets[s]["usable_area"] for s in used)
    return {"valid": not viol, "violations": viol, "sheets_used": len(used), "used_sheet_ids": used,
            "util_per_sheet": [round(100 * util[s], 2) for s in used], "unplaced": len(unplaced),
            "least_filled_util": round(100 * util[least], 2) if least is not None else None,
            "remnant_bbox_frac": rem, "used_usable_area": used_area,
            "overall_util": round(100 * sum(sum(q["mat"].area for q in per_sheet[s]) for s in used) / used_area, 2) if used else 0.0,
            "overlap_pairs": n_pairs}


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("layout")
    ap.add_argument("--tol-frac", type=float, default=1e-4)
    ap.add_argument("--json-out")
    a = ap.parse_args(argv)
    try:
        doc = json.load(open(a.layout, encoding="utf-8"))
    except Exception as e:  # noqa: BLE001
        print(f"bad input: {e}", file=sys.stderr)
        return 2
    r = validate(doc, a.tol_frac)
    print(f"VALID={r['valid']} sheets={r['sheets_used']} unplaced={r['unplaced']} util={r['util_per_sheet']} "
          f"least={r['least_filled_util']} remnant_bbox={r['remnant_bbox_frac']}")
    for v in r["violations"][:20]:
        print("  VIOLATION:", v)
    if a.json_out:
        json.dump(r, open(a.json_out, "w"), indent=1)
    return 0 if r["valid"] else 1


if __name__ == "__main__":
    sys.exit(main())
