# 08 · Text (font)

Render a label to single-stroke engraving polylines using the bundled OpenNest VDA font.

Project: [Windows](downloads/08_text_win.zip) · [macOS](downloads/08_text_mac.zip)

```cpp
// 08 text — render a label to single-stroke engraving polylines (the OpenNest VDA font).
#include "capi/nfp_nest_capi.h"
#include <stdio.h>
#include <stdlib.h>

int main(void) {
    const char* text = "compas_nest\n0 1 2 3";
    double height = 10.0;
    int    font   = 0;                                   // 0 = regular, 1 = bold

    // size the buffers (max_strokes = max_points = 0 just returns the counts)
    int total_points = 0;
    int n_strokes = nfp_text_to_polylines(text, height, font, -1.0, 0, NULL, 0, NULL, &total_points);

    int*    vcount = (int*)   malloc(sizeof(int)    * n_strokes);
    double* xy     = (double*)malloc(sizeof(double) * total_points * 2);
    nfp_text_to_polylines(text, height, font, -1.0, n_strokes, vcount, total_points, xy, &total_points);

    printf("%d stroke(s), %d point(s)\n", n_strokes, total_points);
    int k = 0;
    for (int s = 0; s < n_strokes && s < 3; s++) {       // show the first few strokes
        printf("  stroke %d: %d pts, first (%.2f, %.2f)\n", s, vcount[s], xy[2*k], xy[2*k+1]);
        k += vcount[s];
    }
    free(vcount); free(xy);
    return 0;
}
```
