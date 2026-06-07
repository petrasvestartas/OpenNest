using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Data;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using Sharp3DBinPacking;

namespace opennest_gh2.components
{
    // Pure-geometry utilities (no Rhino document access -> default threading is fine).

    // Inscribed Circle: largest circle that fits inside the closed polylines (GH1 170bf094-b8b4-45fa-abb2-8799117c7e6d).
    [IoId("170bf094-b8b4-45fa-abb2-8799117c7e6d")]
    public class InscribeCircleComponent : Component
    {
        public InscribeCircleComponent() : base(new Nomen("Inscribed Circle", "Largest circle that fits inside closed polylines.", "OpenNest", "Util")) { }
        public InscribeCircleComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("inscribe_circle.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Polylines", "P", "Closed polylines to inscribe a circle in.", Access.Twig);
            inputs.AddNumber("Tolerance", "T", "Precision of the fit.", Access.Item, Requirement.MayBeMissing).Set(10.0);
        }
        protected override void AddOutputs(OutputAdder outputs) => outputs.AddCircle("Circle", "C", "Largest inscribed circle.");

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Curve[] curves) || curves == null || curves.Length == 0) return;
            access.GetItem(1, out double tol);
            var plines = new List<Polyline>();
            foreach (var c in curves) { if (c != null && c.TryGetPolyline(out Polyline p) && p != null) plines.Add(p); }
            if (plines.Count == 0) { access.AddWarning("No polylines", "Input must be closed polylines."); return; }
            // Reuse the exact GH1 algorithm (pole of inaccessibility / Polylabel).
            access.SetItem(0, opennest_2.InscribeCircle.Compute(plines, tol <= 0 ? 1.0 : tol));
        }
    }

    // Region Slits: cut interlocking slits where planar regions meet (GH1 0FEEEACA-8F1F-4D7C-A24A-8E7DD21334A2).
    [IoId("0feeeaca-8f1f-4d7c-a24a-8e7dd21334a2")]
    public class RegionSlitsComponent : Component
    {
        public RegionSlitsComponent() : base(new Nomen("Region Slits", "Cut interlocking slits where planar regions meet.", "OpenNest", "Util")) { }
        public RegionSlitsComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("region_slits.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Regions", "R", "Planar regions to intersect.", Access.Twig);
            inputs.AddNumber("Width", "W", "Width of slits.", Access.Item, Requirement.MayBeMissing).Set(0.0);
            inputs.AddNumber("Gap", "G", "Additional gap at slit meeting points.", Access.Item, Requirement.MayBeMissing).Set(0.0);
        }
        protected override void AddOutputs(OutputAdder outputs) => outputs.AddGeneric("Regions", "R", "Regions with slits (planar Breps).", Access.Twig);

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Curve[] curves) || curves == null || curves.Length == 0) return;
            access.GetItem(1, out double width); access.GetItem(2, out double gap);

            // Reuse the exact GH1 Slab class (intersection + slit-cutting logic).
            var slabs = new List<opennest_2.Component_RegionSlits.Slab>();
            for (int i = 0; i < curves.Length; i++)
            {
                if (curves[i] == null) { slabs.Add(null); continue; }
                try { slabs.Add(new opennest_2.Component_RegionSlits.Slab(i, curves[i])); }
                catch (Exception ex) { slabs.Add(null); access.AddWarning("Slab error", ex.Message); }
            }
            for (int j = 0; j < slabs.Count - 1; j++)
                for (int k = j + 1; k < slabs.Count; k++)
                {
                    var a = slabs[j]; var b = slabs[k];
                    if (a == null || b == null) continue;
                    if (opennest_2.Component_RegionSlits.Slab.Intersect(a, b, out Line la, out Line lb))
                    {
                        if (la.IsValid) a.Cutters.Add(la);
                        if (lb.IsValid) b.Cutters.Add(lb);
                    }
                }
            var outBreps = new List<Brep>();
            foreach (var slab in slabs)
            {
                if (slab == null) continue;
                var breps = slab.ToBrep(width, gap);
                if (breps != null) foreach (var br in breps) if (br != null) outBreps.Add(br);
            }
            access.SetTwig(0, outBreps.ToArray());
        }
    }

    // Box Packing: pack 3D boxes into a container (GH1 BinPacking 9bcf7312-b1fb-4a6e-8b4e-5c213154771c).
    [IoId("9bcf7312-b1fb-4a6e-8b4e-5c213154771c")]
    public class BinPackingComponent : Component
    {
        public BinPackingComponent() : base(new Nomen("Box Packing", "Pack 3D boxes into a container.", "OpenNest", "Util")) { }
        public BinPackingComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("packing_boxes.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Con", "C", "Container to pack into.", Access.Twig);
            inputs.AddGeneric("Geo", "G", "Geometry to pack.", Access.Twig);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddBox("Geo", "G", "Packed boxes.", Access.Twig);
            outputs.AddTransform("Tra", "T", "Packing transforms.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out GeometryBase[] containers) || containers == null || containers.Length == 0) return;
            if (!access.GetItemArray(1, out GeometryBase[] geos) || geos == null || geos.Length == 0) return;

            int n = geos.Length;
            var nested = new bool[n];
            var xforms = new Transform[n];
            for (int i = 0; i < n; i++) xforms[i] = Transform.Identity;
            var b0 = new Box[n];

            foreach (var con in containers)
            {
                if (con == null) continue;
                var b0_U = new double[n]; var b0_V = new double[n]; var b0_C = new Point3d[n];
                var cuboids = new Cuboid[n];
                decimal[] size = GeoToBox(con);

                for (int i = 0; i < n; i++)
                {
                    if (nested[i] || geos[i] == null) continue;
                    BoundingBox bbox = geos[i].GetBoundingBox(true);
                    if (bbox.IsDegenerate(0.1) != 0) bbox.Union(bbox.PointAt(0, 0, 0) + new Point3d(0, 0, 1));
                    Box box = new Box(bbox);
                    Box boxPlanar = new Box(bbox); boxPlanar.Transform(Transform.PlanarProjection(Plane.WorldXY));
                    decimal d0 = (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(1, 0, 0));
                    decimal d1 = (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(0, 1, 0));
                    b0[i] = box;
                    if (d0 > size[3] || d1 > size[4]) continue;
                    cuboids[i] = new Cuboid((decimal)box.X.Length, (decimal)box.Z.Length, (decimal)box.Y.Length, (decimal)box.Volume, i);
                    b0_U[i] = box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(0, 1, 0));
                    b0_V[i] = box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(1, 0, 0));
                    b0_C[i] = box.Center;
                }

                var cuboidsClean = cuboids.Where(c => c != null).ToList();
                var parameter = new BinPackParameter(size[0], size[1], size[2], (decimal)1000000000, false, cuboidsClean);
                var binPacker = BinPacker.GetDefault(BinPackerVerifyOption.BestOnly);
                var result = binPacker.Pack(parameter);
                double dist = 0;
                foreach (IList<Cuboid> bins in result.BestResult)
                {
                    foreach (Cuboid cuboid in bins)
                    {
                        b0[(int)cuboid.Tag] = new Box(new Plane(new Point3d((double)cuboid.X + dist, (double)cuboid.Z, (double)cuboid.Y), Vector3d.ZAxis),
                            new Interval(0, (double)cuboid.Width), new Interval(0, (double)cuboid.Depth), new Interval(0, (double)cuboid.Height));
                        nested[(int)cuboid.Tag] = true;
                    }
                    break;
                }

                for (int i = 0; i < n; i++)
                {
                    if (cuboids[i] == null || !nested[i]) continue;
                    Box box = new Box(new BoundingBox(b0[i].GetCorners()));
                    Point3d center = box.Center;
                    double distU = box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(0, 1, 0));
                    Vector3d v = center - b0_C[i];
                    xforms[i] = Transform.Identity;
                    xforms[i] *= Transform.Translation(new Vector3d((double)size[5], (double)size[6], (double)size[7]));
                    if (Math.Abs(distU - b0_U[i]) > Math.Abs((box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(1, 0, 0))) - b0_U[i]))
                    {
                        xforms[i] *= Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, center);
                        xforms[i] *= Transform.Translation(v);
                    }
                    else xforms[i] *= Transform.Translation(v);
                    b0[i].Transform(Transform.Translation(new Vector3d((double)size[5], (double)size[6], (double)size[7])));
                }
            }
            access.SetTwig(0, b0);
            access.SetTwig(1, xforms);
        }

        private static decimal[] GeoToBox(GeometryBase geo)
        {
            BoundingBox bbox = geo.GetBoundingBox(true);
            if (bbox.IsDegenerate(0.01) != 0) bbox.Union(bbox.PointAt(0, 0, 0) + new Point3d(0, 0, 1.1));
            Box box = new Box(bbox);
            Box boxPlanar = new Box(bbox); boxPlanar.Transform(Transform.PlanarProjection(Plane.WorldXY));
            return new[]
            {
                (decimal)box.X.Length, (decimal)box.Z.Length, (decimal)box.Y.Length,
                (decimal)boxPlanar.PointAt(0,0,0).DistanceTo(boxPlanar.PointAt(1,0,0)),
                (decimal)boxPlanar.PointAt(0,0,0).DistanceTo(boxPlanar.PointAt(0,1,0)),
                (decimal)box.PointAt(0,0,0).X, (decimal)box.PointAt(0,0,0).Y, (decimal)box.PointAt(0,0,0).Z
            };
        }
    }

    // Principal Component Analysis: edge-aligned oriented bounding box (GH1 PCA 170bf094-b8b4-45fa-abb2-8111117c7e6d).
    [IoId("170bf094-b8b4-45fa-abb2-8111117c7e6d")]
    public class PcaComponent : Component
    {
        public PcaComponent() : base(new Nomen("Principal Component Analysis", "Edge-aligned oriented bounding box.", "OpenNest", "Util")) { }
        public PcaComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("pca.svg");

        protected override void AddInputs(InputAdder inputs) => inputs.AddPoint("Points", "P", "Point set to analyze.", Access.Twig);
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddPlane("P", "Plane", "Edge-aligned plane of the oriented box.");
            outputs.AddBox("B", "Box", "Oriented bounding box.");
            outputs.AddPoint("Pt", "Points", "Bounding box corner points.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Point3d[] pointsArr) || pointsArr == null || pointsArr.Length == 0)
            { access.AddWarning("No points", "Need at least one point."); return; }
            var points = pointsArr.ToList();
            var oob = ComputeOrientedBoxCorners(points);
            Vector3d x = oob[1] - oob[0]; Vector3d y = oob[2] - oob[0];
            Plane plane = new Plane((oob[0] + oob[7]) * 0.5, x, y);
            double hx = oob[0].DistanceTo(oob[1]) * 0.5, hy = oob[0].DistanceTo(oob[2]) * 0.5, hz = oob[0].DistanceTo(oob[4]) * 0.5;
            Box box = new Box(plane, new Interval(-hx, hx), new Interval(-hy, hy), new Interval(-hz, hz));
            access.SetItem(0, plane);
            access.SetItem(1, box);
            access.SetTwig(2, oob.ToArray());
        }

        // ---- copied verbatim from opennest_2.PCA (managed OBB; no native dependency) ----
        private static List<Point3d> ComputeOrientedBoxCorners(IList<Point3d> pts)
        {
            int n = pts.Count;
            if (n == 1) return BoxCorners(pts[0], Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, 0, 0, 0);
            var bb = new BoundingBox(pts);
            double planarTol = Math.Max(Rhino.RhinoMath.ZeroTolerance, 1e-6 * bb.Diagonal.Length);
            bool ok = Plane.FitPlaneToPoints(pts, out Plane plane) == PlaneFitResult.Success && plane.IsValid;
            if (ok && n >= 4)
            {
                double residual = 0; foreach (var p in pts) { double d = Math.Abs(plane.DistanceTo(p)); if (d > residual) residual = d; }
                if (residual > planarTol) { var obb = TryMinVolumeObb(pts); if (obb != null) return obb; }
            }
            if (!ok) plane = Plane.WorldXY;
            return PlanarMinAreaBox(pts, plane);
        }

        private static List<Point3d> PlanarMinAreaBox(IList<Point3d> pts, Plane plane)
        {
            int n = pts.Count; var p2 = new double[n][];
            double zmin = double.MaxValue, zmax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3d d = pts[i] - plane.Origin;
                double a = d * plane.XAxis, b = d * plane.YAxis, c = d * plane.ZAxis;
                p2[i] = new[] { a, b }; if (c < zmin) zmin = c; if (c > zmax) zmax = c;
            }
            MinAreaRectangle(p2, out double cx, out double cy, out double ux, out double uy, out double hu, out double hv);
            double cz = (zmin + zmax) * 0.5, hn = (zmax - zmin) * 0.5;
            Vector3d U = ux * plane.XAxis + uy * plane.YAxis;
            Vector3d V = -uy * plane.XAxis + ux * plane.YAxis;
            Vector3d N = plane.ZAxis;
            Point3d center = plane.Origin + cx * plane.XAxis + cy * plane.YAxis + cz * plane.ZAxis;
            return BoxCorners(center, U, V, N, hu, hv, hn);
        }

        private static List<Point3d> TryMinVolumeObb(IList<Point3d> pts)
        {
            try
            {
                var hull = nest_rhino_lib.ConvexHull.ConvexHull.Create(pts, false);
                var hp = new List<Vector3d>();
                foreach (var v in hull.Points) { var pos = v.Position; hp.Add(new Vector3d(pos[0], pos[1], pos[2])); }
                if (hp.Count < 4) return null;
                double bestVol = double.MaxValue; Point3d bC = Point3d.Origin;
                Vector3d bU = Vector3d.XAxis, bV = Vector3d.YAxis, bN = Vector3d.ZAxis;
                double bHu = 0, bHv = 0, bHn = 0; var seen = new List<Vector3d>();
                foreach (var face in hull.Faces)
                {
                    var fv = face.Vertices; if (fv.Length < 3) continue;
                    Vector3d q0 = ToVec(fv[0].Position), q1 = ToVec(fv[1].Position), q2 = ToVec(fv[2].Position);
                    Vector3d nrm = Vector3d.CrossProduct(q1 - q0, q2 - q0); if (!nrm.Unitize()) continue;
                    bool dup = false; foreach (var s in seen) if (Math.Abs(Math.Abs(s * nrm) - 1.0) < 1e-6) { dup = true; break; }
                    if (dup) continue; seen.Add(nrm);
                    Vector3d aRef = Math.Abs(nrm.X) < 0.9 ? Vector3d.XAxis : Vector3d.YAxis;
                    Vector3d u = Vector3d.CrossProduct(nrm, aRef); u.Unitize();
                    Vector3d vv = Vector3d.CrossProduct(nrm, u);
                    var p2 = new double[hp.Count][]; double nmin = double.MaxValue, nmax = double.MinValue;
                    for (int k = 0; k < hp.Count; k++)
                    {
                        double pu = hp[k] * u, pv = hp[k] * vv, pn = hp[k] * nrm;
                        p2[k] = new[] { pu, pv }; if (pn < nmin) nmin = pn; if (pn > nmax) nmax = pn;
                    }
                    MinAreaRectangle(p2, out double cx, out double cy, out double rux, out double ruy, out double hu, out double hv);
                    double height = nmax - nmin; double vol = 4.0 * hu * hv * height;
                    if (vol < bestVol && vol > 0)
                    {
                        bestVol = vol; bU = rux * u + ruy * vv; bV = -ruy * u + rux * vv; bN = nrm;
                        double cn = (nmin + nmax) * 0.5;
                        bC = new Point3d(cx * u.X + cy * vv.X + cn * nrm.X, cx * u.Y + cy * vv.Y + cn * nrm.Y, cx * u.Z + cy * vv.Z + cn * nrm.Z);
                        bHu = hu; bHv = hv; bHn = height * 0.5;
                    }
                }
                if (bestVol == double.MaxValue) return null;
                return BoxCorners(bC, bU, bV, bN, bHu, bHv, bHn);
            }
            catch { return null; }
        }

        private static void MinAreaRectangle(double[][] p, out double cx, out double cy, out double ux, out double uy, out double hu, out double hv)
        {
            cx = cy = 0; ux = 1; uy = 0; hu = hv = 0;
            double[][] hull = ConvexHull2DLocal(p);
            if (hull.Length == 0) return;
            if (hull.Length == 1) { cx = hull[0][0]; cy = hull[0][1]; return; }
            if (hull.Length == 2)
            {
                double dx = hull[1][0] - hull[0][0], dy = hull[1][1] - hull[0][1];
                double L = Math.Sqrt(dx * dx + dy * dy);
                if (L > 1e-15) { ux = dx / L; uy = dy / L; }
                cx = (hull[0][0] + hull[1][0]) * 0.5; cy = (hull[0][1] + hull[1][1]) * 0.5; hu = L * 0.5; hv = 0; return;
            }
            int m = hull.Length; double best = double.MaxValue;
            for (int i = 0; i < m; i++)
            {
                double ex = hull[(i + 1) % m][0] - hull[i][0], ey = hull[(i + 1) % m][1] - hull[i][1];
                double L = Math.Sqrt(ex * ex + ey * ey); if (L < 1e-15) continue;
                double ax = ex / L, ay = ey / L, bx = -ay, by = ax;
                double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                for (int k = 0; k < m; k++)
                {
                    double pa = hull[k][0] * ax + hull[k][1] * ay, pb = hull[k][0] * bx + hull[k][1] * by;
                    if (pa < minA) minA = pa; if (pa > maxA) maxA = pa; if (pb < minB) minB = pb; if (pb > maxB) maxB = pb;
                }
                double w = maxA - minA, h = maxB - minB, area = w * h;
                if (area < best)
                {
                    best = area; double ca = (minA + maxA) * 0.5, cb = (minB + maxB) * 0.5;
                    cx = ca * ax + cb * bx; cy = ca * ay + cb * by; ux = ax; uy = ay; hu = w * 0.5; hv = h * 0.5;
                }
            }
        }

        private static double[][] ConvexHull2DLocal(double[][] pts)
        {
            var sorted = pts.OrderBy(q => q[0]).ThenBy(q => q[1]).ToArray();
            var uniq = new List<double[]>();
            foreach (var q in sorted)
                if (uniq.Count == 0 || uniq[uniq.Count - 1][0] != q[0] || uniq[uniq.Count - 1][1] != q[1]) uniq.Add(q);
            int m = uniq.Count; if (m < 3) return uniq.ToArray();
            var h = new List<double[]>();
            foreach (var q in uniq) { while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            int lower = h.Count + 1;
            for (int i = m - 2; i >= 0; i--) { var q = uniq[i]; while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            h.RemoveAt(h.Count - 1); return h.ToArray();
        }

        private static double Cross(double[] o, double[] a, double[] b) => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);
        private static Vector3d ToVec(double[] p) => new Vector3d(p[0], p[1], p[2]);

        private static List<Point3d> BoxCorners(Point3d c, Vector3d a0, Vector3d a1, Vector3d a2, double h0, double h1, double h2)
        {
            var L = new List<Point3d>(8);
            for (int i = 0; i < 8; i++)
            {
                double o0 = ((i & 1) != 0) ? h0 : -h0, o1 = ((i & 2) != 0) ? h1 : -h1, o2 = ((i & 4) != 0) ? h2 : -h2;
                L.Add(c + a0 * o0 + a1 * o1 + a2 * o2);
            }
            return L;
        }
    }
}
