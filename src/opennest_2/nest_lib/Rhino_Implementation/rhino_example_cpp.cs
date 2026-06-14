using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NfpNest;
using Rhino;
using Rhino.Geometry;

namespace nest_lib
{
    // C++ DLL solver backend for rhino_example. Builds the same parts/sheets the managed engine uses,
    // calls nfp_nest.dll (Boost.Polygon NFP + GA), and writes the placement back into context.Polygons
    // (x, y, rotation, sheet) so get_results / the GH output assembly are reused verbatim.
    public partial class rhino_example
    {
        private void run_cpp()
        {
            // Build partsLocal (offset display geometry) + assign ids exactly like the managed path.
            context.init();

            int N = context.Polygons.Count;
            if (N == 0 || context.Sheets.Count == 0) return;

            // Sheet backdrop for the live preview (layout frame), matching the managed path.
            try { var ls = new List<Polyline>(); foreach (var s in context.Sheets) ls.AddRange(s.ToPolylines()); LiveSheets = ls; } catch { }

            // --- group part instances by source (identical geometry shares NFPs in the DLL) ---
            var srcOrder = new List<int>();
            var instancesBySource = new Dictionary<int, List<int>>();
            for (int i = 0; i < N; i++)
            {
                int s = (int)context.Polygons[i].source;
                if (!instancesBySource.TryGetValue(s, out var list))
                {
                    list = new List<int>();
                    instancesBySource[s] = list;
                    srcOrder.Add(s);
                }
                list.Add(i);
            }
            int G = srcOrder.Count;

            // --- part arrays (UN-offset XY geometry; the DLL applies the spacing offset internally) ---
            var pvc = new List<int>();
            var pxy = new List<double>();
            var pqty = new List<int>();
            var prot = new List<int>();   // per-part rotation-count override (0 = inherit)
            var phc = new List<int>();
            var phvc = new List<int>();
            var phxy = new List<double>();
            foreach (int s in srcOrder)
            {
                var rep = context.Polygons[instancesBySource[s][0]];
                pvc.Add(rep.Points.Length);
                foreach (var p in rep.Points) { pxy.Add(p.x); pxy.Add(p.y); }
                pqty.Add(instancesBySource[s].Count);
                prot.Add(Math.Max(0, rep.rotationCount));
                int nh = rep.children != null ? rep.children.Count : 0;
                phc.Add(nh);
                if (rep.children != null)
                    foreach (var h in rep.children)
                    {
                        phvc.Add(h.Points.Length);
                        foreach (var hp in h.Points) { phxy.Add(hp.x); phxy.Add(hp.y); }
                    }
            }

            // Diagnostic: how many PART HOLES were detected + sent to the engine (read it in the Rhino
            // command line). >0 => element holes reached nfp_nest (it will nest parts into them); 0 => no
            // holes paired upstream (input not closed, or outer+hole not grouped/contained).
            int _phSum = 0; foreach (var _h in phc) _phSum += _h;
            Rhino.RhinoApp.WriteLine("[nfp_nest] parts=" + G + " part_holes_sent=" + _phSum + " useHoles=" + UseHoles);

            // --- sheet arrays (normalized so each sheet's min-corner is at origin; we re-add the C#
            //     sheet's world position on output) ---
            var svc = new List<int>();
            var sxy = new List<double>();
            var shc = new List<int>();
            var shvc = new List<int>();
            var shxy = new List<double>();
            int S = context.Sheets.Count;
            var sheetOx = new double[S];
            var sheetOy = new double[S];
            for (int i = 0; i < S; i++)
            {
                var sh = context.Sheets[i];
                double minx = double.MaxValue, miny = double.MaxValue;
                foreach (var p in sh.Points) { if (p.x < minx) minx = p.x; if (p.y < miny) miny = p.y; }
                if (sh.Points.Length == 0) { minx = 0; miny = 0; }
                sheetOx[i] = minx; sheetOy[i] = miny;
                svc.Add(sh.Points.Length);
                foreach (var p in sh.Points) { sxy.Add(p.x - minx); sxy.Add(p.y - miny); }
                int nh = sh.children != null ? sh.children.Count : 0;
                shc.Add(nh);
                if (sh.children != null)
                    foreach (var h in sh.children)
                    {
                        shvc.Add(h.Points.Length);
                        foreach (var hp in h.Points) { shxy.Add(hp.x - minx); shxy.Add(hp.y - miny); }
                    }
            }

            var pr = new NfpParams
            {
                placementType  = (int)SvgNest.Config.placementType,
                rotations      = Math.Max(1, SvgNest.Config.rotations),
                mutationRate   = SvgNest.Config.mutationRate,
                populationSize = Math.Max(1, SvgNest.Config.populationSize),
                seed           = SvgNest.Config.seed,
                curveTolerance = SvgNest.Config.curveTolerance,
                clipperScale   = SvgNest.Config.clipperScale,
                spacing        = SvgNest.Config.spacing,
                sheetSpacing   = SvgNest.Config.sheetSpacing,
                rotationLimit  = SvgNest.Config.rotation_limit,
                useHoles       = UseHoles,
                exploreConcave = SvgNest.Config.exploreConcave ? 1 : 0,
                clipByHull     = SvgNest.Config.clipByHull ? 1 : 0,
                clipByRects    = SvgNest.Config.clipByRects ? 1 : 0,
                simplify       = SvgNest.Config.simplify ? 1 : 0,
                // Production: canonical DISCRETE-rotation SVGnest GA (correct concave placement) with
                // PARALLEL NFP precompute -> ~3x faster cold first generation. tryAllRotations is the
                // max-tightness opt-in (4-5x slower first run); off by default. Compaction off (the
                // C++ compaction was empirically worse + slower). More rotations (num_of_rotations 8)
                // packs tighter and is ~free with the parallel cache.
                mode           = 1,
                generations    = Math.Max(1, max_iterations),
                numSeeds       = 0,
                useParallel    = 1,
                timeBudgetSecs = 0,
                maxSheets      = 0,
                edgeSamples      = -1,
                compactionPasses = 0,
                tryAllRotations  = TryAllRotations,
                exactNfp         = ExactNfp
            };

            var tx = new double[N]; var ty = new double[N]; var ang = new double[N];
            var sid = new int[N]; var pidx = new int[N];
            int nSheets = 0; double fitness = 0; int placed = 0;

            NfpNestWrapper.nfp_cancel_reset();

            int[] pvcA = pvc.ToArray(), pqtyA = pqty.ToArray(), protA = prot.ToArray(), phcA = phc.ToArray(), phvcA = phvc.ToArray();
            double[] pxyA = pxy.ToArray(), phxyA = phxy.ToArray();
            int[] svcA = svc.ToArray(), shcA = shc.ToArray(), shvcA = shvc.ToArray();
            double[] sxyA = sxy.ToArray(), shxyA = shxy.ToArray();

            var solveTask = Task.Run(() =>
            {
                placed = NfpNestWrapper.nfp_nest(
                    G, pvcA, pxyA, pqtyA, protA, phcA, phvcA, phxyA,
                    S, svcA, sxyA, shcA, shvcA, shxyA,
                    ref pr, tx, ty, ang, sid, pidx, out nSheets, out fitness);
            });

            // Poll for live preview + cooperative cancel while the native solve runs.
            var ptx = new double[N]; var pty = new double[N]; var pang = new double[N];
            var psid = new int[N]; var ppidx = new int[N];
            bool cancelSent = false;
            while (!solveTask.IsCompleted)
            {
                if (StopRequested && !cancelSent) { NfpNestWrapper.nfp_cancel(); cancelSent = true; }
                try
                {
                    CurrentGeneration = (int)NfpNestWrapper.nfp_progress();
                    CurrentFitness = NfpNestWrapper.nfp_fitness();
                    int pc = NfpNestWrapper.nfp_poll_layout(N, ptx, pty, pang, psid, ppidx, out int pns);
                    if (pc > 0)
                    {
                        applyDllLayout(srcOrder, instancesBySource, sheetOx, sheetOy, ptx, pty, pang, psid, ppidx);
                        _liveBorders = BuildLayoutBorders();
                    }
                }
                catch { }
                Thread.Sleep(80);
            }
            try { solveTask.Wait(); } catch (Exception ex) { RhinoApp.WriteLine(ex.ToString()); }

            // Final layout.
            applyDllLayout(srcOrder, instancesBySource, sheetOx, sheetOy, tx, ty, ang, sid, pidx);
            CurrentGeneration = (int)Math.Max(1, max_iterations);
            CurrentFitness = fitness;
        }

