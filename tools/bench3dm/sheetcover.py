"""Stage 2 of the constructive-covering strategy: candidate SHEETS + exact cover.

Candidate sheet = one plate group (from the feasibility cache) + a knapsack set of fillers (long parts first, then by
area, to an area target), packed as ONE joint pack with the plates mandatory (engine E1 `np_nest_prio`). Whatever the
engine keeps is a feasible sheet and becomes a candidate (part set, area, layout). Candidates are generated in
parallel processes. Then a union-cover ILP picks at most K sheets maximising covered part area; shared parts are
assigned once by removing duplicate copies (leftovers go to an extra sheet, compacted).

Usage (from the repo root):
  python -I tools/bench3dm/sheetcover.py --instance ... --cache <group_cache.json> --dll <nest_physics.dll>
      --out <dir> --k 5 --top-groups 40 --targets 0.86,0.90,0.94 --seeds 100 --workers 12 --gens 8000 --starts 2
      --env NP_SMART_ROT=1 --env NP_EXPL_STRIKES=2 --env NP_EXPL_NOIMPR=50 --env NP_SIMPL_MAXDEV=0.5 --env NP_PRIO_MAX_RETRIES=4
  add --cover-only to re-run the cover on an existing candidates.json
"""
import argparse, json, math, os, random, sys, time
from pathlib import Path
from multiprocessing import Pool

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))


def log(*a):
    print(time.strftime("[%H:%M:%S]"), *a, flush=True)


# ----------------------------------------------------------------------------------------------------- workers
_PK = None
_ARGS = None


def _init(instance_path, dll, rotations, curve_tol, env):
    global _PK, _ARGS
    for kv in env:
        k, v = kv.split("=", 1)
        os.environ[k] = v
    import groupfill_nfp as gf
    import engines as eng
    gf.ENGINE = eng.ENGINE_NP
    inst = json.load(open(instance_path, encoding="utf-8"))
    _PK = gf.PackerNp(inst, "gh_np", dll, rotations, curve_tol)


def _pack_candidate(job):
    """job: dict(group, fillers, seed, gens, starts, cap, tag). Returns a candidate sheet (placed set + layout)."""
    pk = _PK
    ids = list(job["group"]) + list(job["fillers"])
    t = time.time()
    placed, res = pk.pack(ids, job["gens"], job["starts"], job["seed"], job["cap"], mandatory=set(job["group"]))
    ok = all(i in placed for i in job["group"])
    area = sum(pk.solve_area[i] for i in placed) / pk.SA
    candidate = {"tag": job["tag"], "group": list(job["group"]), "offered": ids, "placed": {str(k): v for k, v in placed.items()},
                 "plates_ok": ok, "area": area, "wall": time.time() - t, "seed": job["seed"]}
    candidate = validate_sheet_candidate(pk, candidate)
    return candidate


def validate_sheet_candidate(pk, candidate):
    """Use the same solve/source geometry gate for generated and imported columns."""
    from sheetcover_repair import validate_candidate
    valid, reason = validate_candidate(pk, candidate, tolerance=1e-8)
    if not valid and candidate.get("plates_ok") and candidate.get("placed"):
        from sheetcover_polish import polish_candidate
        polished = polish_candidate(pk, candidate)
        if polished is not None:
            candidate = polished
            valid, reason = validate_candidate(pk, candidate, tolerance=1e-8)
    candidate["validated"] = valid
    candidate["validation_reason"] = reason
    return candidate


# ----------------------------------------------------------------------------------------------------- driver
def long_dim(poly):
    b = poly.bounds
    return max(b[2] - b[0], b[3] - b[1])


def select_groups(groups, plates, per_plate, limit):
    """Balance plate coverage before spending a capped budget on extra diversity."""
    plates = sorted(set(plates))
    available = {p: sum(p in g for _, g in groups) for p in plates}
    counts = dict.fromkeys(plates, 0)
    remaining = list(groups)
    selected = []
    while remaining and (limit <= 0 or len(selected) < limit):
        needing = [p for p in plates if counts[p] < per_plate and any(p in g for _, g in remaining)]
        if not needing:
            break
        # Prioritise the least-covered plate, breaking ties by scarcity.
        target = min(needing, key=lambda p: (counts[p], available[p], p))
        options = [(f, g) for f, g in remaining if target in g]
        free, group = max(options, key=lambda item: (
            sum(1 / (counts[p] + 1) for p in item[1] if counts[p] < per_plate), item[0], item[1]))
        selected.append(group)
        remaining = [(f, g) for f, g in remaining if g != group]
        for p in group:
            counts[p] += 1
    return selected, counts


