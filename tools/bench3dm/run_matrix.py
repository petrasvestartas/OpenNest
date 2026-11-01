"""Run a (config x seed) matrix of engine solves on one instance, evaluate each, and tabulate.

    python -I tools/bench3dm/run_matrix.py --instance <instance.json> --configs gh_np,gh_nfp \
        --seeds 100,101,102 --cap 900 --out <dir> [--dll-np <path>] [--dll-nfp <path>] [--repeat-check]

Each solve runs `engines.py` in its own subprocess (stdout+stderr of the process, which includes the
DLL's own prints, go to <out>/runs/<config>_s<seed>/engine_stdout.txt). The whole process tree is
force-killed (taskkill /F /T) at cap + 60 s if the cooperative cancel did not stop it. Each run is then
scored in-process with evaluate.evaluate_files (imported lazily; if it is missing or fails, the row is
written with the engine-side fields only and eval_error set).

Outputs:
  <out>/results.csv            one row per run (appended; header written once)
  <out>/best/<config>/         placements.json + *.svg + metrics.json of the best run per config
                               (best = fewest unplaced, then fewest defects, then fewest sheets, then
                               smallest remnant_used_width, then shortest wall)
  <out>/repeat_check.json      with --repeat-check: first seed run twice per config, instances compared
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import shutil
import statistics
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))   # -I drops the script dir from sys.path

import engines  # noqa: E402

CSV_FIELDS = ["engine", "config", "seed", "sheets", "util_per_sheet", "total_util", "remnant_used_width",
              "total_used_width", "overlaps", "clearance_violations", "oob_parts", "parts_in_holes",
              "placed", "total", "cancelled", "wall_s", "score", "dll_sha256", "cpu_count", "ts",
              # extras (after the fixed columns)
              "exit_code", "eval_error", "run_dir"]
HARD_KILL_GRACE_S = 60.0


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


def _kill_tree(p) -> None:
    """Force-kill a process AND all its descendants (research/nest_physics/research_loop.py pattern)."""
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True, timeout=60)
        else:
            os.killpg(os.getpgid(p.pid), __import__("signal").SIGKILL)
    except Exception:  # noqa: BLE001
        try:
            p.kill()
        except Exception:  # noqa: BLE001
            pass


def run_engine_subprocess(instance: str, config: str, seed: int, cap: float, run_dir: Path,
                          dll: str | None, sets: list) -> int:
    """One engines.py solve in a subprocess. Returns its exit code (124 = cap cancel or hard kill)."""
    run_dir.mkdir(parents=True, exist_ok=True)
    stale = run_dir / "placements.json"
    if stale.exists():
        stale.unlink()
    cmd = [sys.executable, "-I", str(HERE / "engines.py"), "--instance", instance, "--config", config,
           "--seed", str(seed), "--cap", str(cap), "--out", str(run_dir)]
    if dll:
        cmd += ["--dll", dll]
    for s in sets or []:
        cmd += ["--set", s]
    popen_kw = {}
    if os.name != "nt":
        popen_kw["start_new_session"] = True
    env = dict(os.environ, PYTHONUNBUFFERED="1")
    with open(run_dir / "engine_stdout.txt", "w", encoding="utf-8", errors="replace") as fo:
        fo.write("$ " + subprocess.list2cmdline(cmd) + "\n")
        fo.flush()
        p = subprocess.Popen(cmd, stdin=subprocess.DEVNULL, stdout=fo, stderr=subprocess.STDOUT, env=env,
                             **popen_kw)
        try:
            rc = p.wait(timeout=cap + HARD_KILL_GRACE_S)
        except subprocess.TimeoutExpired:
            _kill_tree(p)
            try:
                p.wait(timeout=30)
            except Exception:  # noqa: BLE001
                pass
            fo.write(f"\nHARD KILL after {cap + HARD_KILL_GRACE_S:.0f}s (cooperative cancel did not return)\n")
            rc = 124
    return rc


_EVAL = None


def _evaluate_files():
    global _EVAL
    if _EVAL is None:
        from evaluate import evaluate_files  # noqa: PLC0415  (lazy: written by the geometry half)
        _EVAL = evaluate_files
    return _EVAL


def _best_key(row: dict) -> tuple:
    def num(k, default=float("inf")):
        v = row.get(k)
        try:
            return float(v)
        except (TypeError, ValueError):
            return default
    unplaced = num("total", 0) - num("placed", 0)
    defects = num("overlaps", 0) + num("clearance_violations", 0) + num("oob_parts", 0)
    return (unplaced, defects, num("sheets"), num("remnant_used_width"), num("wall_s"))


def _fmt(v):
    if isinstance(v, float):
        return f"{v:.6g}"
    if isinstance(v, (list, tuple)):
        return ";".join(_fmt(x) for x in v)
    return "" if v is None else str(v)


def append_csv(path: Path, row: dict) -> None:
    new = not path.exists()
    with open(path, "a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=CSV_FIELDS, extrasaction="ignore")
        if new:
            w.writeheader()
        w.writerow({k: _fmt(row.get(k)) for k in CSV_FIELDS})


def one_run(a, cfg_name: str, engine: str, seed: int, run_dir: Path) -> tuple:
    dll = a.dll_np if engine == engines.ENGINE_NP else a.dll_nfp
    t0 = time.perf_counter()
    rc = run_engine_subprocess(a.instance, cfg_name, seed, a.cap, run_dir, dll, a.set)
    proc_wall = time.perf_counter() - t0
    pj = run_dir / "placements.json"
    doc = json.loads(pj.read_text(encoding="utf-8")) if pj.is_file() else None
    row = {"engine": engine, "config": cfg_name, "seed": seed, "cpu_count": os.cpu_count(),
           "ts": time.strftime("%Y-%m-%dT%H:%M:%S"), "exit_code": rc, "run_dir": str(run_dir),
           "cancelled": rc == 124 or bool(doc and doc.get("cancelled")),
           "wall_s": doc["wall_s"] if doc else round(proc_wall, 3),
           "dll_sha256": doc["dll"]["sha256"] if doc else ""}
    if doc:
        row["sheets"] = doc["n_sheets"]
        row["placed"] = sum(1 for i in doc["instances"] if i["sheet"] >= 0)
        row["total"] = len(doc["instances"])
        try:
            m = _evaluate_files()(a.instance, str(pj), str(run_dir), write_3dm=False, write_svg=True)
            (run_dir / "metrics.json").write_text(json.dumps(m, indent=1, default=str), encoding="utf-8")
            row.update({
                "sheets": m.get("n_sheets", row["sheets"]),
                "util_per_sheet": m.get("util_per_sheet"), "total_util": m.get("total_util"),
                "remnant_used_width": m.get("remnant_used_width"), "total_used_width": m.get("total_used_width"),
                "overlaps": m.get("overlap_pairs"), "clearance_violations": m.get("clearance_violations"),
                "oob_parts": m.get("oob_parts"), "parts_in_holes": m.get("parts_in_holes"),
                "placed": m.get("placed", row["placed"]), "total": m.get("total", row["total"]),
                "score": m.get("score"),
            })
        except Exception as e:  # noqa: BLE001
            row["eval_error"] = f"{type(e).__name__}: {e}"
    else:
        row["eval_error"] = "no placements.json"
    return row, doc


def summarize(rows: list) -> None:
    by = {}
    for r in rows:
        by.setdefault(r["config"], []).append(r)
    hdr = f"{'config':<14}{'engine':<10}{'runs':>5}{'sheets med':>11}{'min':>5}{'max':>5}" \
          f"{'best remnant':>14}{'wall med s':>11}{'defects':>9}{'unplaced':>9}{'cancel':>7}"
    print("\n" + hdr + "\n" + "-" * len(hdr))
    for cfg, rs in by.items():
        sheets = [float(r["sheets"]) for r in rs if r.get("sheets") not in (None, "")]
        rem = [float(r["remnant_used_width"]) for r in rs if r.get("remnant_used_width") not in (None, "")]
        walls = [float(r["wall_s"]) for r in rs if r.get("wall_s") not in (None, "")]
        defects = sum(int(r.get("overlaps") or 0) + int(r.get("clearance_violations") or 0)
                      + int(r.get("oob_parts") or 0) for r in rs)
        unpl = sum(int(r.get("total") or 0) - int(r.get("placed") or 0) for r in rs)
        canc = sum(1 for r in rs if r.get("cancelled"))
        f = lambda xs, fn: f"{fn(xs):g}" if xs else "-"  # noqa: E731
        print(f"{cfg:<14}{rs[0]['engine']:<10}{len(rs):>5}{f(sheets, statistics.median):>11}"
              f"{f(sheets, min):>5}{f(sheets, max):>5}{(f'{min(rem):.1f}' if rem else '-'):>14}"
              f"{f(walls, statistics.median):>11}{defects:>9}{unpl:>9}{canc:>7}")


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(description="Run a config x seed matrix of nesting solves.")
    ap.add_argument("--instance", required=True)
    ap.add_argument("--configs", default="gh_np,gh_nfp")
    ap.add_argument("--seeds", default="100,101,102")
    ap.add_argument("--cap", type=float, default=900.0)
    ap.add_argument("--out", required=True)
    ap.add_argument("--dll-np")
    ap.add_argument("--dll-nfp")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VALUE",
                    help="struct override passed to every run (must exist in every config's struct)")
    ap.add_argument("--repeat-check", action="store_true",
                    help="run the first seed twice per config and assert identical instances")
    a = ap.parse_args(argv)
    a.instance = str(Path(a.instance).resolve())
    out = Path(a.out).resolve()
    out.mkdir(parents=True, exist_ok=True)
    cfg_names = [c.strip() for c in a.configs.split(",") if c.strip()]
    seeds = [int(s) for s in a.seeds.split(",") if s.strip()]
    cfgs = {c: engines.resolve_config(c) for c in cfg_names}
    for e, d in ((engines.ENGINE_NP, a.dll_np), (engines.ENGINE_NFP, a.dll_nfp)):
        if any(c["engine"] == e for c in cfgs.values()):
            info = engines.find_dll(e, d)
            log(f"{e}: {info['path']} [{info['source']}] sha256={info['sha256'][:16]} mtime={info['mtime']}")
    log(f"instance {a.instance}; configs {cfg_names}; seeds {seeds}; cap {a.cap}s; cpu_count {os.cpu_count()}")

    rows, repeat = [], {}
    best: dict = {}
    for cfg_name in cfg_names:
        engine = cfgs[cfg_name]["engine"]
        for seed in seeds:
            run_dir = out / "runs" / f"{cfg_name}_s{seed}"
            log(f"run {cfg_name} seed {seed} ...")
            row, doc = one_run(a, cfg_name, engine, seed, run_dir)
            rows.append(row)
            append_csv(out / "results.csv", row)
            log(f"  exit={row['exit_code']} sheets={row.get('sheets')} placed={row.get('placed')}/{row.get('total')} "
                f"remnant={row.get('remnant_used_width')} wall={row['wall_s']}s score={row.get('score')}"
                + (f"  EVAL ERROR: {row['eval_error']}" if row.get("eval_error") else ""))
            if doc and (cfg_name not in best or _best_key(row) < _best_key(best[cfg_name])):
                best[cfg_name] = row
                bdir = out / "best" / cfg_name
                if bdir.exists():
                    shutil.rmtree(bdir)
                bdir.mkdir(parents=True)
                for f in list(run_dir.glob("*.svg")) + [run_dir / "placements.json", run_dir / "metrics.json"]:
                    if f.is_file():
                        shutil.copy2(f, bdir / f.name)
            if a.repeat_check and seed == seeds[0]:
                rdir = out / "runs" / f"{cfg_name}_s{seed}_repeat"
                log(f"repeat-check {cfg_name} seed {seed} ...")
                rc2 = run_engine_subprocess(a.instance, cfg_name, seed, a.cap, rdir,
                                            a.dll_np if engine == engines.ENGINE_NP else a.dll_nfp, a.set)
                p2 = rdir / "placements.json"
                doc2 = json.loads(p2.read_text(encoding="utf-8")) if p2.is_file() else None
                same = bool(doc and doc2 and json.dumps(doc["instances"], sort_keys=True)
                            == json.dumps(doc2["instances"], sort_keys=True))
                cancelled = bool((doc and doc.get("cancelled")) or (doc2 and doc2.get("cancelled")) or rc2 == 124)
                repeat[cfg_name] = {"seed": seed, "identical": same, "cancelled": cancelled, "exit_code": rc2}
                log(f"  repeat identical={same}" + ("  (a run was cancelled: timing-dependent)" if cancelled else ""))

    summarize(rows)
    log(f"results: {out / 'results.csv'}  best: {out / 'best'}")
    if a.repeat_check:
        (out / "repeat_check.json").write_text(json.dumps(repeat, indent=1), encoding="utf-8")
        bad = [c for c, r in repeat.items() if not r["identical"]]
        if bad:
            log(f"REPEAT CHECK FAILED for {bad}")
            return 2
        log("repeat check: all identical")
    return 0


if __name__ == "__main__":
    sys.exit(main())
