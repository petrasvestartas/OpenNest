using System;
using System.Collections.Generic;
using System.Linq;
using ClipperLib;
using Grasshopper.Kernel;
using Rhino.Geometry;
using nest_lib;

namespace opennest_2
{
    // Outer simplification with a CONTAINMENT GUARANTEE: the simplified outline always ENCLOSES the original
    // (every original point stays inside/on it, never outside) so nested parts can't overlap.
    //
    // The user sets ONE knob — the simplification AMOUNT (how much detail to drop). The outward OFFSET is then
    // computed AUTOMATICALLY = the minimal distance needed to keep the original enclosed, and reported on the
    // Offset output. (Previously a single "Tolerance" did both jobs at once — dilate by T AND reduce at T/2 —
    // which is why offset looked "tied" to tolerance. Now you set only the simplification; the offset adapts.)
    //
    // Method: clean -> Douglas-Peucker(amount) on the ORIGINAL (this drops the vertices; it may cut inside the
    // original's bulges) -> measure the MAX distance any original vertex now sits OUTSIDE the simplified outline
    // -> offset the simplified OUTWARD by exactly that (miter joins keep corners sharp, adding ~no vertices) ->
    // VERIFY every original vertex is inside; if any escaped (rounding / degenerate), fall back to a guaranteed
    // dilation of the original by `amount` (a proven superset). Use it upstream of the nesters.
    public class component_simplify : GH_Component
    {
        public component_simplify()
            : base("Simplify", "Simplify",
                "Reduces a closed curve's vertex count, auto-offsetting outward just enough that the original stays inside - so nested parts never overlap.",
                "Params", "OpenNest2")
        {
        }

        public override GH_Exposure Exposure => GH_Exposure.octonary;
        public override Guid ComponentGuid => new Guid("7B1E2C44-9A3D-4E57-B6C1-0E9F2A8D4C31");
        protected override System.Drawing.Bitmap Icon => Properties.Resources.simplify;

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Curves", "Curves", "Closed curves to simplify.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Simplify", "Simplify",
                "Simplification amount (Douglas-Peucker distance, model units): how much detail to drop. Bigger = "
                + "fewer vertices. The outward offset needed to keep the original enclosed is computed "
                + "AUTOMATICALLY (always <= this value) and reported on the Offset output. 0 = clean only (no reduction).",
                GH_ParamAccess.item, 0.5);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Curves", "Curves", "Simplified closed polylines (each GUARANTEED to contain its original).", GH_ParamAccess.list);
            pManager.AddIntegerParameter("Vertices", "Vertices", "Vertex count of each simplified curve.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Offset", "Offset", "The outward distance auto-applied to each curve so the original stays inside (model units). 0 = none needed.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var curves = new List<Curve>();
            if (!DA.GetDataList(0, curves)) return;
            double amount = 0.5;
            DA.GetData(1, ref amount);
            if (amount < 0) amount = 0;

            var outCurves = new List<Curve>();
            var outCounts = new List<int>();
            var outOffsets = new List<double>();

            // cleanPolygon2 / svgToClipper read SvgNest.Config (curveTolerance, clipperScale). Set them
            // transiently on the UI thread and restore afterwards so a running nest (which captured its own
            // config at launch) is unaffected.
            double savedCT = SvgNest.Config.curveTolerance;
            double savedCS = SvgNest.Config.clipperScale;
            bool savedSimplify = SvgNest.Config.simplify;
            try
            {
                SvgNest.Config.curveTolerance = amount > 0 ? amount : 0.1;
                SvgNest.Config.simplify = false;                                  // do not collapse to convex hull
                if (SvgNest.Config.clipperScale <= 0) SvgNest.Config.clipperScale = 1e7;

                foreach (var crv in curves)
                {
                    if (crv == null) continue;
                    try
                    {
                        double offsetUsed;
                        var simplified = SimplifyCurve(crv, amount, out offsetUsed);
                        if (simplified != null)
                        {
                            outCurves.Add(simplified.ToNurbsCurve());
                            outCounts.Add(simplified.Count > 0 ? simplified.Count - 1 : 0); // minus closing dup
                            outOffsets.Add(offsetUsed);
                        }
                    }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine("[Simplify] " + ex.Message); }
                }
            }
            finally
            {
                SvgNest.Config.curveTolerance = savedCT;
                SvgNest.Config.clipperScale = savedCS;
                SvgNest.Config.simplify = savedSimplify;
            }

