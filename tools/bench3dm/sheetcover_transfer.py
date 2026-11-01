"""Joint-pack an uncovered elongated part with a similar part transferred from another sheet.

The donor becomes a validated subset without repacking. Recipient plates and both
elongated parts are mandatory; optional small parts may be displaced and recovered
through global union selection. No instance-specific part IDs or grouping rules.
"""
import argparse
import json
import math
import sys
from multiprocessing import Pool
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_repair import pack_repair, save_json, offer_key
from sheetcover_exchange import valid_column
from validate_strict import validate_strict


def init_transfer(instance, dll, rotations, curve_tol, env, native_seconds=None):
    sc._init(instance, dll, rotations, curve_tol, env)
    if native_seconds is not None:
        sc._PK.base.update(iter_mode=0, time_budget_secs=float(native_seconds))


def shape_descriptor(polygon):
    corners = list(polygon.minimum_rotated_rectangle.exterior.coords)
    short, long = sorted(math.dist(corners[i], corners[i + 1]) for i in (0, 1))
    return long, short, polygon.area


def schedule_transfers(pk, sheets, missing, *, limit=10, long_frac=.33, min_aspect=3,
                       big_frac=.05, gens=8000, starts=1, cap=180, seed=100):
    descriptors = {p: shape_descriptor(pk.solve_poly[p]) for p in pk.parts}
    threshold = long_frac * max(pk.W, pk.H)
    def elongated(p):
        length, width, _ = descriptors[p]
        return length >= threshold and length / max(width, 1e-12) >= min_aspect
    proposals, seen = {}, set()
    for incoming in sorted(set(missing), key=lambda p: (-pk.solve_area[p], p)):
        if not elongated(incoming):
            continue
        il, iw, ia = descriptors[incoming]
        options = []
        for donor_index, donor in enumerate(sheets):
            for transferred in donor["placed"]:
                if not elongated(transferred):
                    continue
                length, width, area = descriptors[transferred]
                similarity = abs(math.log(length / il)) + abs(math.log(width / iw)) + abs(math.log(area / ia))
                for recipient_index, recipient in enumerate(sheets):
                    if recipient_index == donor_index:
                        continue
                    current = set(recipient["placed"])
                    mandatory = {incoming, transferred} | {p for p in current if pk.solve_area[p] >= big_frac * pk.SA}
                    if sum(pk.solve_area[p] for p in mandatory) > pk.SA:
                        continue
                    ids = current | {incoming, transferred}
                    key = offer_key(ids, mandatory)
                    if key in seen:
                        continue
                    seen.add(key)
                    donor_subset = {str(p): v for p, v in donor["placed"].items() if p != transferred}
                    options.append({"group": sorted(mandatory), "fillers": sorted(ids - mandatory),
                                    "incoming": [incoming], "transferred": transferred,
                                    "source_sheets": [donor_index, recipient_index], "similarity": similarity,
                                    "donor_subset": donor_subset, "original_recipient": sorted(current),
                                    "rank": [similarity, sum(pk.solve_area[p] for p in mandatory) / pk.SA,
                                             sum(pk.solve_area[p] for p in ids) / pk.SA]})
        options.sort(key=lambda job: (job["rank"], job["transferred"], job["source_sheets"]))
        proposals[incoming] = options
    jobs = []
    for index in range(max((len(v) for v in proposals.values()), default=0)):
        for incoming in proposals:
            if index >= len(proposals[incoming]):
                continue
            job = dict(proposals[incoming][index])
            job.update(seed=seed + len(jobs), gens=gens, starts=starts, cap=cap, tag=f"transfer_s{seed}_j{len(jobs)}")
            jobs.append(job)
            if len(jobs) >= limit:
                return jobs
    return jobs


