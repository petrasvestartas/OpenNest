#!/usr/bin/env python3
"""
nest_physics — one-day research / optimization loop.
====================================================

Drives an AI coding agent (Claude Code CLI, headless) over a long session to study the
`src/nest_physics_cpp` solver FROM ALL ANGLES and iteratively make it FASTER — improving the
IMPLEMENTATION EFFICIENCY of each component (hot kernels, data layout, complexity, allocations,
SIMD) WITHOUT changing what it computes — while guaranteeing it stays overlap-free and never
packs worse. (Density on the target instance is already near-optimal at 2 sheets, so the real
headroom is SPEED; packing is a hard guardrail, wall-time is the objective.)

Design — the script owns the *objective* parts, the agent owns the *creative* part:

  Each iteration:
    1. SNAPSHOT the engine source (so a bad experiment can be reverted — no git commits).
    2. Ask the agent (subprocess) to study ONE component/technique and make ONE focused change.
    3. If the agent changed C++: REBUILD the CLI and BENCHMARK it (deterministic iteration
       mode => reproducible quality; wall-time => speed; overlaps/bounds => validity).
    4. ACCEPT only a valid, non-regressing improvement; otherwise RESTORE the snapshot.
    5. Append everything to a LEDGER (ledger.csv + ledger.jsonl) so the loop accumulates
       knowledge across the day and never repeats itself.
    6. Repeat until the time budget (default 24 h) or the iteration cap is hit.

Why the script (not the agent) runs build+benchmark+git: so the "is it actually better?"
signal is trustworthy and the agent can never fool the score or leave the tree broken.

Run a harness self-test first (no agent, just baseline build+benchmark):
    python research_loop.py --dry-run

Then the full loop (review AGENT_CMD / autonomy flags first!):
    python research_loop.py --hours 24

Requirements: Python 3.8+, CMake, a C++ compiler, and (for the loop) the `claude` CLI
authenticated. Everything is configurable via flags — see `--help`.
"""

import argparse
import csv
import json
import os
import re
import shlex
import shutil
import statistics
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

# --------------------------------------------------------------------------------------
# Defaults (override on the command line)
# --------------------------------------------------------------------------------------
HERE = Path(__file__).resolve().parent
REPO = HERE.parents[1]                                   # .../OpenNest
ENGINE_DIR = REPO / "src" / "nest_physics_cpp"           # the solver we study
ENGINE_SRC_GLOBS = ("*.cpp", "*.hpp", "*.h")             # what to snapshot (source only)
ENGINE_SUBDIRS = ("", "geometry", "solver")              # source sub-folders
# The REAL CGSHOP parts (÷100 scale, ~2 sheets) — a meaningful packing challenge, NOT the squished
# sample_polygons.svg display catalog (which trivially fits on one sheet ~17%).
DEFAULT_INSTANCE = Path("C:/pc/3_code/code_cpp/shadoks-CGSHOP2024/parts_510x635.json")
DEFAULT_BUDGET = "1500i"     # deterministic iters; SMALLER => fast runs => more repeats => stabler timing.
                             # The hot kernels (overlap/sampler/evaluator) dominate at ANY budget, so a
                             # speedup measured at 1500i transfers to production iteration counts.
DEFAULT_WORKERS = "1"        # single worker => deterministic, comparable timing
DEFAULT_POLES = "16"
TIMING_REPEATS = 5           # median wall-time over N runs — IMPLEMENTATION speed IS the objective in
                             # efficiency mode, so measure it stably (idle machine + deterministic workload).
NOISE = 0.015                # 1.5% wall-time band: a real hot-kernel speedup clears it; run-to-run jitter doesn't.
WIDTH_EPS = 0.5              # used-width change (sheet units) under this = "same packing" (FP-noise tolerant)
SPEED_CAP = 0.15             # a TIGHTER pack is kept only if it's <= this fraction slower than the best
                             # (tighter AND fast — reject density gains that blow the speed budget). Fewer
                             # sheets is exempt (a whole sheet of material is worth a slowdown).

