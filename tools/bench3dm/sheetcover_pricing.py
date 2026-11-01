"""Generate finite joint-pack offers by fractional-cover column pricing.

Only proposes layouts: cached plate feasibility does not establish feasibility
after long parts are added. Outputs jobs for sheetcover_repair.pack_repair.
"""
import argparse
import hashlib
import itertools
import json
from pathlib import Path

import numpy as np
from scipy.optimize import linprog, milp, Bounds, LinearConstraint
from shapely.geometry import Polygon


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "cache", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--jobs", type=int, default=10)
    ap.add_argument("--big-frac", type=float, default=.05)
    ap.add_argument("--long-frac", type=float, default=.33)
    ap.add_argument("--core-max", type=float, default=.90)
    ap.add_argument("--target", type=float, default=.86)
    ap.add_argument("--cap", type=float, default=90)
    ap.add_argument("--gens", type=int, default=4000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--seed", type=int, default=61000)
    ap.add_argument("--stop-lp", type=float, default=0,
                    help="stop hypothetical pricing when this fractional sheet bound is reached")
    ap.add_argument("--integer-k", type=int, default=0,
                    help="also require an integer hard-part cover before stopping")
    ap.add_argument("--exclude-jobs", action="append", default=[],
                    help="skip exact cores already attempted at this budget; not geometric infeasibility")
    args = ap.parse_args()
    if min(args.jobs, args.cap, args.gens, args.starts, args.big_frac, args.long_frac) <= 0:
        ap.error("job, packing, and classification budgets must be positive")
    if not .62 <= args.core_max <= 1 or not 0 < args.target <= 1:
        ap.error("core-max must be within .62..1 and target within 0..1")
    if args.integer_k < 0 or args.stop_lp < 0:
        ap.error("stopping bounds must be nonnegative")
    inst, pool, cache = [json.loads(Path(x).read_text()) for x in
                         (args.instance, args.pool, args.cache)]
    w, h = [inst["solve_sheet"][k] for k in ("w", "h")]
    shapes = {p["id"]: Polygon((p.get("solve") or p)["outer"],
                               (p.get("solve") or p).get("holes", []))
              for p in inst["parts"]}
    area = {i: p.area / (w * h) for i, p in shapes.items()}
    big = {i for i in shapes if area[i] >= args.big_frac}
    long = {i for i, p in shapes.items() if
            max(p.bounds[2] - p.bounds[0], p.bounds[3] - p.bounds[1])
            >= args.long_frac * max(w, h)}
    hard = sorted(big | long)
    sets = [set(map(int, c["placed"])) for c in pool]
    freq = {i: sum(i in s for s in sets) for i in shapes}
    options, seen = [], set()
    attempted = {frozenset(job["group"]) for path in args.exclude_jobs
                 for job in json.loads(Path(path).read_text())}
    for key, rec in cache.items():
        if not rec.get("feasible"):
            continue
        group = tuple(int(x) for x in key.strip("()").split(",") if x.strip())
        if not 2 <= len(group) <= 5 or not set(group) <= big:
            continue
        for n in range(1, 4):
            for extra in itertools.combinations(sorted(long - big), n):
                core = frozenset(group + extra)
                ar = sum(area[i] for i in core)
                if not .62 <= ar <= args.core_max or core in seen or core in attempted:
                    continue
                if any(core <= existing for existing in sets):
                    continue
                seen.add(core)
                options.append((group, extra, core, ar))
    proposals, used, certificate, integer_plan = [], set(), None, None
    for rank in range(args.jobs):
        matrix = np.array([[i in s for s in sets] for i in hard], dtype=float)
        result = linprog(np.ones(len(sets)), A_ub=-matrix,
                         b_ub=-np.ones(len(hard)), bounds=(0, None), method="highs")
        if not result.success:
            raise RuntimeError(result.message)
        prices = dict(zip(hard, -result.ineqlin.marginals))
        if certificate is None:
            certificate = {"hard_parts": hard, "fractional_min_sheets": result.fun,
                           "dual_prices": prices,
                           "max_existing_column_price": max(
                               (sum(prices.get(i, 0) for i in s) for s in sets), default=0),
                           "dual_sum": sum(prices.values())}
        if args.stop_lp > 0 and result.fun <= args.stop_lp + 1e-9:
            if not args.integer_k:
                break
            constraints = np.vstack((matrix, np.ones(len(sets))))
            integer = milp(np.array([0.] * len(pool) + [1.] * len(proposals)),
                           integrality=np.ones(len(sets)), bounds=Bounds(0, 1),
                           constraints=LinearConstraint(constraints,
                               [1.] * len(hard) + [0.],
                               [np.inf] * len(hard) + [args.integer_k]),
                           options={"time_limit": 10})
            if integer.x is not None:
                integer_plan = [int(i) for i in np.flatnonzero(integer.x > .5)]
                break
        choices = [o for o in options if o[0] not in used]
        if not choices:
            break
        group, extra, core, ar = max(choices, key=lambda o:
            (sum(prices[i] for i in o[2]) - 1, -o[3], -sum(freq[i] for i in o[2])))
        used.add(group)
        sets.append(core)
        fillers, total = [], ar
        for i in sorted(set(shapes) - big - long, key=lambda i: (freq[i], -area[i])):
            if total + area[i] <= args.target:
                fillers.append(i)
                total += area[i]
        proposals.append({"group": sorted(core), "fillers": fillers,
                          "incoming": list(extra), "plate_group": list(group),
                          "seed": args.seed + rank, "gens": args.gens,
                          "starts": args.starts, "cap": args.cap,
                          "tag": f"priced_core:{rank}", "core_area": ar,
                          "offered_area": total, "lp_before": result.fun,
                          "reduced_cost_gain": sum(prices[i] for i in core) - 1})
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "jobs.json").write_text(json.dumps(proposals, indent=2))
    (out / "pricing-certificate.json").write_text(json.dumps({
        "pool": args.pool, "pool_sha256": hashlib.sha256(Path(args.pool).read_bytes()).hexdigest(),
        "original": certificate, "proposal_count": len(proposals),
        "hypothetical_integer_plan_columns": integer_plan,
        "warning": "Later pricing assumes hypothetical cores feasible; validate packing before pool insertion."
    }, indent=2))
    print(json.dumps({"jobs": len(proposals), "fractional_min_sheets":
                      certificate["fractional_min_sheets"] if certificate else None}))


if __name__ == "__main__":
    main()
