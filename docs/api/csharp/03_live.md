# 03 · Live animation (NFP)

Run the NFP engine on a background thread and poll the evolving best layout (progress, fitness, live poses).

Project: [Windows](downloads/03_live_win.zip) · [macOS](downloads/03_live_mac.zip)

```csharp
// 03 live — run the NFP engine on a background thread, poll the evolving best layout.
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

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
    static extern int nfp_nest(int part_count, int[] pvc, double[] pxy, int[] pqty, int[] prot,
        int[] phc, int[] phvc, double[] phxy, int sheet_count, int[] svc, double[] sxy,
        int[] shc, int[] shvc, double[] shxy, ref NfpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx, out int nSheets, out double fitness);

    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)] static extern long nfp_progress();
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)] static extern double nfp_fitness();
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)] static extern void nfp_cancel();
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)] static extern void nfp_cancel_reset();
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_poll_layout(int n, double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx, out int nSheets);

    static void Main() {
        var pvc = new int[12]; var pxy = new double[12 * 8];
        for (int i = 0; i < 12; i++) {
            double w = 20 + (i % 4) * 6, h = 14;
            pvc[i] = 4;
            double[] r = { 0,0, w,0, w,h, 0,h };
            Array.Copy(r, 0, pxy, i * 8, 8);
        }
        var pqty = new int[12]; for (int i = 0; i < 12; i++) pqty[i] = 1;
        int[] svc = { 4 }; double[] sxy = { 0,0, 200,0, 200,200, 0,200 };

        var p = new NfpParams { placementType = 1, rotations = 8, populationSize = 10, mutationRate = 10,
                                seed = 7, clipperScale = 1e7, curveTolerance = 0.3, mode = 1,
                                generations = 200, useParallel = 1, numSeeds = 4 };
        var tx = new double[12]; var ty = new double[12]; var ang = new double[12];
        var sid = new int[12]; var pidx = new int[12];

        nfp_cancel_reset();
        var task = Task.Run(() => nfp_nest(12, pvc, pxy, pqty, null, new int[12], null, null,
            1, svc, sxy, new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out _, out _));

        for (int frame = 0; frame < 5; frame++) {
            Thread.Sleep(100);
            var ptx = new double[12]; var pty = new double[12]; var pang = new double[12];
            var psid = new int[12]; var ppidx = new int[12];
            int placed = nfp_poll_layout(12, ptx, pty, pang, psid, ppidx, out _);
            Console.WriteLine($"gen {nfp_progress()}, fitness {nfp_fitness():F3}, placed {placed} so far");
        }
        nfp_cancel();          // ask the solve to stop early
        task.Wait();
        Console.WriteLine("done.");
    }
}
```
