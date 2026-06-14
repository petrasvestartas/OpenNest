// 01 collision — nest parts into a sheet with the physics (collision) engine.
#include "nest_physics_capi.h"
#include <stdio.h>

int main(void) {
    int    pvc[] = { 4, 4, 4, 3 };                       // 4 parts (3 rects + a triangle)
    double pxy[] = { 0,0, 30,0, 30,12, 0,12,
                     0,0, 20,0, 20,20, 0,20,
                     0,0, 40,0, 40,8,  0,8,
                     0,0, 24,0, 0,24 };
    int    svc[] = { 4 };                                // one 150x150 sheet
    double sxy[] = { 0,0, 150,0, 150,150, 0,150 };

    NpParams q = {0};
    q.num_rotations = 16; q.seed = 1; q.iter_mode = 0; q.time_budget_secs = 2.0;
    q.n_starts = 1; q.max_sheets = 6; q.fit_mode = 0;

    double tx[4], ty[4], ang[4]; int sid[4]; int n_sheets;
    int rc = np_nest(4, pvc, pxy, NULL,
                     1, svc, sxy, (int[]){0}, NULL, NULL,    // sheet outers + (no) sheet holes
                     (int[]){0,0,0,0}, NULL, NULL,           // part holes (parts can nest INTO these)
                     &q, tx, ty, ang, sid, &n_sheets);

    printf("np_nest rc=%d, %d sheet(s)\n", rc, n_sheets);
    for (int i = 0; i < 4; i++)
        printf("  part %d -> sheet %d  (%.2f, %.2f) @ %.3f rad\n", i, sid[i], tx[i], ty[i], ang[i]);
    return rc;
}
