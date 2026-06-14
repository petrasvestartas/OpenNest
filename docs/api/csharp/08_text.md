# 08 · Text (font)

Render a label to single-stroke engraving polylines using the bundled OpenNest VDA font.

Project: [Download `08_text.zip`](downloads/08_text.zip) — Windows `run.bat`, macOS/Linux `./run.command`.

```csharp
// 08 text — render a label to single-stroke engraving polylines (the OpenNest VDA font).
using System;
using System.Runtime.InteropServices;

static class Program {
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int nfp_text_to_polylines(string text, double height, int font, double spacing,
        int max_strokes, int[] vcount, int max_points, double[] xy, out int total);

    static void Main() {
        string text = "compas_nest\n0 1 2 3";
        double height = 10.0;
        int    font   = 0;                               // 0 = regular, 1 = bold

        // size the buffers (max_strokes = max_points = 0 just returns the counts)
        nfp_text_to_polylines(text, height, font, -1.0, 0, null, 0, null, out int total);
        var vcount = new int[total];                      // >= n_strokes (strokes <= points)
        var xy     = new double[total * 2];
        int nStrokes = nfp_text_to_polylines(text, height, font, -1.0, vcount.Length, vcount, total, xy, out total);

        Console.WriteLine($"{nStrokes} stroke(s), {total} point(s)");
        int k = 0;
        for (int s = 0; s < nStrokes && s < 3; s++) {     // show the first few strokes
            Console.WriteLine($"  stroke {s}: {vcount[s]} pts, first ({xy[2*k]:F2}, {xy[2*k+1]:F2})");
            k += vcount[s];
        }
    }
}
```
