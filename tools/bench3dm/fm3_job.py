"""Customer-job runs for fit_mode 3 (and the fit 0 / fit 2 references), one engine process at a time, every result
re-checked by evaluate.py (un-offset part rings, clearance, sheet bounds).

    python -I tools/bench3dm/fm3_job.py --instance <instance.json> --dll <nest_physics.dll> --out <dir>
        [--fits 2,3] [--seeds 100,101] [--iters 4000] [--threads 4] [--sheets 10] [--cap 900] [--env K=V ...]

Each run = `engines.py` in a subprocess (config gh_np, the GH component's call) with --set fit_mode / iter_budget /
n_threads / sheets / max_sheets; NP_WORKERS=<threads> so fit 0/2 use the same thread count as the fit 3 portfolio.
Writes <out>/<tag>/placements.json + evaluate output, and <out>/job_results.json / job_results.md.
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


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--instance", required=True)
    ap.add_argument("--dll", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--fits", default="2,3")
    ap.add_argument("--seeds", default="100,101")
    ap.add_argument("--iters", type=int, default=4000)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--sheets", type=int, default=10)
    ap.add_argument("--cap", type=float, default=900.0)
    ap.add_argument("--label", default="")
    ap.add_argument("--env", action="append", default=[])
    a = ap.parse_args(argv)
    from evaluate import evaluate_files
    out = Path(a.out)
    out.mkdir(parents=True, exist_ok=True)
    res_path = out / "job_results.json"
    recs = json.load(open(res_path)) if res_path.exists() else []
    inst_name = Path(a.instance).stem if Path(a.instance).stem != "instance" else Path(a.instance).parent.name
    for fit in [int(x) for x in a.fits.split(",")]:
        for seed in [int(x) for x in a.seeds.split(",")]:
            tag = f"{a.label or inst_name}_fit{fit}_s{seed}_i{a.iters}"
            if any(r["tag"] == tag for r in recs):
                continue
            d = out / tag
            d.mkdir(parents=True, exist_ok=True)
            env = dict(os.environ)
            env["NP_WORKERS"] = str(a.threads)
            env.setdefault("NP_FM3_VERBOSE", "1")
            for kv in a.env:
                k, v = kv.split("=", 1)
                env[k] = v
            cmd = [sys.executable, "-I", str(HERE / "engines.py"), "--instance", a.instance, "--config", "gh_np",
                   "--seed", str(seed), "--cap", str(a.cap), "--dll", a.dll, "--out", str(d),
                   "--set", f"fit_mode={fit}", "--set", f"iter_budget={a.iters}", "--set", f"n_threads={a.threads}",
                   "--set", f"sheets={a.sheets}", "--set", f"max_sheets={a.sheets}"]
            t0 = time.time()
            with open(d / "engine.log", "w") as f:
                rc = subprocess.run(cmd, stdout=f, stderr=subprocess.STDOUT, env=env).returncode
            wall = time.time() - t0
            log = (d / "engine.log").read_text(errors="replace")
            winner = next((ln.split("winner")[1].split(":")[0].strip() for ln in log.splitlines() if "fit_mode 3: winner" in ln), "")
            rec = {"tag": tag, "instance": a.instance, "fit": fit, "seed": seed, "iters": a.iters, "rc": rc,
                   "wall_s": round(wall, 1), "winner": winner}
            try:
                m = evaluate_files(a.instance, str(d / "placements.json"), str(d), write_3dm=False, write_svg=True)
                rec.update({"sheets": m["n_sheets"], "util_per_sheet": [round(u, 1) for u in m["util_per_sheet"]],
                            "overlap_pairs": m["overlap_pairs"], "clearance_violations": m["clearance_violations"],
                            "placed": m["placed"], "total": m["total"],
                            "oob": m.get("oob_parts", m.get("out_of_bounds", None)),
                            "remnant_bbox_frac": m.get("remnant_bbox_frac")})
            except Exception as e:  # noqa: BLE001
                rec["error"] = str(e)
            recs.append(rec)
            json.dump(recs, open(res_path, "w"), indent=1)
            print(json.dumps(rec), flush=True)
    lines = ["| run | fit | seed | iters | sheets | placed | util per sheet % | overlaps | clearance viol | oob | remnant bbox | winner | wall s |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for r in recs:
        rb = r.get("remnant_bbox_frac")
        lines.append(f"| {r['tag']} | {r['fit']} | {r['seed']} | {r['iters']} | {r.get('sheets')} | {r.get('placed')}/{r.get('total')} | "
                     f"{' / '.join(str(u) for u in r.get('util_per_sheet', []))} | {r.get('overlap_pairs')} | "
                     f"{r.get('clearance_violations')} | {r.get('oob')} | {'' if rb is None else f'{100 * rb:.1f}%'} | {r.get('winner', '')} | {r['wall_s']} |")
    (out / "job_results.md").write_text("\n".join(lines) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
