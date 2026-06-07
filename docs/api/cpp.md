# C++ API

OpenNest nests with **two native engines**. Both do the same simple thing:

> **You give:** part shapes + sheet shapes.
> **You get back:** for each part — *move it by (tx, ty)*, *rotate it by an angle*, and *which sheet* it landed on.

You call them through a plain **C ABI** (works from any language).

| Want this… | Call | In |
| --- | --- | --- |
| Clean polygon packing, no overlap | **`nfp_nest`** | `nfp_nest.dll` / `.dylib` |
| Dense packing, parts into holes | **`np_nest`** | `nest_physics.dll` / `.dylib` |

To place instance *i*: `final = Rotate(part, angle[i], origin) + (tx[i], ty[i])`, on sheet `sheet_id[i]`
(`-1` = didn't fit). `(tx, ty)` are sheet‑local — add the sheet's own position.

---

## Runnable example

The easiest way to nest from C++ is the header‑only
[`opennest.hpp`](https://github.com/petrasvestartas/OpenNest/blob/main/examples/cpp_console/opennest.hpp) binding,
which mirrors the [compas_nest](python.md) Python API — build a `nest_geo`, build a `nest_sheets`, call `.solve()`,
read the placed outlines:

```cpp
#include "opennest.hpp"
using namespace opennest;

nest_geo geo;
geo.add_part({{0,0},{30,0},{30,12},{0,12}}, /*holes*/ {}, /*copies*/ 4);
geo.add_part({{0,0},{20,0},{20,20},{0,20}}, {{{6,6},{14,6},{14,14},{6,14}}}, 3);

nest_sheets sheets;
sheets.add_sheet({{0,0},{120,0},{120,120},{0,120}}, {{{50,50},{65,50},{65,65},{50,65}}});

nest_result r = opennest_collision{}.solve(geo, sheets);   // physics — or opennest_nfp{} for NFP + GA
for (const auto& group : r.placed_polylines())
    for (const auto& part : group.parts) {
        part.shape.outer;   // placed (transformed) outer ring
        part.shape.holes;   // placed holes
    }
```

The full program is
[`examples/cpp_console`](https://github.com/petrasvestartas/OpenNest/tree/main/examples/cpp_console). Build & run it
with the CMake **superbuild** in [`examples/`](https://github.com/petrasvestartas/OpenNest/tree/main/examples):

```bash
cmake -S examples -B examples/build                                  # add -A x64 on Windows
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release  # builds + runs both apps
```

Built & run on Windows, macOS and Linux by
[`examples.yml`](https://github.com/petrasvestartas/OpenNest/blob/main/.github/workflows/examples.yml).
`opennest.hpp` is a thin wrapper over the raw C ABI documented below — call the ABI directly only when binding
from another language.

---

## How a polygon crosses the boundary

A C ABI can't take a `vector<Polygon>`, so geometry is passed as **plain number arrays**. The rule is simple:

- **One polygon** = a vertex count + a flat `x,y` list: `x0,y0, x1,y1, x2,y2, …`
- **A list of polygons** = a `count`, a *lengths* array (vertices in each), and **one** big `xy` buffer with every polygon's points concatenated.
- **Holes** repeat the same pattern: how many holes each part has, how many vertices each hole has, and one concatenated `xy`.

```c
// Two triangles as "a list of polygons":
int    count         = 2;
int    vertex_counts[] = { 3, 3 };
double xy[]            = { 0,0, 10,0, 0,8,    // triangle A
                           0,0, 10,0, 0,8 };  // triangle B
```

So the long signatures below are just **"a list of polygons (with holes)"** spelled out — once for the parts,
once for the sheets. That's all the `_counts` / `_xy` arrays are.

---

## `nfp_nest` — clean polygon packing

Give it parts (+ optional holes) and sheets; it **returns how many instances it placed**. Outputs are
caller‑allocated, one slot per instance (i.e. `sum(part_quantities)` long).

```c
int n = nfp_nest(
    // ---- parts: a list of polygons, with holes + a copy-count each ----
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]  vertices in each part's outline
    const double* part_xy,                   // all part-outline points, x,y,x,y,…
    const int*    part_quantities,           // [part_count]  how many copies of each part
    const int*    part_hole_counts,          // [part_count]  holes in each part
    const int*    part_hole_vertex_counts,   // vertices in each hole
    const double* part_hole_xy,              // all part-hole points, x,y,…
    // ---- sheets: same shape (a list of polygons, with holes) ----
    int           sheet_count,
    const int*    sheet_vertex_counts,
    const double* sheet_xy,
    const int*    sheet_hole_counts,
    const int*    sheet_hole_vertex_counts,
    const double* sheet_hole_xy,
    // ---- tuning (see table below) ----
    const NfpParams* params,
    // ---- outputs: caller-allocated, one slot per placed instance ----
    double* out_tx, double* out_ty, double* out_angle,   // angle in DEGREES
    int*    out_sheet_id,                    // which sheet each instance landed on (-1 = didn't fit)
    int*    out_part_index,                  // which source part each instance came from
    int*    out_n_sheets,                    // (single value) sheets used
    double* out_fitness);                    // (single value) layout score
// return value = number of placed instances
```

The few `NfpParams` knobs that matter: `rotations` (orientations to try), `seed`, `generations` (how long to
search), `spacing` (gap), `useHoles`. Everything else has a sensible default.

??? info "All 25 NfpParams fields"

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

Same idea, plus parts can nest **into** holes. One output slot per part (original order). Returns `0` on success.

```c
int rc = np_nest(
    // ---- parts: a list of polygons ----
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]  vertices in each part
    const double* part_xy,                   // all part points, x,y,…
    // ---- sheets: outlines + holes ----
    int           sheet_count,
    const int*    sheet_outer_vertex_counts, // [sheet_count]
    const double* sheet_outer_xy,
    const int*    sheet_hole_counts,         // [sheet_count]  holes in each sheet
    const int*    hole_vertex_counts,        // vertices in each sheet-hole
    const double* hole_xy,                   // all sheet-hole points
    // ---- part holes (parts can be nested INTO these) ----
    const int*    part_hole_counts,          // [part_count]  holes in each part
    const int*    part_hole_vertex_counts,
    const double* part_hole_xy,
    // ---- tuning (see table below) ----
    const NpParams* params,
    // ---- outputs: caller-allocated, one slot per part ----
    double* out_tx, double* out_ty, double* out_angle,   // angle in RADIANS
    int*    out_sheet_id,                    // which sheet (-1 = didn't fit)
    int*    out_n_sheets);                   // (single value) sheets used
// return value = 0 on success
```

The few `NpParams` knobs that matter: `num_rotations`, `seed`, `iter_budget` (how long), `n_starts` (tries),
`pole_max` (accuracy), `fit_mode` (one sheet vs. fewest sheets).

??? info "All 13 NpParams fields"

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