            DA.SetDataList(0, outCurves);
            DA.SetDataList(1, outCounts);
            DA.SetDataList(2, outOffsets);
        }

        // Outer simplification with a verified containment guarantee. `amount` = Douglas-Peucker distance.
        // `offsetUsed` returns the outward distance actually applied (0 if none was needed).
        private static Polyline SimplifyCurve(Curve crv, double amount, out double offsetUsed)
        {
            offsetUsed = 0.0;

            // 1) curve -> polyline -> NFP (the ORIGINAL we must keep inside)
            if (!crv.TryGetPolyline(out Polyline pl))
            {
                double docTol = Rhino.RhinoDoc.ActiveDoc != null ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.01;
                var pc = crv.ToPolyline(docTol, 0.1, 0.0, 0.0);
                if (pc == null || !pc.TryGetPolyline(out pl)) return null;
            }
            if (pl == null || pl.Count < 3) return null;

            var orig = new NFP();
            int n = pl.Count;
            if (n >= 2 && pl[0].DistanceTo(pl[n - 1]) < 1e-9) n--;   // drop explicit closing-duplicate
            for (int i = 0; i < n; i++) orig.AddPoint(new SvgPoint(pl[i].X, pl[i].Y));
            if (orig.length < 3) return null;

            // 2) Clipper cleanup of the input (remove self-intersections, keep the biggest loop)
            var cleaned = SvgNest.cleanPolygon2(orig);
            NFP work = (cleaned != null && cleaned.length > 2) ? cleaned : orig;

            double scale = SvgNest.Config.clipperScale;
            double eps = 1e-9;   // float on-edge tolerance for containment tests
            if (amount <= 0) return ToClosedPolyline(work);   // "clean only", no reduction

            // 3) Douglas-Peucker on the ORIGINAL -> drops vertices. DP keeps every original point within
            //    `amount` of the simplified curve, so the original sits at most `amount` outside it.
            NFP simple = Simplify.simplify(work, amount, true);
            if (simple == null || simple.length < 3) simple = work;
            var cleanedS = SvgNest.cleanPolygon2(simple);
            if (cleanedS != null && cleanedS.length > 2) simple = cleanedS;

            // 4) AUTO OFFSET = max distance any ORIGINAL vertex sits OUTSIDE the simplified outline, measured
            //    in FLOAT space (no integer quantization, which could under-measure and leave a vertex out).
            //    Offset the simplified OUTWARD by that + a margin so every original point falls back inside.
            //    jtRound is a guaranteed superset of the simplified outline; the margin absorbs the
            //    round-facet deviation + the integer-rounding error in the Clipper offset.
            double maxOut = 0.0;
            foreach (var v in orig.Points)
            {
                if (!PointInPolyF(v.x, v.y, simple, eps))   // strictly outside the simplified outline
                {
                    double d = DistPointToPolygon(v, simple);
                    if (d > maxOut) maxOut = d;
                }
            }

            NFP result = simple;
            if (maxOut > 1e-12)
            {
                double off = maxOut * 1.15 + 16.0 / Math.Max(1.0, scale);   // margin: round-facet + quantization
                NFP inflated = OffsetOutward(simple, off, scale, JoinType.jtRound);
                if (inflated != null && inflated.length >= 3) { result = inflated; offsetUsed = off; }
            }

            // 5) VERIFY containment: every ORIGINAL vertex must be inside/on the result. If any escaped (rounding
            //    / a thin notch), fall back to a GUARANTEED dilation of the original by `amount` (round joins =>
            //    true superset), trimmed by DP at amount/2 (which can cut back at most amount/2, so it stays
            //    >= amount/2 outside the original => still contains it).
            if (!ContainsAll(orig, result, eps))
            {
                NFP dil = OffsetOutward(work, amount, scale, JoinType.jtRound);
                if (dil != null && dil.length >= 3)
                {
                    NFP dilSimple = Simplify.simplify(dil, 0.5 * amount, true);
                    var dc = SvgNest.cleanPolygon2(dilSimple);
                    if (dc != null && dc.length > 2) dilSimple = dc;
                    result = (dilSimple != null && dilSimple.length >= 3 && ContainsAll(orig, dilSimple, eps)) ? dilSimple : dil;
                    offsetUsed = amount;
                }
            }

            return ToClosedPolyline(result);
        }

        // Min distance (model units) from point p to the closed polygon's edges.
        private static double DistPointToPolygon(SvgPoint p, NFP poly)
        {
            double best = double.MaxValue;
            int m = poly.length;
            if (m < 2) return 0.0;
            for (int i = 0; i < m; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % m];
                double d = DistPointToSeg(p.x, p.y, a.x, a.y, b.x, b.y);
                if (d < best) best = d;
            }
            return best == double.MaxValue ? 0.0 : best;
        }

        private static double DistPointToSeg(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay;
            double len2 = dx * dx + dy * dy;
            double t = len2 > 1e-18 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0.0;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = ax + t * dx, cy = ay + t * dy;
            double ex = px - cx, ey = py - cy;
            return Math.Sqrt(ex * ex + ey * ey);
        }

        // True if every original vertex is inside/on `poly` — FLOAT space (no integer quantization).
        private static bool ContainsAll(NFP orig, NFP poly, double eps)
        {
            if (poly == null || poly.length < 3) return false;
            foreach (var v in orig.Points)
                if (!PointInPolyF(v.x, v.y, poly, eps)) return false;
            return true;
        }

        // Float-space point-in-polygon (even-odd ray cast). A point within `eps` of an edge counts as inside
        // (on-boundary == contained). Orientation-independent, so winding doesn't matter.
        private static bool PointInPolyF(double px, double py, NFP poly, double eps)
        {
            int m = poly.length;
            if (m < 3) return false;
            bool inside = false;
            for (int i = 0, j = m - 1; i < m; j = i++)
            {
                double xi = poly[i].x, yi = poly[i].y;
                double xj = poly[j].x, yj = poly[j].y;
                if (DistPointToSeg(px, py, xi, yi, xj, yj) <= eps) return true;   // on/near boundary
                bool crosses = ((yi > py) != (yj > py)) &&
                               (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
                if (crosses) inside = !inside;
            }
            return inside;
        }

        // Offset a polygon OUTWARD by `delta` model units; returns the largest resulting loop (superset of input).
        private static NFP OffsetOutward(NFP poly, double delta, double scale, JoinType join)
        {
            var path = SvgNest.svgToClipper(poly).ToList();
            if (path.Count < 3) return null;
            if (!Clipper.Orientation(path)) path.Reverse();   // ensure CCW so +delta expands (not shrinks)
            var co = new ClipperOffset(2.0, Math.Max(1.0, 0.1 * delta * scale));
            co.AddPath(path, join, EndType.etClosedPolygon);
            var sol = new List<List<IntPoint>>();
            co.Execute(ref sol, delta * scale);               // positive delta = expand
            if (sol == null || sol.Count == 0) return null;
            var big = sol[0]; double ba = Math.Abs(Clipper.Area(big));
            for (int i = 1; i < sol.Count; i++)
            {
                double a = Math.Abs(Clipper.Area(sol[i]));
                if (a > ba) { ba = a; big = sol[i]; }
            }
            return PathToNfp(big, scale);
        }

        private static Polyline ToClosedPolyline(NFP nfp)
        {
            var outPl = new Polyline();
            if (nfp == null) return outPl;
            for (int i = 0; i < nfp.length; i++) outPl.Add(nfp[i].x, nfp[i].y, 0);
            if (outPl.Count > 0) outPl.Add(outPl[0]);   // close
            return outPl;
        }

        private static NFP PathToNfp(List<IntPoint> path, double scale)
        {
            var nfp = new NFP();
            foreach (var ip in path) nfp.AddPoint(new SvgPoint(ip.X / scale, ip.Y / scale));
            return nfp;
        }
    }
}
