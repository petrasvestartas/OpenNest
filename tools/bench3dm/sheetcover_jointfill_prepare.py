"""Prepare finite joint-fill offers from a proposed disjoint hard-core plan; no packing."""
import argparse
import json
from pathlib import Path
from shapely.geometry import Polygon


def allocate(cores, areas, actual_sets):
    flat = [p for core in cores for p in core]
    if len(set(flat)) != len(flat) or not set(flat) <= set(areas):
        raise ValueError("cores must be disjoint and contain known IDs")
    if not cores or any(not core for core in cores):
        raise ValueError("nonempty cores required")
    fillers = [[] for _ in cores]
    loads = [sum(areas[p] for p in core) for core in cores]
    support = [[ids for ids in actual_sets if set(core) <= ids] for core in cores]
    for pid in sorted(set(areas) - set(flat), key=lambda p: (-areas[p], p)):
        # Prefer spare area, then demonstrated co-occurrence among complete cores.
        sheet = min(range(len(cores)), key=lambda s: (
            max(0, loads[s] + areas[pid] - .90),
            -sum(pid in ids for ids in support[s]), loads[s], s))
        fillers[sheet].append(pid)
        loads[sheet] += areas[pid]
    return fillers, support, loads


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    for name in ("instance", "pool", "plan", "out"):
        ap.add_argument("--" + name, required=True)
    ap.add_argument("--cap", type=float, default=180)
    ap.add_argument("--gens", type=int, default=16000)
    ap.add_argument("--starts", type=int, default=1)
    ap.add_argument("--seed", type=int, default=83000)
    args = ap.parse_args()
    if min(args.cap, args.gens, args.starts) <= 0:
        ap.error("packing budgets must be positive")
    instance = json.loads(Path(args.instance).read_text())
    pool = json.loads(Path(args.pool).read_text())
    plan = json.loads(Path(args.plan).read_text())["plan"]
    sheet = instance["solve_sheet"]
    areas = {}
    for p in instance["parts"]:
        if int(p.get("qty", 1)) != 1:
            raise ValueError("candidate pools require quantity one")
        shape = p.get("solve") or p
        areas[p["id"]] = Polygon(shape["outer"], shape.get("holes", [])).area / (sheet["w"] * sheet["h"])
    cores = [job["group"] for job in plan]
    sets = [set(map(int, c["placed"])) for c in pool]
    fillers, support, loads = allocate(cores, areas, sets)
    jobs = [dict(group=sorted(core), fillers=fillers[s], incoming=[],
                 seed=args.seed + s, gens=args.gens, starts=args.starts, cap=args.cap,
                 tag=f"jointfill_{s}", offered_area=loads[s],
                 supported_core=bool(support[s])) for s, core in enumerate(cores)]
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "jobs.json").write_text(json.dumps(jobs, indent=2))
    (out / "preparation.json").write_text(json.dumps({
        "instance": args.instance, "pool": args.pool, "plan": args.plan,
        "all_cores_supported": all(support), "jobs": len(jobs),
        "offered_parts": sum(len(j["group"]) + len(j["fillers"]) for j in jobs),
        "warning": "Preparation only. Support uses supplied pool membership, not fresh geometry validation. "
                   "Optional fillers may be dropped; accept only independently validated returned subsets."
    }, indent=2))
    print(json.dumps({"all_cores_supported": all(support), "jobs": len(jobs), "offered_area": loads}))


if __name__ == "__main__":
    main()
