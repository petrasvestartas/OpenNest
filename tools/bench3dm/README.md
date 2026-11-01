# tools/bench3dm — headless nesting benchmark

Converts a Rhino job (`.3dm` parts + sheet) into a JSON instance, runs the two OpenNest C++
engines headlessly through their C ABIs (exactly as the Grasshopper components call them), and
scores the layouts with an independent geometric evaluator.

| file | role |
|---|---|
| `rhino3dm_to_instance.py`, `bench_geom.py`, `rhino_sampling.py` | `.3dm` → `instance.json` (+ `instance.nfp.txt` for `nfp_bench`) |
| `engines.py` | ctypes driver for `nest_physics.dll` (`np_nest`) and `nfp_nest.dll` (`nfp_nest`) → `placements.json` |
| `configs.json` | named engine configs (`gh_np`, `gh_nfp`, `np_rot360`, `np_quality`, `np_fit2`, `nfp_gens40`, `nfp_compact`) |
| `evaluate.py` | `instance.json` + `placements.json` → metrics, `result.svg`, optional `.3dm` |
| `reference_eval.py` | scores a hand-made / GH reference layout in the same metric |
| `run_matrix.py` | config × seed matrix → `results.csv`, `best/<config>/`, summary table |
| `test_engines.py` | synthetic smoke + determinism test of both engines |

Run everything from the repo root with `python -I` (isolated mode; the scripts add their own folder
to `sys.path`). Python 3.14 with numpy + shapely (+ rhino3dm for the converter).

## Spacing semantics

`--clearance 10 --margin 15` reproduces the GH setup **Geometry offset 5 + Sheets margin 10**:

* **clearance** = minimum gap between two parts. Every part is offset outward by `clearance/2 = 5`
  (the GH Geometry offset) into `parts[i].solve` (holes shrink by the same 5), and the engines pack the
  offset rings touching each other → real gap 10. The offset uses **round joins** (`--offset-join round`,
  the default, = what `nest_geo.offset_nesting_boundary` does in the plugin): that is the exact clearance
  region (Minkowski sum with a disk of radius 5). A mitre offset sticks out up to ~5 mm further at convex
  corners (mitre limit 2), i.e. more than the requested gap; `--offset-join mitre` keeps the old behaviour
  for A/B. Fillet chords stay within `round_sagitta_target(d) = clamp(0.004·d, 0.01, 0.05)` mm of the true
  arc (0.02 at d = 5; shapely `quad_segs` from `bench_geom.round_quad_segs`, 14 at d = 5), vertices on the
  arc. The join style is recorded in `instance.json["sampling"]["offset_join"]`. `evaluate.py` checks
  clearance on the UN-offset rings, so it is independent of the join style.
* **margin** = minimum distance from a part to the sheet edge. The solve sheet is the real sheet inset
  by `solve_sheet.inset = margin − clearance/2 = 10` (the GH Sheets margin) so an offset ring touching
  the solve-sheet edge leaves 5 + 10 = 15 of material.
* The engines get `spacing = 0` / `sheetSpacing = 0`: all spacing is baked into the geometry
  (`NpParams.spacing` is ignored by the engine anyway).

Placement frame (both engines, in `placements.json`):
`p_world = sheet_world_origin[sid] + (inset, inset) + R(angle_rad) * p_local + (tx, ty)`, with
`sheet_world_origin[sid] = world_origin + (0, sid * pitch_y)` and `p_local` in the part's local frame
(first un-offset outer vertex at the origin). `np_nest` returns radians; `nfp_nest` degrees (converted).
Unplaced instances have `"sheet": -1`.

## DLL discovery order

For each engine (`find_dll` in `engines.py`), first hit wins:

1. explicit path: `engines.py --dll <path>` / `run_matrix.py --dll-np <path> --dll-nfp <path>`
   (a missing explicit path is an error, never a silent fallback)
2. env var `BENCH3DM_NP_DLL` / `BENCH3DM_NFP_DLL` (same rule)
3. `tools/bench3dm/out/dll_baseline/nest_physics.dll` / `nfp_nest.dll`
4. `src/nest_physics_cpp/build/Release/nest_physics.dll` / `src/opennest_cpp/build/Release/nfp_nest.dll`

The resolved path, sha256 and mtime are written into every `placements.json` and CSV row. Both DLLs
are cdecl, static CRT (no VC redist), loaded by absolute path.

## Commands

### 1. Self-test (struct layout 80/144 bytes + offsets, both DLLs load, all symbols, configs resolve)
```
python -I tools/bench3dm/engines.py --selftest
python -I tools/bench3dm/test_engines.py
```