def build_jobs(pk, groups, fillers, args):
    """For each plate group: filler sets by knapsack (long parts first, then by area) to each target, plus
    randomised variants per extra seed. Long parts are offered to every group (the cover decides)."""
    thr = args.long_frac * max(pk.W, pk.H)
    longs = sorted([i for i in fillers if long_dim(pk.solve_poly[i]) >= thr], key=lambda i: -pk.solve_area[i])
    smalls = sorted([i for i in fillers if i not in longs], key=lambda i: -pk.solve_area[i])
    jobs = []
    for gi, g in enumerate(groups):
        g_area = sum(pk.solve_area[i] for i in g)
        for T in args.targets:
            for s_i, seed in enumerate(args.seeds):
                order_long = list(longs)
                order_small = list(smalls)
                if s_i > 0:  # diversify: shuffle within the two classes
                    rnd = random.Random(seed * 7919 + gi * 131 + int(T * 1000))
                    rnd.shuffle(order_long)
                    rnd.shuffle(order_small)
                sel, acc = [], g_area
                for i in order_long + order_small:
                    if acc + pk.solve_area[i] <= T * pk.SA and len(sel) < args.max_fillers:
                        sel.append(i)
                        acc += pk.solve_area[i]
                jobs.append({"group": list(g), "fillers": sel, "seed": seed, "gens": args.gens, "starts": args.starts,
                             "cap": args.cap, "tag": f"g{gi}_T{T:.2f}_s{seed}"})
    # dedupe identical offers
    seen, out = set(), []
    for j in jobs:
        key = (tuple(j["group"]), tuple(sorted(j["fillers"])), j["seed"])
        if key not in seen:
            seen.add(key)
            out.append(j)
    return out


def exact_cover(cands, all_parts, k, pk, required_ids=None, objective="area"):
    """Choose <= k sheets maximizing union area, or count then area when requested.

    Candidate layouts may share parts: removing duplicate copies preserves feasibility.
    Call assign_candidates before exporting the selected layouts. Optional required_ids
    must all be covered; an infeasible required cover returns (None, parts).
    """
    import numpy as np
    from scipy.optimize import milp, LinearConstraint, Bounds
    if k < 0:
        raise ValueError("sheet limit must be nonnegative")
    if objective not in ("area", "count"):
        raise ValueError("coverage objective must be area or count")
    n = len(cands)
    parts = sorted(set(all_parts))
    required = set(required_ids or ())
    if required - set(parts):
        raise ValueError(f"required contains unknown part IDs: {sorted(required - set(parts))}")
    pidx = {p: r for r, p in enumerate(parts)}
    sets = [{int(p) for p in c["placed"]} for c in cands]
    unknown = set().union(*sets) - set(parts) if sets else set()
    if unknown:
        raise ValueError(f"candidate contains unknown part IDs: {sorted(unknown)}")
    if not parts or not cands or k == 0:
        return (None if required else []), parts
    m = len(parts)
    # x selects sheets; y counts each covered part once, irrespective of duplicates.
    A = np.zeros((m + 1, n + m))
    for j, part_set in enumerate(sets):
        for p in part_set:
            A[pidx[p], j] = -1.0
        A[m, j] = 1.0
    A[:m, n:] = np.eye(m)
    hi = np.zeros(m + 1)
    hi[m] = k
    # Recompute from the instance; cached candidate area can include duplicate parts
    # or come from a different area normalisation.
    areas = np.array([pk.solve_area[p] / pk.SA for p in parts])
    # Total area reward is <= 1/2, so one extra distinct part always dominates it.
    weights = areas if objective == "area" else 1 + .5 * areas / max(1., float(areas.sum()))
    c = np.concatenate((np.zeros(n), -weights))
    bounds = Bounds(0, 1)
    if required:
        lower = np.zeros(n + m)
        for p in required:
            lower[n + pidx[p]] = 1
        bounds = Bounds(lower, 1)
    res = milp(c, constraints=LinearConstraint(A, -np.inf, hi), integrality=np.ones(n + m), bounds=bounds,
               options={"time_limit": 120})
    if res.x is None:
        return None, parts
    chosen = [j for j in range(n) if res.x[j] > 0.5]
    covered = {int(p) for j in chosen for p in cands[j]["placed"]}
    if not required.issubset(covered):
        return None, parts
    return chosen, [p for p in parts if p not in covered]


