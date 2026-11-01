"""fit_mode 3 (group & fill) test sets: np_bench runs x fit modes x seeds, every result re-checked by the
independent shapely validator (validate_layout.py). One np_bench process at a time (each uses <= --threads
solver threads: fit 0/2 via NP_WORKERS, fit 3 via NpParams.n_threads).

    python -I tools/bench3dm/fm3_tests.py --bench <np_bench.exe> --dll <nest_physics.dll> --out <dir>
        [--sets R1,I1,...] [--fits 0,2,3] [--seeds 100,101] [--iters 4000] [--threads 4] [--env K=V ...]

Writes <out>/results.json, <out>/results.md (one table per set + a fit 3 vs fit 2 summary) and per run the
np_bench log + layout json. Sets (all ids are general synthetic geometry, nothing job-specific):
  R1-R3 rectangular sheet, parts with holes / concave parts (frames + ell / U / C)
  I1-I3 irregular sheet outlines: L-shaped, notched + curved edge, trapezoid
  H1-H2 sheets with interior keep-outs (circle + rectangle; circle)
  M1-M2 mixed sheet sizes (three sizes in one job)
  F1    frames whose holes can host small parts (part-in-hole)
  V1    varied parts (plates + long strips + small parts) on a plain sheet
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from validate_layout import validate  # noqa: E402

BASE = "--sw=2000 --sh=1200 --pw=400 --ph=290 --sheets=10 --rots=32"
SETS = {
    "R1": f"{BASE} --frames=4 --shape=ell --parts=40",
    "R2": f"{BASE} --frames=4 --shape=u --parts=36",
    "R3": f"{BASE} --frames=3 --shape=c --parts=36",
    "I1": f"{BASE} --shape=varied --parts=36 --outline=L",
    "I2": f"{BASE} --shape=varied --parts=36 --outline=notch",
    "I3": f"{BASE} --shape=varied --parts=36 --outline=trap",
    "H1": f"{BASE} --shape=varied --parts=36 --sholes=both",
    "H2": f"{BASE} --shape=ell --parts=40 --sholes=circ",
    "M1": f"{BASE} --shape=varied --parts=40 --sizes=2000x1200,1500x1000,2400x1400",
    "M2": f"{BASE} --shape=ell --parts=44 --sizes=1500x1000,2400x1400,2000x1200",
    "F1": f"{BASE} --frames=6 --shape=rect --parts=40",
    "V1": f"{BASE} --shape=varied --parts=42",
}


def run_one(a, set_id, fit, seed, out: Path) -> dict:
    tag = f"{set_id}_fit{fit}_s{seed}"
    js = out / f"{tag}.json"
    log = out / f"{tag}.log"
    args = [a.bench, f"--dll={a.dll}", *SETS[set_id].split(), f"--fit={fit}", f"--seed={seed}",
            f"--iters={a.iters}", f"--json={js}", f"--threads={a.threads}"]
    env = dict(os.environ)
    env["NP_WORKERS"] = str(a.threads)
    for kv in a.env:
        k, v = kv.split("=", 1)
        env[k] = v
    t0 = time.time()
    with open(log, "w") as f:
        rc = subprocess.run(args, stdout=f, stderr=subprocess.STDOUT, env=env).returncode
    wall = time.time() - t0
    text = log.read_text(errors="replace")
    verdict = next((ln.split("VERDICT:")[1].strip() for ln in text.splitlines() if "VERDICT:" in ln), "?")
    winner = next((ln.split("winner")[1].split(":")[0].strip() for ln in text.splitlines() if "fit_mode 3: winner" in ln), "")
    rec = {"set": set_id, "fit": fit, "seed": seed, "rc": rc, "wall_s": round(wall, 1), "verdict": verdict,
           "winner": winner}
    try:
        doc = json.load(open(js))
        v = validate(doc)
        rec.update({k: v[k] for k in ("valid", "sheets_used", "util_per_sheet", "unplaced", "least_filled_util",
                                       "remnant_bbox_frac", "overall_util", "used_usable_area")})
        rec["violations"] = v["violations"][:5]
    except Exception as e:  # noqa: BLE001
        rec.update({"valid": False, "error": str(e)})
    return rec


def better(a: dict, b: dict) -> int:
    """+1 if a beats b (fewer unplaced, fewer sheets, smaller used area, lower least-filled util), -1 worse, 0 tie."""
    ka = (a.get("unplaced", 99), a.get("sheets_used", 99), round(a.get("used_usable_area", 0), 0), a.get("least_filled_util") or 0)
    kb = (b.get("unplaced", 99), b.get("sheets_used", 99), round(b.get("used_usable_area", 0), 0), b.get("least_filled_util") or 0)
    return (ka < kb) - (ka > kb)


def write_md(recs, path: Path, fits, seeds):
    lines = ["# fit_mode 3 test sets", ""]
    by_set = {}
    for r in recs:
        by_set.setdefault(r["set"], []).append(r)
    for s, rs in by_set.items():
        lines += [f"## {s}: `{SETS[s]}`", "", "| fit | seed | sheets | unplaced | util per sheet % | least-filled % | valid (shapely) | engine verdict | winner | wall s |",
                  "|---|---|---|---|---|---|---|---|---|---|"]
        for r in sorted(rs, key=lambda r: (r["fit"], r["seed"])):
            lines.append(f"| {r['fit']} | {r['seed']} | {r.get('sheets_used')} | {r.get('unplaced')} | "
                         f"{' / '.join(f'{u:.1f}' for u in r.get('util_per_sheet', []))} | {r.get('least_filled_util')} | "
                         f"{r.get('valid')} | {r['verdict']} | {r.get('winner', '')} | {r['wall_s']} |")
        lines.append("")
    lines += ["## fit 3 vs fit 2 (same seed): +1 = fit 3 better, 0 = tie, -1 = worse", "",
              "| set | seed | fit 2 sheets / least % | fit 3 sheets / least % | fit 3 winner | verdict |", "|---|---|---|---|---|---|"]
    for s, rs in by_set.items():
        for sd in seeds:
            a = next((r for r in rs if r["fit"] == 3 and r["seed"] == sd), None)
            b = next((r for r in rs if r["fit"] == 2 and r["seed"] == sd), None)
            if a and b:
                lines.append(f"| {s} | {sd} | {b.get('sheets_used')} / {b.get('least_filled_util')} | "
                             f"{a.get('sheets_used')} / {a.get('least_filled_util')} | {a.get('winner')} | {better(a, b):+d} |")
    path.write_text("\n".join(lines) + "\n")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--bench", required=True)
    ap.add_argument("--dll", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--sets", default=",".join(SETS))
    ap.add_argument("--fits", default="0,2,3")
    ap.add_argument("--seeds", default="100,101")
    ap.add_argument("--iters", type=int, default=4000)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--env", action="append", default=[])
    a = ap.parse_args(argv)
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    fits = [int(x) for x in a.fits.split(",")]
    seeds = [int(x) for x in a.seeds.split(",")]
    res_path = out / "results.json"
    recs = json.load(open(res_path)) if res_path.exists() else []
    done = {(r["set"], r["fit"], r["seed"]) for r in recs}
    for s in a.sets.split(","):
        for fit in fits:
            for sd in seeds:
                if (s, fit, sd) in done:
                    continue
                r = run_one(a, s, fit, sd, out)
                recs.append(r)
                json.dump(recs, open(res_path, "w"), indent=1)
                print(f"{s} fit{fit} s{sd}: sheets {r.get('sheets_used')} unplaced {r.get('unplaced')} util {r.get('util_per_sheet')} "
                      f"valid {r.get('valid')} {r['verdict']} {r.get('winner', '')} {r['wall_s']}s", flush=True)
                write_md(recs, out / "results.md", fits, seeds)
    write_md(recs, out / "results.md", fits, seeds)
    return 0


if __name__ == "__main__":
    sys.exit(main())