### 2. Convert the Rhino job
```
python -I tools/bench3dm/rhino3dm_to_instance.py --parts "tools/bench3dm/data/job_2026-10-17/Geometry.3dm" --sheet "tools/bench3dm/data/job_2026-10-17/Sheet.3dm" --margin 15 --clearance 10 --sheets 10 --sheet-origin 20955.72 6734.621 --sheet-pitch 2200 --no-merge-mirrored --out tools/bench3dm/out/job1
```
→ `tools/bench3dm/out/job1/instance.json` (+ `instance.nfp.txt`).

`out/job1_fixed` (mitre, before the round-join change) and `out/job1_fixed_round` (round) were built with
```
python -I -X utf8 tools/bench3dm/rhino3dm_to_instance.py --parts tools/bench3dm/data/job_2026-10-17/Geometry.3dm --sheet tools/bench3dm/data/job_2026-10-17/Sheet.3dm --margin 15 --clearance 10 --sheets 10 --sheet-origin 20955.72 6734.621 --sheet-pitch 2200 --no-merge-duplicates --no-merge-mirrored --sagitta-abs 0.02 --offset-join round --out tools/bench3dm/out/job1_fixed_round
```
(`--offset-join mitre` reproduces `job1_fixed`: 10976 solve vertices; round: 18447.)

### 3. Round trip (identity placement back into world → `.3dm`, checks the frame math)
```
python -I tools/bench3dm/evaluate.py --instance tools/bench3dm/out/job1/instance.json --identity --3dm --out tools/bench3dm/out/job1/roundtrip
```

### 4. Reference layout (the existing GH result, scored with the same metric)
```
python -I tools/bench3dm/reference_eval.py --instance tools/bench3dm/out/job1/instance.json --layout "tools/bench3dm/data/job_2026-10-17/Best solution.3dm" --sheet-grid 20955.72 6734.621 6000 2000 2200 5 --out tools/bench3dm/out/job1/reference
```

### 5. Single engine run
```
python -I tools/bench3dm/engines.py --instance tools/bench3dm/out/job1/instance.json --config gh_np  --seed 100 --cap 900 --out tools/bench3dm/out/job1/np_s100
python -I tools/bench3dm/engines.py --instance tools/bench3dm/out/job1/instance.json --config gh_nfp --seed 30  --cap 900 --out tools/bench3dm/out/job1/nfp_s30
```
Options: `--dll <path>`, `--set key=value` (any `NpParams` / `NfpParams` field, or `sheets`;
repeatable, e.g. `--set iter_budget=20000 --set fit_mode=2`), `--progress` (engine progress on
stderr every 15 s). Exit codes: `0` ok, `1` failure, `124` stopped by the `--cap` timer (the
cooperative `np_cancel` / `nfp_cancel` fired; `placements.json` holds the best-so-far layout and
`"cancelled": true`). The DLLs' own prints go to this process' stdout/stderr — `run_matrix.py`
captures them per run into `engine_stdout.txt`.

Note: `np_nest` with `iter_mode 1` and `iter_budget < 500` switches to a fast PREVIEW profile —
never benchmark below 500 (the driver warns).

### 6. Evaluate one run
```
python -I tools/bench3dm/evaluate.py --instance tools/bench3dm/out/job1/instance.json --placements tools/bench3dm/out/job1/np_s100/placements.json --out tools/bench3dm/out/job1/np_s100
```

### 7. Matrix
```
python -I tools/bench3dm/run_matrix.py --instance tools/bench3dm/out/job1/instance.json --configs gh_np,gh_nfp --seeds 100,101,102 --cap 900 --out tools/bench3dm/out/job1/matrix --repeat-check
```
One subprocess per run (process tree force-killed with `taskkill /F /T` at cap + 60 s), evaluated
in-process with `evaluate.evaluate_files(..., write_3dm=False, write_svg=True)`. Outputs:
`results.csv` (one appended row per run), `runs/<config>_s<seed>/` (placements, metrics, svg,
`engine_stdout.txt`), `best/<config>/` (best run: fewest unplaced → fewest defects → fewest sheets →
smallest `remnant_used_width` → shortest wall), `repeat_check.json` (with `--repeat-check`: first seed
run twice per config, `instances` must be identical; exit code 2 otherwise — a run stopped by the cap
is timing-dependent and is flagged `cancelled`). Add `--dll-np` / `--dll-nfp` to A/B a new build
against the baseline.

### 8. Cross-check with the C++ `nfp_bench`
```
src\opennest_cpp\build_bench5\Release\nfp_bench.exe --dataset file:tools/bench3dm/out/job1/instance.nfp.txt --sheets 10 --rotations 4 --placement 1 --pop 10 --gens 10 --tryAllRotations 1 --exactNfp 1 --compaction 0 --stagnation 30 --seed 30 --dumpPlacements
```
Should agree with `engines.py --config gh_nfp --seed 30 --cap 900`. The bench's `NfpParams` differ
from `gh_nfp` only in `numSeeds 4` (used by `mode 2` turbo only) and `useHoles 0` (a no-op in
`nfp_nest_capi.cpp`), so the layouts should be identical if the dataset file matches `instance.json`.

