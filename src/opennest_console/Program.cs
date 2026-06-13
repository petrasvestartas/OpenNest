using System;
using System.Collections.Generic;
using System.IO;
using NestPhysics;
using NfpNest;

namespace OpenNestConsole
{
    // Standalone, Rhino-free demonstration of BOTH OpenNest engines on the 48-element example:
    //   * OpenNestCollision  -> nest_physics.dll / np_nest   (penetration-depth overlap relaxation)
    //   * OpenNest2          -> nfp_nest.dll   / nfp_nest    (Boost.Polygon NFP + genetic algorithm)
    // It parses sample_polygons.svg into plain polygon rings, flattens them into the C-ABI arrays, runs
    // each solver, and writes a result SVG per engine. No RhinoCommon / no nest_geo / no Grasshopper.
    internal static class Program
    {
        // The C++ CLI (nest_physics.cpp) packs the example into a fixed 510x635 sheet and skips the first
        // (decorative "CONTAINER") polyline; we match that so results are directly comparable.
        private const double SheetW = 510.0;
        private const double SheetH = 635.0;
        private const int MaxSheets = 6;

        private const string DefaultSvg = @"C:\pc\3_code\code_cpp\shadoks-CGSHOP2024\sample_polygons.svg";

        private static int Main(string[] args)
        {
            string svgPath = args.Length > 0 ? args[0] : DefaultSvg;
            int budget = args.Length > 1 && int.TryParse(args[1], out int b) ? b : 2000;

            Console.WriteLine("OpenNest standalone console demo");
            Console.WriteLine("================================");

            List<double[]> parts = LoadParts(svgPath);
            if (parts.Count == 0)
            {
                Console.WriteLine("No parts to nest. Aborting.");
                return 1;
            }

            double partArea = 0;
            foreach (var p in parts) partArea += Math.Abs(SignedArea(p));
            Console.WriteLine($"Parts: {parts.Count} | total part area: {partArea:0} | sheet: {SheetW:0}x{SheetH:0} (area {SheetW * SheetH:0})");
            Console.WriteLine($"Lower-bound sheets (area only): {Math.Ceiling(partArea / (SheetW * SheetH)):0}");
            Console.WriteLine();

            string outDir = Directory.GetCurrentDirectory();

            RunCollision(parts, budget, Path.Combine(outDir, "out_collision.svg"));
            Console.WriteLine();
            RunNfp(parts, Math.Max(1, budget / 200), Path.Combine(outDir, "out_nfp.svg"));

            Console.WriteLine();
            Console.WriteLine($"Result SVGs written to: {outDir}");
            return 0;
        }

        private static List<double[]> LoadParts(string svgPath)
        {
            if (File.Exists(svgPath))
            {
                var rings = SvgPolylineReader.ReadRings(svgPath);
                Console.WriteLine($"Loaded {rings.Count} polylines from {svgPath}");
                if (rings.Count > 1)
                {
                    // Skip ring[0] (the decorative container), exactly like nest_physics.cpp (first_part = 1).
                    rings.RemoveAt(0);
                    return rings;
                }
            }
            Console.WriteLine($"SVG not found or empty ('{svgPath}'); generating a fallback set of rectangles.");
            return FallbackRectangles();
        }

        // Deterministic fallback parts so the demo runs even without the sample SVG present.
        private static List<double[]> FallbackRectangles()
        {
            var parts = new List<double[]>();
            var sizes = new (double w, double h)[] { (120, 80), (90, 90), (160, 60), (70, 140), (110, 110) };
            for (int i = 0; i < 30; i++)
            {
                var (w, h) = sizes[i % sizes.Length];
                parts.Add(new double[] { 0, 0, w, 0, w, h, 0, h });
            }
            return parts;
        }

        // ---- OpenNestCollision (nest_physics.dll / np_nest) ----
        private static void RunCollision(List<double[]> parts, long iterBudget, string outSvg)
        {
            Console.WriteLine("== OpenNestCollision (nest_physics / np_nest) ==");
            int n = parts.Count;
            NestFlatten.Concat(parts, out int[] pvc, out double[] pxy);

            // One sheet definition; the engine opens up to max_sheets bins of that size (each bin local).
            var sheetRing = RectRing(SheetW, SheetH);
            NestFlatten.Concat(new List<double[]> { sheetRing }, out int[] sovc, out double[] soxy);
            int sheetCount = 1;
            int[] shc = new int[sheetCount];            // no sheet holes
            int[] hvc = new int[1];                     // dummy (counts are 0)
            double[] hxy = new double[1];

            int[] phc = new int[n];                     // no part holes (v1)
            int[] phvc = new int[1];
            double[] phxy = new double[1];

            var np = new NpParams
            {
                num_rotations = 4,
                spacing = 0.0,
                simplify_tolerance = 0.0,
                seed = 100,
                time_budget_secs = 0,
                iter_budget = iterBudget,
                iter_mode = 1,            // budget = relaxation rounds
                max_sheets = MaxSheets,
                n_starts = 1,
                part_holes_mode = 0,
                pole_max = 0,
                final_compact = 1,
                fit_mode = 0
            };

            var tx = new double[n];
            var ty = new double[n];
            var ang = new double[n];
            var sid = new int[n];

            int rc;
            try
            {
                NestPhysicsWrapper.np_cancel_reset();
                Console.WriteLine($"Solving (rounds budget = {iterBudget}, rotations = {np.num_rotations})...");
                rc = NestPhysicsWrapper.np_nest(
                    n, pvc, pxy, sheetCount, sovc, soxy, shc, hvc, hxy,
                    phc, phvc, phxy, null, ref np, tx, ty, ang, sid, out int nSheets);

                if (rc != 0) { Console.WriteLine($"np_nest returned error code {rc}."); return; }

                var placements = new List<NestFlatten.Placement>(n);
                int placed = 0;
                for (int i = 0; i < n; i++)
                {
                    bool ok = sid[i] >= 0;
                    if (ok) placed++;
                    placements.Add(new NestFlatten.Placement
                    {
                        PartIndex = i,
                        SheetId = sid[i],
                        Xy = NestFlatten.PlaceRing(parts[i], ang[i], tx[i], ty[i]) // np angle is radians
                    });
                }
                long rounds = NestPhysicsWrapper.np_progress();
                Console.WriteLine($"Placed {placed}/{n} parts on {nSheets} sheet(s); rounds done = {rounds}.");
                SvgWriter.Write(outSvg, placements, SheetW, SheetH, Math.Max(nSheets, 1));
                Console.WriteLine($"Wrote {outSvg}");
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine("nest_physics.dll not found next to the exe - build it (nest_physics_cpp) or copy it in.");
            }
        }

