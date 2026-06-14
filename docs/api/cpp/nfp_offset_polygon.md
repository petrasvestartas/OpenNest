# `nfp_offset_polygon` — grow / shrink one polygon

Clipper2 inflate with miter joins; positive `delta` grows, negative shrinks. This is the building block for
**spacing**: grow each part by `spacing/2` before nesting so placed parts keep a gap. Returns the vertex count
written (largest‑area result loop), `0` if the offset vanished, or the **negated** required count when
`max_out_vertices` is too small (call again with a bigger buffer).

## Example

Grow a 20×20 square outward by 2 units:

```c
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    double xy[] = { 0,0, 20,0, 20,20, 0,20 };   // closed ring (no duplicate end point)

    double out[64];                              // 32 points max
    int n = nfp_offset_polygon(
        /*vertex_count*/ 4, xy,
        /*delta*/ 2.0, /*miter_limit*/ 2.0,
        /*max_out_vertices*/ 32, out);

    if (n < 0)      printf("buffer too small, need %d vertices\n", -n);
    else if (n == 0) printf("offset vanished (over-shrunk)\n");
    else {
        printf("offset to %d vertices:\n", n);
        for (int i = 0; i < n; i++) printf("  (%.2f, %.2f)\n", out[2*i], out[2*i+1]);
    }
    return 0;
}
```

Pass a **negative** `delta` to shrink (e.g. to inset a sheet boundary). If shrinking collapses the polygon the
call returns `0`.

## Signature

```c
int n = nfp_offset_polygon(
    int           vertex_count,
    const double* xy,                        // closed ring, x,y,… (no duplicate end point needed)
    double        delta,                     // model units; + grows, − shrinks
    double        miter_limit,               // <= 0 → 2.0
    int           max_out_vertices,
    double*       out_xy);                   // [max_out_vertices*2]
// return value = vertices written, 0 if vanished, or -(required count) if the buffer was too small
```
