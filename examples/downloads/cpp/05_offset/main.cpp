// 05 offset — grow (or shrink) one polygon by a distance (Clipper2).
#include "capi/nfp_nest_capi.h"
#include <stdio.h>

int main(void) {
    double xy[] = { 0,0, 20,0, 20,20, 0,20 };           // a 20x20 square (closed ring)

    double out[64];                                      // up to 32 points
    int n = nfp_offset_polygon(4, xy, /*delta*/ 2.0, /*miter_limit*/ 2.0, /*max_out_vertices*/ 32, out);

    if (n < 0)       printf("buffer too small, need %d vertices\n", -n);
    else if (n == 0) printf("offset vanished (over-shrunk)\n");
    else {
        printf("offset to %d vertices:\n", n);
        for (int i = 0; i < n; i++) printf("  (%.2f, %.2f)\n", out[2*i], out[2*i+1]);
    }
    return 0;
}
