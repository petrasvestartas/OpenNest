"""Retain proven cores, transfer one plate, and repartition the remaining demand.

This produces hypothetical offers only. Cache failures are not geometric proofs;
this bounded heuristic searches cached feasible residual groups for efficiency.
"""
import argparse
import itertools
import json
import math
from pathlib import Path

from shapely.geometry import Polygon


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "plans", "cache", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--retain", type=int, default=2)
    ap.add_argument("--core-max", type=float, default=.90)
    ap.add_argument("--big-frac", type=float, default=.05)
    ap.add_argument("--seed", type=int, default=81000)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=16000)
    ap.add_argument("--starts", type=int, default=1)
    args = ap.parse_args()
    if min(args.retain, args.cap, args.gens, args.starts, args.big_frac) <= 0:
        ap.error("retention and packing budgets must be positive")
    if not 0 < args.core_max <= 1:
        ap.error("core-max must be within 0..1")
    inst, pool, proposal, cache = [json.loads(Path(x).read_text()) for x in
                                  (args.instance, args.pool, args.plans, args.cache)]
    sa = inst["solve_sheet"]["w"] * inst["solve_sheet"]["h"]
    area = {p["id"]: Polygon((p.get("solve") or p)["outer"],
                            (p.get("solve") or p).get("holes", [])).area / sa
            for p in inst["parts"]}
    actual = [set(map(int, c["placed"])) for c in pool]
    hard = set(proposal["hard_parts"])
    big = {i for i in hard if area[i] >= args.big_frac}
    longs = hard - big
    groups = {frozenset(int(i) for i in key.strip("()").split(",") if i.strip()): rec
              for key, rec in cache.items() if rec.get("feasible")}

    def support(core):
        return next((c["tag"] for c, ids in zip(pool, actual) if set(core) <= ids), None)

    choices = []
    for plan in proposal["plans"]:
        good = [job for job in plan if support(job["group"])]
        good.sort(key=lambda j: max(area[i] for i in j["group"]), reverse=True)
        if args.retain <= len(good) and args.retain < len(plan):
            fixed = good[:args.retain]
            choices.append((sum(max(area[i] for i in j["group"]) for j in fixed), fixed, len(plan)))
    if not choices:
        raise RuntimeError("no plan has enough supported cores to retain")
    _, fixed, k = max(choices, key=lambda item: item[0])
    used = set(i for job in fixed for i in job["group"])
    remaining = big - used
    residual_count = k - len(fixed)

    def partitions(rest, count):
        if count == 1:
            if rest in groups:
                yield [rest]
            return
        if not rest:
            return
        for group in groups:
            if min(rest) in group and group <= rest and len(rest - group) >= count - 1:
                for tail in partitions(rest - group, count - 1):
                    yield [group] + tail

    best, examined = None, 0
    for moved in sorted(remaining):
        for target, base in enumerate(fixed):
            augmented = set(base["group"]) | {moved}
            if sum(area[i] for i in augmented) > args.core_max:
                continue
            for partition in partitions(frozenset(remaining - {moved}), residual_count):
                left = sorted(longs - used)
                for slots in itertools.product(range(residual_count), repeat=len(left)):
                    cores = [set(group) for group in partition]
                    for pid, slot in zip(left, slots):
                        cores[slot].add(pid)
                    if any(sum(area[i] for i in core) > args.core_max or len(core & longs) > 3
                           for core in cores):
                        continue
                    examined += 1
                    supported = sum(support(core) is not None for core in cores)
                    affinity = sum(math.log1p(sum({a, b} <= ids for ids in actual))
                                   for core in cores for a, b in itertools.combinations(core, 2))
                    score = (supported, affinity, -max(sum(area[i] for i in core) for core in cores))
                    if best is None or score > best[0]:
                        best = score, moved, target, augmented, cores
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    if best is None:
        (out / "jobs.json").write_text("[]")
        (out / "plan.json").write_text(json.dumps({"status": "no cached residual partition found"}))
        return
    _, moved, target, augmented, cores = best
    full = [augmented if j == target else set(job["group"]) for j, job in enumerate(fixed)] + cores
    if set.union(*full) != hard or sum(map(len, full)) != len(hard):
        raise RuntimeError("proposal did not partition hard demand exactly once")
    plan, jobs = [], []
    for index, core in enumerate(full):
        known = support(core)
        job = {"group": sorted(core), "fillers": [], "incoming": sorted(core & longs),
               "plate_group": sorted(core & big), "core_area": sum(area[i] for i in core),
               "tag": f"constrained_transfer_{index}", "seed": args.seed + index,
               "gens": args.gens, "starts": args.starts, "cap": args.cap, "supported_by": known}
        plan.append(job)
        if known is None:
            jobs.append(job)
    (out / "jobs.json").write_text(json.dumps(jobs, indent=2))
    (out / "plan.json").write_text(json.dumps({"moved_plate": moved, "target_core": target,
        "fixed_provenance": fixed, "plan": plan, "alternatives_examined": examined,
        "status": "hypothetical until unsupported jobs are packed and validated"}, indent=2))
    print(json.dumps({"jobs": len(jobs), "alternatives": examined}))


if __name__ == "__main__":
    main()
