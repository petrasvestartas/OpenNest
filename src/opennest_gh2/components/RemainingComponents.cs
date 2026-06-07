using System;
using System.Collections.Generic;
using System.Linq;
using ClipperLib;
using Grasshopper2.Components;
using Grasshopper2.Data;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_lib;

namespace opennest_gh2.components
{
    // Geometry (Rhino): build nesting parts from referenced Rhino objects (GH1 D5A8325A-...-8C32-1A54A5D1A10E).
    // Reads the active document -> UiSingleThreaded.
    [IoId("d5a8325a-5bf3-45a2-8c32-1a54a5d1a10e")]
    public class GeometryRhinoComponent : Component
    {
        public GeometryRhinoComponent() : base(new Nomen("Geometry (Rhino)", "Nesting parts from referenced Rhino objects.", "OpenNest", "Input"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public GeometryRhinoComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("nest_rhino_geo.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddText("Layer", "L", "Boundary layer name in Rhino.", Access.Item, Requirement.MayBeMissing).Set("");
            inputs.AddInteger("Copies", "C", "Copies per branch.", Access.Twig, Requirement.MayBeMissing).Set(1);
            inputs.AddNumber("Simplify", "S", "Simplify params: [segment divisions, hull 0/1].", Access.Twig, Requirement.MayBeMissing).Set(100.0);
            inputs.AddNumber("Sort", "So", "Sort polylines (0 = off).", Access.Item, Requirement.MayBeMissing).Set(1.0);
            inputs.AddNumber("Offset", "O", "Nesting clearance offset (0 = off).", Access.Item, Requirement.MayBeMissing).Set(0.0);
            inputs.AddGeneric("Guid", "G", "Referenced geometry as guid (mesh, brep, curves); one branch per part.", Access.Tree);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geometry", "Geometry", "Assembled nesting geometry for the solver.");
            outputs.AddCurve("Borders", "Borders", "Outline curves per part.", Access.Twig);
            outputs.AddCurve("BC", "BC", "Convex-hull borders per part.", Access.Twig);
            outputs.AddInteger("Groups", "Groups", "Object group indices per part.", Access.Twig);
            outputs.AddGeneric("All", "All", "All referenced geometry per part.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            access.GetItem(0, out string layer); layer = layer ?? "";
            access.GetItemArray(1, out int[] copiesArr);
            access.GetItemArray(2, out double[] simplifyArr);
            access.GetItem(3, out double sort);
            access.GetItem(4, out double offset);
            if (!access.GetTree(5, out Tree<Guid> tree) || tree == null || tree.LeafCount == 0)
            { access.AddWarning("No guids", "Connect referenced object ids."); return; }

            tree.ToArrays(out Guid[][] branches);
            var guids = branches.Where(b => b != null && b.Length > 0).Select(b => b.ToArray()).ToList();
            if (guids.Count == 0) return;
            var copies = (copiesArr != null && copiesArr.Length > 0) ? copiesArr.ToList() : new List<int> { 1 };
            var simplifyParams = (simplifyArr != null && simplifyArr.Length > 0) ? simplifyArr.ToList() : new List<double> { 100, 0 };

            var geo = nest_rhino_lib.nest_geo_util.guid_to_nest_geo(guids, layer, copies, simplifyParams);
            if (offset != 0) geo.offset_nesting_boundary(offset);
            if (sort != 0) geo.sort_groups(sort == 1);

            access.SetItem(0, geo);
            var borders = new List<Curve>(); var bc = new List<Curve>(); var groups = new List<int>(); var all = new List<GeometryBase>();
            foreach (var grp in geo.boundary_sorted)
                foreach (var tup in grp) { if (tup.Item2 != null) borders.Add(tup.Item2.ToNurbsCurve()); if (tup.Item4 != null) bc.Add(tup.Item4); }
            for (int i = 0; i < geo.geometry_sorted.Count; i++)
                foreach (var id in geo.geometry_sorted[i]) { groups.Add(id); if (id >= 0 && id < geo.geometry.Count) all.Add(geo.geometry[id]); }
            access.SetTwig(1, borders.ToArray());
            access.SetTwig(2, bc.ToArray());
            access.SetTwig(3, groups.ToArray());
            access.SetTwig(4, all.ToArray());
        }
    }

    // Simplify: reduce a closed curve's vertex count with a containment guarantee (GH1 7B1E2C44-...).
    // Mutates SvgNest.Config (process-global) + may read the doc tolerance -> UiSingleThreaded.
    [IoId("7b1e2c44-9a3d-4e57-b6c1-0e9f2a8d4c31")]
    public class SimplifyComponent : Component
    {
        public SimplifyComponent() : base(new Nomen("Simplify", "Reduce vertex count, auto-offset so the original stays inside.", "OpenNest", "Util"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public SimplifyComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("simplify.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Curves", "C", "Closed curves to simplify.", Access.Twig);
            inputs.AddNumber("Simplify", "S", "Simplification amount (Douglas-Peucker distance). 0 = clean only.", Access.Item, Requirement.MayBeMissing).Set(0.5);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Curves", "C", "Simplified closed polylines (each contains its original).", Access.Twig);
            outputs.AddInteger("Vertices", "V", "Vertex count of each simplified curve.", Access.Twig);
            outputs.AddNumber("Offset", "O", "Outward distance auto-applied per curve.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Curve[] curves) || curves == null || curves.Length == 0) return;
            access.GetItem(1, out double amount); if (amount < 0) amount = 0;

            var outCurves = new List<Curve>(); var outCounts = new List<int>(); var outOffsets = new List<double>();
            double savedCT = SvgNest.Config.curveTolerance, savedCS = SvgNest.Config.clipperScale; bool savedSimp = SvgNest.Config.simplify;
            try
            {
                SvgNest.Config.curveTolerance = amount > 0 ? amount : 0.1;
                SvgNest.Config.simplify = false;
                if (SvgNest.Config.clipperScale <= 0) SvgNest.Config.clipperScale = 1e7;
                foreach (var crv in curves)
                {
                    if (crv == null) continue;
                    try
                    {
                        var simplified = SimplifyCurve(crv, amount, out double offsetUsed);
                        if (simplified != null)
                        {
                            outCurves.Add(simplified.ToNurbsCurve());
                            outCounts.Add(simplified.Count > 0 ? simplified.Count - 1 : 0);
                            outOffsets.Add(offsetUsed);
                        }
                    }
                    catch (Exception ex) { access.AddWarning("Simplify", ex.Message); }
                }
            }
            finally { SvgNest.Config.curveTolerance = savedCT; SvgNest.Config.clipperScale = savedCS; SvgNest.Config.simplify = savedSimp; }

            access.SetTwig(0, outCurves.ToArray());
            access.SetTwig(1, outCounts.ToArray());
            access.SetTwig(2, outOffsets.ToArray());
        }

        // ---- copied verbatim from opennest_2.component_simplify (containment-guaranteed outer simplify) ----
        private static Polyline SimplifyCurve(Curve crv, double amount, out double offsetUsed)
        {
            offsetUsed = 0.0;
            if (!crv.TryGetPolyline(out Polyline pl))
            {
                double docTol = Rhino.RhinoDoc.ActiveDoc != null ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.01;
                var pc = crv.ToPolyline(docTol, 0.1, 0.0, 0.0);
                if (pc == null || !pc.TryGetPolyline(out pl)) return null;
            }
            if (pl == null || pl.Count < 3) return null;
            var orig = new NFP();
            int n = pl.Count;
            if (n >= 2 && pl[0].DistanceTo(pl[n - 1]) < 1e-9) n--;
            for (int i = 0; i < n; i++) orig.AddPoint(new SvgPoint(pl[i].X, pl[i].Y));
            if (orig.length < 3) return null;
            var cleaned = SvgNest.cleanPolygon2(orig);
            NFP work = (cleaned != null && cleaned.length > 2) ? cleaned : orig;
            double scale = SvgNest.Config.clipperScale; double eps = 1e-9;
            if (amount <= 0) return ToClosedPolyline(work);
            NFP simple = Simplify.simplify(work, amount, true);
            if (simple == null || simple.length < 3) simple = work;
            var cleanedS = SvgNest.cleanPolygon2(simple);
            if (cleanedS != null && cleanedS.length > 2) simple = cleanedS;
            double maxOut = 0.0;
            foreach (var v in orig.Points)
                if (!PointInPolyF(v.x, v.y, simple, eps)) { double d = DistPointToPolygon(v, simple); if (d > maxOut) maxOut = d; }
            NFP result = simple;
            if (maxOut > 1e-12)
            {
                double off = maxOut * 1.15 + 16.0 / Math.Max(1.0, scale);
                NFP inflated = OffsetOutward(simple, off, scale, JoinType.jtRound);
                if (inflated != null && inflated.length >= 3) { result = inflated; offsetUsed = off; }
            }
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

        private static double DistPointToPolygon(SvgPoint p, NFP poly)
        {
            double best = double.MaxValue; int m = poly.length; if (m < 2) return 0.0;
            for (int i = 0; i < m; i++) { var a = poly[i]; var b = poly[(i + 1) % m]; double d = DistPointToSeg(p.x, p.y, a.x, a.y, b.x, b.y); if (d < best) best = d; }
            return best == double.MaxValue ? 0.0 : best;
        }
        private static double DistPointToSeg(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay; double len2 = dx * dx + dy * dy;
            double t = len2 > 1e-18 ? ((px - ax) * dx + (py - ay) * dy) / len2 : 0.0; if (t < 0) t = 0; else if (t > 1) t = 1;
            double cx = ax + t * dx, cy = ay + t * dy, ex = px - cx, ey = py - cy; return Math.Sqrt(ex * ex + ey * ey);
        }
        private static bool ContainsAll(NFP orig, NFP poly, double eps)
        {
            if (poly == null || poly.length < 3) return false;
            foreach (var v in orig.Points) if (!PointInPolyF(v.x, v.y, poly, eps)) return false; return true;
        }
        private static bool PointInPolyF(double px, double py, NFP poly, double eps)
        {
            int m = poly.length; if (m < 3) return false; bool inside = false;
            for (int i = 0, j = m - 1; i < m; j = i++)
            {
                double xi = poly[i].x, yi = poly[i].y, xj = poly[j].x, yj = poly[j].y;
                if (DistPointToSeg(px, py, xi, yi, xj, yj) <= eps) return true;
                bool crosses = ((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi);
                if (crosses) inside = !inside;
            }
            return inside;
        }
        private static NFP OffsetOutward(NFP poly, double delta, double scale, JoinType join)
        {
            var path = SvgNest.svgToClipper(poly).ToList(); if (path.Count < 3) return null;
            if (!Clipper.Orientation(path)) path.Reverse();
            var co = new ClipperOffset(2.0, Math.Max(1.0, 0.1 * delta * scale));
            co.AddPath(path, join, EndType.etClosedPolygon);
            var sol = new List<List<IntPoint>>(); co.Execute(ref sol, delta * scale);
            if (sol == null || sol.Count == 0) return null;
            var big = sol[0]; double ba = Math.Abs(Clipper.Area(big));
            for (int i = 1; i < sol.Count; i++) { double a = Math.Abs(Clipper.Area(sol[i])); if (a > ba) { ba = a; big = sol[i]; } }
            return PathToNfp(big, scale);
        }
        private static Polyline ToClosedPolyline(NFP nfp)
        {
            var outPl = new Polyline(); if (nfp == null) return outPl;
            for (int i = 0; i < nfp.length; i++) outPl.Add(nfp[i].x, nfp[i].y, 0);
            if (outPl.Count > 0) outPl.Add(outPl[0]); return outPl;
        }
        private static NFP PathToNfp(List<IntPoint> path, double scale)
        {
            var nfp = new NFP(); foreach (var ip in path) nfp.AddPoint(new SvgPoint(ip.X / scale, ip.Y / scale)); return nfp;
        }
    }

    // Pack Objects: lay objects out in a row, orienting each from 3D to 2D (GH1 Pack 3278ccf2-7220-1478-ae14-1b111e786bbd).
    // Flat-list port (one row); pure geometry.
    [IoId("3278ccf2-7220-1478-ae14-1b111e786bbd")]
    public class PackComponent : Component
    {
        public PackComponent() : base(new Nomen("Pack Objects", "Lay objects out in a row, oriented from 3D to 2D.", "OpenNest", "Util")) { }
        public PackComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("pack_objects.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Geo", "G", "Objects to pack.", Access.Twig);
            inputs.AddPlane("Plane", "P", "Base plane per object to orient from 3D to 2D.", Access.Twig);
            inputs.AddNumber("X", "X", "Offset distance in X.", Access.Item, Requirement.MayBeMissing).Set(1.0);
            inputs.AddNumber("Y", "Y", "Offset distance in Y.", Access.Item, Requirement.MayBeMissing).Set(1.0);
            inputs.AddNumber("tolerance", "t", "Rotation step (radians) searching the min bounding box; 0 = no rotation.", Access.Item, Requirement.MayBeMissing).Set(0.0);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geo", "G", "Packed objects.", Access.Twig);
            outputs.AddTransform("Transformation", "T", "Transform applied to each object.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out GeometryBase[] geos) || geos == null || geos.Length == 0) return;
            access.GetItemArray(1, out Plane[] planes);
            access.GetItem(2, out double x); access.GetItem(3, out double y); access.GetItem(4, out double t);
            bool noRotation = (t == 0);
            double max = 3.14; t = Math.Max(3.14, 0.01);

            var outGeo = new List<GeometryBase>(); var outX = new List<Transform>();
            double massAddition = 0;
            for (int j = 0; j < geos.Length; j++)
            {
                if (geos[j] == null) continue;
                Plane basePlane = (planes != null && j < planes.Length && planes[j].IsValid) ? planes[j] : Plane.WorldXY;

                Transform orient = Transform.PlaneToPlane(basePlane, Plane.WorldXY);
                var g = geos[j].Duplicate(); g.Transform(orient);
                Transform all = orient;

                BoundingBox bbox = g.GetBoundingBox(true);
                var gBest = g; Transform rotBest = Transform.Identity; double vol = 0;
                for (double k = 0.0; k < max; k += t)
                {
                    Transform rot = noRotation ? Transform.Rotation(0, bbox.Center) : Transform.Rotation(k, bbox.Center);
                    var gT = g.Duplicate(); gT.Transform(rot);
                    BoundingBox tb = gT.GetBoundingBox(true); double tv = new Box(tb).Volume;
                    if (k == 0.0 || tv < vol) { vol = tv; gBest = gT; rotBest = rot; bbox = tb; }
                }
                all = rotBest * all;
                Point3d[] corners = bbox.GetCorners();
                if (corners[1].DistanceTo(corners[0]) < corners[1].DistanceTo(corners[2]))
                {
                    Transform xf = noRotation ? Transform.Rotation(0, bbox.Center) : Transform.Rotation(Math.PI * 0.5, bbox.Center);
                    all = xf * all; gBest.Transform(xf); bbox = gBest.GetBoundingBox(true);
                }
                corners = bbox.GetCorners();
                Vector3d vec = (Vector3d)Point3d.Origin - (Vector3d)corners[3];
                Transform tr0 = Transform.Translation(vec); gBest.Transform(tr0); all = tr0 * all;
                Transform tr1 = Transform.Translation(new Vector3d(massAddition, 0, 0)); gBest.Transform(tr1); all = tr1 * all;
                double dist = corners[0].DistanceTo(corners[1]) + x; if (x < 0) dist = -x; massAddition += dist;

                outGeo.Add(gBest); outX.Add(all);
            }
            access.SetTwig(0, outGeo.ToArray());
            access.SetTwig(1, outX.ToArray());
        }
    }
}
