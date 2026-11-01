"""Run a finite pack_repair job manifest with strict geometry checkpoints."""
import argparse
import hashlib
import json
from multiprocessing import Pool
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_repair import pack_repair, save_json, validate_candidate


def initialize(instance, dll, rotations, tolerance, env, native_seconds):
    sc._init(instance, dll, rotations, tolerance, env)
    if native_seconds > 0:
        sc._PK.base.update(iter_mode=0, time_budget_secs=native_seconds)


def pack_manifest(job):
    candidate = pack_repair(job)
    if job.get("salvage_partial") and candidate.get("placed"):
        candidate["requested_group"] = list(job["group"])
        candidate["unmet_core"] = sorted(set(job["group"]) - set(map(int, candidate["placed"])))
        candidate["partial_proposal"] = bool(candidate["unmet_core"])
        candidate["generation_plates_ok"] = candidate["plates_ok"]
        candidate["group"] = sorted(set(job["group"]) & set(map(int, candidate["placed"])))
        candidate["plates_ok"] = True
        from sheetcover_polish import polish_candidate
        polished = polish_candidate(sc._PK, candidate)
        if polished is not None:
            candidate = polished
        candidate["validated"], candidate["validation_reason"] = validate_candidate(
            sc._PK, candidate, tolerance=1e-8)
    return candidate


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "dll", "pool", "jobs", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--native-workers", type=int, default=12)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--seed", type=int, default=61000)
    ap.add_argument("--native-seconds", type=float, default=0)
    ap.add_argument("--core-only", action="store_true")
    ap.add_argument("--salvage-partials", action="store_true")
    args = ap.parse_args()
    env = ["NP_SMART_ROT=1", "NP_EXPL_STRIKES=2", "NP_EXPL_NOIMPR=50",
           "NP_SIMPL_MAXDEV=0.5", "NP_PRIO_MAX_RETRIES=1",
           f"NP_WORKERS={args.native_workers}"]
    initargs = (args.instance, args.dll, 3600, 0, env, args.native_seconds)
    initialize(*initargs)
    pk = sc._PK
    out = Path(args.out)
    (out / "raw_candidates").mkdir(parents=True, exist_ok=True)
    source = json.loads(Path(args.pool).read_text())
    candidates = [c for c in source if validate_candidate(pk, c, tolerance=1e-8)[0]]
    signatures = {tuple(sorted(map(int, c["placed"]))) for c in candidates}
    jobs = json.loads(Path(args.jobs).read_text())
    if args.core_only:
        jobs = [dict(job, fillers=[]) for job in jobs]
    if args.salvage_partials:
        jobs = [dict(job, salvage_partial=True) for job in jobs]
    save_json(out / "executed_jobs.json", jobs)
    results = []

    def checkpoint():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
        if chosen is None:
            raise RuntimeError("Coverage MILP did not return an incumbent")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(out / "progress.json", {
            "instance_sha256": hashlib.sha256(Path(args.instance).read_bytes()).hexdigest(),
            "uncovered": missing, "chosen": [candidates[j]["tag"] for j in chosen],
            "results": results, "completed": len(results), "total_jobs": len(jobs)})
        import groupfill_nfp as gf
        gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        from validate_strict import validate_strict
        save_json(out / "strict_metrics.json", validate_strict(
            pk.instance, json.loads((out / "placements.json").read_text()), max_sheets=args.k))
        sc.log(f"cover {len(sheets)} sheets, missing {missing}; pool {len(candidates)}")
        return missing

    checkpoint()
    with Pool(args.workers, initializer=initialize, initargs=initargs) as workers:
        for candidate in workers.imap_unordered(pack_manifest, jobs):
            filename = candidate["tag"].replace(":", "_").replace("/", "_")
            save_json(out / "raw_candidates" / (filename + ".json"), candidate)
            signature = tuple(sorted(map(int, candidate["placed"])))
            accepted = candidate["validated"] and signature not in signatures
            if accepted:
                candidates.append(candidate)
                signatures.add(signature)
            results.append({"tag": candidate["tag"], "accepted": accepted,
                            "validated": candidate["validated"], "wall": candidate["wall"],
                            "missing_mandatory": candidate["missing_mandatory"],
                            "validation_reason": candidate["validation_reason"]})
            sc.log(results[-1])
            if not checkpoint():
                workers.terminate()
                break


if __name__ == "__main__":
    main()
