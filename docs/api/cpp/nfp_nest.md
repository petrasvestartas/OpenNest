# `nfp_nest` — clean polygon packing

NFP + genetic algorithm. Give it parts (+ optional holes) and sheets; it packs them **without overlap** and
**returns how many instances it placed**. Outputs are caller‑allocated, one slot per instance (i.e.
`sum(part_quantities)` long). Angle is in **degrees**.

> New to the array convention? See [How a polygon crosses the boundary](index.md#how-a-polygon-crosses-the-boundary).

## Example

Three rectangles + a triangle, 3 copies each, onto one 150×150 sheet — calling the raw ABI directly:

```c
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    // 4 parts: 3 rectangles + a triangle (a "list of polygons")
    int    pvc[] = { 4, 4, 4, 3 };
    double pxy[] = { 0,0, 30,0, 30,12, 0,12,     // 30x12 rect
                     0,0, 20,0, 20,20, 0,20,     // 20x20 rect
                     0,0, 40,0, 40,8,  0,8,      // 40x8  rect
                     0,0, 24,0, 0,24 };          // triangle
    int    pqty[] = { 3, 3, 3, 3 };              // 3 copies each -> 12 instances
    int    part_count = 4, instances = 12;

    // one 150x150 sheet
    int    svc[] = { 4 };
    double sxy[] = { 0,0, 150,0, 150,150, 0,150 };

    NfpParams p = {0};
    p.placementType = 1;   // gravity
    p.rotations     = 4;   // 0, 90, 180, 270
    p.populationSize = 10; p.mutationRate = 10;
    p.seed = 1; p.clipperScale = 1e7; p.curveTolerance = 0.3;
    p.mode = 1; p.generations = 10; p.useParallel = 1;

    double tx[12], ty[12], ang[12]; int sid[12], pidx[12]; int n_sheets; double fitness;
    int placed = nfp_nest(
        part_count, pvc, pxy, pqty, /*part_rotations*/ NULL,
        /*part holes*/ (int[]){0,0,0,0}, NULL, NULL,
        /*sheets*/ 1, svc, sxy, (int[]){0}, NULL, NULL,
        &p, tx, ty, ang, sid, pidx, &n_sheets, &fitness);

    printf("placed %d/%d on %d sheet(s), fitness %.3f\n", placed, instances, n_sheets, fitness);
    for (int i = 0; i < instances; i++)
        printf("  part %d -> sheet %d  (%.2f, %.2f) @ %.1f deg\n",
               pidx[i], sid[i], tx[i], ty[i], ang[i]);
    return 0;
}
```

## Signature

```c
int n = nfp_nest(
    // ---- parts: a list of polygons, with holes + a copy-count each ----
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]  vertices in each part's outline
    const double* part_xy,                   // all part-outline points, x,y,x,y,…
    const int*    part_quantities,           // [part_count]  how many copies of each part
    const int*    part_rotations,            // [part_count]  per-part rotation override: 0 = use
                                             //   params->rotations (default), N>0 = only N orientations
                                             //   (360/N° steps; 1 = fixed at 0°). NULL = no overrides.
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

> Long‑running solves should run on a worker thread so the UI stays live — see
> [Run without freezing](progress.md).
