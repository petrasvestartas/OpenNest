"""Bridge between the bench3dm instance/placements files and the headless `opennest_job` CLI
(src/opennest_job). Lets the server-side runner be benchmarked with the existing evaluator.

    python -I tools/bench3dm/opennest_job_io.py to-job <instance.json> <job.json>
        [--config gh_np] [--seed 100] [--set fit_mode=3 --set iter_budget=4000 ...] [--sheets N] [--wall S]
    python -I tools/bench3dm/opennest_job_io.py to-placements <instance.json> <result.json> <placements.json>

`to-job` flattens exactly like engines.NpInputs (solve geometry = clearance-offset rings, qty kept as qty,
N identical solve sheets (0,0)-(w,h), no sheet holes) and takes the NpParams values from configs.json, so a
job run through opennest_job is the same np_nest call engines.py makes. `to-placements` turns the
opennest.result/1 document back into an opennest.bench3dm.placements/1 file for evaluate.py.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import engines  # noqa: E402

JOB_SCHEMA = "opennest.job/1"
RESULT_SCHEMA = "opennest.result/1"
NP_PARAM_KEYS = ("num_rotations", "spacing", "simplify_tolerance", "seed", "time_budget_secs", "iter_budget",
                 "iter_mode", "max_sheets", "n_starts", "part_holes_mode", "pole_max", "final_compact",
                 "fit_mode", "n_threads")


def to_job(instance: dict, cfg: dict, seed: int, wall: float) -> dict:
    if cfg["engine"] != engines.ENGINE_NP:
        raise SystemExit("opennest_job runs nest_physics only (config engine must be np_nest)")
    parts = []
    for p in instance["parts"]:
        outer, holes = engines._solve_geom(p)
        parts.append({"id": int(p["id"]), "name": p.get("name", str(p["id"])), "qty": int(p.get("qty", 1)),
                      "rotations": max(int(p.get("rotations", 0) or 0), 0),
                      "outer": [[float(x), float(y)] for x, y in outer.tolist()],
                      "holes": [[[float(x), float(y)] for x, y in h.tolist()] for h in holes]})
    n, w, h, _ = engines._sheets(instance, cfg)
    params = {k: v for k, v in cfg["params"].items() if k in NP_PARAM_KEYS}
    params["seed"] = int(seed)
    job = {"schema": JOB_SCHEMA, "units": instance.get("units", "mm"),
           "name": Path(instance.get("source", {}).get("parts_3dm", "job")).stem,
           "parts": parts,
           "sheets": [{"id": "solve_sheet", "count": n, "outer": [[0, 0], [w, 0], [w, h], [0, h]]}],
           "params": params}
    if wall > 0:
        job["limits"] = {"wall_seconds": wall}
    return job


def to_placements(instance_path: str, result: dict, seed: int | None = None) -> dict:
    if result.get("schema") != RESULT_SCHEMA:
        raise SystemExit(f"not an {RESULT_SCHEMA} document")
    inst = [{"part": int(p["part"]), "copy": int(p["copy"]), "sheet": int(p["sheet"]),
             "tx": float(p["tx"]), "ty": float(p["ty"]), "angle_rad": float(p["angle"])}
            for p in result["placements"]]
    q = result.get("quality", {})
    eng = result.get("engine", {})
    return {
        "schema": engines.PLACEMENTS_SCHEMA,
        "instance": str(Path(instance_path).resolve()),
        "engine": engines.ENGINE_NP,
        "config": f"opennest_job ({eng.get('platform')}, {eng.get('compiler')})",
        "seed": int(seed or 0),
        "dll": {"path": eng.get("runner", "opennest_job"), "sha256": "", "mtime": None},
        "wall_s": float(result.get("wall_s", 0.0)),
        "cancelled": result.get("status") == "cancelled",
        "engine_report": {"rc": 0 if result.get("status") != "error" else 1,
                          "np_last_quality": {"overlap_pairs": q.get("overlap_pairs"), "oob": q.get("out_of_bounds"),
                                              "unplaced": q.get("unplaced"), "demoted": q.get("demoted"),
                                              "clean": int(bool(q.get("clean")))},
                          "rounds": result.get("rounds")},
        "frame": engines.FRAME,
        "n_sheets": int(result.get("n_sheets", 0)),
        "sheets_used": result.get("sheets_used", []),
        "instances": inst,
    }


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    a1 = sub.add_parser("to-job")
    a1.add_argument("instance")
    a1.add_argument("job")
    a1.add_argument("--config", default="gh_np")
    a1.add_argument("--seed", type=int, default=100)
    a1.add_argument("--set", action="append", default=[], help="NpParams override key=value")
    a1.add_argument("--sheets", type=int, default=0)
    a1.add_argument("--wall", type=float, default=0.0)
    a2 = sub.add_parser("to-placements")
    a2.add_argument("instance")
    a2.add_argument("result")
    a2.add_argument("placements")
    a2.add_argument("--seed", type=int, default=None)
    a = ap.parse_args(argv)
    with open(a.instance, encoding="utf-8") as f:
        instance = json.load(f)
    if a.cmd == "to-job":
        cfg = engines.apply_overrides(engines.resolve_config(a.config), a.set)
        if a.sheets:
            cfg["sheets"] = a.sheets
            cfg["params"]["max_sheets"] = a.sheets
        job = to_job(instance, cfg, a.seed, a.wall)
        with open(a.job, "w", encoding="utf-8", newline="\n") as f:
            json.dump(job, f)
        print(f"wrote {a.job}: {len(job['parts'])} parts, {job['sheets'][0]['count']} sheets, params {job['params']}")
    else:
        with open(a.result, encoding="utf-8") as f:
            result = json.load(f)
        doc = to_placements(a.instance, result, a.seed)
        errs = engines.validate_placements(doc, instance)
        if errs:
            print("placements problems:", *errs[:10], sep="\n  ", file=sys.stderr)
            return 1
        with open(a.placements, "w", encoding="utf-8", newline="\n") as f:
            json.dump(doc, f, indent=1)
        print(f"wrote {a.placements}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
