# 02 · NFP + GA

Nest parts into a sheet with the NFP + genetic-algorithm engine (handles concave parts + holes).

Project: [Windows](downloads/02_nfp_win.zip) · [macOS](downloads/02_nfp_mac.zip)

```csharp
// 02 nfp — nest parts into a sheet with the NFP + genetic-algorithm engine.
using System;
using System.Runtime.InteropServices;

[StructLayout(LayoutKind.Sequential)]
struct NfpParams {
    public int placementType, rotations, mutationRate, populationSize, seed;
    public double curveTolerance, clipperScale, spacing, sheetSpacing, rotationLimit;
    public int useHoles, exploreConcave, clipByHull, clipByRects, simplify, mode, generations, numSeeds, useParallel;
    public double timeBudgetSecs;
    public int maxSheets, edgeSamples, compactionPasses, tryAllRotations, exactNfp;
}

static class Program {
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_nest(int part_count, int[] pvc, double[] pxy, int[] pqty, int[] part_rotations,
        int[] phc, int[] phvc, double[] phxy, int sheet_count, int[] svc, double[] sxy,
        int[] shc, int[] shvc, double[] shxy, ref NfpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx, out int nSheets, out double fitness);

    static void Main() {
        int[] pvc = { 4, 4, 4, 3 };
        double[] pxy = { 0,0, 30,0, 30,12, 0,12,  0,0, 20,0, 20,20, 0,20,
                         0,0, 40,0, 40,8, 0,8,    0,0, 24,0, 0,24 };
        int[] pqty = { 3, 3, 3, 3 };                    // 12 instances
        int[] svc = { 4 }; double[] sxy = { 0,0, 150,0, 150,150, 0,150 };

        var p = new NfpParams { placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10,
                                seed = 1, curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 10, useParallel = 1 };
        var tx = new double[12]; var ty = new double[12]; var ang = new double[12];
        var sid = new int[12]; var pidx = new int[12];
        int placed = nfp_nest(4, pvc, pxy, pqty, null, new int[4], null, null, 1, svc, sxy,
                              new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);

        Console.WriteLine($"placed {placed}/12 on {nSheets} sheet(s), fitness {fitness:F3}");
        for (int i = 0; i < 12; i++)
            Console.WriteLine($"  part {pidx[i]} -> sheet {sid[i]}  ({tx[i]:F2}, {ty[i]:F2}) @ {ang[i]:F1} deg");
    }
}
```
