// OpenNest — standalone C# console example (no Rhino).
//
// Runs the 8 examples from the docs site (the same set as the compas_nest Python examples) by
// P/Invoking the two native engines directly. The native libraries (nfp_nest, nest_physics) are
// resolved by bare name, so they must sit next to this exe — the .csproj copies them after build,
// and the CMake superbuild builds them first. See docs/api/csharp/.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
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

    const string NFP = "nfp_nest";
    const string NP = "nest_physics";

    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_nest(int part_count, int[] pvc, double[] pxy, int[] pqty, int[] prot,
        int[] phc, int[] phvc, double[] phxy,
        int sheet_count, int[] svc, double[] sxy, int[] shc, int[] shvc, double[] shxy,
        ref NfpParams p, double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx,
        out int nSheets, out double fitness);

    [DllImport(NP, CallingConvention = CallingConvention.Cdecl)]
    static extern int np_nest(int part_count, int[] pvc, double[] pxy, int[] prot,
        int sheet_count, int[] sovc, double[] soxy, int[] shc, int[] hvc, double[] hxy,
        int[] phc, int[] phvc, double[] phxy,
        ref NpParams p, double[] tx, double[] ty, double[] ang, int[] sid, out int nSheets);

    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_pack(int part_count, int[] pvc, double[] pxy, int[] pqty,
        int columns, double gap_x, double gap_y, double max_width,
        double[] tx, double[] ty, double[] ang, int[] sid);

    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_offset_polygon(int vertex_count, double[] xy, double delta,
        double miter_limit, int max_out_vertices, double[] outXy);

    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    static extern int nfp_text_to_polylines(string text, double height, int font, double spacing,
        int max_strokes, int[] vcount, int max_points, double[] xy, out int total);

    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)] static extern long nfp_progress();
    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)] static extern double nfp_fitness();
    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)] static extern void nfp_cancel();
    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)] static extern void nfp_cancel_reset();
    [DllImport(NFP, CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_poll_layout(int instance_count, double[] tx, double[] ty, double[] ang,
        int[] sid, int[] pidx, out int nSheets);

    // ---- small flat-geometry helpers ----
    static void Rect(List<int> vc, List<double> xy, double w, double h) { vc.Add(4); xy.AddRange(new[] { 0d, 0, w, 0, w, h, 0, h }); }
    static int Sum(int[] a) { int s = 0; foreach (var v in a) s += v; return s; }

    static void Main()
    {
        // 01 · Collision — physics nest (np_nest), one output slot per part.
        {
            var pvc = new List<int>(); var pxy = new List<double>();
            for (int i = 0; i < 4; i++) Rect(pvc, pxy, 30, 12);
            for (int i = 0; i < 3; i++) Rect(pvc, pxy, 20, 20);
            int pc = pvc.Count;
            var svc = new List<int>(); var sxy = new List<double>(); Rect(svc, sxy, 120, 120);
            var q = new NpParams { num_rotations = 16, seed = 1, iter_mode = 1, iter_budget = 800, n_starts = 1, max_sheets = 6 };
            var tx = new double[pc]; var ty = new double[pc]; var ang = new double[pc]; var sid = new int[pc];
            int rc = np_nest(pc, pvc.ToArray(), pxy.ToArray(), null, 1, svc.ToArray(), sxy.ToArray(),
                new int[1], null, null, new int[pc], null, null, ref q, tx, ty, ang, sid, out int ns);
            int placed = 0; foreach (var s in sid) if (s >= 0) placed++;
            Console.WriteLine($"01 collision    : rc={rc}, placed {placed}/{pc} on {ns} sheet(s)");
        }

        // 02 · NFP + GA — nfp_nest, one output slot per instance (sum of quantities).
        {
            var pvc = new List<int>(); var pxy = new List<double>();
            Rect(pvc, pxy, 30, 12); Rect(pvc, pxy, 20, 20); pvc.Add(3); pxy.AddRange(new[] { 0d, 0, 22, 0, 0, 22 });
            int pc = pvc.Count; var pqty = new[] { 4, 2, 4 };
            var svc = new List<int>(); var sxy = new List<double>(); Rect(svc, sxy, 120, 120);
            int inst = Sum(pqty);
            var p = new NfpParams { placementType = 1, rotations = 8, mutationRate = 10, populationSize = 10, seed = 7, curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 20, useParallel = 1, useHoles = 1, numSeeds = 4 };
            var tx = new double[inst]; var ty = new double[inst]; var ang = new double[inst]; var sid = new int[inst]; var pidx = new int[inst];
            int placed = nfp_nest(pc, pvc.ToArray(), pxy.ToArray(), pqty, null, new int[pc], null, null,
                1, svc.ToArray(), sxy.ToArray(), new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out int ns, out double fit);
            Console.WriteLine($"02 nfp+ga       : placed {placed}/{inst} instance(s), fitness {fit:F3}");
        }

        // 03 · Live animation — run nfp_nest on a thread; poll the evolving best layout.
        {
            var pvc = new List<int>(); var pxy = new List<double>();
            for (int i = 0; i < 12; i++) Rect(pvc, pxy, 20 + i % 4 * 6, 14);
            int pc = pvc.Count; var pqty = new int[pc]; for (int i = 0; i < pc; i++) pqty[i] = 1;
            var svc = new List<int>(); var sxy = new List<double>(); Rect(svc, sxy, 200, 200);
            var p = new NfpParams { placementType = 1, rotations = 8, mutationRate = 10, populationSize = 10, seed = 7, curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 200, useParallel = 1, numSeeds = 4 };
            var tx = new double[pc]; var ty = new double[pc]; var ang = new double[pc]; var sid = new int[pc]; var pidx = new int[pc];
            nfp_cancel_reset();
            var task = Task.Run(() => nfp_nest(pc, pvc.ToArray(), pxy.ToArray(), pqty, null, new int[pc], null, null,
                1, svc.ToArray(), sxy.ToArray(), new int[1], null, null, ref p, tx, ty, ang, sid, pidx, out _, out _));
            for (int frame = 0; frame < 3 && !task.IsCompleted; frame++)
            {
                Thread.Sleep(120);
                var ptx = new double[pc]; var pty = new double[pc]; var pang = new double[pc]; var psid = new int[pc]; var ppidx = new int[pc];
                int snap = nfp_poll_layout(pc, ptx, pty, pang, psid, ppidx, out _);
                Console.WriteLine($"03 live         : gen {nfp_progress()}, fitness {nfp_fitness():F3}, placed {snap} so far");
            }
            nfp_cancel(); task.Wait();
        }

        // 04 · Clearance offset — grow a part / shrink a sheet with nfp_offset_polygon, then nest.
        {
            double[] part = { 0, 0, 30, 0, 30, 12, 0, 12 };
            double[] grown = new double[64];
            int gn = nfp_offset_polygon(4, part, 1.0, 2.0, 32, grown);   // +1 clearance
            double[] sheet = { 0, 0, 120, 0, 120, 120, 0, 120 };
            double[] shrunk = new double[64];
            int sn = nfp_offset_polygon(4, sheet, -1.0, 2.0, 32, shrunk);  // -1 clearance
            // nest the offset part (6 copies) onto the offset sheet
            var pvc = new[] { gn }; var pxy = new double[gn * 2]; Array.Copy(grown, pxy, gn * 2);
            var svc = new[] { sn }; var sxy = new double[sn * 2]; Array.Copy(shrunk, sxy, sn * 2);
            var q = new NpParams { num_rotations = 16, seed = 1, iter_mode = 1, iter_budget = 800, n_starts = 1, max_sheets = 6 };
            var pqty = new[] { 6 }; var pcopies = new List<int>(); var pxy2 = new List<double>();
            for (int c = 0; c < 6; c++) { pcopies.Add(gn); pxy2.AddRange(pxy); }
            int pc = 6;
            var tx = new double[pc]; var ty = new double[pc]; var ang = new double[pc]; var sid = new int[pc];
            int rc = np_nest(pc, pcopies.ToArray(), pxy2.ToArray(), null, 1, svc, sxy, new int[1], null, null,
                new int[pc], null, null, ref q, tx, ty, ang, sid, out int ns);
            int placed = 0; foreach (var s in sid) if (s >= 0) placed++;
            Console.WriteLine($"04 offset       : part {4}->{gn} pts, sheet {4}->{sn} pts; placed {placed}/6 with 1.0 clearance");
        }

        // 05 · Attributes — carry a point at a part's centroid; transform it to the placed pose.
        {
            double[] outline = { 0, 0, 30, 0, 30, 12, 0, 12 };
            double cx = 15, cy = 6;   // centroid
            var pvc = new List<int>(); var pxy = new List<double>();
            for (int i = 0; i < 4; i++) { pvc.Add(4); pxy.AddRange(outline); }
            int pc = pvc.Count;
            var svc = new List<int>(); var sxy = new List<double>(); Rect(svc, sxy, 120, 120);
            var q = new NpParams { num_rotations = 16, seed = 1, iter_mode = 1, iter_budget = 600, n_starts = 1, max_sheets = 6 };
            var tx = new double[pc]; var ty = new double[pc]; var ang = new double[pc]; var sid = new int[pc];
            np_nest(pc, pvc.ToArray(), pxy.ToArray(), null, 1, svc.ToArray(), sxy.ToArray(), new int[1], null, null,
                new int[pc], null, null, ref q, tx, ty, ang, sid, out _);
            int withAttr = 0;
            for (int i = 0; i < pc; i++)
            {
                if (sid[i] < 0) continue;
                double c = Math.Cos(ang[i]), s = Math.Sin(ang[i]);
                double ax = cx * c - cy * s + tx[i], ay = cx * s + cy * c + ty[i];   // placed centroid
                withAttr++;
                if (i == 0) Console.Write($"05 attributes   : centroid of part 0 placed at ({ax:F2}, {ay:F2}); ");
            }
            Console.WriteLine($"{withAttr} placed part(s) carry their marker");
        }

        // 06 · Pack (array) — deterministic grid, a fixed number of columns per row.
        {
            var pvc = new List<int>(); var pxy = new List<double>();
            Rect(pvc, pxy, 30, 12); Rect(pvc, pxy, 20, 20);
            var pqty = new[] { 6, 6 }; int inst = Sum(pqty);
            var tx = new double[inst]; var ty = new double[inst]; var ang = new double[inst]; var sid = new int[inst];
            int n = nfp_pack(2, pvc.ToArray(), pxy.ToArray(), pqty, 5, 1.0, 1.0, 0.0, tx, ty, ang, sid);
            Console.WriteLine($"06 pack array   : laid out {n} instance(s) in rows of 5");
        }

        // 07 · Pack (distance) — fill a row up to max_width, then wrap.
        {
            var pvc = new List<int>(); var pxy = new List<double>();
            Rect(pvc, pxy, 30, 12); Rect(pvc, pxy, 20, 20);
            var pqty = new[] { 6, 6 }; int inst = Sum(pqty);
            var tx = new double[inst]; var ty = new double[inst]; var ang = new double[inst]; var sid = new int[inst];
            int n = nfp_pack(2, pvc.ToArray(), pxy.ToArray(), pqty, 0, 5.0, 5.0, 120.0, tx, ty, ang, sid);
            Console.WriteLine($"07 pack dist    : laid out {n} instance(s), wrapping at width 120");
        }

        // 08 · Text — render a label to single-stroke engraving polylines (OpenNest VDA font).
        {
            nfp_text_to_polylines("compas_nest\n0 1 2 3", 10.0, 0, -1.0, 0, null, 0, null, out int total);
            var vc = new int[4096]; var xy = new double[total * 2];
            int strokes = nfp_text_to_polylines("compas_nest\n0 1 2 3", 10.0, 0, -1.0, 4096, vc, total, xy, out total);
            Console.WriteLine($"08 text         : {strokes} stroke(s), {total} point(s)");
        }
    }
}
