"""Bounded two-sheet plate-exchange neighbourhood for candidate union coverage.

Move or swap one large part between selected sheets, add one uncovered part, then
jointly repack BOTH complete sets. Accept only when every original part plus the
incoming part remains, both geometries validate, and source-ring gaps/margins pass.
"""
import argparse
import hashlib
import json
import sys
import time
from multiprocessing import Pool
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sheetcover as sc
from sheetcover_polish import polish_candidate
from sheetcover_repair import save_json, validate_candidate, geometry_diagnostics
from validate_strict import validate_strict


def exchange_key(left, right):
    sets = sorted([sorted(left), sorted(right)])
    return hashlib.sha256(json.dumps(sets, separators=(",", ":")).encode()).hexdigest()


def schedule_exchanges(pk, sheets, missing, attempted, *, big_frac=.05, limit=10,
                       gens=4000, starts=1, cap=120, seed=100):
    """Rank feasible-area exchanges, then interleave incoming parts and recipient pairs."""
    proposals = {}
    for incoming in sorted(set(missing), key=lambda p: (-pk.solve_area[p], p)):
        for ai, left_sheet in enumerate(sheets):
            left = set(left_sheet["placed"])
            for bi, right_sheet in enumerate(sheets):
                if ai == bi:
                    continue
                right = set(right_sheet["placed"])
                if left & right or incoming in left | right:
                    continue
                outgoing = sorted(p for p in left if pk.solve_area[p] >= big_frac * pk.SA)
                returns = [None] + sorted(p for p in right if pk.solve_area[p] >= big_frac * pk.SA)
                for moved in outgoing:
                    for returned in returns:
                        next_left = (left - {moved}) | {incoming}
                        next_right = right | {moved}
                        if returned is not None:
                            next_left.add(returned)
                            next_right.remove(returned)
                        la = sum(pk.solve_area[p] for p in next_left) / pk.SA
                        ra = sum(pk.solve_area[p] for p in next_right) / pk.SA
                        if max(la, ra) > 1:
                            continue
                        key = exchange_key(next_left, next_right)
                        if key in attempted or key in proposals:
                            continue
                        proposals[key] = {"left": sorted(next_left), "right": sorted(next_right),
                            "original": sorted(left | right), "incoming": incoming, "moved": moved,
                            "returned": returned, "source_sheets": [ai, bi], "key": key,
                            "rank": [max(la, ra), abs(la - ra), len(next_left) + len(next_right)]}
    # One best exchange per incoming/recipient pair before its alternatives.
    buckets = {}
    for proposal in proposals.values():
        bucket = (proposal["incoming"], tuple(sorted(proposal["source_sheets"])))
        buckets.setdefault(bucket, []).append(proposal)
    for values in buckets.values():
        values.sort(key=lambda p: (p["rank"], p["key"]))
    by_incoming = {}
    for bucket in buckets:
        by_incoming.setdefault(bucket[0], []).append(bucket)
    for values in by_incoming.values():
        values.sort(key=lambda b: (buckets[b][0]["rank"], b))
    incoming_order = sorted(by_incoming, key=lambda p: (-pk.solve_area[p], p))
    ordered = [by_incoming[p][index]
               for index in range(max((len(v) for v in by_incoming.values()), default=0))
               for p in incoming_order if index < len(by_incoming[p])]
    jobs = []
    depth = 0
    while len(jobs) < limit:
        added = False
        for bucket in ordered:
            if depth >= len(buckets[bucket]):
                continue
            proposal = dict(buckets[bucket][depth])
            proposal.update(gens=gens, starts=starts, cap=cap, seed=seed + len(jobs), tag=f"exchange_s{seed}_j{len(jobs)}")
            jobs.append(proposal)
            added = True
            if len(jobs) >= limit:
                break
        if not added:
            break
        depth += 1
    return jobs


def pair_preserved(job, candidates):
    if len(candidates) != 2:
        return False
    a, b = ({int(p) for p in candidate["placed"]} for candidate in candidates)
    return not (a & b) and a | b == set(job["original"]) | {job["incoming"]}


