# `np_nest` — physics packing (into holes)

The penetration‑depth / overlap‑relaxation engine: it lets parts overlap, then slides them apart until they fit —
denser than NFP, and parts can nest **into** holes. One output slot **per part** (original order, not per instance).
Returns `0` on success. Angle is in **radians**.

> New to the array convention? See [How a polygon crosses the boundary](index.md#how-a-polygon-crosses-the-boundary).

## Example

Four parts onto one 150×150 sheet, with a 2‑second time budget:

```c
#include "nest_physics_capi.h"
#include <stdio.h>

int main(void) {
    int    pvc[] = { 4, 4, 4, 3 };
    double pxy[] = { 0,0, 30,0, 30,12, 0,12,
                     0,0, 20,0, 20,20, 0,20,
                     0,0, 40,0, 40,8,  0,8,
                     0,0, 24,0, 0,24 };
    int    part_count = 4;

    int    svc[] = { 4 };
    double sxy[] = { 0,0, 150,0, 150,150, 0,150 };

    NpParams q = {0};
    q.num_rotations = 16; q.seed = 1;
    q.iter_mode = 0; q.time_budget_secs = 2.0;   // 0 = wall-clock budget
    q.n_starts = 1; q.max_sheets = 6; q.fit_mode = 0;

    double tx[4], ty[4], ang[4]; int sid[4]; int n_sheets;
    int rc = np_nest(
        part_count, pvc, pxy, /*part_rotations*/ NULL,
        /*sheets*/ 1, svc, sxy, (int[]){0}, NULL, NULL,   // sheet outers + (no) sheet holes
        /*part holes*/ (int[]){0,0,0,0}, NULL, NULL,      // parts can nest INTO these
        &q, tx, ty, ang, sid, &n_sheets);

    printf("np_nest rc=%d, %d sheet(s)\n", rc, n_sheets);
    for (int i = 0; i < part_count; i++)
        printf("  part %d -> sheet %d  (%.2f, %.2f) @ %.3f rad\n",
               i, sid[i], tx[i], ty[i], ang[i]);
    return rc;
}
```

## Signature

```c
int rc = np_nest(
    // ---- parts: a list of polygons ----
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]  vertices in each part
    const double* part_xy,                   // all part points, x,y,…
    const int*    part_rotations,            // [part_count]  per-part rotation override: 0 = free
                                             //   continuous rotation (default), N>0 = only N discrete
                                             //   orientations (1 = fixed at 0°). NULL = no overrides.
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

!!! note "`spacing` is ignored"
    The physics engine does **not** offset parts; gaps are applied upstream (the host offsets parts and sheets
    before solving). `spacing` is kept in the struct for ABI stability but has no effect — pass `0`.

??? info "All 13 NpParams fields"

    | Field | Type | Meaning |
    | --- | --- | --- |
    | `num_rotations` | int | orientations sampled per part (≥1) |
    | `spacing` | double | **ignored** (offset is applied upstream) |
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

> Long‑running solves should run on a worker thread so the UI stays live — see
> [Run without freezing](progress.md).