# The agent command (a STRING, run via the shell so the `claude` .cmd/.ps1 shim resolves on Windows).
# Prompt is fed on STDIN (avoids OS arg-length limits). REVIEW THIS:
# `--dangerously-skip-permissions` lets the agent edit/build autonomously for a day.
DEFAULT_AGENT_CMD = "claude -p --dangerously-skip-permissions"


def log(msg: str) -> None:
    print(f"[{datetime.now().strftime('%H:%M:%S')}] {msg}", flush=True)


def _kill_tree(p):
    """Force-kill a process AND all its descendants.

    The agent (`claude` via a shell) spawns grandchildren; on Windows a plain timeout/kill only
    reaps the shell, orphaning the grandchildren with the stdout pipe still open — so the parent's
    communicate() blocks forever and the whole loop wedges (observed: hung 2.5 h on one iteration).
    taskkill /T /F (Windows) / killpg (POSIX) tears down the entire tree.
    """
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)],
                           capture_output=True, timeout=60)
        else:
            os.killpg(os.getpgid(p.pid), __import__("signal").SIGKILL)
    except Exception:
        try:
            p.kill()
        except Exception:
            pass


def run(cmd, cwd=None, timeout=None, stdin_text=None, check=False, shell=False):
    """Thin subprocess wrapper returning (returncode, stdout+stderr).

    Uses Popen + a hard process-tree kill on timeout so a hung agent can never wedge the loop.
    """
    popen_kw = dict(cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT, text=True, shell=shell)
    if os.name != "nt":
        popen_kw["start_new_session"] = True   # own process group => killpg reaps the tree
    p = subprocess.Popen(cmd, **popen_kw)
    try:
        out, _ = p.communicate(input=stdin_text, timeout=timeout)
    except subprocess.TimeoutExpired:
        _kill_tree(p)
        try:
            out, _ = p.communicate(timeout=30)   # drain after the tree is dead
        except Exception:
            out = ""
        return 124, f"TIMEOUT after {timeout}s\n{out or ''}"
    out = out or ""
    if check and p.returncode != 0:
        raise RuntimeError(f"command failed ({p.returncode}): {cmd}\n{out[-2000:]}")
    return p.returncode, out


# --------------------------------------------------------------------------------------
# Build + benchmark the nest_physics CLI
# --------------------------------------------------------------------------------------
class Harness:
    def __init__(self, args):
        self.args = args
        self.build_dir = ENGINE_DIR / "build_research"
        self.exe = None

    def configure_once(self):
        cfg = ["cmake", "-S", str(ENGINE_DIR), "-B", str(self.build_dir),
               "-DCMAKE_BUILD_TYPE=Release", "-DNEST_PHYSICS_BUILD_CLI=ON"]
        cfg += shlex.split(self.args.cmake_configure_args)
        log(f"cmake configure: {' '.join(cfg)}")
        rc, out = run(cfg, timeout=600)
        if rc != 0:
            raise RuntimeError(f"cmake configure failed:\n{out[-3000:]}")

    def build(self):
        # CMake target is `nest_physics_cli` (NEST_PHYSICS_BUILD_CLI=ON); its OUTPUT_NAME is "nest_physics".
        bld = ["cmake", "--build", str(self.build_dir), "--config", "Release",
               "--target", "nest_physics_cli"] + shlex.split(self.args.cmake_build_args)
        rc, out = run(bld, timeout=1800)
        if rc != 0:
            return False, out[-3000:]
        self.exe = self._find_exe()
        return (self.exe is not None), ("" if self.exe else "built exe not found")

    def _find_exe(self):
        names = ["nest_physics.exe", "nest_physics"]
        for sub in ("", "Release"):
            for n in names:
                p = self.build_dir / sub / n
                if p.exists():
                    return p
        # fall back to a recursive search
        for n in names:
            hits = list(self.build_dir.rglob(n))
            if hits:
                return hits[0]
        return None

    def _run_once(self, out_svg):
        cmd = [str(self.exe), str(self.args.instance), str(out_svg),
               self.args.budget, self.args.workers, self.args.poles]
        rc, out = run(cmd, timeout=self.args.run_timeout)
        return rc, out

    @staticmethod
    def parse(out: str):
        """Pull the metrics the CLI prints; returns None if the run didn't produce a result."""
        m = {}
        r = re.search(r"(\d+)\s+sheets?\s+for\s+\d+\s+parts", out)
        if not r:
            return None
        m["sheets"] = int(r.group(1))
        r = re.search(r"bounds violations\s*=\s*(\d+)\s*\|\s*brute-force overlaps\s*=\s*(\d+)\s*\|\s*all parts placed:\s*(YES|NO)", out)
        if not r:
            return None
        m["bounds"] = int(r.group(1))
        m["overlaps"] = int(r.group(2))
        m["all_placed"] = (r.group(3) == "YES")
        r = re.search(r"total material\s*=\s*([\d.]+)%.*?wall time\s*([\d.]+)s", out, re.S)
        m["material"] = float(r.group(1)) if r else 0.0   # parts/sheet area ratio — CONSTANT (display only)
        m["wall_time"] = float(r.group(2)) if r else float("nan")
        # The real density signal: total used width across the sheets (lower = tighter pack).
        widths = re.findall(r"used width\s+([\d.]+)\s*/", out)
        m["used_width"] = round(sum(float(w) for w in widths), 2)
        return m

    def benchmark(self):
        """Quality from one deterministic run; wall-time as the median of TIMING_REPEATS runs."""
        out_svg = self.build_dir / "bench_out.svg"
        rc, out = self._run_once(out_svg)
        base = self.parse(out)
        if base is None:
            return None, out[-2000:]
        times = [base["wall_time"]]
        for _ in range(max(0, TIMING_REPEATS - 1)):
            _, o2 = self._run_once(out_svg)
            mm = self.parse(o2)
            if mm:
                times.append(mm["wall_time"])
        base["wall_time"] = round(statistics.median(times), 3)
        base["valid"] = (base["overlaps"] == 0 and base["bounds"] == 0 and base["all_placed"])
        return base, out[-1200:]


