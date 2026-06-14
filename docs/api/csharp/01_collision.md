# 01 · Collision

Nest parts into a sheet with the physics (collision) engine.

Project: [Windows](downloads/01_collision_win.zip) · [macOS](downloads/01_collision_mac.zip)

```csharp
// 01 collision — nest parts into a sheet with the physics (collision) engine.
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
    static extern int np_nest(int part_count, int[] pvc, double[] pxy, int[] part_rotations,
        int sheet_count, int[] svc, double[] sxy, int[] shc, int[] hvc, double[] hxy,
        int[] phc, int[] phvc, double[] phxy, ref NpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, out int nSheets);

    static void Main() {
        int[] pvc = { 4, 4, 4, 3 };
        double[] pxy = { 0,0, 30,0, 30,12, 0,12,  0,0, 20,0, 20,20, 0,20,
                         0,0, 40,0, 40,8, 0,8,    0,0, 24,0, 0,24 };
        int[] svc = { 4 }; double[] sxy = { 0,0, 150,0, 150,150, 0,150 };

        var q = new NpParams { num_rotations = 16, seed = 1, iter_mode = 0, time_budget_secs = 2.0,
                               n_starts = 1, max_sheets = 6, fit_mode = 0 };
        var tx = new double[4]; var ty = new double[4]; var ang = new double[4]; var sid = new int[4];
        int rc = np_nest(4, pvc, pxy, null, 1, svc, sxy, new int[1], null, null,
                         new int[4], null, null, ref q, tx, ty, ang, sid, out int nSheets);

        Console.WriteLine($"np_nest rc={rc}, {nSheets} sheet(s)");
        for (int i = 0; i < 4; i++)
            Console.WriteLine($"  part {i} -> sheet {sid[i]}  ({tx[i]:F2}, {ty[i]:F2}) @ {ang[i]:F3} rad");
    }
}
```
