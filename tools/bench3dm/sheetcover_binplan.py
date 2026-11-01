"""Propose coordinated hard-part bins without treating cache failures as proofs."""
import argparse
import itertools
import json
from pathlib import Path

import numpy as np
from scipy.optimize import Bounds, LinearConstraint, milp
from shapely.geometry import Polygon


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "cache", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--plans", type=int, default=2)
    ap.add_argument("--seed", type=int, default=74000)
    ap.add_argument("--core-max", type=float, default=.90)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=16000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--exclude-jobs", action="append", default=[])
    args = ap.parse_args()
    if min(args.k, args.plans, args.cap, args.gens, args.starts) <= 0:
        ap.error("sheet, plan, and packing budgets must be positive")
    if not .62 <= args.core_max <= 1:
        ap.error("core-max must be within .62..1")
    inst, pool, cache = [json.loads(Path(x).read_text()) for x in
                         (args.instance, args.pool, args.cache)]
    w, h = (inst["solve_sheet"][i] for i in ("w", "h"))
    shapes = {p["id"]: Polygon((p.get("solve") or p)["outer"],
                              (p.get("solve") or p).get("holes", [])) for p in inst["parts"]}
    area = {i: p.area / (w * h) for i, p in shapes.items()}
    big = {i for i in shapes if area[i] >= .05}
    longs = {i for i, p in shapes.items() if
             max(p.bounds[2] - p.bounds[0], p.bounds[3] - p.bounds[1]) >= .33 * max(w, h)} - big
    hard = sorted(big | longs)
    anchors = sorted(big, key=lambda i: -area[i])[:args.k]
    if len(anchors) != args.k:
        raise ValueError("not enough large parts for anchor-bin proposal heuristic")
    actual = [set(map(int, c["placed"])) for c in pool]
    n = len(hard) * args.k
    rows, lo, hi = [], [], []

    def constraint(indices, values, lower, upper):
        row = np.zeros(n)
        row[indices] = values
        rows.append(row)
        lo.append(lower)
        hi.append(upper)

    for j, pid in enumerate(hard):
        constraint([j * args.k + s for s in range(args.k)], 1, 1, 1)
    for s in range(args.k):
        constraint([j * args.k + s for j in range(len(hard))],
                   [area[i] for i in hard], .62, args.core_max)
        constraint([j * args.k + s for j, i in enumerate(hard) if i in big], 1, 2, 5)
        constraint([j * args.k + s for j, i in enumerate(hard) if i in longs], 1, 0, 3)
        constraint([hard.index(anchors[s]) * args.k + s], 1, 1, 1)
    attempted = {frozenset(job["group"]) for path in args.exclude_jobs
                 for job in json.loads(Path(path).read_text())}
    for core in attempted:
        for s in range(args.k):
            constraint([j * args.k + s for j in range(len(hard))],
                       [1 if pid in core else -1 for pid in hard],
                       -np.inf, len(core) - 1)
    plans, jobs = [], []
    for iteration in range(args.plans):
        rng = np.random.default_rng(args.seed + iteration)
        costs = np.zeros(n)
        for j, pid in enumerate(hard):
            for s, anchor in enumerate(anchors):
                pair = {pid, anchor}
                cooccur = sum(pair <= ids for ids in actual)
                rec = cache.get(",".join(map(str, sorted(pair))))
                penalty = 0 if rec and rec.get("feasible") else .25 if rec is None else 1
                # Failures remain soft evidence; every non-anchor assignment is allowed.
                costs[j * args.k + s] = penalty - .1 * np.log1p(cooccur) + rng.uniform(0, .3)
        result = milp(costs, integrality=np.ones(n), bounds=Bounds(0, 1),
                      constraints=LinearConstraint(np.array(rows), lo, hi),
                      options={"time_limit": 20, "mip_rel_gap": .05})
        if result.x is None:
            break
        selected = list(map(int, np.flatnonzero(result.x > .5)))
        plan = []
        for s in range(args.k):
            core = [pid for j, pid in enumerate(hard) if j * args.k + s in selected]
            plates = sorted(set(core) & big)
            rec = cache.get(",".join(map(str, plates)))
            job = {"group": core, "fillers": [], "incoming": sorted(set(core) & longs),
                   "plate_group": plates, "core_area": sum(area[i] for i in core),
                   "cached_plate_group": rec.get("feasible") if rec else None,
                   "already_supported": any(set(core) <= ids for ids in actual),
                   "seed": args.seed + len(jobs), "gens": args.gens, "starts": args.starts,
                   "cap": args.cap, "tag": f"coordinated_{iteration}_{s}"}
            plan.append(job)
            jobs.append(job)
        plans.append(plan)
        constraint(selected, 1, 0, len(hard) - 1)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "jobs.json").write_text(json.dumps(jobs, indent=2))
    (out / "plans.json").write_text(json.dumps({"hard_parts": hard, "anchors": anchors,
        "limitation": "Area-compatible coordinated proposals only; anchor separation is a heuristic, not a proof.",
        "plans": plans}, indent=2))
    print(json.dumps({"plans": len(plans), "jobs": len(jobs)}))


if __name__ == "__main__":
    main()
