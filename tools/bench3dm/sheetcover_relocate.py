"""Compose bounded relocations into already supported candidate cores.

Pool entries must already have passed geometry validation. This planner does not
pack or validate geometry and never inserts hypothetical layouts into that pool.
"""
import argparse
import itertools
import json
from pathlib import Path

from shapely.geometry import Polygon


def enumerate_relocations(plan, pool, areas, *, max_moves=2, max_plans=10,
                          max_examined=10000, area_cap=.94):
    if min(max_moves, max_plans, max_examined) < 1 or not 0 < area_cap <= 1:
        raise ValueError("enumeration budgets must be positive and area_cap within 0..1")
    cores = [frozenset(job["group"]) for job in plan]
    flattened = [pid for job in plan for pid in job["group"]]
    if not cores or any(not core for core in cores) or len(flattened) != len(set(flattened)):
        raise ValueError("input plan must be a nonempty exact-once partition")
    if set(flattened) - areas.keys():
        raise ValueError("input plan contains unknown parts")
    actual = [frozenset(map(int, candidate["placed"])) for candidate in pool]

    def support(core):
        return next((i for i, ids in enumerate(actual) if core <= ids), None)

    def fraction(core):
        return sum(areas[i] for i in core)

    originals = [support(core) for core in cores]
    ranked, signatures = [], set()
    examined, truncated = 0, False
    for source, core in enumerate(cores):
        if originals[source] is not None:
            continue
        singles = []
        for pid in sorted(core):
            for target, destination in enumerate(cores):
                if target == source or originals[target] is None:
                    continue
                expanded = destination | {pid}
                evidence = support(expanded)
                if evidence is not None and fraction(expanded) <= area_cap:
                    singles.append((pid, target, evidence))
        for count in range(1, min(max_moves, len(core) - 1) + 1):
            for moves in itertools.combinations(singles, count):
                if examined >= max_examined:
                    truncated = True
                    break
                examined += 1
                if len({p for p, _, _ in moves}) != count or len({t for _, t, _ in moves}) != count:
                    continue
                result = list(cores)
                result[source] = core - {p for p, _, _ in moves}
                for pid, target, _ in moves:
                    result[target] = result[target] | {pid}
                signature = tuple(tuple(sorted(group)) for group in result)
                if signature in signatures:
                    continue
                signatures.add(signature)
                support_indices = [support(group) for group in result]
                unsupported = [i for i, evidence in enumerate(support_indices) if evidence is None]
                fractions = [fraction(group) for group in result]
                score = (len(unsupported), max((fractions[i] for i in unsupported), default=0),
                         max(fractions), -count, signature)
                ranked.append((score, {"source": source,
                    "moves": [{"part": p, "target": t, "support_candidate": evidence}
                              for p, t, evidence in moves],
                    "cores": [list(group) for group in signature],
                    "support_candidate_indices": support_indices,
                    "unsupported_core_indices": unsupported, "core_areas": fractions,
                    "status": "hypothetical until unsupported cores are packed and validated"}))
            if truncated:
                break
        if truncated:
            break
    ranked.sort(key=lambda item: item[0])
    return {"plans": [item[1] for item in ranked[:max_plans]],
            "combinations_examined": examined, "truncated": truncated,
            "input_parts": sorted(flattened)}


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "plan", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--max-moves", type=int, default=2)
    ap.add_argument("--max-plans", type=int, default=10)
    ap.add_argument("--max-examined", type=int, default=10000)
    ap.add_argument("--area-cap", type=float, default=.94)
    ap.add_argument("--seed", type=int, default=84000)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=16000)
    ap.add_argument("--starts", type=int, default=1)
    args = ap.parse_args()
    if min(args.cap, args.gens, args.starts) <= 0:
        ap.error("packing budgets must be positive")
    instance = json.loads(Path(args.instance).read_text())
    pool = json.loads(Path(args.pool).read_text())
    raw = json.loads(Path(args.plan).read_text())
    plan = raw["plan"] if isinstance(raw, dict) else raw
    sheet_area = instance["solve_sheet"]["w"] * instance["solve_sheet"]["h"]
    areas = {p["id"]: Polygon((p.get("solve") or p)["outer"],
                             (p.get("solve") or p).get("holes", [])).area / sheet_area
             for p in instance["parts"]}
    result = enumerate_relocations(plan, pool, areas, max_moves=args.max_moves,
        max_plans=args.max_plans, max_examined=args.max_examined, area_cap=args.area_cap)
    jobs, emitted = [], {}
    for rank, proposed in enumerate(result["plans"]):
        proposed["jobs"] = []
        proposed["supported_by"] = [None if index is None else
            pool[index].get("tag", f"pool_index_{index}")
            for index in proposed["support_candidate_indices"]]
        for index in proposed["unsupported_core_indices"]:
            core = tuple(proposed["cores"][index])
            if core not in emitted:
                emitted[core] = len(jobs)
                jobs.append({"group": list(core), "fillers": [], "incoming": [],
                    "seed": args.seed + len(jobs), "gens": args.gens,
                    "starts": args.starts, "cap": args.cap,
                    "tag": f"composed_offer_{len(jobs)}", "core_area": proposed["core_areas"][index]})
            proposed["jobs"].append(emitted[core])
    result["provenance"] = {"instance": args.instance, "pool": args.pool, "plan": args.plan}
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "plans.json").write_text(json.dumps(result, indent=2))
    (out / "jobs.json").write_text(json.dumps(jobs, indent=2))
    print(json.dumps({"plans": len(result["plans"]), "jobs": len(jobs),
                      "examined": result["combinations_examined"], "truncated": result["truncated"]}))


if __name__ == "__main__":
    main()
