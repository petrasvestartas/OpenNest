# C++ API (native C ABI)

OpenNest's nesting is done by two native libraries you can call from any language through a flat **C ABI**
(`extern "C"`, `cdecl`). Geometry crosses the boundary as **interleaved `x,y` `double[]` arrays** plus
**per‑ring vertex counts**; the caller pre‑allocates the output buffers.

| Library | Header | Engine |
| --- | --- | --- |
| `nfp_nest.dll` / `.dylib` | `src/opennest_cpp/src/capi/nfp_nest_capi.h` | No‑fit‑polygon + genetic algorithm (SvgNest port) |
| `nest_physics.dll` / `.dylib` | `src/nest_physics_cpp/nest_physics_capi.h` | Penetration‑depth overlap relaxation (sparrow port) |

## Calling convention

- A ring is a list of vertices given **CCW or CW, not closed** (a trailing duplicate point is tolerated).
- For *N* rings you pass an `int[N]` of vertex counts and one `double[2·Σcounts]` of interleaved `x,y`.
- Holes are passed as parallel "count" + "xy" arrays, grouped per part / per sheet.
- Output buffers are **caller‑allocated**; `out_sheet_id == -1` marks an unplaced instance.

**Placement contract** — apply rotation about the origin, then translate; `(tx, ty)` are **sheet‑local** (add the
sheet's own world origin yourself):

```text
final_point = Rotate(part_point, angle, about (0,0)) + (tx, ty)
```

!!! warning "Angle units differ"
    `nfp_nest` returns `out_angle` in **degrees**; `np_nest` returns it in **radians**.

---

## `nfp_nest.dll`

```c
// Returns the number of placed instances (>=0), or a negative error code.
// Output arrays have length = instance_count = sum(part_quantities), in expansion order.
int nfp_nest(
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]
    const double* part_xy,                   // [2*sum(part_vertex_counts)]
    const int*    part_quantities,           // [part_count] (>=1 each)
    const int*    part_hole_counts,          // [part_count]
    const int*    part_hole_vertex_counts,   // [sum(part_hole_counts)]
    const double* part_hole_xy,              // [2*sum(part_hole_vertex_counts)]
    int           sheet_count,
    const int*    sheet_vertex_counts,       // [sheet_count]
    const double* sheet_xy,                  // [2*sum(sheet_vertex_counts)]
    const int*    sheet_hole_counts,         // [sheet_count]
    const int*    sheet_hole_vertex_counts,  // [sum(sheet_hole_counts)]
    const double* sheet_hole_xy,             // [2*sum(sheet_hole_vertex_counts)]
    const NfpParams* params,
    double* out_tx, double* out_ty, double* out_angle,  // [instance_count]; angle in DEGREES
    int*    out_sheet_id,                    // [instance_count] (-1 = unplaced)
    int*    out_part_index,                  // [instance_count] source part index
    int*    out_n_sheets,                    // single
    double* out_fitness);                    // single

void      nfp_cancel(void);        // ask the solve to stop at the next generation, keep best-so-far
void      nfp_cancel_reset(void);  // clear the cancel flag (also cleared on nfp_nest entry)
long long nfp_progress(void);      // GA generation reached so far
double    nfp_fitness(void);       // best fitness so far

// Snapshot the current best layout mid-solve (UI-thread safe). Returns placed-instance count.
int nfp_poll_layout(int instance_count, double* out_tx, double* out_ty, double* out_angle,
                    int* out_sheet_id, int* out_part_index, int* out_n_sheets);
```

### `NfpParams`

| Field | Type | Meaning |
| --- | --- | --- |
| `placementType` | int | 0 = box, 1 = gravity, 2 = squeeze |
| `rotations` | int | discrete rotation count (e.g. 4) |
| `mutationRate` | int | GA mutation rate (applied as `0.01 * rate`) |
| `populationSize` | int | GA pool size (one generation = this many candidates) |
| `seed` | int | RNG seed (`-1` = time‑based, non‑deterministic) |
| `curveTolerance` | double | simplification tolerance |
| `clipperScale` | double | Clipper integer scale (e.g. `1e7`) |
| `spacing` | double | gap between parts |
| `sheetSpacing` | double | gap inside the sheet edge |
| `rotationLimit` | double | max continuous rotation jitter (degrees), default 360 |
| `useHoles` | int (bool) | nest parts into holes |
| `exploreConcave` | int (bool) | explore concave regions |
| `clipByHull` | int (bool) | clip by convex hull |
| `clipByRects` | int (bool) | clip by rectangles |
| `simplify` | int (bool) | convex‑hull simplify |
| `mode` | int | 0 = faithful (parity, single‑thread), 1 = default, 2 = turbo (multi‑seed) |
| `generations` | int | GA generations (= the component "Iterations") |
| `numSeeds` | int | turbo: parallel independent seeds |
| `useParallel` | int (bool) | parallel NFP / population evaluation |
| `timeBudgetSecs` | double | `>0` ⇒ run until elapsed (overrides the generations loop length) |
| `maxSheets` | int | 0 = use all provided sheets |
| `edgeSamples` | int | feasible‑region edge samples per part (0 = off; lower = faster) |
| `compactionPasses` | int | post‑placement compaction passes (0 = off; lower = faster) |
| `tryAllRotations` | int (bool) | evaluate every rotation per placement (slower, tighter) |
| `exactNfp` | int (bool) | full‑resolution exact NFP (no simplify/dilate ⇒ no gap, slower) |

---

## `nest_physics.dll`

```c
// Returns 0 on success, non-zero on failure. Outputs have length part_count (indexed by INPUT order).
int np_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    int           sheet_count,
    const int*    sheet_outer_vertex_counts,
    const double* sheet_outer_xy,
    const int*    sheet_hole_counts,        // may be NULL => no sheet holes
    const int*    hole_vertex_counts,       // sheet-major (may be NULL)
    const double* hole_xy,                  // sheet-major (may be NULL)
    const int*    part_hole_counts,         // [part_count] (NULL => no part holes)
    const int*    part_hole_vertex_counts,  // part-major (NULL ok)
    const double* part_hole_xy,             // part-major (NULL ok)
    const NpParams* params,
    double* out_tx, double* out_ty, double* out_angle,  // angle in RADIANS
    int*    out_sheet_id,                   // -1 = unplaced
    int*    out_n_sheets);                  // length 1

void      np_cancel(void);        // stop at next round, keep best-so-far
void      np_cancel_reset(void);  // clear the flag (np_nest clears it on entry too)
long long np_progress(void);      // relaxation rounds completed so far

// Snapshot the current best layout mid-solve. Returns placed-part count (0 if idle).
int np_poll_layout(int part_count, double* out_tx, double* out_ty, double* out_angle,
                   int* out_sheet_id, int* out_n_sheets);
```

### `NpParams`

| Field | Type | Meaning |
| --- | --- | --- |
| `num_rotations` | int | discrete orientations sampled per part (≥1) |
| `spacing` | double | min gap between parts / from holes (0 = touching allowed) |
| `simplify_tolerance` | double | geometry simplification (≤0 keeps the lean default) |
| `seed` | int | base RNG seed (≥0) |
| `time_budget_secs` | double | wall‑clock budget, used when `iter_mode == 0` |
| `iter_budget` | long long | relaxation‑round budget, used when `iter_mode != 0` (deterministic) |
| `iter_mode` | int | 0 = wall‑clock, 1 = deterministic iteration count |
| `max_sheets` | int | cap on sheets used (0 → default 6) |
| `n_starts` | int | multi‑start: run this many seeds, keep the densest (0/1 → single) |
| `part_holes_mode` | int | 0 = keep part holes empty; 1 = fill (parts nest into other parts' holes) |
| `pole_max` | int | surrogate poles per part (0 → default 48; ~16 ≈ 2.7× faster per round) |
| `final_compact` | int | 1 = post‑relaxation left/down compaction slide (tighter pack) |
| `fit_mode` | int | 0 = all parts on fewest sheets; 1 = ONE sheet, max fill (overflow reported unplaced) |

---

## Running on a background thread (live preview)

Both engines are designed to run on a worker thread while the host polls progress and draws an animated preview:

1. Start `nfp_nest` / `np_nest` on a worker thread.
2. On a UI timer, read `*_progress()` for the round/generation and call `*_poll_layout(...)` for the current best
   layout to draw.
3. Call `*_cancel()` to stop early (the solve returns its best‑so‑far). `*_cancel_reset()` clears the flag; the
   `*_nest` entry also clears it.

This is exactly how the Grasshopper components and the `OpenNest` command drive the engines.

---

## Internal architecture (for maintainers)

The NFP engine (`src/opennest_cpp/src/`) is organised as:

| Class | Role |
| --- | --- |
| `NestingEngine` | core geometry: polygon offset, NFP computation, placement‑cost evaluation |
| `NestingContext` | GA solver orchestration; drives generations, tracks the best layout, multi‑seed runs |
| `NfpWorker` | NFP cache + Minkowski‑convolution executor, keyed by `(partA, partB, rotations)` |
| `GeneticAlgorithm` | population evolution (mutation/crossover/selection); "faithful" + heuristic modes |
| `NFP` | polygon with an outer ring + child holes |
| `GeometryUtil` | static geometry helpers (area, bounds, point‑in‑polygon, rotate, offset) |
| `NestConfig` | configuration struct |

The physics engine lives under `src/nest_physics_cpp/` (header‑only solver + the `nest_physics_capi.cpp`
translation unit). Both are built from source by CMake (`cmake --build … --target nfp_nest | nest_physics`).