def quality_key(m):
    # smaller is better: fewer sheets first, then tighter (less total used width)
    return (m["sheets"], m["used_width"])


def is_improvement(new, best):
    """Accept a valid Pareto step: never worse packing, and (tighter packing OR faster)."""
    if not new["valid"]:
        return False, "invalid (overlap / bounds / unplaced)"
    if new["sheets"] < best["sheets"]:
        return True, f"FEWER sheets ({best['sheets']} -> {new['sheets']})"
    if new["sheets"] > best["sheets"]:
        return False, "more sheets"
    # same sheet count -> tighter (less total used width) is better packing, BUT only keep it if it stays
    # within the speed budget: a tighter pack more than SPEED_CAP slower than the best is rejected, so the
    # loop pursues "tighter AND fast" rather than stacking slow compaction passes for marginal density.
    if new["used_width"] < best["used_width"] - WIDTH_EPS:
        slow = new["wall_time"] / best["wall_time"] - 1.0 if best["wall_time"] > 0 else 0.0
        if new["wall_time"] <= best["wall_time"] * (1.0 + SPEED_CAP):
            return True, f"tighter ({best['used_width']:.1f} -> {new['used_width']:.1f} used width, {slow*100:+.0f}% time)"
        return False, f"tighter but too slow ({slow*100:+.0f}% > {SPEED_CAP*100:.0f}% cap)"
    if new["used_width"] > best["used_width"] + WIDTH_EPS:
        return False, "looser packing"
    # same packing -> require a real speedup
    if new["wall_time"] < best["wall_time"] * (1.0 - NOISE):
        return True, f"faster ({best['wall_time']:.2f}s -> {new['wall_time']:.2f}s, same packing)"
    return False, "no measurable gain"


# --------------------------------------------------------------------------------------
# Snapshot / restore the engine source (revert bad experiments WITHOUT git commits)
# --------------------------------------------------------------------------------------
def snapshot(dest: Path):
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    for sub in ENGINE_SUBDIRS:
        src = ENGINE_DIR / sub if sub else ENGINE_DIR
        tgt = dest / sub if sub else dest
        tgt.mkdir(parents=True, exist_ok=True)
        for pat in ENGINE_SRC_GLOBS:
            for f in src.glob(pat):
                if f.is_file():
                    shutil.copy2(f, tgt / f.name)


