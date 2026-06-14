# `nfp_offset_polygon` (P/Invoke) — grow / shrink one polygon

Clipper2 inflate with miter joins; positive `delta` grows, negative shrinks. This is the building block for
**spacing** (grow each part by `gap/2` before nesting). Returns the vertex count written (largest‑area result
loop), `0` if the offset vanished, or the **negated** required count when the buffer is too small.

## Declaration

```csharp
[DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
static extern int nfp_offset_polygon(
    int vertex_count, double[] xy,
    double delta, double miter_limit,
    int max_out_vertices, double[] out_xy);
```

## Example

```csharp
// grow a 20x20 square outward by 2 units
double[] xy = { 0, 0, 20, 0, 20, 20, 0, 20 };   // closed ring (no duplicate end point)

double[] outXy = new double[64];                // up to 32 points
int n = nfp_offset_polygon(4, xy, delta: 2.0, miter_limit: 2.0,
                           max_out_vertices: 32, outXy);

if (n < 0)        Console.WriteLine($"buffer too small, need {-n} vertices");
else if (n == 0)  Console.WriteLine("offset vanished (over-shrunk)");
else
{
    Console.WriteLine($"offset to {n} vertices:");
    for (int i = 0; i < n; i++) Console.WriteLine($"   ({outXy[2*i]:F2}, {outXy[2*i+1]:F2})");
}
```

Pass a **negative** `delta` to shrink (e.g. to inset a sheet boundary). If shrinking collapses the polygon the
call returns `0`. If `n < 0`, re‑call with `out_xy` resized to `-n * 2`.
