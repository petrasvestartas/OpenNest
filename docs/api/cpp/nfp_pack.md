# `nfp_pack` — simple row/grid layout (no nesting)

Lays instances out left‑to‑right in rows — the `compas_nest` `pack()` semantics. No nesting, no search: just a
tidy grid. **array mode** (`max_width <= 0`): wrap every `columns` items. **distance mode** (`max_width > 0`):
wrap when the next part would exceed `max_width`. Angle is always `0`, sheet id always `0`.

> New to the array convention? See [How a polygon crosses the boundary](index.md#how-a-polygon-crosses-the-boundary).

## Example

Pack 3 parts, 4 copies each, into rows of 5 with a 2‑unit gap:

```c
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    int    pvc[]  = { 4, 4, 3 };
    double pxy[]  = { 0,0, 30,0, 30,12, 0,12,
                      0,0, 20,0, 20,20, 0,20,
                      0,0, 24,0, 0,24 };
    int    pqty[] = { 4, 4, 4 };          // 12 instances total
    int    part_count = 3, instances = 12;

    double tx[12], ty[12], ang[12]; int sid[12];
    int n = nfp_pack(
        part_count, pvc, pxy, pqty,
        /*columns*/ 5, /*gap_x*/ 2.0, /*gap_y*/ 2.0,
        /*max_width*/ 0.0,            // array mode (wrap every 5)
        tx, ty, ang, sid);

    printf("packed %d instances\n", n);
    for (int i = 0; i < instances; i++)
        printf("  (%.1f, %.1f)\n", tx[i], ty[i]);
    return 0;
}
```

Switch to **distance mode** by passing `columns = 0` and `max_width = 200` — it then wraps whenever the next part
would push past `x = 200` (an oversized part gets its own row).

## Signature

```c
int n = nfp_pack(
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]
    const double* part_xy,                   // all part points, x,y,…
    const int*    part_quantities,           // [part_count] (NULL = 1 each)
    int           columns,                   // array mode: cells per row
    double        gap_x, double gap_y,       // cell gaps
    double        max_width,                 // >0 = distance mode
    double* out_tx, double* out_ty, double* out_angle, int* out_sheet_id);
// return value = number of placed instances (= sum of quantities)
```