def restore(src: Path):
    for sub in ENGINE_SUBDIRS:
        s = src / sub if sub else src
        t = ENGINE_DIR / sub if sub else ENGINE_DIR
        if not s.exists():
            continue
        for f in s.glob("*"):
            if f.is_file():
                shutil.copy2(f, t / f.name)


def engine_changed_since(snap: Path) -> bool:
    """Did the agent touch any engine source file relative to the snapshot?"""
    for sub in ENGINE_SUBDIRS:
        live = ENGINE_DIR / sub if sub else ENGINE_DIR
        snd = snap / sub if sub else snap
        for pat in ENGINE_SRC_GLOBS:
            for f in live.glob(pat):
                ref = snd / f.name
                if (not ref.exists()) or f.read_bytes() != ref.read_bytes():
                    return True
    return False


# --------------------------------------------------------------------------------------
# Ledger
# --------------------------------------------------------------------------------------
class Ledger:
    FIELDS = ["iter", "ts", "type", "component", "summary", "kept", "reason",
              "sheets", "used_width", "wall_time"]

    def __init__(self, d: Path):
        self.csv = d / "ledger.csv"
        self.jsonl = d / "ledger.jsonl"
        self.rows = []
        if self.jsonl.exists():
            self.rows = [json.loads(l) for l in self.jsonl.read_text().splitlines() if l.strip()]
        if not self.csv.exists():
            with open(self.csv, "w", newline="") as f:
                csv.DictWriter(f, self.FIELDS).writeheader()

    def add(self, entry: dict):
        entry["ts"] = datetime.now(timezone.utc).isoformat(timespec="seconds")
        self.rows.append(entry)
        with open(self.jsonl, "a") as f:
            f.write(json.dumps(entry) + "\n")
        with open(self.csv, "a", newline="") as f:
            csv.DictWriter(f, self.FIELDS).writerow({k: entry.get(k, "") for k in self.FIELDS})

    def recent_summary(self, n=25) -> str:
        out = []
        for e in self.rows[-n:]:
            tag = "KEPT" if e.get("kept") else ("study" if e.get("type") == "study" else "reverted")
            out.append(f"  #{e.get('iter')} [{tag}] {e.get('component', '?')}: {e.get('summary', '')[:140]}")
        return "\n".join(out) if out else "  (none yet)"

    def studied_components(self):
        return sorted({e.get("component", "") for e in self.rows if e.get("component")})


