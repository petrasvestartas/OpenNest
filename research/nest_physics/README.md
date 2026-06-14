# nest_physics research / optimization loop

`research_loop.py` drives an AI coding agent over a long (default 24 h) session to study the
`src/nest_physics_cpp` solver **from all angles** and iteratively make it **faster** — improving
the **implementation efficiency** of each component (hot kernels, data layout, complexity,
allocations, SIMD) *without changing what it computes*. Density on the target instance is already
near‑optimal at 2 sheets, so **wall‑time is the objective and the packing is a hard guardrail**:
a kept change must never pack worse and must stay overlap‑free.

## What it does each iteration
1. **Snapshot** the engine source (so a bad experiment can be reverted — *no git commits*).
2. **Ask the agent** to either *study* one component (record technique + complexity + literature
   alternatives) or make *one* focused change.
3. If the agent changed C++: **rebuild** the CLI and **benchmark** it.
4. **Keep** only a valid, non‑regressing improvement; otherwise **restore** the snapshot.
5. Append to the **ledger** (`ledger.csv` + `ledger.jsonl`) so the loop accumulates knowledge.

The script — not the agent — owns build + benchmark + snapshot, so the "is it actually better?"
signal is trustworthy and the tree is never left broken.

## The benchmark
It builds the `nest_physics_cli` target and runs it on a fixed instance in **deterministic
iteration mode** (`1500i` — small enough for fast, stable repeated timing), parsing the CLI's own
output: `sheets` + total `used width` (the packing — a **guardrail** that must not regress),
`brute‑force overlaps` / `bounds violations` (must be 0), `all parts placed` (YES), and `wall
time` (**median of 5 runs** — the objective). A change is **accepted** only if it's valid, the
packing does **not** regress, and it is **faster by > 1.5%** (a real hot‑kernel win — jitter
won't clear it). A genuinely tighter / fewer‑sheet packing is also kept, but speed is the target.

## Run it

First a harness self‑test (no agent — just build + benchmark the baseline):
```bash
python research/nest_physics/research_loop.py --dry-run
```

Then the full loop (**review the agent command + autonomy first**):
```bash
python research/nest_physics/research_loop.py --hours 24
```

Key flags (`--help` for all):
- `--instance <path>` — parts file (`.svg` or CGSHOP `.json`). Default:
  `C:/pc/3_code/code_cpp/shadoks-CGSHOP2024/sample_polygons.svg`.
- `--budget 1500i` — deterministic iteration count (fast, reproducible, stable timing). Use `120` for seconds.
- `--hours 24`, `--max-iters N`.
- `--agent-cmd ...` — override the agent invocation (the prompt is piped on **stdin**).
- `--cmake-configure-args ...` — extra cmake configure args. **On a machine without MSVC**, build
  with MinGW/Ninja, e.g.: `--cmake-configure-args -G Ninja` (with `CC=gcc CXX=g++` in the env),
  or `-A x64` on Windows/MSVC. Pass it as ONE quoted string, e.g. `--cmake-configure-args="-G Ninja"`.

> **Pick a non-trivial instance.** `sample_polygons.svg` is the squished *display catalog* — all parts
> total ~17% of one sheet, so they fit trivially and there is nothing to optimize. For a real
> better/faster campaign point `--instance` at the actual CGSHOP parts geometry (the `.json` whose
> parts need ~2 sheets), e.g. a `parts_510x635.json` (read with the CLI's `÷100` scale).

## Prerequisites
- Python 3.8+, CMake, a C++ compiler.
- For the loop: the `claude` CLI installed and authenticated. The default agent command is
  `claude -p --dangerously-skip-permissions` (prompt on stdin) — it lets the agent edit and build
  autonomously for the day; review/adjust to taste.

## Output
- `ledger.csv` / `ledger.jsonl` — every study + experiment, with metrics and keep/revert reason.
- The **working tree** ends holding the *best* source state — review the diff and commit it yourself.

Generated/transient files (`.snap_*`, `build_research/`, `experiment.json`, `ledger.*`) are
git‑ignored via `.gitignore` in this folder.