        // Write a DLL layout (per-instance tx/ty/angle/sheetId in expansion order) into context.Polygons.
        // Converts the DLL's rotate-about-origin convention to the C# GetTransform (rotate-about-Points[0])
        // convention and re-adds each sheet's world min-corner.
        private void applyDllLayout(
            List<int> srcOrder, Dictionary<int, List<int>> instancesBySource,
            double[] sheetOx, double[] sheetOy,
            double[] tx, double[] ty, double[] ang, int[] sid, int[] pidx)
        {
            for (int i = 0; i < context.Sheets.Count; i++) context.Sheets[i].id = i;
            for (int i = 0; i < context.Polygons.Count; i++)
            {
                context.Polygons[i].id = i;
                context.Polygons[i].sheet = null;
            }

            var counter = new Dictionary<int, int>();
            var usedSheets = new HashSet<int>();
            int N = context.Polygons.Count;
            for (int k = 0; k < N; k++)
            {
                int sPos = pidx[k];
                if (sPos < 0 || sPos >= srcOrder.Count) continue;
                int s = srcOrder[sPos];
                if (!counter.TryGetValue(s, out int occ)) occ = 0;
                var list = instancesBySource[s];
                if (occ >= list.Count) continue;
                int polyIdx = list[occ];
                counter[s] = occ + 1;

                int shid = sid[k];
                var poly = context.Polygons[polyIdx];
                if (shid >= 0 && shid < context.Sheets.Count && poly.Points.Length > 0)
                {
                    double th = ang[k] * Math.PI / 180.0;
                    double c = Math.Cos(th), sn = Math.Sin(th);
                    var p0 = poly.Points[0];
                    double corrX = p0.x * c - p0.y * sn - p0.x;
                    double corrY = p0.x * sn + p0.y * c - p0.y;
                    poly.x = tx[k] + sheetOx[shid] + corrX;
                    poly.y = ty[k] + sheetOy[shid] + corrY;
                    poly.rotation = (float)ang[k];
                    poly.sheet = context.Sheets[shid];
                    usedSheets.Add(shid);
                }
                else
                {
                    poly.x = -1000; poly.y = 0; poly.sheet = null;
                }
            }
            context.SheetsNotUsed = context.Sheets.Count - usedSheets.Count;
        }
    }
}