# --------------------------------------------------------------------------------------
# The research prompt the agent receives every iteration
# --------------------------------------------------------------------------------------
PROMPT = r"""
You are an algorithm-engineering agent improving the C++ 2D irregular nesting solver in
`src/nest_physics_cpp/` (a C++ port of the `sparrow` strip-packing heuristic on a `jagua-rs`-style
geometry layer).

CAMPAIGN GOAL = PACK TIGHTER and run FASTER. Two real objectives:
  1. TIGHTER PACKING (primary) — fewer 510x635 sheets for the same parts, and at equal sheet count
     a smaller TOTAL USED WIDTH across the sheets (a denser layout). This is ALGORITHMIC: you MAY
     change how the solver places / compacts / searches, as long as the result stays overlap-free.
  2. FASTER (secondary) — less wall time. Implementation-efficiency wins (SIMD, data layout, fewer
     allocations, better complexity) are still welcome and kept.
The harness rebuilds + benchmarks in DETERMINISTIC iteration mode and keeps your change only if it
is VALID (overlaps=0, bounds=0, all placed) AND it packs tighter (fewer sheets, or lower used width)
OR it is faster — and NEVER worse on packing. A genuine density gain is kept even if a little slower;
aim for both, and avoid changes that pack tighter but are massively slower.

KEY RESEARCH QUESTION (from the project lead — pursue this): sparrow packs into an OPEN STRIP and
shrinks the strip WIDTH — it compacts along essentially ONE edge/direction. But we pack into a FIXED
510x635 sheet, so single-edge strip-shrinking may not be the tightest strategy. INVESTIGATE AND TRY
OTHER COMPACTION / PACKING DIRECTIONS:
  - corner-anchored packing: pull parts toward a corner (bottom-left-fill), or try all 4 corners and
    keep the best.
  - compaction from ALL edges, or "gravity" toward a point (a corner / the centre) instead of one axis.
  - multi-directional slide-to-contact: after relaxation, slide each part along several directions
    (+/-x, +/-y, diagonals) until it touches a neighbour, closing gaps strip-shrink leaves behind.
  - shrink the OTHER dimension (height) too, or alternate width/height; or pack the fixed rectangle
    directly as a 2D bin rather than a 1D strip.
  - iterative SHAKING / perturb-and-recompact: ruin a subset of placements and re-insert them; random
    kicks + re-compaction (large-neighbourhood search / simulated-annealing-style diversification) to
    escape the local optimum a single-direction compaction gets stuck in.
  - extra post-relaxation compaction passes that specifically detect and close residual voids.
Per CHANGE iteration pick ONE such idea, implement it minimally and locally, and let the harness
measure whether it really packs tighter.

THIS iteration, do exactly ONE of:
  (A) STUDY a component you have NOT studied yet (map below): how it places/compacts/searches, plus
      2-3 concrete alternative STRATEGIES (from the directions above + the packing literature) that
      could pack tighter or converge in fewer rounds. Write findings to
      research/nest_physics/experiment.json. No code change — a sharp analysis is valuable.
  (B) CHANGE: implement ONE focused strategy/algorithm change (or an implementation-speed win), and
      let the harness judge it on tightness + speed.

SELF-CHECK (encouraged): you MAY temporarily add timers/counters or dump intermediate layouts to
confirm a hypothesis, then run the CLI yourself — but REMOVE all temporary instrumentation before you
finish, or the measured result is contaminated.

COMPONENT MAP (current technique in parens):
  - solver/driver.hpp          greedy_fill / run_strip orchestration, STRIP-WIDTH control, multi-start
                               <== the single-edge strip-shrink strategy lives here
  - solver/optimizer.hpp       InitialPlacer (LBF), separator/relax loop, explore + compress phases
  - solver/sampler.hpp         placement search: uniform bbox random samples + coordinate descent
  - solver/overlap.hpp         collision "loss" = sum pole-pair penetration depth (SIMD), GLS weights
  - solver/evaluator.hpp       the per-candidate cost
  - geometry/surrogate.hpp     part -> inscribed "poles" (Poles of Inaccessibility, biggest-first)
  - geometry/quadtree.hpp      broad-phase collision (quadtree over surrogate circles)
  - geometry/collision_engine.hpp  fail-fast pole tests + exact polygon overlap fallback
  - geometry/polygon.hpp / shape*.hpp  polygon ops, bbox, area, convex hull
  - solver/rng.hpp, constants.hpp, config.hpp   randomness, tuning constants

LEVERS:
  PACK TIGHTER: alternative compaction directions (above); better placement search (no-fit-polygon
    sliding, bottom-left-fill, low-discrepancy/Sobol sampling vs uniform random); metaheuristic
    diversification (reheating/restart pools, tabu, ILS, ruin-and-recreate) to beat the local optimum;
    smarter part ordering / rotation choice; tighter strip-width schedule.
  FASTER: SoA pole layout, AVX2 penetration kernel, incremental overlap update, buffer reuse,
    squared-distance early-outs, quadtree/broad-phase tuning, build flags.

HARD CONSTRAINTS (a violation = the harness reverts you, wasted iteration):
  - Stay overlap-free: `overlaps = 0`, `bounds = 0`, `all parts placed: YES`. Never trade correctness
    for density.
  - Keep it building (the harness compiles the `nest_physics` CLI target).
  - Do NOT change the C ABI in `nest_physics_capi.{h,cpp}` or break `driver.hpp`'s shared entry.
  - Deterministic: quality is measured in deterministic iteration mode; don't add nondeterminism the
    harness can't reproduce.
  - Remove any temporary instrumentation before finishing. Do NOT run `git commit` / `git push`.

OUTPUT (always): write `research/nest_physics/experiment.json`:
  {"type": "study" | "change",
   "component": "<file or area you worked on>",
   "summary": "<one line: what you studied or changed>",
   "strategy": "<the packing/compaction/search strategy involved>",
   "hypothesis": "<why it should pack tighter and/or faster>",
   "alternatives": ["<other strategy idea>", "..."],
   "files_touched": ["..."]}

The harness appends the live BASELINE metrics and the recent LEDGER below so you don't repeat work.
Pick the highest-leverage next step toward TIGHTER packing (and speed).
"""


