# opennest_job

Headless OpenNest worker: one JSON job in, one JSON result out. It is the compute half of the planned
OpenNest web service: an API queues jobs, and a worker runs `opennest_job` once per job (in a container),
streams its progress events to the browser and returns the result.

It links the `nest_physics` solver directly (`../nest_physics_cpp/nest_physics_capi.cpp` is compiled into
the executable): the same C ABI the Grasshopper components call, standard library and threads only, so the
same source builds on Linux, Windows and macOS.

## Build

```sh
# Linux / macOS (static binary, runs on any x86-64 Linux)
cmake -S src/opennest_job -B build/opennest_job -DCMAKE_BUILD_TYPE=Release -DOPENNEST_JOB_STATIC=ON
cmake --build build/opennest_job -j

# Windows (MSVC)
cmake -S src/opennest_job -B build/opennest_job -A x64
cmake --build build/opennest_job --config Release

# Container (from the repository root)
docker build -f src/opennest_job/Dockerfile -t opennest-job:0.1.0 .
```

No `-march=native` and no fast-math: a binary built on one machine runs on any x86-64 server, and
deterministic (iteration-count) jobs give the same layout on Linux and Windows.

## Run

```sh
opennest_job job.json result.json [--events events.ndjson] [--events-layout] [--interval 2] [--max-wall 900] [--quiet]
opennest_job - -   < job.json > result.json        # stdin / stdout
```

| Option | Meaning |
| --- | --- |
| `--events <file>` | Write progress as one JSON object per line (see below). |
| `--events-layout` | Include the current best layout in every progress event (for a live preview). |
| `--interval <s>` | Seconds between progress events (default 2). |
| `--max-wall <s>` | Operator wall-clock cap. The job's `limits.wall_seconds` can only lower it. |
| `--cancel-file <path>` | Stop (keeping the best layout) as soon as this file exists; works on every OS. |

Exit codes: `0` ok or partial, `1` error (invalid job or solver failure), `2` usage, `124` cancelled.
SIGTERM / SIGINT (`docker stop`, Ctrl+C) and the wall cap stop the solver at its next round; the best
layout found so far is still written, with `"status": "cancelled"`.

The solver reads a few developer switches from the environment (`NP_*`, for example `NP_WORKERS`). A
service must start the worker with a clean environment and never pass user-supplied variables.

## Job: `opennest.job/1`

```json
{
  "schema": "opennest.job/1",
  "units": "mm",
  "spacing": 10,
  "margin": 15,
  "parts": [
    {"id": "A", "qty": 4, "rotations": 0,
     "outer": [[0, 0], [200, 0], [200, 100], [0, 100]],
     "holes": [[[50, 25], [150, 25], [150, 75], [50, 75]]]}
  ],
  "sheets": [
    {"id": "steel_3000x1500", "count": 5,
     "outer": [[0, 0], [3000, 0], [3000, 1500], [0, 1500]],
     "holes": []}
  ],
  "params": {"fit_mode": 0, "iter_mode": 1, "iter_budget": 4000, "seed": 1},
  "limits": {"wall_seconds": 600}
}
```

* Rings are `[[x, y], ...]`, clockwise or counter-clockwise, open (a closing duplicate point is ignored),
  at least 3 vertices, in the units of `units` (informational).
* `spacing` (minimum distance between parts) and `margin` (minimum distance from a part to a sheet edge
  or sheet hole) are applied here with round-joined offsets (Clipper2), like the Grasshopper intake:
  part outlines grow by spacing/2, part holes shrink by spacing/2, sheets shrink and sheet holes grow by
  margin - spacing/2. Leave both 0 when the geometry is already offset (the bench3dm instances are).
* `id` is any JSON value and is echoed back in the result. `qty` defaults to 1, `count` to 1.
* `rotations`: `0` = free continuous rotation, `N > 0` = only N orientations (360/N degree steps,
  `1` = fixed).
* Part `holes` may receive other parts when `part_holes_mode` is 1. Sheet `holes` are keep-out regions.
* Unknown keys are rejected, so a typo cannot silently fall back to a default.

`params` are the `NpParams` fields of `nest_physics_capi.h`; defaults are the Grasshopper component's
call:

| Key | Default | Notes |
| --- | --- | --- |
| `fit_mode` | 0 | 0 fewest sheets, 1 one sheet max fill, 2 global bin pack, 3 group-and-fill (best quality, slowest) |
| `iter_mode` | 1 | 1 deterministic iteration budget, 0 wall-clock `time_budget_secs` |
| `iter_budget` | 4000 | below 500 selects the fast preview profile |
| `time_budget_secs` | 0 | required when `iter_mode` is 0 |
| `num_rotations` | 3600 | orientations sampled per free part |
| `seed` | 0 | |
| `max_sheets` | total sheet count | |
| `n_starts` | 1 | multi-start |
| `part_holes_mode` | 1 | 0 keep part holes empty, 1 fill them |
| `pole_max` | 48 | |
| `final_compact` | 1 | |
| `n_threads` | 0 (= 4) | fit_mode 3 portfolio threads |
| `simplify_tolerance`, `spacing` | 0 | `params.spacing` is ignored by the solver: use the top-level `spacing` |

## Result: `opennest.result/1`

```json
{
 "schema": "opennest.result/1",
 "status": "ok",
 "engine": {"name": "nest_physics", "runner": "opennest_job 0.1.0", "platform": "linux", "compiler": "gcc 13.3.0"},
 "wall_s": 0.98, "rounds": 432, "n_sheets": 1, "sheets_used": [0],
 "quality": {"clean": true, "overlap_pairs": 0, "out_of_bounds": 0, "unplaced": 0, "demoted": 0},
 "frame": "...",
 "placements": [
  {"part": "A", "copy": 0, "sheet": 0, "sheet_id": "steel_3000x1500", "sheet_copy": 0,
   "tx": 251.72, "ty": 334.77, "angle": -0.937}
 ]
}
```

* `status`: `ok`, `partial` (finished, but some parts fit on no sheet), `cancelled` (with
  `cancel_reason`: `signal`, `wall_cap` or `solver`) or `error` (with `error`).
* One placement per part copy, in job order. `sheet` is the index among all sheet copies (sheet-major),
  `-1` when unplaced.
* Placing a part: rotate its input geometry about the origin by `angle` (radians,
  counter-clockwise), then translate by `(tx, ty)`. The result is in the coordinates the sheet was
  given in, so a sheet at (5000, 2000) gets parts around (5000, 2000).
* `clearance` echoes the spacing and margin that were applied.

## Progress events

With `--events`, one JSON object per line, flushed as written:

```
{"event":"start","t":0,"parts":23,"sheets":3,"fit_mode":0,"wall_cap":120}
{"event":"progress","t":2.0,"rounds":205,"placed":23,"n_sheets":1}
{"event":"cap","t":900.0}
{"event":"done","t":0.98,"status":"ok","n_sheets":1,"unplaced":0}
```

With `--events-layout`, each progress event also carries `"layout": [[sheet, tx, ty, angle], ...]` per part
copy, in job order.

## Benchmark

`tools/opennest_job_check.py job.json result.json` checks a result independently (shapely): parts
inside their sheets, at least `spacing` apart and `margin` from edges and keep-outs.

`tools/bench3dm/opennest_job_io.py` converts a bench3dm instance into a job and the result back into a
`placements.json` for `tools/bench3dm/evaluate.py`, so the worker can be checked against the existing
customer benchmarks.
