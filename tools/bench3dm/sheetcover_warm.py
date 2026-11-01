"""Bounded warm insertion into jointly movable sheet layouts.

Requires the np_nest_warm_one DLL export and engines.solve_np_warm adapter.
Every accepted candidate preserves all incumbent parts and adds one missing part.
"""
import argparse
import hashlib
import json
import math
import sys
import time
from multiprocessing import Pool
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_repair import save_json, validate_candidate
from sheetcover_polish import polish_candidate


def warm_key(placed, incoming):
    data = [int(incoming), sorted((int(p), list(v)) for p, v in placed.items())]
    return hashlib.sha256(json.dumps(data, separators=(",", ":")).encode()).hexdigest()


def schedule_warm(pk, sheets, missing, done, *, limit=24, seed=100, gens=4000, starts=1, cap=60):
    """Expose different incoming parts and recipients early; deduplicate exact warm states."""
    recipients = sorted(sheets, key=lambda s: (sum(pk.solve_area[p] for p in s["placed"]), sorted(s["placed"])))
    incoming_parts = sorted(set(missing), key=lambda p: (-pk.solve_area[p], p))
    jobs, scheduled = [], set(done)
    for offset in range(len(recipients)):
        for index, incoming in enumerate(incoming_parts):
            placed = recipients[(offset + index) % len(recipients)]["placed"]
            if incoming in placed or sum(pk.solve_area[p] for p in placed) + pk.solve_area[incoming] > pk.SA:
                continue
            key = warm_key(placed, incoming)
            if key in scheduled:
                continue
            scheduled.add(key)
            jobs.append({"placed": placed, "incoming": incoming, "key": key, "seed": seed + len(jobs),
                         "gens": gens, "starts": starts, "cap": cap, "tag": f"warm_s{seed}_j{len(jobs)}"})
            if len(jobs) >= limit:
                return jobs
    return jobs


