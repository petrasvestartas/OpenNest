"""Strict clearance/margin gate on source evaluation polygons, including hole pairs.

This leaves the legacy evaluator unchanged. Evaluation rings approximate curves;
passing polygon checks is not an exact-curve certificate. An optional externally
justified Hausdorff error bound gives conservative conditional curve checks.
"""
import argparse
import json
import math
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from evaluate import evaluate


def validate_strict(instance, placements, *, tolerance=1e-6, overlap_area_tolerance=1e-8,
                    max_sheets=None, curve_error_bound=None):
    if tolerance < 0 or overlap_area_tolerance < 0 or (curve_error_bound is not None and curve_error_bound < 0):
        raise ValueError("tolerances and error bounds must be nonnegative")
    expected = {(int(p["id"]), copy) for p in instance["parts"] for copy in range(int(p["qty"]))}
    actual = [(int(p["part"]), int(p.get("copy", 0))) for p in placements.get("instances", [])]
    if len(actual) != len(set(actual)):
        raise ValueError("duplicate part-copy placement")
    if set(actual) - expected:
        raise ValueError("unknown part-copy placement")
    result = evaluate(instance, placements, rings="true")
    clearance = float(instance.get("clearance", 0))
    margin = float(instance.get("margin", 0))
    pairs = result["_pairs"]
    items = result["_items"]
    distance = min((pair["distance"] for pair in pairs), default=None)
    min_margin = min((item["margin"] for item in items), default=None)
    clearance_bad = [pair for pair in pairs if pair["distance"] + tolerance < clearance]
    overlap_bad = [pair for pair in pairs if pair["overlap_area"] > overlap_area_tolerance]
    margin_bad = [{"part": item["part"], "copy": item["copy"], "sheet": item["sheet"], "margin": item["margin"]}
                  for item in items if item["margin"] + tolerance < margin]
    full = result["placed"] == len(expected)
    sheet_ok = max_sheets is None or result["n_sheets"] <= max_sheets
    sampled_pass = full and sheet_ok and not (clearance_bad or overlap_bad or margin_bad)
    parts = {int(p["id"]): p for p in instance["parts"]}
    near_exact = 0
    for item in items:
        copies = parts[item["part"]].get("copies", [])
        near_exact += int(item["copy"] < len(copies) and bool(copies[item["copy"]].get("eval")))
    curve_check = None
    if curve_error_bound is not None:
        curve_check = sampled_pass and (distance is None or distance - 2 * curve_error_bound + tolerance >= clearance) and (
            min_margin is None or min_margin - curve_error_bound + tolerance >= margin)
    return {"passed_sampled_polygons": bool(sampled_pass), "scope": "source evaluation polygons; not exact analytic curves",
            "placed": result["placed"], "total": len(expected), "complete": full,
            "n_sheets": result["n_sheets"], "max_sheets": max_sheets, "sheet_limit_passed": sheet_ok,
            "required_clearance_mm": clearance, "required_margin_mm": margin,
            "numeric_distance_tolerance_mm": tolerance, "numeric_overlap_area_tolerance_mm2": overlap_area_tolerance,
            "min_pair_distance_mm_including_holes": distance, "min_margin_mm": min_margin,
            "clearance_violations": clearance_bad, "margin_violations": margin_bad, "overlap_pairs": overlap_bad,
            "unplaced": result["_unplaced"], "near_exact_eval_ring_parts": near_exact,
            "fallback_nesting_ring_parts": len(items) - near_exact,
            "curve_error_bound_mm": curve_error_bound,
            "curve_bound_basis": "externally supplied; not verified by this script" if curve_error_bound is not None else "not established",
            "conditional_curve_bounds_passed": curve_check,
            "approximation_note": "Per-copy eval rings use the importer's nominal 0.005mm sagitta target. "
                                  "This script does not verify a Hausdorff bound against the original CAD curves. "
                                  "An externally justified bound e reduces the guaranteed pair gap by 2e and edge margin by e."}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--placements", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--max-sheets", type=int)
    ap.add_argument("--tolerance", type=float, default=1e-6)
    ap.add_argument("--curve-error-bound", type=float)
    args = ap.parse_args()
    instance = json.loads(Path(args.instance).read_text(encoding="utf-8"))
    placements = json.loads(Path(args.placements).read_text(encoding="utf-8"))
    result = validate_strict(instance, placements, tolerance=args.tolerance,
                             max_sheets=args.max_sheets, curve_error_bound=args.curve_error_bound)
    path = Path(args.out)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(result, indent=1), encoding="utf-8")
    print(f"sampled_polygon_pass={result['passed_sampled_polygons']} placed={result['placed']}/{result['total']} "
          f"sheets={result['n_sheets']} min_gap={result['min_pair_distance_mm_including_holes']} "
          f"min_margin={result['min_margin_mm']} conditional_curve_pass={result['conditional_curve_bounds_passed']}")
    return 0 if result["passed_sampled_polygons"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