## Configs

`gh_np` mirrors `src/opennest_2/grasshopper/component_nest.cs` 1262-1286: `num_rotations 3600,
spacing 0, simplify_tolerance 0, iter_mode 1, iter_budget 4000, max_sheets 10, n_starts 1,
part_holes_mode 1, pole_max 48, final_compact 1, fit_mode 0`. `gh_nfp` mirrors
`rhino_example_cpp.cs` 97-137: `placementType 1, rotations 4, mutationRate 10, populationSize 10,
curveTolerance 0.72, clipperScale 1e7, mode 1, useParallel 1, maxSheets 0, edgeSamples -1,
compactionPasses 0, tryAllRotations 1, exactNfp 1, stagnationGens 30, exactVoids 0, useHoles 1,
generations 10`. Both pass 10 identical sheets. Variants inherit and override `params`; `seed` is
injected per run.

## Multi-sheet prototypes added for the 5-sheets case (2026-10-18)

All scripts run from the repo root with `python -I`, always pass `--dll` explicitly (the DLL finder prefers
`out/dll_baseline`), and use the collision engine with the env levers
`--env NP_SMART_ROT=1 --env NP_EXPL_STRIKES=2 --env NP_EXPL_NOIMPR=50 --env NP_SIMPL_MAXDEV=0.5`.
Benchmark instance: `out/job1_fixed_round/instance.json` (round clearance offsets). Results log and plan:
`C:\Users\Petras\.claude\plans\we-are-working-wiht-eager-pascal.md`; research note `research5/RESEARCH_5SHEETS.md`.

* `groupfill_nfp.py` (group-and-fill): plate groups (parts >= 5 % of the sheet) tested for single-sheet feasibility
  (`--tests-only`, `--shard i/n`, `--cache`; merge shards with `merge_group_cache.py`), MILP exact cover of the plates
  (`--k`, `--partitions`), then per-sheet fill: `--two-stage` (plates frozen as sheet holes), `--fill-insert`
  (largest-first single-part insertion), `--fill-balanced` (all sheets open), `--joint-long-frac F` (long parts packed
  jointly with the plates), `--fill-knap` (engine E1: plates mandatory + knapsack filler set in ONE pack; needs the
  `np_nest_prio` DLL), `--repack-knap`, `--hole-fill` (part-in-hole post pass), `--remnant-compact` (least-filled sheet
  re-packed to the minimal bounding rectangle = usable offcut), `--force-groups "a,b;c,d,e;..."` to replay a given
  plate assignment, `--engine nfp|np`.
* `sheetcover.py` (Stage 2 of the constructive-covering strategy): candidate SHEETS = plate group x knapsack filler set,
  joint-packed in parallel worker processes with plates mandatory; exact-cover ILP over feasible candidate sheets;
  leftover repack, hole fill, remnant compaction, evaluation. `--cover-only` re-solves the cover on an existing
  `candidates.json`; `--extra-cands` merges candidate files.
* `instance_to_nestfab.py`: exports the instance to DXF parts + a NestFab parts CSV for the NestFab command line
  (black-box benchmark only; the NestFab licence forbids decompiling).
* `render_png.py`: matplotlib renderer for placements (used for the reference layout and diagnostics).
* `evaluate.py` now also reports `used_height`, `bbox_frac` per sheet, `remnant_bbox` and draws the used bounding box
  (dashed red) in `result.svg`.

Status: best layouts are 6 sheets (3 small parts on the 6th); given the reference per-sheet part sets as one joint pack
the engine reproduces the reference density on 4 of 5 sheets, so the open problem is set selection (see the plan).

### Frozen small-part insertion

`sheetcover_frozen.py` adds missing small parts to an existing placement file while keeping every incumbent transform unchanged. It uses incumbent outer rings as native sheet keep-outs, then validates each proposed addition against all actual solve polygons and source polygons, including occupants already inside holes. Accepted parts are assigned once across sheets. `--cavities` also samples existing part holes geometrically; `--cavities-only` skips native solving. Unsupported discrete orientations and invalid additions are rejected.

```powershell
python -I tools/bench3dm/sheetcover_frozen.py --instance instance.json --baseline placements.json --dll path/to/nest_physics.dll --out output/frozen --cap 30 --workers 5 --native-workers 4 --cavities
python -I tools/bench3dm/test_sheetcover_frozen.py
```

The DLL must support the existing fixed-sheet-hole API; `NP_WORKERS` support is needed for the requested single-start worker limit. Native cancellation is cooperative, so wall time can exceed `--cap`. The tool currently requires quantity-one parts and contiguous nonempty sheet labels. The default small-part threshold is 5% of solve-sheet area (`--small-frac`). It saves reusable `candidates.json`, `placements.json`, raw proposals, arguments, remaining IDs, and strict metrics. Incomplete layouts remain explicitly incomplete; a successful insertion does not certify the requested sheet-count solution.