def run_warm(job):
    import engines as eng
    pk = sc._PK
    warm = {int(p): tuple(v) for p, v in job["placed"].items()}
    ids = sorted(warm) + [job["incoming"]]
    sub = dict(pk.instance)
    sub["parts"] = [dict(pk.parts[p], qty=1) for p in ids]
    sub["sheet"] = dict(pk.instance["sheet"], count=1)
    inputs = eng.flatten_np(sub, pk.cfg)
    values = dict(pk.base)
    values.update(iter_budget=max(500, job["gens"]), n_starts=job["starts"], seed=job["seed"])
    params = eng.params_struct(eng.ENGINE_NP, values)
    started = time.time()
    result = eng.solve_np_warm(pk.lib, inputs, params, warm, job["cap"])
    placed = {str(pid): [float(result["tx"][index]), float(result["ty"][index]), float(result["angle_rad"][index])]
              for index, (pid, _) in enumerate(inputs.copies) if int(result["sheet"][index]) == 0}
    candidate = {"tag": job["tag"], "placed": placed, "group": ids, "offered": ids, "plates_ok": False,
                 "incoming": job["incoming"], "seed": job["seed"], "warm_key": job["key"],
                 "wall": time.time() - started, "rc": int(result["rc"]),
                 "quality": result.get("quality", {}), "cancelled": bool(result.get("cancelled", False)),
                 "area": sum(pk.solve_area[int(p)] for p in placed) / pk.SA}
    complete = candidate["rc"] == 0 and set(map(int, placed)) == set(ids)
    if not complete:
        candidate.update(validated=False, validation_reason="warm engine did not return every incumbent plus incoming")
        return candidate
    candidate["plates_ok"] = True
    valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
    if not valid:
        polished = polish_candidate(pk, candidate)
        if polished is not None:
            candidate = polished
            valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
    candidate.update(validated=valid, validation_reason=why)
    return candidate


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--pool", required=True)
    ap.add_argument("--required-parts", help="JSON ID list or certificate with hard_parts; enforce these in every selected cover")
    ap.add_argument("--coverage-objective", choices=("area", "count"), default="area")
    ap.add_argument("--dll", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--rounds", type=int, default=2)
    ap.add_argument("--max-jobs", type=int, default=24, help="finite jobs per round")
    ap.add_argument("--workers", type=int, default=4)
    ap.add_argument("--gens", type=int, default=4000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--cap", type=float, default=60)
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--rotations", type=int, default=3600)
    ap.add_argument("--curve-tol", type=float, default=0)
    ap.add_argument("--env", action="append", default=[])
    args = ap.parse_args()
    if args.rounds < 0 or min(args.k, args.max_jobs, args.workers, args.gens, args.starts, args.cap) <= 0:
        ap.error("positive sheet/job/worker/budget limits required; rounds may be zero")
    env = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5"}
    for kv in args.env:
        key, value = kv.split("=", 1)
        env[key] = value
    env_list = [f"{key}={value}" for key, value in env.items()]
    sc._init(args.instance, args.dll, args.rotations, args.curve_tol, env_list)
    pk = sc._PK
    required = []
    if args.required_parts:
        document = json.loads(Path(args.required_parts).read_text(encoding="utf-8"))
        required = sorted(set(map(int, document["hard_parts"] if isinstance(document, dict) else document)))
        if not set(required) <= set(pk.parts):
            ap.error("required-parts contains unknown IDs")
    if any(int(p.get("qty", 1)) != 1 for p in pk.parts.values()):
        ap.error("candidate pools currently require unique part IDs with qty=1")
    if args.rounds:
        import engines as eng
        if not hasattr(eng, "solve_np_warm") or not hasattr(pk.lib, "np_nest_warm_one"):
            ap.error("warm repair requires engines.solve_np_warm and DLL np_nest_warm_one export")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "raw_candidates").mkdir(exist_ok=True)
    fingerprint = hashlib.sha256(Path(args.instance).read_bytes()).hexdigest()
    previous = {}
    if (out / "progress.json").exists():
        previous = json.loads((out / "progress.json").read_text(encoding="utf-8"))
        if previous.get("instance_sha256") != fingerprint:
            raise ValueError("checkpoint instance mismatch; use a new output directory")
        if previous.get("required_parts", []) != required:
            raise ValueError("checkpoint coverage constraint mismatch; use a new output directory")
        if previous.get("coverage_objective", "area") != args.coverage_objective:
            raise ValueError("checkpoint coverage objective mismatch; use a new output directory")
    source = out / "candidates.json" if previous else Path(args.pool)
    candidates = []
    for candidate in json.loads(source.read_text(encoding="utf-8")):
        valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        if not valid:
            candidate = polish_candidate(pk, candidate)
            if candidate is None:
                sc.log(f"skip cached candidate: {why}")
                continue
        candidates.append(candidate)
    done = set(previous.get("completed_keys", []))
    records = previous.get("results", [])

    def checkpoint():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk, required_ids=required,
                                         objective=args.coverage_objective)
        if chosen is None:
            raise RuntimeError("union selection returned no incumbent")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(out / "progress.json", {"instance_sha256": fingerprint, "required_parts": required,
                  "coverage_objective": args.coverage_objective, "completed_keys": sorted(done),
                  "results": records, "uncovered": missing, "chosen": [candidates[j]["tag"] for j in chosen]})
        import groupfill_nfp as gf
        gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        sc.log(f"warm cover: {len(sheets)} sheets, missing {missing}, pool {len(candidates)}")
        return sheets, missing

    sheets, missing = checkpoint()
    for round_index in range(args.rounds):
        if not missing:
            break
        jobs = schedule_warm(pk, sheets, missing, done, limit=args.max_jobs,
                             seed=args.seed + round_index * args.max_jobs,
                             gens=args.gens, starts=args.starts, cap=args.cap)
        if not jobs:
            sc.log("no new warm repair offers")
            break
        save_json(out / f"jobs_s{args.seed}_r{round_index}.json", jobs)
        sc.log(f"warm round {round_index + 1}: {len(jobs)} jobs, {args.workers} workers, cap {args.cap}s")
        with Pool(args.workers, initializer=sc._init,
                  initargs=(args.instance, args.dll, args.rotations, args.curve_tol, env_list)) as pool:
            for candidate in pool.imap_unordered(run_warm, jobs):
                save_json(out / "raw_candidates" / (candidate["tag"] + ".json"), candidate)
                done.add(candidate["warm_key"])
                if candidate["validated"]:
                    candidates.append(candidate)
                records.append({k: candidate[k] for k in ("tag", "incoming", "warm_key", "wall", "rc", "quality",
                                                          "cancelled", "validated", "validation_reason")})
                sc.log(f"{candidate['tag']}: rc={candidate['rc']}, accepted={candidate['validated']}, "
                       f"wall={candidate['wall']:.1f}s cancelled={candidate['cancelled']}")
                sheets, missing = checkpoint()
                if not missing:
                    pool.terminate()
                    break
    from validate_strict import validate_strict
    placements = json.loads((out / "placements.json").read_text(encoding="utf-8"))
    metrics = validate_strict(pk.instance, placements, max_sheets=args.k)
    save_json(out / "strict_metrics.json", metrics)
    from evaluate import evaluate_files
    evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)
    sc.log(f"RESULT strict_polygon_pass={metrics['passed_sampled_polygons']} placed={metrics['placed']}/{metrics['total']} "
           f"sheets={metrics['n_sheets']} min_gap={metrics['min_pair_distance_mm_including_holes']} min_margin={metrics['min_margin_mm']}")


if __name__ == "__main__":
    main()
