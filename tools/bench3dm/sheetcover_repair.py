"""Adaptive whole-sheet candidate repair for sheetcover; no instance-specific assignments.

Reuse feasible layouts, require missing parts in new joint packs, and allow existing
small parts to move to other candidate sheets through the union-cover selection.
"""
import argparse
import hashlib
import itertools
import json
import math
import os
import sys
from multiprocessing import Pool
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc


def offer_key(ids, mandatory):
    return (tuple(sorted(ids)), tuple(sorted(mandatory)))


def schedule_repairs(pk, sheets, missing, attempted, *, big_frac=.05, keep_long=False,
                     long_frac=.33, pairs=False, limit=24, seed=100, gens=4000,
                     starts=1, cap=120, pair_quota=0):
    """Round-robin over incoming parts and recipient sheets, preferring roomy sheets.

    Incoming parts are mandatory. Existing plates (and optionally long parts) stay
    mandatory; other existing parts may be displaced to create useful new columns.
    """
    missing = sorted(set(missing), key=lambda p: (-pk.solve_area[p], p))
    families = [[(p,) for p in missing]]
    if pairs:
        families.append(list(itertools.combinations(missing, 2)))
    recipients = sorted(sheets, key=lambda s: sum(pk.solve_area[p] for p in s["placed"]))
    jobs = []
    # Diagonal traversal exposes different missing parts AND different recipients
    # early. Finish all single-part offers before spending a budget on pairs.
    offers = ((incoming, (index + offset) % len(recipients))
              for bundles in families for offset in range(len(recipients))
              for index, incoming in enumerate(bundles))
    if pair_quota:
        offers = list(offers)
        pair_offers = [offer for offer in offers if len(offer[0]) == 2]
        single_offers = [offer for offer in offers if len(offer[0]) == 1]
        offers = pair_offers[:pair_quota] + single_offers + pair_offers[pair_quota:]
    for incoming, rank in offers:
        current = set(recipients[rank]["placed"])
        ids = current | set(incoming)
        mandatory = set(incoming) | {p for p in current if pk.solve_area[p] >= big_frac * pk.SA}
        if keep_long:
            mandatory |= {p for p in current if sc.long_dim(pk.solve_poly[p]) >= long_frac * max(pk.W, pk.H)}
        key = offer_key(ids, mandatory)
        if key in attempted or sum(pk.solve_area[p] for p in mandatory) > pk.SA:
            continue
        attempted.add(key)
        jobs.append({"group": sorted(mandatory), "fillers": sorted(ids - mandatory),
                     "incoming": list(incoming), "seed": seed + len(jobs), "gens": gens,
                     "starts": starts, "cap": cap, "tag": f"repair_s{seed}_j{len(jobs)}"})
        if len(jobs) >= limit:
            return jobs
    return jobs


def validate_source_candidate(pk, candidate):
    """Check the actual source polygons, including holes and requested sheet margin."""
    if not hasattr(pk, "instance"):
        return True, "source instance unavailable; solve geometry only"
    from validate_strict import validate_strict
    actual = sorted(map(int, candidate.get("placed", {})))
    sub = dict(pk.instance, parts=[dict(pk.parts[p], qty=1) for p in actual])
    placements = {"instances": [{"part": int(p), "copy": 0, "sheet": 0,
                                 "tx": v[0], "ty": v[1], "angle_rad": v[2]}
                                for p, v in candidate.get("placed", {}).items()]}
    result = validate_strict(sub, placements, max_sheets=1)
    if result["passed_sampled_polygons"]:
        return True, "valid source polygons"
    return False, (f"source geometry: min gap {result['min_pair_distance_mm_including_holes']} "
                   f"(required {result['required_clearance_mm']}), margin {result['min_margin_mm']} "
                   f"(required {result['required_margin_mm']})")


def validate_candidate(pk, candidate, tolerance=1e-4, check_source=True):
    """Check solve polygons, then source-ring gaps/margins when an instance is available."""
    from shapely import affinity
    from shapely.geometry import box
    sheet = box(0, 0, pk.W, pk.H)
    geometries = []
    seen = set()
    for raw_id, transform in candidate.get("placed", {}).items():
        pid = int(raw_id)
        if pid not in pk.parts or pid in seen:
            return False, "unknown or duplicate part ID"
        seen.add(pid)
        if len(transform) != 3 or not all(math.isfinite(v) for v in transform):
            return False, "invalid transform"
        tx, ty, angle = transform
        geom = affinity.translate(affinity.rotate(pk.solve_poly[pid], math.degrees(angle), origin=(0, 0)), tx, ty)
        if not geom.is_valid or geom.difference(sheet).area > tolerance:
            return False, f"part {pid} outside solve sheet or invalid"
        for other_id, other in geometries:
            if geom.intersection(other).area > tolerance:
                return False, f"parts {pid}/{other_id} overlap"
        geometries.append((pid, geom))
    if not seen:
        return False, "empty layout"
    if not set(map(int, candidate.get("group", []))) <= seen:
        return False, "missing mandatory part"
    if check_source:
        return validate_source_candidate(pk, candidate)
    return True, "valid"