        // ---- OpenNest2 (nfp_nest.dll / nfp_nest) ----
        private static void RunNfp(List<double[]> parts, int generations, string outSvg)
        {
            Console.WriteLine("== OpenNest2 (nfp_nest / NFP + GA) ==");
            int n = parts.Count;
            NestFlatten.Concat(parts, out int[] pvc, out double[] pxy);
            int[] pqty = new int[n];
            for (int i = 0; i < n; i++) pqty[i] = 1;     // instance_count = n
            int[] phc = new int[n];
            int[] phvc = new int[1];
            double[] phxy = new double[1];

            // Provide several identical sheets so the GA can spill across bins (maxSheets = 0 => use all).
            var sheetList = new List<double[]>();
            for (int s = 0; s < MaxSheets; s++) sheetList.Add(RectRing(SheetW, SheetH));
            NestFlatten.Concat(sheetList, out int[] svc, out double[] sxy);
            int sheetCount = sheetList.Count;
            int[] shc = new int[sheetCount];
            int[] shvc = new int[1];
            double[] shxy = new double[1];

            var pr = new NfpParams
            {
                placementType = 1,        // gravity (bottom-left)
                rotations = 4,
                mutationRate = 10,
                populationSize = 10,
                seed = 100,
                curveTolerance = 0.72,
                clipperScale = 1e7,
                spacing = 2.0,
                sheetSpacing = 0,
                rotationLimit = 360,
                useHoles = 0,
                exploreConcave = 0,
                clipByHull = 0,
                clipByRects = 1,
                simplify = 0,
                mode = 1,
                generations = generations,
                numSeeds = 0,
                useParallel = 1,
                timeBudgetSecs = 0,
                maxSheets = 0,
                edgeSamples = -1,
                compactionPasses = 0,
                tryAllRotations = 0,
                exactNfp = 0
            };

            int instanceCount = n;
            var tx = new double[instanceCount];
            var ty = new double[instanceCount];
            var ang = new double[instanceCount];
            var sid = new int[instanceCount];
            var pidx = new int[instanceCount];

            try
            {
                NfpNestWrapper.nfp_cancel_reset();
                Console.WriteLine($"Solving (generations = {generations}, rotations = {pr.rotations})...");
                int placedCount = NfpNestWrapper.nfp_nest(
                    n, pvc, pxy, pqty, phc, phvc, phxy, null,
                    sheetCount, svc, sxy, shc, shvc, shxy,
                    ref pr, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);

                if (placedCount < 0) { Console.WriteLine($"nfp_nest returned error code {placedCount}."); return; }

                var placements = new List<NestFlatten.Placement>(instanceCount);
                int placed = 0;
                for (int k = 0; k < instanceCount; k++)
                {
                    int src = pidx[k] >= 0 && pidx[k] < n ? pidx[k] : 0;
                    bool ok = sid[k] >= 0;
                    if (ok) placed++;
                    double angRad = ang[k] * Math.PI / 180.0;   // nfp angle is DEGREES
                    placements.Add(new NestFlatten.Placement
                    {
                        PartIndex = src,
                        SheetId = sid[k],
                        Xy = NestFlatten.PlaceRing(parts[src], angRad, tx[k], ty[k])
                    });
                }
                Console.WriteLine($"Placed {placed}/{instanceCount} instances on {nSheets} sheet(s); fitness = {fitness:0.###}.");
                SvgWriter.Write(outSvg, placements, SheetW, SheetH, Math.Max(nSheets, 1));
                Console.WriteLine($"Wrote {outSvg}");
            }
            catch (DllNotFoundException)
            {
                Console.WriteLine("nfp_nest.dll not found. Build the OpenNest2 engine first:");
                Console.WriteLine("    cmake -S opennest_cpp -B opennest_cpp/build -DCMAKE_BUILD_TYPE=Release");
                Console.WriteLine("    cmake --build opennest_cpp/build --config Release");
                Console.WriteLine("then copy opennest_cpp/build/Release/nfp_nest.dll next to this exe and re-run.");
            }
        }

        private static double[] RectRing(double w, double h) => new double[] { 0, 0, w, 0, w, h, 0, h };

        // Shoelace signed area (for the material-utilization readout).
        private static double SignedArea(double[] ring)
        {
            double a = 0;
            int nv = ring.Length / 2;
            for (int i = 0; i < nv; i++)
            {
                int j = (i + 1) % nv;
                a += ring[2 * i] * ring[2 * j + 1] - ring[2 * j] * ring[2 * i + 1];
            }
            return a * 0.5;
        }
    }
}
