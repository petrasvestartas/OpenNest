# C++ API

OpenNest nests with **two native engines**. Both do the same simple thing:

> **You give:** part shapes + sheet shapes.
> **You get back:** for each part — *move it by (tx, ty)*, *rotate it by an angle*, and *which sheet* it landed on.

You call them through a plain **C ABI** (works from any language). Shapes cross as flat `x,y` number arrays.

| Want this… | Call | In |
| --- | --- | --- |
| Clean polygon packing, no overlap | **`nfp_nest`** | `nfp_nest.dll` / `.dylib` |
| Dense packing, parts into holes | **`np_nest`** | `nest_physics.dll` / `.dylib` |

To place instance *i*: `final = Rotate(part, angle[i], origin) + (tx[i], ty[i])`, on sheet `sheet_id[i]`
(`-1` = didn't fit). `(tx, ty)` are sheet‑local — add the sheet's own position.

---

## `nfp_nest` — clean polygon packing

Give it parts (+ optional holes) and sheets; it returns the placements and **how many it placed**.

```c
int n = nfp_nest(parts…, sheets…, &params,
                 out_tx, out_ty, out_angle,   // angle in DEGREES
                 out_sheet_id, out_part_index, &out_n_sheets, &out_fitness);
```

The few knobs that matter: `rotations` (orientations to try), `seed`, `generations` (how long to search),
`spacing` (gap), `useHoles`. Everything else has a sensible default.

??? info "Full signature + all 25 NfpParams options"

    ```c
    int nfp_nest(
        int part_count, const int* part_vertex_counts, const double* part_xy,
        const int* part_quantities, const int* part_hole_counts,
        const int* part_hole_vertex_counts, const double* part_hole_xy,
        int sheet_count, const int* sheet_vertex_counts, const double* sheet_xy,
        const int* sheet_hole_counts, const int* sheet_hole_vertex_counts, const double* sheet_hole_xy,
        const NfpParams* params,
        double* out_tx, double* out_ty, double* out_angle,
        int* out_sheet_id, int* out_part_index, int* out_n_sheets, double* out_fitness);
    ```

    | Field | Type | Meaning |
    | --- | --- | --- |
    | `placementType` | int | 0 = box, 1 = gravity, 2 = squeeze |
    | `rotations` | int | discrete rotation count (e.g. 4) |
    | `mutationRate` | int | GA mutation rate (applied as `0.01 * rate`) |
    | `populationSize` | int | GA pool size per generation |
    | `seed` | int | RNG seed (`-1` = time‑based) |
    | `curveTolerance` | double | simplification tolerance |
    | `clipperScale` | double | Clipper integer scale (e.g. `1e7`) |
    | `spacing` | double | gap between parts |
    | `sheetSpacing` | double | gap inside the sheet edge |
    | `rotationLimit` | double | max continuous rotation jitter (degrees), default 360 |
    | `useHoles` | int | nest parts into holes |
    | `exploreConcave` | int | explore concave regions |
    | `clipByHull` | int | clip by convex hull |
    | `clipByRects` | int | clip by rectangles |
    | `simplify` | int | convex‑hull simplify |
    | `mode` | int | 0 = faithful, 1 = default, 2 = turbo (multi‑seed) |
    | `generations` | int | GA generations (= the "Iterations" knob) |
    | `numSeeds` | int | turbo: parallel independent seeds |
    | `useParallel` | int | parallel NFP / population evaluation |
    | `timeBudgetSecs` | double | `>0` ⇒ run until elapsed (overrides generations) |
    | `maxSheets` | int | 0 = use all provided sheets |
    | `edgeSamples` | int | feasible‑region edge samples (0 = off, faster) |
    | `compactionPasses` | int | post‑placement compaction passes (0 = off) |
    | `tryAllRotations` | int | score every rotation per placement (tighter, slower) |
    | `exactNfp` | int | full‑resolution exact NFP (no gap, slower) |

---

## `np_nest` — physics packing (into holes)

Same idea, plus parts can nest **into** holes. Returns `0` on success.

```c
int rc = np_nest(parts…, sheets…, part_holes…, &params,
                 out_tx, out_ty, out_angle,   // angle in RADIANS
                 out_sheet_id, &out_n_sheets);
```

The few knobs that matter: `num_rotations`, `seed`, `iter_budget` (how long), `n_starts` (tries), `pole_max`
(accuracy), `fit_mode` (one sheet vs. fewest sheets).

??? info "Full signature + all 13 NpParams options"

    ```c
    int np_nest(
        int part_count, const int* part_vertex_counts, const double* part_xy,
        int sheet_count, const int* sheet_outer_vertex_counts, const double* sheet_outer_xy,
        const int* sheet_hole_counts, const int* hole_vertex_counts, const double* hole_xy,
        const int* part_hole_counts, const int* part_hole_vertex_counts, const double* part_hole_xy,
        const NpParams* params,
        double* out_tx, double* out_ty, double* out_angle, int* out_sheet_id, int* out_n_sheets);
    ```

    | Field | Type | Meaning |
    | --- | --- | --- |
    | `num_rotations` | int | orientations sampled per part (≥1) |
    | `spacing` | double | min gap (0 = touching allowed) |
    | `simplify_tolerance` | double | ≤0 keeps the lean default |
    | `seed` | int | base RNG seed (≥0) |
    | `time_budget_secs` | double | wall‑clock budget (when `iter_mode == 0`) |
    | `iter_budget` | long long | relaxation rounds (when `iter_mode != 0`, deterministic) |
    | `iter_mode` | int | 0 = wall‑clock, 1 = deterministic count |
    | `max_sheets` | int | cap on sheets (0 → default 6) |
    | `n_starts` | int | multi‑start: run N seeds, keep the densest |
    | `part_holes_mode` | int | 0 = keep part holes empty; 1 = fill them |
    | `pole_max` | int | poles per part (0 → 48; ~16 ≈ 2.7× faster) |
    | `final_compact` | int | 1 = post‑relaxation tighten slide |
    | `fit_mode` | int | 0 = fewest sheets; 1 = one sheet, max fill |

---

## Run it without freezing

Both engines run on a worker thread while you watch:

1. Start `nfp_nest` / `np_nest` on a background thread.
2. On a timer, read **`*_progress()`** (round reached) and **`*_poll_layout(...)`** (current best layout) to draw.
3. **`*_cancel()`** stops early and keeps the best so far.

??? note "Under the hood (for maintainers)"

    NFP engine in `src/opennest_cpp/src/`: `NestingEngine` (geometry + NFP + placement cost), `NestingContext`
    (GA orchestration), `NfpWorker` (NFP cache + Minkowski), `GeneticAlgorithm` (evolve), `NFP`, `GeometryUtil`,
    `NestConfig`. Physics engine in `src/nest_physics_cpp/`. Both build from source via CMake.
    Headers: `src/opennest_cpp/src/capi/nfp_nest_capi.h`, `src/nest_physics_cpp/nest_physics_capi.h`.
