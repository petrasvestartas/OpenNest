# 05 · Offset

Grow (or shrink) one polygon by a distance — the building block for spacing.

Project: [Windows](downloads/05_offset_win.zip) · [macOS](downloads/05_offset_mac.zip)

```csharp
// 05 offset — grow (or shrink) one polygon by a distance (Clipper2).
using System;
using System.Runtime.InteropServices;

static class Program {
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_offset_polygon(int vertex_count, double[] xy, double delta, double miter_limit,
        int max_out_vertices, double[] out_xy);

    static void Main() {
        double[] xy = { 0, 0, 20, 0, 20, 20, 0, 20 };   // a 20x20 square (closed ring)

        var outXy = new double[64];                     // up to 32 points
        int n = nfp_offset_polygon(4, xy, delta: 2.0, miter_limit: 2.0, max_out_vertices: 32, outXy);

        if (n < 0)       Console.WriteLine($"buffer too small, need {-n} vertices");
        else if (n == 0) Console.WriteLine("offset vanished (over-shrunk)");
        else {
            Console.WriteLine($"offset to {n} vertices:");
            for (int i = 0; i < n; i++) Console.WriteLine($"  ({outXy[2*i]:F2}, {outXy[2*i+1]:F2})");
        }
    }
}
```
