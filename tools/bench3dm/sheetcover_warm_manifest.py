"""Run bounded warm jobs whose explicit alternative sheet partition must survive."""
import argparse
import json
from multiprocessing import Pool
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_repair import save_json, validate_candidate
from sheetcover_warm import run_warm


def validate_partition(pk, job, result=None):
    sheets = [{"placed": {int(p): list(v) for p, v in s["placed"].items()}}
              for s in job["incumbent_partition"]]
    source = job["source_sheet"]
    if not 0 <= source < len(sheets):
        return None, "invalid source sheet"
    expected_seed = {int(p): list(v) for p, v in job["placed"].items()}
    if sheets[source]["placed"] != expected_seed:
        return None, "warm seed differs from alternative partition"
    expected = set(job["preserved_parts"])
    if not set(job["required_parts"]) <= expected:
        return None, "required part absent from preserved partition"
    if job["incoming"] in expected:
        return None, "incoming already present"
    if result is not None:
        if not result.get("validated") or result.get("rc") != 0:
            return None, "warm repair not validated complete success"
        placed = {int(p): list(v) for p, v in result["placed"].items()}
        if set(placed) != set(expected_seed) | {job["incoming"]}:
            return None, "warm result changes incumbent membership"
        sheets[source]["placed"] = placed
        expected.add(job["incoming"])
    flat = [p for s in sheets for p in s["placed"]]
    if len(flat) != len(set(flat)) or set(flat) != expected:
        return None, "partition duplicates or loses original parts"
    for index, sheet in enumerate(sheets):
        candidate = dict(sheet, group=list(sheet["placed"]), plates_ok=True)
        valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        if not valid:
            return None, f"sheet {index}: {why}"
    return sheets, "valid complete preserved partition"


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "jobs", "dll", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--workers", type=int, default=3)
    ap.add_argument("--native-workers", type=int, default=12)
    ap.add_argument("--max-jobs", type=int, default=6)
    ap.add_argument("--cap", type=float, default=90)
    ap.add_argument("--gens", type=int, default=4000)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--seed", type=int, default=85000)
    args = ap.parse_args()
    if min(args.workers, args.native_workers, args.max_jobs, args.cap, args.gens, args.k) <= 0:
        ap.error("positive worker, job, sheet and packing budgets required")
    env = ["NP_SMART_ROT=1", "NP_EXPL_STRIKES=2", "NP_EXPL_NOIMPR=50", "NP_SIMPL_MAXDEV=0.5",
           f"NP_WARM_WORKERS={args.native_workers}"]
    initargs = (args.instance, args.dll, 3600, 0, env)
    sc._init(*initargs)
    pk = sc._PK
    if any(int(p.get("qty", 1)) != 1 for p in pk.parts.values()):
        ap.error("candidate pools require quantity one")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "raw_candidates").mkdir(exist_ok=True)
    candidates = [c for c in json.loads(Path(args.pool).read_text())
                  if validate_candidate(pk, c, tolerance=1e-8)[0]]
    signatures = {json.dumps(c["placed"], sort_keys=True) for c in candidates}
    jobs = json.loads(Path(args.jobs).read_text())[:args.max_jobs]
    if not jobs:
        ap.error("manifest has no jobs")
    required = sorted(set(jobs[0]["required_parts"]))
    for job in jobs:
        if sorted(set(job["required_parts"])) != required:
            raise ValueError("inconsistent required-parts constraints")
        if len(job["incumbent_partition"]) > args.k:
            raise ValueError("alternative partition exceeds sheet limit")
        valid, why = validate_partition(pk, job)
        if valid is None:
            raise ValueError(f"invalid job {job['tag']}: {why}")
        job.update(cap=args.cap, gens=args.gens)
    save_json(out / "executed_jobs.json", jobs)
    by_tag = {job["tag"]: job for job in jobs}
    if len(by_tag) != len(jobs):
        raise ValueError("job tags must be unique")
    results = []

    def checkpoint():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk,
                                         required_ids=required, objective="count")
        if chosen is None:
            raise RuntimeError("no feasible required-part cover")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(out / "progress.json", {"required_parts": required, "coverage_objective": "count",
            "uncovered": missing, "chosen": [candidates[j]["tag"] for j in chosen],
            "results": results, "completed": len(results), "total_jobs": len(jobs)})
        import groupfill_nfp as gf
        gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        from validate_strict import validate_strict
        metrics = validate_strict(pk.instance, json.loads((out / "placements.json").read_text()), max_sheets=args.k)
        save_json(out / "strict_metrics.json", metrics)
        sc.log(f"partition warm cover: {len(pk.parts)-len(missing)}/{len(pk.parts)}, missing {missing}")
        return metrics["passed_sampled_polygons"]

    checkpoint()
    with Pool(args.workers, initializer=sc._init, initargs=initargs) as workers:
        for candidate in workers.imap_unordered(run_warm, jobs):
            job = by_tag[candidate["tag"]]
            filename = candidate["tag"].replace(":", "_").replace("/", "_").replace("\\", "_")
            save_json(out / "raw_candidates" / (filename + ".json"), candidate)
            partition, why = validate_partition(pk, job, candidate)
            if partition is not None:
                # Preserve every modified sheet, including the supported destination.
                save_json(out / (filename + "_partition.json"), partition)
                for index, sheet in enumerate(partition):
                    column = dict(sheet, placed={str(p): v for p, v in sheet["placed"].items()},
                        tag=f"{candidate['tag']}:partition:{index}", group=sorted(sheet["placed"]),
                        plates_ok=True, validated=True,
                        area=sum(pk.solve_area[p] for p in sheet["placed"]) / pk.SA)
                    signature = json.dumps(column["placed"], sort_keys=True)
                    if signature not in signatures:
                        signatures.add(signature)
                        candidates.append(column)
            results.append({"tag": candidate["tag"], "rc": candidate["rc"], "wall": candidate["wall"],
                "accepted": partition is not None, "reason": why, "quality": candidate.get("quality")})
            sc.log(results[-1])
            if checkpoint():
                workers.terminate()
                break
    from evaluate import evaluate_files
    evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)


if __name__ == "__main__":
    main()