def assign_candidates(cands, chosen):
    """Assign every shared part to the first selected candidate, deterministically.

    Subsets of feasible layouts remain feasible. Empty sheets are discarded.
    """
    assigned, sheets = set(), []
    for j in sorted(set(chosen)):
        c = cands[j]
        placed = {int(p): tuple(v) for p, v in c["placed"].items() if int(p) not in assigned}
        if not placed:
            continue
        assigned.update(placed)
        sheets.append({"group": [int(p) for p in c.get("group", []) if int(p) in placed], "placed": placed})
    return sheets


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instance", required=True)
    ap.add_argument("--cache", required=True, help="group feasibility cache (groupfill_nfp --tests-only output)")
    ap.add_argument("--dll", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--big-frac", type=float, default=0.05, help="plate = part area >= this fraction of the sheet")
    ap.add_argument("--long-frac", type=float, default=0.33)
    ap.add_argument("--top-groups", type=int, default=40, help="max plate groups to use")
    ap.add_argument("--max-free", type=float, default=0.40, help="skip groups whose plates cover < 1 - max_free of a sheet")
    ap.add_argument("--per-plate", type=int, default=6, help="target groups per plate, subject to available groups and top-groups budget")
    ap.add_argument("--group-sizes", default="3,4")
    ap.add_argument("--targets", type=lambda v: [float(x) for x in v.split(",")], default=[0.86, 0.90, 0.94])
    ap.add_argument("--seeds", type=lambda v: [int(x) for x in v.split(",")], default=[100])
    ap.add_argument("--max-fillers", type=int, default=14)
    ap.add_argument("--gens", type=int, default=8000)
    ap.add_argument("--starts", type=int, default=2)
    ap.add_argument("--cap", type=float, default=1500.0)
    ap.add_argument("--workers", type=int, default=8)
    ap.add_argument("--rotations", type=int, default=3600)
    ap.add_argument("--curve-tol", type=float, default=0.0)
    ap.add_argument("--env", action="append", default=[])
    ap.add_argument("--cover-only", action="store_true")
    ap.add_argument("--generate-only", action="store_true", help="save generated candidates and exit before selection or repacking")
    ap.add_argument("--selection-only", action="store_true", help="export selected candidate subsets without packing or compaction")
    ap.add_argument("--extra-cands", default=None, help="additional candidates.json files to merge (comma separated)")
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    for kv in args.env:
        k, v = kv.split("=", 1)
        os.environ[k] = v
    import groupfill_nfp as gf
    import engines as eng
    gf.ENGINE = eng.ENGINE_NP
    inst = json.load(open(args.instance, encoding="utf-8"))
    pk = gf.PackerNp(inst, "gh_np", args.dll, args.rotations, args.curve_tol)
    all_parts = sorted(pk.parts)
    big = [i for i in all_parts if pk.solve_area[i] >= args.big_frac * pk.SA]
    fillers = [i for i in all_parts if i not in big]
    total = sum(pk.solve_area.values()) / pk.SA
    log(f"parts {len(all_parts)}: plates {len(big)}, fillers {len(fillers)}, total {total:.3f} sheets, k {args.k}")

    cand_path = out / "candidates.json"
    if not args.cover_only:
        cache = json.load(open(args.cache, encoding="utf-8"))
        sizes = {int(s) for s in args.group_sizes.split(",")}
        groups = []
        for key, rec in cache.items():
            if not rec.get("feasible"):
                continue
            g = tuple(int(x) for x in key.strip("()").split(",") if x.strip())
            if len(g) in sizes and all(i in big for i in g):
                groups.append((pk.SA - sum(pk.solve_area[i] for i in g), g))
        # Useful groups carry >= (1 - max_free) of a sheet in plates. Balance
        # per-plate coverage within the group budget, preferring roomier ties.
        groups = [(f, g) for f, g in groups if f <= args.max_free * pk.SA]
        groups.sort(reverse=True)
        groups, count = select_groups(groups, big, args.per_plate, args.top_groups)
        log(f"plates with fewest groups: {sorted(count.items(), key=lambda t: t[1])[:5]}")
        if not groups:
            log("no feasible plate groups satisfy the filters; no candidate packs generated")
            json.dump([], open(cand_path, "w"))
            return
        missing_plates = [p for p, n in count.items() if n == 0]
        if missing_plates:
            log(f"warning: selected groups omit plates {missing_plates}; increase group budget or relax filters")
        free_fractions = [1 - sum(pk.solve_area[i] for i in g) / pk.SA for g in groups]
        log(f"feasible plate groups of size {sorted(sizes)}: using {len(groups)} balanced groups "
            f"({100 * min(free_fractions):.0f}% .. {100 * max(free_fractions):.0f}% free)")
        jobs = build_jobs(pk, groups, fillers, args)
        log(f"{len(jobs)} candidate packs on {args.workers} workers (gens {args.gens} x {args.starts}, cap {args.cap}s)")
        t0 = time.time()
        cands = []
        with Pool(args.workers, initializer=_init,
                  initargs=(args.instance, args.dll, args.rotations, args.curve_tol, args.env)) as pool:
            for n, c in enumerate(pool.imap_unordered(_pack_candidate, jobs), 1):
                cands.append(c)
                log(f"  [{n}/{len(jobs)}] {c['tag']}: plates {'ok' if c['plates_ok'] else 'LOST'}, "
                    f"{len(c['placed'])}/{len(c['offered'])} parts, {100 * c['area']:.1f}%, {c['wall']:.0f}s")
                if n % 10 == 0:
                    json.dump(cands, open(cand_path, "w"), indent=0)
        json.dump(cands, open(cand_path, "w"), indent=0)
        log(f"candidates: {len(cands)} in {time.time() - t0:.0f}s")
        if args.generate_only:
            return
    cands = json.load(open(cand_path, encoding="utf-8"))
    if args.extra_cands:
        for f in args.extra_cands.split(","):
            cands += json.load(open(f, encoding="utf-8"))
    cands = [validate_sheet_candidate(pk, c) for c in cands if c["plates_ok"] and c["placed"]]
    cands = [c for c in cands if c["validated"]]
    log(f"feasible candidate sheets: {len(cands)}; best areas: "
        f"{[round(100 * c['area'], 1) for c in sorted(cands, key=lambda c: -c['area'])[:8]]}")

    chosen, uncovered = exact_cover(cands, all_parts, args.k, pk)
    if chosen is None:
        log("cover ILP failed")
        return
    sheets = assign_candidates(cands, chosen)
    covered_area = sum(pk.solve_area[p] for s in sheets for p in s["placed"]) / pk.SA
    log(f"cover: {len(sheets)} sheets, covered area {covered_area:.3f} sheets, uncovered {len(uncovered)} parts "
        f"({100 * sum(pk.solve_area[i] for i in uncovered) / pk.SA:.1f}% of a sheet): {uncovered}")
    for s in sheets:
        log(f"  sheet: group {tuple(s['group'])} + {len(s['placed']) - len(s['group'])} fillers, "
            f"{100 * sum(pk.solve_area[i] for i in s['placed']) / pk.SA:.1f}%")

    class A:
        pass
    a = A()
    a.fill_gens, a.fill_pop, a.cap_fill, a.seed = args.gens, args.starts, args.cap, args.seeds[0]
    a.hole_angle_step = 10.0
    a.remnant_heights = [1.0, 0.875, 0.75, 0.625, 0.5, 0.4375, 0.375, 0.3125, 0.25, 0.1875, 0.125]
    a.two_stage, a.repack_knap = True, True
    if args.selection_only:
        ppath = gf.write_placements(pk, sheets, uncovered, str(out), args.instance, a, [])
        log(f"selection only: wrote {ppath}; {len(uncovered)} parts remain uncovered")
        return
    pool_left = list(uncovered)
    if pool_left:
        sheets, pool_left = gf.repack_leftovers(pk, sheets, pool_left, a, a.seed)
    if pool_left:
        sheets, pool_left = gf.hole_fill(pk, sheets, pool_left, a)
    extra = []
    rest = list(pool_left)
    while rest:
        placed, res = pk.pack(rest, args.gens, args.starts, a.seed, args.cap)
        if not placed:
            break
        extra.append(placed)
        rest = [i for i in rest if i not in placed]
    gf.compact_last_sheet(pk, sheets, extra, a, a.seed)
    ppath = gf.write_placements(pk, sheets, rest, str(out), args.instance, a, extra)
    from evaluate import evaluate_files
    m = evaluate_files(args.instance, ppath, str(out), write_3dm=False, write_svg=True)
    log(f"RESULT: sheets {m['n_sheets']} util {[round(u, 1) for u in m['util_per_sheet']]} overlaps {m['overlap_pairs']} "
        f"viol {m['clearance_violations']} placed {m['placed']}/{m['total']} remnant bbox {100 * m['remnant_bbox_frac']:.1f}%")


if __name__ == "__main__":
    main()