def run_transfer(job):
    result = pack_repair(job)
    result["transfer_source_sheets"] = job["source_sheets"]
    result["transferred"] = job["transferred"]
    if not result["validated"]:
        return {"accepted": False, "recipient": result, "reason": result["validation_reason"]}
    recipient = valid_column(sc._PK, result)
    if recipient is None:
        return {"accepted": False, "recipient": result, "reason": "recipient source geometry gate"}
    donor = {"tag": job["tag"] + "_donor", "placed": job["donor_subset"], "group": sorted(map(int, job["donor_subset"])),
             "plates_ok": True, "transferred_out": job["transferred"],
             "area": sum(sc._PK.solve_area[int(p)] for p in job["donor_subset"]) / sc._PK.SA}
    donor = valid_column(sc._PK, donor) if donor["placed"] else donor
    if donor is None:
        return {"accepted": False, "recipient": result, "reason": "donor source geometry gate"}
    recipient["displaced_optional"] = sorted(set(job["original_recipient"]) - set(map(int, recipient["placed"])))
    return {"accepted": True, "recipient": recipient, "donor": donor, "reason": "validated joint long-part transfer"}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for arg in ("instance", "pool", "dll", "out"):
        ap.add_argument("--" + arg, required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--max-jobs", type=int, default=10)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=8000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--native-seconds", type=float, help="optional native time-mode budget inside the outer cap; default keeps iteration mode")
    ap.add_argument("--replay-jobs", help="replay a saved generic job manifest, retaining offered sets, seeds and donor transforms")
    ap.add_argument("--big-frac", type=float, default=.05)
    ap.add_argument("--long-frac", type=float, default=.33)
    ap.add_argument("--min-aspect", type=float, default=3)
    ap.add_argument("--prepare-only", action="store_true")
    ap.add_argument("--env", action="append", default=[])
    args = ap.parse_args()
    if min(args.k, args.workers, args.max_jobs, args.cap, args.gens, args.starts) <= 0:
        ap.error("sheet/job/worker budgets must be positive")
    if args.native_seconds is not None and not (0 < args.native_seconds < args.cap):
        ap.error("native-seconds must be positive and less than the outer cap")
    env = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5", "NP_PRIO_MAX_RETRIES": "1"}
    env.update(kv.split("=", 1) for kv in args.env)
    settings = [f"{k}={v}" for k, v in env.items()]
    init_transfer(args.instance, args.dll, 3600, 0, settings, args.native_seconds)
    pk = sc._PK
    candidates = json.loads(Path(args.pool).read_text(encoding="utf-8"))
    chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
    if chosen is None:
        raise RuntimeError("union selection failed")
    sheets = sc.assign_candidates(candidates, chosen)
    if args.replay_jobs:
        jobs = json.loads(Path(args.replay_jobs).read_text(encoding="utf-8"))[:args.max_jobs]
        for job in jobs:
            offered = set(job["group"]) | set(job["fillers"])
            if not offered <= set(pk.parts):
                raise ValueError("replay manifest contains unknown part IDs")
            job.update(gens=args.gens, starts=args.starts, cap=args.cap,
                       source_job_manifest=str(Path(args.replay_jobs).resolve()))
    else:
        jobs = schedule_transfers(pk, sheets, missing, limit=args.max_jobs, long_frac=args.long_frac, min_aspect=args.min_aspect,
                                  big_frac=args.big_frac, gens=args.gens, starts=args.starts, cap=args.cap, seed=args.seed)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    save_json(out / "jobs.json", jobs)
    sc.log(f"prepared {len(jobs)} transfer jobs; missing {missing}")
    if args.prepare_only:
        return
    results = []
    with Pool(args.workers, initializer=init_transfer, initargs=(args.instance, args.dll, 3600, 0, settings, args.native_seconds)) as pool:
        for result in pool.imap_unordered(run_transfer, jobs):
            save_json(out / (result["recipient"]["tag"] + ".json"), result)
            if result["accepted"]:
                candidates.append(result["recipient"])
                if result["donor"]["placed"]:
                    candidates.append(result["donor"])
            results.append({"tag": result["recipient"]["tag"], "accepted": result["accepted"], "reason": result["reason"]})
            chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
            if chosen is None:
                raise RuntimeError("union selection failed")
            sheets = sc.assign_candidates(candidates, chosen)
            save_json(out / "candidates.json", candidates)
            save_json(out / "progress.json", {"uncovered": missing, "results": results})
            import groupfill_nfp as gf
            path = gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
            strict = validate_strict(pk.instance, json.loads(Path(path).read_text(encoding="utf-8")), max_sheets=args.k)
            save_json(out / "strict_metrics.json", strict)
            sc.log(f"{result['recipient']['tag']}: {result['reason']}; missing {missing}, pool {len(candidates)}")
            if not missing and strict["passed_sampled_polygons"]:
                pool.terminate()
                break


if __name__ == "__main__":
    main()
