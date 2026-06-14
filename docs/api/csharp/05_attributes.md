# 05 · Attributes

Carry a point (a part's centroid) and read it at the placed pose: attributes move with the part.

Project: [Windows](downloads/05_attributes_win.zip) · [macOS](downloads/05_attributes_mac.zip)

```csharp
// 05 attributes — carry a point (the part centroid) and read it at the placed pose.
// Attributes are just extra geometry transformed by the SAME (rotate, translate) the part gets.
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct NpParams {
    public int num_rotations; public double spacing, simplify_tolerance; public int seed;
    public double time_budget_secs; public long iter_budget;
    public int iter_mode, max_sheets, n_starts, part_holes_mode, pole_max, final_compact, fit_mode;
}

static class Program {
    [DllImport("nest_physics", CallingConvention = CallingConvention.Cdecl)]
    static extern int np_nest(int part_count, int[] pvc, double[] pxy, int[] prot,
        int sheet_count, int[] svc, double[] sxy, int[] shc, int[] hvc, double[] hxy,
        int[] phc, int[] phvc, double[] phxy, ref NpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, out int nSheets);

    static void Main() {
        int[] pvc = { 4, 4, 4, 4 };                      // 4 copies of one 30x12 rectangle
        double[] pxy = { 0,0, 30,0, 30,12, 0,12,  0,0, 30,0, 30,12, 0,12,
                         0,0, 30,0, 30,12, 0,12,  0,0, 30,0, 30,12, 0,12 };
        double attrX = 15.0, attrY = 6.0;                // a point at the part's centroid

        int[] svc = { 4 }; double[] sxy = { 0,0, 120,0, 120,120, 0,120 };

        var q = new NpParams { num_rotations = 16, seed = 1, iter_mode = 1, iter_budget = 600, n_starts = 1, max_sheets = 6 };
        var tx = new double[4]; var ty = new double[4]; var ang = new double[4]; var sid = new int[4];
        np_nest(4, pvc, pxy, null, 1, svc, sxy, new int[1], null, null,
                new int[4], null, null, ref q, tx, ty, ang, sid, out _);

        Console.WriteLine("each part's centroid, moved to its placed pose:");
        for (int i = 0; i < 4; i++) {
            if (sid[i] < 0) continue;
            double c = Math.Cos(ang[i]), s = Math.Sin(ang[i]);
            double ax = attrX * c - attrY * s + tx[i];   // rotate about origin, then translate
            double ay = attrX * s + attrY * c + ty[i];
            Console.WriteLine($"  part {i} -> sheet {sid[i]}  centroid at ({ax:F2}, {ay:F2})");
        }
    }
}
```