def geometry_diagnostics(pk, placed):
    """Quantify raw geometry errors without changing acceptance thresholds."""
    from shapely import affinity
    from shapely.geometry import box
    sheet = box(0, 0, pk.W, pk.H)
    shapes, outside, overlaps = {}, [], []
    for raw_id, transform in placed.items():
        pid = int(raw_id)
        tx, ty, angle = transform
        if pid not in pk.solve_poly or not all(math.isfinite(v) for v in transform):
            continue
        geom = affinity.translate(affinity.rotate(pk.solve_poly[pid], math.degrees(angle), origin=(0, 0)), tx, ty)
        if not geom.is_valid:
            outside.append({"part": pid, "invalid": True})
            continue
        x0, y0, x1, y1 = geom.bounds
        area = geom.difference(sheet).area
        if area > 1e-8:
            outside.append({"part": pid, "area_mm2": area,
                            "bbox_protrusion_mm": max(0, -x0, -y0, x1 - pk.W, y1 - pk.H)})
        for other, shape in shapes.items():
            area = geom.intersection(shape).area
            if area > 1e-8:
                overlaps.append({"a": other, "b": pid, "area_mm2": area})
        shapes[pid] = geom
    return {"outside": outside, "overlaps": overlaps}


def pack_repair(job):
    import time
    pk = sc._PK
    ids = job["group"] + job["fillers"]
    started = time.time()
    placed, result = pk.pack(ids, job["gens"], job["starts"], job["seed"], job["cap"], mandatory=set(job["group"]))
    candidate = {"tag": job["tag"], "group": job["group"], "offered": ids,
                 "placed": {str(p): v for p, v in placed.items()}, "seed": job["seed"],
                 "incoming": job["incoming"], "plates_ok": set(job["group"]) <= set(placed),
                 "area": sum(pk.solve_area[p] for p in placed) / pk.SA,
                 "wall": time.time() - started, "cancelled": bool(result.get("cancelled", False)),
                 "quality": result.get("quality", {})}
    candidate["raw_placed"] = dict(candidate["placed"])
    candidate["native_placed"] = {str(pid): [float(result["tx"][index]), float(result["ty"][index]), float(result["angle_rad"][index])]
                                  for index, pid in enumerate(ids) if int(result["sheet"][index]) == 0}
    candidate["raw_geometry_diagnostics"] = geometry_diagnostics(pk, candidate["raw_placed"])
    candidate["missing_mandatory"] = sorted(set(job["group"]) - set(placed))
    valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
    if not valid and candidate["plates_ok"] and candidate["placed"]:
        from sheetcover_polish import polish_candidate
        polished = polish_candidate(pk, candidate)
        if polished is not None:
            candidate = polished
            valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
    candidate["validated"] = valid
    candidate["validation_reason"] = why
    return candidate


