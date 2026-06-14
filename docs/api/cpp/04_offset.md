# 04 · Clearance offset

Grow every part and shrink the sheet by a clearance with nfp_offset_polygon, then nest the offset geometry — the spacing-via-offset workflow (the solver `spacing` param is ignored).

Project: [Download `04_offset.zip`](downloads/04_offset.zip) — Windows `run.bat`, macOS/Linux `./run.command`.

```cpp
// 04 offset (clearance) — the right way to nest with a GAP between parts: grow every PART and shrink the
// SHEET by the clearance with nfp_offset_polygon, then nest the OFFSET geometry. nfp_nest / np_nest ignore
// the old `spacing` param on purpose — you offset UPSTREAM, exactly like the Grasshopper Geometry/Sheets
// components and the Rhino nest_geo.offset_nesting_boundary / nest_sheets.offset_sheet_boundary methods.
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    const double CLEARANCE = 4.0;                        // gap between parts AND from the sheet edge

    double p0[]    = { 0,0, 30,0, 30,12, 0,12 };         // two rectangular parts ...
    double p1[]    = { 0,0, 20,0, 20,20, 0,20 };
    double sheet[] = { 0,0, 120,0, 120,120, 0,120 };     // ... and one sheet (closed rings)

    // grow each PART by CLEARANCE/2, shrink the SHEET by CLEARANCE/2  ->  neighbours keep CLEARANCE apart
    double g0[64], g1[64], gs[64];
    int n0 = nfp_offset_polygon(4, p0,     CLEARANCE * 0.5, 2.0, 32, g0);
    int n1 = nfp_offset_polygon(4, p1,     CLEARANCE * 0.5, 2.0, 32, g1);
    int ns = nfp_offset_polygon(4, sheet, -CLEARANCE * 0.5, 2.0, 32, gs);

    // flatten the OFFSET part outlines into nfp_nest's arrays
    int    pvc[] = { n0, n1 };
    double pxy[128]; int k = 0;
    for (int i = 0; i < 2 * n0; i++) pxy[k++] = g0[i];
    for (int i = 0; i < 2 * n1; i++) pxy[k++] = g1[i];
    int    pqty[] = { 4, 4 };                            // 4 copies each -> 8 instances
    int    svc[]  = { ns };

    NfpParams p = {0};
    p.placementType = 1; p.rotations = 4; p.populationSize = 10; p.mutationRate = 10;
    p.seed = 1; p.clipperScale = 1e7; p.curveTolerance = 0.3; p.mode = 1; p.generations = 10; p.useParallel = 1;

    int    part_holes[2]  = {0,0};
    int    sheet_holes[1] = {0};
    double tx[8], ty[8], ang[8]; int sid[8], pidx[8]; int n_sheets; double fitness;
    int placed = nfp_nest(2, pvc, pxy, pqty, NULL, part_holes, NULL, NULL,
                          1, svc, gs, sheet_holes, NULL, NULL,
                          &p, tx, ty, ang, sid, pidx, &n_sheets, &fitness);

    // The poses fit the OFFSET parts; draw your ORIGINAL parts at (tx,ty,ang) and the CLEARANCE gap shows.
    printf("clearance %.1f -> placed %d/8 on %d sheet(s), fitness %.3f\n", CLEARANCE, placed, n_sheets, fitness);
    for (int i = 0; i < 8; i++)
        printf("  part %d -> sheet %d  (%.2f, %.2f) @ %.1f deg\n", pidx[i], sid[i], tx[i], ty[i], ang[i]);
    return 0;
}
```
