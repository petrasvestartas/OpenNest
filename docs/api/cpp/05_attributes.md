# 05 · Attributes

Carry a point (a part's centroid) and read it at the placed pose: attributes move with the part.

Project: [Download `05_attributes.zip`](downloads/05_attributes.zip) — Windows `run.bat`, macOS/Linux `./run.command`.

```cpp
// 05 attributes — carry a point (the part centroid) and read it at the placed pose.
// Attributes are just extra geometry transformed by the SAME (rotate, translate) the part gets.
#include "nest_physics_capi.h"
#include <stdio.h>
#include <math.h>

int main(void) {
    int    pvc[] = { 4, 4, 4, 4 };                       // 4 copies of one 30x12 rectangle
    double pxy[] = { 0,0, 30,0, 30,12, 0,12,
                     0,0, 30,0, 30,12, 0,12,
                     0,0, 30,0, 30,12, 0,12,
                     0,0, 30,0, 30,12, 0,12 };
    double attr_x = 15.0, attr_y = 6.0;                  // a point at the part's centroid
    int    no_part_holes[4] = {0};

    int    svc[] = { 4 };
    double sxy[] = { 0,0, 120,0, 120,120, 0,120 };
    int    sheet_holes[1] = {0};

    NpParams q = {0};
    q.num_rotations = 16; q.seed = 1; q.iter_mode = 1; q.iter_budget = 600; q.n_starts = 1; q.max_sheets = 6;

    double tx[4], ty[4], ang[4]; int sid[4]; int n_sheets;
    np_nest(4, pvc, pxy, NULL, 1, svc, sxy, sheet_holes, NULL, NULL,
            no_part_holes, NULL, NULL, &q, tx, ty, ang, sid, &n_sheets);

    printf("each part's centroid, moved to its placed pose:\n");
    for (int i = 0; i < 4; i++) {
        if (sid[i] < 0) continue;
        double c = cos(ang[i]), s = sin(ang[i]);
        double ax = attr_x * c - attr_y * s + tx[i];     // rotate about origin, then translate
        double ay = attr_x * s + attr_y * c + ty[i];
        printf("  part %d -> sheet %d  centroid at (%.2f, %.2f)\n", i, sid[i], ax, ay);
    }
    return 0;
}
```
