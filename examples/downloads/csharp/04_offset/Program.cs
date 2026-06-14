// 04 offset (clearance) — the right way to nest with a GAP between parts: grow every PART and shrink the
// SHEET by the clearance with nfp_offset_polygon, then nest the OFFSET geometry. nfp_nest / np_nest ignore
// the old `spacing` param on purpose — you offset UPSTREAM, exactly like the Grasshopper Geometry/Sheets
// components and the Rhino nest_geo.offset_nesting_boundary / nest_sheets.offset_sheet_boundary methods.
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
    static extern int nfp_offset_polygon(int vertex_count, double[] xy, double delta, double miter_limit,
        int max_out_vertices, double[] out_xy);

    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_nest(int part_count, int[] pvc, double[] pxy, int[] pqty, int[] part_rotations,
        int[] phc, int[] phvc, double[] phxy, int sheet_count, int[] svc, double[] sxy,
        int[] shc, int[] shvc, double[] shxy, ref NfpParams p,
        double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx, out int nSheets, out double fitness);

    // grow (delta>0) / shrink (delta<0) one closed ring; returns just the offset vertices.
    static double[] Offset(double[] xy, double delta) {
        var o = new double[64];
        int n = nfp_offset_polygon(xy.Length / 2, xy, delta, 2.0, 32, o);
        var r = new double[2 * n];
        Array.Copy(o, r, 2 * n);
        return r;
    }

    static void Main() {
        const double CLEARANCE = 4.0;                    // gap between parts AND from the sheet edge

        double[] p0    = { 0, 0, 30, 0, 30, 12, 0, 12 };
        double[] p1    = { 0, 0, 20, 0, 20, 20, 0, 20 };
        double[] sheet = { 0, 0, 120, 0, 120, 120, 0, 120 };

        // grow parts by CLEARANCE/2, shrink the sheet by CLEARANCE/2 -> neighbours keep CLEARANCE apart
        double[] g0 = Offset(p0, CLEARANCE * 0.5);
        double[] g1 = Offset(p1, CLEARANCE * 0.5);
        double[] gs = Offset(sheet, -CLEARANCE * 0.5);

        int[] pvc = { g0.Length / 2, g1.Length / 2 };
        double[] pxy = new double[g0.Length + g1.Length];
        Array.Copy(g0, 0, pxy, 0, g0.Length);
        Array.Copy(g1, 0, pxy, g0.Length, g1.Length);
        int[] pqty = { 4, 4 };                           // 4 copies each -> 8 instances
        int[] svc = { gs.Length / 2 };

        var p = new NfpParams { placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10,
                                seed = 1, curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 10, useParallel = 1 };
        var tx = new double[8]; var ty = new double[8]; var ang = new double[8];
        var sid = new int[8]; var pidx = new int[8];
        int placed = nfp_nest(2, pvc, pxy, pqty, null, new int[2], null, null, 1, svc, gs,
                              new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);

        // The poses fit the OFFSET parts; draw your ORIGINAL parts at (tx,ty,ang) and the CLEARANCE gap shows.
        Console.WriteLine($"clearance {CLEARANCE} -> placed {placed}/8 on {nSheets} sheet(s), fitness {fitness:F3}");
        for (int i = 0; i < 8; i++)
            Console.WriteLine($"  part {pidx[i]} -> sheet {sid[i]}  ({tx[i]:F2}, {ty[i]:F2}) @ {ang[i]:F1} deg");
    }
}