def build_iteration_prompt(best, ledger: Ledger) -> str:
    studied = ledger.studied_components()
    ctx = [
        PROMPT,
        "\n----- LIVE BASELINE (current best, deterministic iteration mode) -----",
        f"  sheets = {best['sheets']}   used_width = {best['used_width']:.1f}   <== PACK TIGHTER: fewer sheets, "
        f"then lower used width (this is the primary objective)",
        f"  wall_time = {best['wall_time']:.2f}s   <== and FASTER (median of {TIMING_REPEATS} runs; "
        f"> {NOISE*100:.1f}% to count as a speedup)   valid = {best['valid']}",
        f"  A change is KEPT if it stays overlap-free AND: FEWER sheets (any speed), OR tighter used width that "
        f"is at most {SPEED_CAP*100:.0f}% slower than the best (a tighter pack that blows the speed budget is "
        f"REJECTED), OR faster at the same packing. So pursue tighter AND fast — cheap compaction, not slow passes.",
        f"  instance = {DEFAULT_INSTANCE.name}   budget = {DEFAULT_BUDGET}   workers = {DEFAULT_WORKERS}   "
        f"poles = {DEFAULT_POLES}",
        "\n----- ALREADY STUDIED -----",
        ("  " + ", ".join(studied)) if studied else "  (nothing yet)",
        "\n----- RECENT LEDGER -----",
        ledger.recent_summary(),
        "\nNow do ONE study or ONE change, then write research/nest_physics/experiment.json.",
    ]
    return "\n".join(ctx)


def read_experiment(d: Path):
    f = d / "experiment.json"
    if not f.exists():
        return {}
    try:
        return json.loads(f.read_text())
    finally:
        try:
            f.unlink()
        except OSError:
            pass