def save_json(path, value):
    temp = path.with_suffix(path.suffix + ".tmp")
    temp.write_text(json.dumps(value, indent=1), encoding="utf-8")
    os.replace(temp, path)


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--dll", required=True)
    ap.add_argument("--pool", required=True, help="candidate pool JSON")
    ap.add_argument("--out", required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--rounds", type=int, default=2)
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--max-jobs", type=int, default=24, help="maximum pack jobs per round")
    ap.add_argument("--cap", type=float, default=120)
    ap.add_argument("--gens", type=int, default=4000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--big-frac", type=float, default=.05)
    ap.add_argument("--keep-long", action="store_true")
    ap.add_argument("--long-frac", type=float, default=.33)
    ap.add_argument("--pairs", action="store_true")
    ap.add_argument("--pair-quota", type=int, default=0, help="with --pairs, reserve this many early offers for pairs before singles")
    ap.add_argument("--rotations", type=int, default=3600)
    ap.add_argument("--curve-tol", type=float, default=0)
    ap.add_argument("--env", action="append", default=[])
    args = ap.parse_args()
    if args.k < 1 or args.rounds < 0 or min(args.workers, args.max_jobs, args.gens, args.starts, args.cap) <= 0:
        ap.error("k and budgets must be positive; rounds may be zero")
    defaults = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50",
                "NP_SIMPL_MAXDEV": "0.5", "NP_PRIO_MAX_RETRIES": "1"}
    for kv in args.env:
        key, value = kv.split("=", 1)
        defaults[key] = value
    env = [f"{key}={value}" for key, value in defaults.items()]
    sc._init(args.instance, args.dll, args.rotations, args.curve_tol, env)
    pk = sc._PK
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    fingerprint = hashlib.sha256(Path(args.instance).read_bytes()).hexdigest()
    progress_path = out / "progress.json"
    previous = {}
    if progress_path.exists():
        previous = json.loads(progress_path.read_text(encoding="utf-8"))
        if previous.get("instance_sha256") != fingerprint:
            raise ValueError("output checkpoint belongs to a different instance; use a new output directory")
    source_path = out / "candidates.json" if previous else Path(args.pool)
    source = json.loads(source_path.read_text(encoding="utf-8"))
    candidates, sets = [], set()
    for candidate in source:
        valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        if not valid and candidate.get("placed"):
            from sheetcover_polish import polish_candidate
            polished = polish_candidate(pk, candidate)
            if polished is not None:
                candidate = polished
                valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        if not valid:
            sc.log(f"skip cached {candidate.get('tag')}: {why}")
            continue
        signature = tuple(sorted(map(int, candidate["placed"])))
        if signature in sets:
            continue
        sets.add(signature)
        candidate["area"] = sum(pk.solve_area[p] for p in signature) / pk.SA
        candidates.append(candidate)
    attempted = set()
    results = previous.get("results", [])
    attempted = {offer_key(x[0], x[1]) for x in previous.get("attempted", [])}

    def select_and_save():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
        if chosen is None:
            raise RuntimeError("union coverage MILP returned no incumbent")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(progress_path, {"instance_sha256": fingerprint,
                                 "uncovered": missing, "chosen": [candidates[j]["tag"] for j in chosen],
                                 "attempted": sorted(attempted), "results": results,
                                 "covered_area": sum(pk.solve_area[p] for s in sheets for p in s["placed"]) / pk.SA})
        import groupfill_nfp as gf
        gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        sc.log(f"cover {len(sheets)} sheets, missing {missing}; pool {len(candidates)}")
        return sheets, missing

    sheets, missing = select_and_save()
    for round_index in range(args.rounds):
        if not missing:
            break
        jobs = schedule_repairs(pk, sheets, missing, set(attempted), big_frac=args.big_frac,
                                keep_long=args.keep_long, long_frac=args.long_frac, pairs=args.pairs,
                                limit=args.max_jobs, seed=args.seed + round_index * args.max_jobs,
                                gens=args.gens, starts=args.starts, cap=args.cap, pair_quota=args.pair_quota)
        if not jobs:
            sc.log("no new repair offers")
            break
        sc.log(f"round {round_index + 1}: {len(jobs)} jobs, {args.workers} workers, cap {args.cap}s")
        with Pool(args.workers, initializer=sc._init,
                  initargs=(args.instance, args.dll, args.rotations, args.curve_tol, env)) as pool:
            for candidate in pool.imap_unordered(pack_repair, jobs):
                diagnostic_dir = out / "raw_candidates"
                diagnostic_dir.mkdir(exist_ok=True)
                save_json(diagnostic_dir / f"{candidate['tag']}.json", candidate)
                attempted.add(offer_key(candidate["offered"], candidate["group"]))
                signature = tuple(sorted(map(int, candidate["placed"])))
                accepted = candidate["validated"] and signature not in sets
                if accepted:
                    candidates.append(candidate)
                    sets.add(signature)
                results.append({key: candidate[key] for key in ("tag", "incoming", "validated", "validation_reason", "wall", "cancelled", "quality", "plates_ok", "missing_mandatory", "raw_geometry_diagnostics")})
                sc.log(f"{candidate['tag']}: {len(signature)} parts, {candidate['wall']:.1f}s, accepted={accepted}, "
                       f"cancelled={candidate['cancelled']}, {candidate['validation_reason']}")
                sheets, missing = select_and_save()
                if not missing:
                    pool.terminate()
                    break
    from evaluate import evaluate_files
    metrics = evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)
    from validate_strict import validate_strict
    strict = validate_strict(pk.instance, json.loads((out / "placements.json").read_text(encoding="utf-8")), max_sheets=args.k)
    save_json(out / "strict_metrics.json", strict)
    sc.log(f"RESULT sheets={metrics['n_sheets']} placed={metrics['placed']}/{metrics['total']} "
           f"overlaps={metrics['overlap_pairs']} clearance_violations={metrics['clearance_violations']}")


if __name__ == "__main__":
    main()
