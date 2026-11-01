"""Retain independently validated actual subsets of failed joint-pack offers."""
import argparse
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_polish import polish_candidate
from sheetcover_repair import validate_candidate, save_json
from validate_strict import validate_strict


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "dll", "pool", "raw", "out"):
        ap.add_argument("--" + name, required=True)
    args = ap.parse_args()
    sc._init(args.instance, args.dll, 3600, 0, [])
    pk = sc._PK
    candidates = json.loads(Path(args.pool).read_text())
    seen = {tuple(sorted(map(int, c["placed"]))) for c in candidates}
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    reports = []
    import engines as eng
    for path in sorted(Path(args.raw).glob("*.json")):
        candidate = json.loads(path.read_text())
        placed = candidate.get("placed", {})
        if not placed:
            continue
        requested = candidate.get("requested_group", candidate.get("group", []))
        candidate["requested_group"] = requested
        candidate["unmet_core"] = sorted(set(requested) - set(map(int, placed)))
        candidate["partial_proposal"] = bool(candidate["unmet_core"])
        candidate["generation_plates_ok"] = candidate.get("plates_ok", False)
        candidate["group"] = sorted(set(requested) & set(map(int, placed)))
        candidate["plates_ok"] = True
        polished = polish_candidate(pk, candidate)
        if polished is not None:
            candidate = polished
        valid, reason = validate_candidate(pk, candidate, tolerance=1e-8)
        placements = {"frame": eng.FRAME, "instances": [
            {"part": int(i), "copy": 0, "sheet": 0, "tx": t[0], "ty": t[1], "angle_rad": t[2]}
            for i, t in candidate["placed"].items()]}
        strict = validate_strict(pk.instance, placements, max_sheets=1)
        source_ok = not (strict["clearance_violations"] or strict["margin_violations"] or strict["overlap_pairs"])
        signature = tuple(sorted(map(int, candidate["placed"])))
        accepted = valid and source_ok and signature not in seen
        candidate["validated"] = valid and source_ok
        candidate["validation_reason"] = ("solve and source polygons checked" if valid and source_ok
                                          else reason if not valid else "source geometry failed")
        candidate["source_geometry_metrics"] = strict
        if accepted:
            candidate["tag"] += ":actual_subset"
            candidate["area"] = sum(pk.solve_area[i] for i in signature) / pk.SA
            candidates.append(candidate)
            seen.add(signature)
        reports.append({"source": str(path), "accepted": accepted, "solve_valid": valid,
                        "source_geometry_valid": source_ok, "actual_parts": signature,
                        "unmet_core": candidate["unmet_core"], "strict": strict})
    save_json(out / "candidates.json", candidates)
    save_json(out / "salvage-report.json", reports)
    print(json.dumps({"pool": len(candidates), "added": sum(x["accepted"] for x in reports)}))


if __name__ == "__main__":
    main()