# --------------------------------------------------------------------------------------
# Main loop
# --------------------------------------------------------------------------------------
def main():
    ap = argparse.ArgumentParser(description="nest_physics one-day research/optimization loop.")
    ap.add_argument("--instance", type=Path, default=DEFAULT_INSTANCE, help="parts instance (.svg or .json)")
    ap.add_argument("--budget", default=DEFAULT_BUDGET, help="solver budget, e.g. '3000i' (iters) or '120' (seconds)")
    ap.add_argument("--workers", default=DEFAULT_WORKERS)
    ap.add_argument("--poles", default=DEFAULT_POLES)
    ap.add_argument("--hours", type=float, default=24.0, help="wall-clock budget for the whole loop")
    ap.add_argument("--max-iters", type=int, default=100000)
    ap.add_argument("--run-timeout", type=int, default=900, help="per CLI run timeout (s)")
    ap.add_argument("--agent-timeout", type=int, default=3600, help="per agent iteration timeout (s)")
    ap.add_argument("--agent-cmd", default=None,
                    help="agent command STRING, run via the shell with the prompt piped on stdin "
                         "(default: 'claude -p --dangerously-skip-permissions')")
    ap.add_argument("--cmake-configure-args", default="",
                    help='extra cmake configure args as ONE quoted string, e.g. --cmake-configure-args="-G Ninja" or "-A x64"')
    ap.add_argument("--cmake-build-args", default="")
    ap.add_argument("--dry-run", action="store_true", help="just build + benchmark the baseline, no agent")
    args = ap.parse_args()

    agent_cmd = args.agent_cmd if args.agent_cmd else DEFAULT_AGENT_CMD

    if not args.instance.exists():
        log(f"WARNING: instance not found: {args.instance} (set --instance). Continuing — the CLI may "
            f"fall back to its built-in default path.")

    harness = Harness(args)
    harness.configure_once()

    log("Building + benchmarking the BASELINE…")
    ok, err = harness.build()
    if not ok:
        log(f"baseline build FAILED:\n{err}")
        sys.exit(1)
    best, raw = harness.benchmark()
    if best is None:
        log(f"baseline benchmark produced no parseable result:\n{raw}")
        sys.exit(1)
    log(f"BASELINE: {best['sheets']} sheets, used_width {best['used_width']:.1f}, "
        f"{best['wall_time']:.2f}s, valid={best['valid']}")
    if args.dry_run:
        log("dry-run complete (harness works).")
        return

    ledger = Ledger(HERE)
    ledger.add({"iter": 0, "type": "baseline", "component": "-", "summary": "baseline measured",
                "kept": True, "reason": "baseline", **{k: best[k] for k in ("sheets", "used_width", "wall_time")}})

    snap_best = HERE / ".snap_best"      # the accepted (best) source state
    snap_try = HERE / ".snap_try"        # snapshot before each experiment, for revert
    snapshot(snap_best)

    deadline = time.time() + args.hours * 3600
    it = 0
    while time.time() < deadline and it < args.max_iters:
        it += 1
        remaining_h = (deadline - time.time()) / 3600
        log(f"=== iteration {it} ({remaining_h:.1f} h left) — best {best['sheets']}sh "
            f"w{best['used_width']:.1f} {best['wall_time']:.2f}s ===")

        snapshot(snap_try)
        prompt = build_iteration_prompt(best, ledger)
        rc, out = run(agent_cmd, cwd=str(REPO), timeout=args.agent_timeout, stdin_text=prompt, shell=True)
        exp = read_experiment(HERE)
        comp = exp.get("component", "?")
        summary = exp.get("summary", "(agent wrote no experiment.json)")

        if not engine_changed_since(snap_try):
            # study-only iteration (or the agent made no edit) — log and move on
            ledger.add({"iter": it, "type": exp.get("type", "study"), "component": comp,
                        "summary": summary, "kept": False, "reason": "no code change",
                        "sheets": "", "used_width": "", "wall_time": ""})
            log(f"  study/no-change: {comp}: {summary[:120]}")
            continue

        ok, err = harness.build()
        if not ok:
            log(f"  build FAILED -> revert. {err[:300]}")
            restore(snap_best)
            ledger.add({"iter": it, "type": "change", "component": comp, "summary": summary,
                        "kept": False, "reason": "build failed", "sheets": "", "used_width": "", "wall_time": ""})
            continue

        cand, raw = harness.benchmark()
        if cand is None:
            log(f"  benchmark unparseable -> revert. {raw[:200]}")
            restore(snap_best)
            ledger.add({"iter": it, "type": "change", "component": comp, "summary": summary,
                        "kept": False, "reason": "no result", "sheets": "", "used_width": "", "wall_time": ""})
            continue

        better, reason = is_improvement(cand, best)
        entry = {"iter": it, "type": "change", "component": comp, "summary": summary,
                 "kept": better, "reason": reason,
                 "sheets": cand["sheets"], "used_width": cand["used_width"],
                 "wall_time": cand["wall_time"]}
        if better:
            best = cand
            snapshot(snap_best)             # this state is the new best
            log(f"  KEPT ({reason}). new best: {best['sheets']}sh w{best['used_width']:.1f} {best['wall_time']:.2f}s")
        else:
            restore(snap_best)              # roll back to the last accepted state
            log(f"  reverted ({reason}). cand was {cand['sheets']}sh w{cand['used_width']:.1f} {cand['wall_time']:.2f}s")
        ledger.add(entry)

    log(f"loop finished after {it} iterations. "
        f"best: {best['sheets']} sheets, used_width {best['used_width']:.1f}, {best['wall_time']:.2f}s. "
        f"Ledger: {ledger.csv}")
    log("The current working tree holds the BEST source state — review the diff, then commit it yourself.")


if __name__ == "__main__":
    main()
