# 06 · Pack (array)

Lay parts out in a deterministic grid: a fixed number of columns per row (no nesting).

Project: [Windows](downloads/06_pack_array_win.zip) · [macOS](downloads/06_pack_array_mac.zip)

```cpp
// 06 pack array — lay parts out in a grid: a fixed number of columns per row.
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    int    pvc[]  = { 4, 4, 3 };
    double pxy[]  = { 0,0, 30,0, 30,12, 0,12,
                      0,0, 20,0, 20,20, 0,20,
                      0,0, 24,0, 0,24 };
    int    pqty[] = { 4, 4, 4 };                         // 12 instances

    double tx[12], ty[12], ang[12]; int sid[12];
    int n = nfp_pack(3, pvc, pxy, pqty,
                     /*columns*/ 5, /*gap_x*/ 2.0, /*gap_y*/ 2.0, /*max_width*/ 0.0,
                     tx, ty, ang, sid);

    printf("packed %d instances\n", n);
    for (int i = 0; i < n; i++) printf("  (%.1f, %.1f)\n", tx[i], ty[i]);
    return 0;
}
```