def valid_column(pk, candidate):
    """Accept an actual subset only after solve and source-polygon validation."""
    valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
    if not valid:
        candidate = polish_candidate(pk, candidate)
        if candidate is None:
            return None
    actual = set(map(int, candidate["placed"]))
    sub = dict(pk.instance, parts=[dict(pk.parts[p], qty=1) for p in sorted(actual)])
    plc = {"instances": [{"part": int(p), "copy": 0, "sheet": 0, "tx": v[0], "ty": v[1], "angle_rad": v[2]}
                         for p, v in candidate["placed"].items()]}
    strict = validate_strict(sub, plc, max_sheets=1)
    return candidate if strict["passed_sampled_polygons"] else None


def run_exchange(job):
    pk = sc._PK
    started = time.time()
    candidates = []
    partials = []
    reports = []
    for side, ids in enumerate((job["left"], job["right"])):
        placed, result = pk.pack(ids, job["gens"], job["starts"], job["seed"] + side * 1009,
                                job["cap"], mandatory=set(ids))
        native_placed = {str(pid): [float(result["tx"][index]), float(result["ty"][index]), float(result["angle_rad"][index])]
                         for index, pid in enumerate(ids) if int(result["sheet"][index]) == 0}
        reports.append({"quality": result.get("quality", {}), "cancelled": bool(result.get("cancelled", False)),
                        "wall_s": result.get("wall_s"), "raw_placed": {str(p): v for p, v in placed.items()},
                        "native_placed": native_placed,
                        "raw_geometry_diagnostics": geometry_diagnostics(pk, placed),
                        "missing_mandatory": sorted(set(ids) - set(placed))})
        if set(placed) != set(ids):
            # A partial exchange is NOT a successful pair, but its actual placed
            # subset may still be a useful globally selectable sheet column.
            novel = {job["incoming"]} if side == 0 else {job["moved"]}
            if side == 0 and job["returned"] is not None:
                novel.add(job["returned"])
            if novel & set(placed):
                partial = {"tag": job["tag"] + f"_{side}_partial", "placed": {str(p): v for p, v in placed.items()},
                           "group": sorted(placed), "requested_group": ids, "offered": ids, "plates_ok": True,
                           "partial_exchange": True, "exchange_complete": False,
                           "area": sum(pk.solve_area[p] for p in placed) / pk.SA}
                partial = valid_column(pk, partial)
                if partial is not None:
                    partials.append(partial)
            partials.extend(dict(c, partial_exchange=True, exchange_complete=False) for c in candidates)
            return {"job": job, "accepted": False, "reason": f"side {side} incomplete", "reports": reports,
                    "partial_candidates": partials, "wall": time.time() - started}
        candidate = {"tag": job["tag"] + f"_{side}", "placed": {str(p): v for p, v in placed.items()},
                     "group": ids, "offered": ids, "plates_ok": True, "area": sum(pk.solve_area[p] for p in ids) / pk.SA}
        candidate = valid_column(pk, candidate)
        if candidate is None:
            return {"job": job, "accepted": False, "reason": "solve/source geometry gate", "reports": reports, "wall": time.time() - started}
        candidates.append(candidate)
    if not pair_preserved(job, candidates):
        return {"job": job, "accepted": False, "reason": "pair lost/duplicated an incumbent", "reports": reports, "wall": time.time() - started}
    expected = set(job["original"]) | {job["incoming"]}
    sub = dict(pk.instance, parts=[dict(pk.parts[p], qty=1) for p in sorted(expected)])
    placements = {"instances": [{"part": int(p), "copy": 0, "sheet": si, "tx": v[0], "ty": v[1], "angle_rad": v[2]}
                                for si, c in enumerate(candidates) for p, v in c["placed"].items()]}
    strict = validate_strict(sub, placements, max_sheets=2)
    if not strict["passed_sampled_polygons"]:
        return {"job": job, "accepted": False, "reason": "source polygon clearance/margin gate", "strict": strict,
                "reports": reports, "wall": time.time() - started}
    return {"job": job, "accepted": True, "reason": "complete strict-valid pair", "candidates": candidates,
            "strict": strict, "reports": reports, "wall": time.time() - started}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for arg in ("instance", "pool", "dll", "out"):
        ap.add_argument("--" + arg, required=True)
    ap.add_argument("--k", type=int, default=5)
    ap.add_argument("--rounds", type=int, default=1)
    ap.add_argument("--prepare-only", action="store_true", help="save selected layout and bounded exchange job list without native packs")
    ap.add_argument("--workers", type=int, default=5)
    ap.add_argument("--max-jobs", type=int, default=10)
    ap.add_argument("--gens", type=int, default=4000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--cap", type=float, default=120, help="cap per one-sheet call; at most two calls per job")
    ap.add_argument("--seed", type=int, default=100)
    ap.add_argument("--big-frac", type=float, default=.05)
    ap.add_argument("--rotations", type=int, default=3600)
    ap.add_argument("--curve-tol", type=float, default=0)
    ap.add_argument("--env", action="append", default=[])
    args = ap.parse_args()
    if args.rounds < 0 or min(args.k, args.workers, args.max_jobs, args.gens, args.starts, args.cap) <= 0:
        ap.error("budgets must be positive; rounds may be zero")
    env = {"NP_SMART_ROT": "1", "NP_EXPL_STRIKES": "2", "NP_EXPL_NOIMPR": "50", "NP_SIMPL_MAXDEV": "0.5", "NP_PRIO_MAX_RETRIES": "1"}
    env.update(kv.split("=", 1) for kv in args.env)
    settings = [f"{k}={v}" for k, v in env.items()]
    sc._init(args.instance, args.dll, args.rotations, args.curve_tol, settings)
    pk = sc._PK
    if any(int(p.get("qty", 1)) != 1 for p in pk.parts.values()):
        ap.error("candidate exchange requires distinct part IDs with qty=1")
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    candidates = []
    for candidate in json.loads(Path(args.pool).read_text(encoding="utf-8")):
        valid, why = validate_candidate(pk, candidate, tolerance=1e-8)
        if not valid:
            candidate = polish_candidate(pk, candidate)
        if candidate is not None:
            candidates.append(candidate)
    attempted, reports = set(), []

    def checkpoint():
        chosen, missing = sc.exact_cover(candidates, pk.parts, args.k, pk)
        if chosen is None:
            raise RuntimeError("union selection failed")
        sheets = sc.assign_candidates(candidates, chosen)
        save_json(out / "candidates.json", candidates)
        save_json(out / "progress.json", {"uncovered": missing, "attempted": sorted(attempted), "results": reports})
        import groupfill_nfp as gf
        path = gf.write_placements(pk, sheets, missing, str(out), args.instance, args, [])
        strict = validate_strict(pk.instance, json.loads(Path(path).read_text(encoding="utf-8")), max_sheets=args.k)
        save_json(out / "strict_metrics.json", strict)
        sc.log(f"exchange cover: {len(sheets)} sheets, missing {missing}, pool {len(candidates)}")
        return sheets, missing

    sheets, missing = checkpoint()
    for round_index in range(args.rounds):
        if not missing:
            break
        jobs = schedule_exchanges(pk, sheets, missing, attempted, big_frac=args.big_frac, limit=args.max_jobs,
                                  gens=args.gens, starts=args.starts, cap=args.cap, seed=args.seed + round_index * args.max_jobs)
        save_json(out / f"jobs_round{round_index}.json", jobs)
        if not jobs:
            break
        sc.log(f"exchange round {round_index + 1}: {len(jobs)} jobs, {args.workers} workers, <=2 calls/job at {args.cap}s each")
        if args.prepare_only:
            break
        with Pool(args.workers, initializer=sc._init,
                  initargs=(args.instance, args.dll, args.rotations, args.curve_tol, settings)) as pool:
            for result in pool.imap_unordered(run_exchange, jobs):
                diagnostic_dir = out / "raw_candidates"
                diagnostic_dir.mkdir(exist_ok=True)
                save_json(diagnostic_dir / f"{result['job']['tag']}.json", result)
                attempted.add(result["job"]["key"])
                if result["accepted"]:
                    candidates.extend(result["candidates"])
                candidates.extend(result.get("partial_candidates", []))
                reports.append({k: v for k, v in result.items() if k not in ("candidates", "partial_candidates")})
                sc.log(f"{result['job']['tag']}: {result['reason']}, partial columns {len(result.get('partial_candidates', []))}, {result['wall']:.1f}s")
                sheets, missing = checkpoint()
                if not missing:
                    pool.terminate()
                    break
    from evaluate import evaluate_files
    evaluate_files(args.instance, str(out / "placements.json"), str(out), write_3dm=False, write_svg=True)


if __name__ == "__main__":
    main()
