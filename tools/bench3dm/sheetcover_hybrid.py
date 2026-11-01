"""Recover failed exchange partners with warm insertion, reusing a validated first sheet.

Consumes a bounded job manifest containing warm incumbents, one incoming part,
the cached partner-column tag, and the expected disjoint pair union. No part IDs
or reference-derived assignments are embedded in the algorithm.
"""
import argparse
import json
import sys
from multiprocessing import Pool
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_warm import run_warm
from sheetcover_exchange import valid_column
from sheetcover_repair import save_json
from validate_strict import validate_strict


def validate_pair_sets(left, right, expected):
    a, b = set(map(int, left["placed"])), set(map(int, right["placed"]))
    return not (a & b) and a | b == set(expected)


def baseline_sheet_for_seed(seed, baseline):
    """Ensure the warm seed is an unchanged subset of one documented baseline sheet."""
    sheets = {}
    for p in baseline["instances"]:
        if p["sheet"] >= 0:
            sheets.setdefault(p["sheet"], {})[int(p["part"])] = (p["tx"], p["ty"], p["angle_rad"])
    wanted = {int(p): tuple(v) for p, v in seed.items()}
    for index, placed in sorted(sheets.items()):
        if wanted and all(p in placed and placed[p] == v for p, v in wanted.items()):
            return index
    raise ValueError("warm seed is not an unchanged subset of the supplied baseline")


def run_hybrid(job):
    result = run_warm(job)
    result["source_manifest"] = job["source_manifest"]
    result["source_baseline"] = job["source_baseline"]
    result["baseline_sheet"] = job["baseline_sheet"]
    result["paired_completed_column"] = job["paired_completed_column"]
    if not result["validated"]:
        return {"accepted": False, "warm": result, "reason": result["validation_reason"]}
    right = valid_column(sc._PK, result)
    left = valid_column(sc._PK, job["cached_left"])
    if right is None or left is None:
        return {"accepted": False, "warm": result, "reason": "strict source/solve geometry gate"}
    if not validate_pair_sets(left, right, job["expected_pair_union"]):
        return {"accepted": False, "warm": result, "reason": "cached/warm pair did not preserve exact expected union"}
    return {"accepted": True, "warm": right, "left": left, "reason": "complete strict-valid hybrid exchange"}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for arg in ("instance", "dll", "pool", "jobs", "baseline", "out"):
        ap.add_argument("--" + arg, required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--workers", type=int, default=2)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=16000)
    ap.add_argument("--max-jobs", type=int, default=2)
    ap.add_argument("--env", action="append", default=[])
    args = ap.parse_args()
    if min(args.k, args.workers, args.cap, args.gens, args.max_jobs) <= 0:
        ap.error("budgets must be positive")
    env = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5"}
    env.update(kv.split("=", 1) for kv in args.env)
    settings = [f"{key}={value}" for key, value in env.items()]
    sc._init(args.instance, args.dll, 3600, 0, settings)
    pk = sc._PK
    candidates = json.loads(Path(args.pool).read_text(encoding="utf-8"))
    by_tag = {c["tag"]: c for c in candidates}
    baseline = json.loads(Path(args.baseline).read_text(encoding="utf-8"))
    jobs = json.loads(Path(args.jobs).read_text(encoding="utf-8"))[:args.max_jobs]
    for job in jobs:
        job["cached_left"] = by_tag[job["paired_completed_column"]]
        job["baseline_sheet"] = baseline_sheet_for_seed(job["placed"], baseline)
        job["source_baseline"] = str(Path(args.baseline).resolve())
        job["source_manifest"] = str(Path(args.jobs).resolve())
        job["cap"], job["gens"] = args.cap, args.gens
        expected_right = set(map(int, job["placed"])) | {int(job["incoming"])}
        cached = set(map(int, job["cached_left"]["placed"]))
        if cached & expected_right or cached | expected_right != set(job["expected_pair_union"]):
            raise ValueError("manifest pair sets do not match cached first sheet and seed-plus-incoming")
        if int(job["incoming"]) in set(map(int, job["placed"])):
            raise ValueError("incoming part already present in warm seed")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    save_json(out / "jobs.json", jobs)
    args.seed = jobs[0]["seed"] if jobs else 0
    reports = []

    def checkpoint():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
        if chosen is None:
            raise RuntimeError("union selection failed")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(out / "progress.json", {"uncovered": missing, "results": reports})
        import groupfill_nfp as gf
        path = gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        strict = validate_strict(pk.instance, json.loads(Path(path).read_text(encoding="utf-8")), max_sheets=args.k)
        save_json(out / "strict_metrics.json", strict)
        sc.log(f"hybrid cover: {len(sheets)} sheets, missing {missing}, pool {len(candidates)}")
        return missing

    checkpoint()
    with Pool(args.workers, initializer=sc._init, initargs=(args.instance, args.dll, 3600, 0, settings)) as pool:
        for result in pool.imap_unordered(run_hybrid, jobs):
            save_json(out / (result["warm"]["tag"] + ".json"), result)
            if result["accepted"]:
                candidates.append(result["warm"])
            reports.append({"tag": result["warm"]["tag"], "accepted": result["accepted"], "reason": result["reason"],
                            "rc": result["warm"]["rc"], "wall": result["warm"]["wall"]})
            sc.log(f"{result['warm']['tag']}: {result['reason']}, {result['warm']['wall']:.1f}s")
            if not checkpoint():
                pool.terminate()
                break
    from evaluate import evaluate_files
    evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)


if __name__ == "__main__":
    main()
