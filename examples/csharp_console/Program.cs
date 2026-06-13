// OpenNest — standalone C# console example (no Rhino).
//
// P/Invokes the two native nesting engines directly: nfp_nest (NFP + GA) and np_nest (physics).
// The native libraries are resolved by bare name, so nfp_nest.dll / nest_physics.dll (or the
// .dylib/.so) must sit next to this exe — the .csproj copies them after build, and the CMake
// superbuild builds them first. See docs/api/csharp.md.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

internal static class Program
{
    // --- parameter blocks (mirror NfpParams / NpParams in the engine headers) ---
    [StructLayout(LayoutKind.Sequential)]
    public struct NfpParams
    {
        public int placementType, rotations, mutationRate, populationSize, seed;
        public double curveTolerance, clipperScale, spacing, sheetSpacing, rotationLimit;
        public int useHoles, exploreConcave, clipByHull, clipByRects, simplify,
                   mode, generations, numSeeds, useParallel;
        public double timeBudgetSecs;
        public int maxSheets, edgeSamples, compactionPasses, tryAllRotations, exactNfp;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NpParams
    {
        public int num_rotations;
        public double spacing, simplify_tolerance;
        public int seed;
        public double time_budget_secs;
        public long iter_budget;
        public int iter_mode, max_sheets, n_starts, part_holes_mode, pole_max, final_compact, fit_mode;
    }

    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_nest(
        int part_count, int[] part_vertex_counts, double[] part_xy, int[] part_quantities,
        int[] part_rotations,   // per-part rotation override (0 = global, 1 = fixed; null ok)
        int[] part_hole_counts, int[] part_hole_vertex_counts, double[] part_hole_xy,
        int sheet_count, int[] sheet_vertex_counts, double[] sheet_xy,
        int[] sheet_hole_counts, int[] sheet_hole_vertex_counts, double[] sheet_hole_xy,
        ref NfpParams parameters,
        double[] out_tx, double[] out_ty, double[] out_angle,
        int[] out_sheet_id, int[] out_part_index, out int out_n_sheets, out double out_fitness);

    [DllImport("nest_physics", CallingConvention = CallingConvention.Cdecl)]
    static extern int np_nest(
        int part_count, int[] part_vertex_counts, double[] part_xy,
        int[] part_rotations,   // per-part rotation override (0 = free continuous; null ok)
        int sheet_count, int[] sheet_outer_vertex_counts, double[] sheet_outer_xy,
        int[] sheet_hole_counts, int[] hole_vertex_counts, double[] hole_xy,
        int[] part_hole_counts, int[] part_hole_vertex_counts, double[] part_hole_xy,
        ref NpParams parameters,
        double[] out_tx, double[] out_ty, double[] out_angle, int[] out_sheet_id, out int out_n_sheets);

    static void Main()
    {
        // ---- build the problem: 3 rectangles + a triangle as parts, one 150x150 sheet ----
        var pvc = new List<int>();
        var pxy = new List<double>();
        void Rect(double w, double h) { pvc.Add(4); pxy.AddRange(new[] { 0d, 0, w, 0, w, h, 0, h }); }
        Rect(30, 12); Rect(20, 20); Rect(40, 8);
        pvc.Add(3); pxy.AddRange(new[] { 0d, 0, 24, 0, 0, 24 });   // a triangle

        int partCount = pvc.Count;
        var pqty = new int[partCount]; for (int i = 0; i < partCount; i++) pqty[i] = 3;   // 3 copies each
        var phc = new int[partCount];                                                     // no part holes

        var svc = new List<int>(); var sxy = new List<double>();
        svc.Add(4); sxy.AddRange(new[] { 0d, 0, 150, 0, 150, 150, 0, 150 });
        int sheetCount = 1; var shc = new int[sheetCount];

        int instances = 0; foreach (var q in pqty) instances += q;

        // ---- nfp_nest: one output slot per INSTANCE (sum of quantities) ----
        var p = new NfpParams
        {
            placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10, seed = 1,
            curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 10, useParallel = 1
        };
        var tx = new double[instances]; var ty = new double[instances]; var ang = new double[instances];
        var sid = new int[instances]; var pidx = new int[instances];
        int placed = nfp_nest(partCount, pvc.ToArray(), pxy.ToArray(), pqty, null, phc, null, null,
            sheetCount, svc.ToArray(), sxy.ToArray(), shc, null, null,
            ref p, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);
        Console.WriteLine($"nfp_nest : placed {placed}/{instances} instances on {nSheets} sheet(s), fitness {fitness:F3}");
        for (int i = 0; i < instances; i++)
            Console.WriteLine($"   part {pidx[i]} -> sheet {sid[i]}  ({tx[i],7:F2}, {ty[i],7:F2}) @ {ang[i],5:F1} deg");

        // ---- np_nest: one output slot per PART (original order) ----
        var q2 = new NpParams { num_rotations = 16, seed = 1, iter_mode = 0, time_budget_secs = 2.0, n_starts = 1, max_sheets = 6, fit_mode = 0 };
        var ntx = new double[partCount]; var nty = new double[partCount]; var nang = new double[partCount];
        var nsid = new int[partCount];
        int rc = np_nest(partCount, pvc.ToArray(), pxy.ToArray(), null, sheetCount, svc.ToArray(), sxy.ToArray(),
            shc, null, null, phc, null, null, ref q2, ntx, nty, nang, nsid, out int nSheets2);
        Console.WriteLine($"np_nest  : rc={rc}, {nSheets2} sheet(s)");
        for (int i = 0; i < partCount; i++)
            Console.WriteLine($"   part {i} -> sheet {nsid[i]}  ({ntx[i],7:F2}, {nty[i],7:F2}) @ {nang[i],6:F3} rad");
    }
}
