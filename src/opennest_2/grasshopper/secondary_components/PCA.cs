using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;
using nest_rhino_lib.ConvexHull;

namespace opennest_2
{
    public class PCA : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the InscribeCircle class.
        /// </summary>
        public PCA()
          : base("Principal Component Analysis", "PCA",
              "Oriented bounding box aligned to the part's edges: a minimum-area rectangle for flat (planar) point sets, a minimum-volume box for solids. Returns the aligned plane, the box, and its 8 corner points.",
          "Params", "OpenNest2")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddPointParameter("Points", "P", "Point set to analyze", GH_ParamAccess.list);
        }


        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPlaneParameter("P", "Plane", "Edge-aligned plane of the oriented box", GH_ParamAccess.item);
            pManager.AddBoxParameter("B", "Box", "Oriented bounding box", GH_ParamAccess.item);
            pManager.AddPointParameter("Pt", "Points", "Bounding box corner points", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            List<Point3d> points = new List<Point3d>();
            DA.GetDataList(0, points);

            if (points.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Need at least one point.");
                return;
            }

            // Edge-aligned oriented bounding box, computed in managed C# (no native dependency). Flat/planar
            // input -> 2D minimum-area rectangle (rotating calipers); solid input -> 3D minimum-volume box
            // (per convex-hull-face rotating calipers). Returns the 8 corners in bit order
            // (pt0 = min,min,min ; pt1 = max,min,min ; pt2 = min,max,min ; pt4 = min,min,max ; pt7 = max,max,max),
            // matching what the Plane/Box construction below expects.
            var points_oob = ComputeOrientedBoxCorners(points);


            Vector3d x = points_oob[1] - points_oob[0];
            Vector3d y = points_oob[2] - points_oob[0];
            Plane plane = new Plane((points_oob[0]+ points_oob[7])*0.5, x, y);
            double half_x_distance = (points_oob[0].DistanceTo(points_oob[1])) * 0.5;
            double half_y_distance = (points_oob[0].DistanceTo(points_oob[2])) * 0.5;
            double half_z_distance = (points_oob[0].DistanceTo(points_oob[4])) * 0.5;

            Box box = new Box(plane, new Interval(-half_x_distance, half_x_distance), new Interval(-half_y_distance, half_y_distance), new Interval(-half_z_distance, half_z_distance));

            DA.SetData(0, plane);
            DA.SetData(1, box);
            DA.SetDataList(2, points_oob);
        }


        /// <summary>
        /// Edge-aligned oriented bounding box. Auto-selects the method: a planar point set yields a 2D
        /// minimum-area rectangle (a rotation about the plane normal); a solid set yields a true 3D
        /// minimum-volume box. Both are robust to symmetry (unlike PCA's covariance axes, which are arbitrary
        /// when two extents are equal). Returns 8 corners in bit order (see SolveInstance).
        /// </summary>
        private static List<Point3d> ComputeOrientedBoxCorners(IList<Point3d> pts)
        {
            int n = pts.Count;
            if (n == 1)
                return BoxCorners(pts[0], Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis, 0, 0, 0);

            var bb = new BoundingBox(pts);
            double planarTol = Math.Max(Rhino.RhinoMath.ZeroTolerance, 1e-6 * bb.Diagonal.Length);

            Plane plane;
            bool ok = Plane.FitPlaneToPoints(pts, out plane) == PlaneFitResult.Success && plane.IsValid;

            if (ok && n >= 4)
            {
                double residual = 0;
                foreach (var p in pts) { double d = Math.Abs(plane.DistanceTo(p)); if (d > residual) residual = d; }
                if (residual > planarTol)
                {
                    var obb = TryMinVolumeObb(pts);     // genuinely 3D
                    if (obb != null) return obb;
                }
            }

            // Planar / degenerate -> 2D minimum-area rectangle in the best-fit plane.
            if (!ok) plane = Plane.WorldXY;
            return PlanarMinAreaBox(pts, plane);
        }

        /// <summary>Flat-part path: minimum-area rectangle in the best-fit plane (rotation about the normal).</summary>
        private static List<Point3d> PlanarMinAreaBox(IList<Point3d> pts, Plane plane)
        {
            int n = pts.Count;
            var p2 = new double[n][];
            double zmin = double.MaxValue, zmax = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3d d = pts[i] - plane.Origin;
                double a = d * plane.XAxis, b = d * plane.YAxis, c = d * plane.ZAxis;
                p2[i] = new double[] { a, b };
                if (c < zmin) zmin = c; if (c > zmax) zmax = c;
            }
            MinAreaRectangle(p2, out double cx, out double cy, out double ux, out double uy, out double hu, out double hv);

            double cz = (zmin + zmax) * 0.5, hn = (zmax - zmin) * 0.5;
            Vector3d U = ux * plane.XAxis + uy * plane.YAxis;
            Vector3d V = -uy * plane.XAxis + ux * plane.YAxis;
            Vector3d N = plane.ZAxis;
            Point3d center = plane.Origin + cx * plane.XAxis + cy * plane.YAxis + cz * plane.ZAxis;
            return BoxCorners(center, U, V, N, hu, hv, hn);
        }

        /// <summary>
        /// Solid path: 3D minimum-volume oriented box. Builds the convex hull, then for every hull face aligns
        /// one axis to the face normal, finds the in-plane minimum-area rectangle, and keeps the box of least
        /// volume (O'Rourke's flush-face property -> exact for box / cube inputs). Returns null on failure
        /// (too few / coplanar points) so the caller can fall back to the planar path.
        /// </summary>
        private static List<Point3d> TryMinVolumeObb(IList<Point3d> pts)
        {
            try
            {
                var hull = ConvexHull.Create(pts, false);   // 3D hull (MIConvexHull / QuickHull)
                var hp = new List<Vector3d>();
                foreach (var v in hull.Points) { var pos = v.Position; hp.Add(new Vector3d(pos[0], pos[1], pos[2])); }
                if (hp.Count < 4) return null;

                double bestVol = double.MaxValue;
                Point3d bC = Point3d.Origin;
                Vector3d bU = Vector3d.XAxis, bV = Vector3d.YAxis, bN = Vector3d.ZAxis;
                double bHu = 0, bHv = 0, bHn = 0;
                var seen = new List<Vector3d>();

                foreach (var face in hull.Faces)
                {
                    var fv = face.Vertices;
                    if (fv.Length < 3) continue;
                    Vector3d q0 = ToVec(fv[0].Position), q1 = ToVec(fv[1].Position), q2 = ToVec(fv[2].Position);
                    Vector3d nrm = Vector3d.CrossProduct(q1 - q0, q2 - q0);
                    if (!nrm.Unitize()) continue;

                    bool dup = false;
                    foreach (var s in seen) { if (Math.Abs(Math.Abs(s * nrm) - 1.0) < 1e-6) { dup = true; break; } }
                    if (dup) continue;
                    seen.Add(nrm);

                    Vector3d aRef = Math.Abs(nrm.X) < 0.9 ? Vector3d.XAxis : Vector3d.YAxis;
                    Vector3d u = Vector3d.CrossProduct(nrm, aRef); u.Unitize();
                    Vector3d v = Vector3d.CrossProduct(nrm, u);

                    var p2 = new double[hp.Count][];
                    double nmin = double.MaxValue, nmax = double.MinValue;
                    for (int k = 0; k < hp.Count; k++)
                    {
                        double pu = hp[k] * u, pv = hp[k] * v, pn = hp[k] * nrm;
                        p2[k] = new double[] { pu, pv };
                        if (pn < nmin) nmin = pn; if (pn > nmax) nmax = pn;
                    }
                    MinAreaRectangle(p2, out double cx, out double cy, out double rux, out double ruy, out double hu, out double hv);

                    double height = nmax - nmin;
                    double vol = 4.0 * hu * hv * height;
                    if (vol < bestVol && vol > 0)
                    {
                        bestVol = vol;
                        bU = rux * u + ruy * v;
                        bV = -ruy * u + rux * v;
                        bN = nrm;
                        double cn = (nmin + nmax) * 0.5;
                        bC = new Point3d(cx * u.X + cy * v.X + cn * nrm.X,
                                         cx * u.Y + cy * v.Y + cn * nrm.Y,
                                         cx * u.Z + cy * v.Z + cn * nrm.Z);
                        bHu = hu; bHv = hv; bHn = height * 0.5;
                    }
                }
                if (bestVol == double.MaxValue) return null;
                return BoxCorners(bC, bU, bV, bN, bHu, bHv, bHn);
            }
            catch { return null; }
        }

        /// <summary>
        /// Minimum-area enclosing rectangle of a 2D point set (rotating calipers on the convex hull). Outputs
        /// the rectangle centre (cx,cy), its primary unit axis (ux,uy), and half-extents (hu along the axis,
        /// hv perpendicular). Handles collinear (hv = 0) and single-point (hu = hv = 0) inputs.
        /// </summary>
        private static void MinAreaRectangle(double[][] p, out double cx, out double cy,
                                             out double ux, out double uy, out double hu, out double hv)
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
                cx = (hull[0][0] + hull[1][0]) * 0.5; cy = (hull[0][1] + hull[1][1]) * 0.5;
                hu = L * 0.5; hv = 0; return;
            }

            int m = hull.Length;
            double best = double.MaxValue;
            for (int i = 0; i < m; i++)
            {
                double ex = hull[(i + 1) % m][0] - hull[i][0], ey = hull[(i + 1) % m][1] - hull[i][1];
                double L = Math.Sqrt(ex * ex + ey * ey);
                if (L < 1e-15) continue;
                double ax = ex / L, ay = ey / L, bx = -ay, by = ax;
                double minA = double.MaxValue, maxA = double.MinValue, minB = double.MaxValue, maxB = double.MinValue;
                for (int k = 0; k < m; k++)
                {
                    double pa = hull[k][0] * ax + hull[k][1] * ay;
                    double pb = hull[k][0] * bx + hull[k][1] * by;
                    if (pa < minA) minA = pa; if (pa > maxA) maxA = pa;
                    if (pb < minB) minB = pb; if (pb > maxB) maxB = pb;
                }
                double w = maxA - minA, h = maxB - minB, area = w * h;
                if (area < best)
                {
                    best = area;
                    double ca = (minA + maxA) * 0.5, cb = (minB + maxB) * 0.5;
                    cx = ca * ax + cb * bx; cy = ca * ay + cb * by;
                    ux = ax; uy = ay; hu = w * 0.5; hv = h * 0.5;
                }
            }
        }

        /// <summary>2D convex hull (Andrew monotone chain), CCW, no repeated closing vertex.</summary>
        private static double[][] ConvexHull2DLocal(double[][] pts)
        {
            var sorted = pts.OrderBy(q => q[0]).ThenBy(q => q[1]).ToArray();
            var uniq = new List<double[]>();
            foreach (var q in sorted)
                if (uniq.Count == 0 || uniq[uniq.Count - 1][0] != q[0] || uniq[uniq.Count - 1][1] != q[1])
                    uniq.Add(q);

            int m = uniq.Count;
            if (m < 3) return uniq.ToArray();

            var h = new List<double[]>();
            foreach (var q in uniq)
            {
                while (h.Count >= 2 && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            int lower = h.Count + 1;
            for (int i = m - 2; i >= 0; i--)
            {
                var q = uniq[i];
                while (h.Count >= lower && Cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1);
                h.Add(q);
            }
            h.RemoveAt(h.Count - 1);   // last == first
            return h.ToArray();
        }

        private static double Cross(double[] o, double[] a, double[] b)
            => (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0]);

        private static Vector3d ToVec(double[] p) => new Vector3d(p[0], p[1], p[2]);

        /// <summary>
        /// Builds the 8 box corners in bit order: corner i takes +half on local axis k iff bit k of i is set,
        /// so index 0 = (min,min,min), 1 = (max,min,min), 2 = (min,max,min), 4 = (min,min,max), 7 = (max,max,max).
        /// </summary>
        private static List<Point3d> BoxCorners(Point3d c, Vector3d a0, Vector3d a1, Vector3d a2,
                                                double h0, double h1, double h2)
        {
            var L = new List<Point3d>(8);
            for (int i = 0; i < 8; i++)
            {
                double o0 = ((i & 1) != 0) ? h0 : -h0;
                double o1 = ((i & 2) != 0) ? h1 : -h1;
                double o2 = ((i & 4) != 0) ? h2 : -h2;
                L.Add(c + a0 * o0 + a1 * o1 + a2 * o2);
            }
            return L;
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {

                return Properties.Resources.pca;
            }
        }
        public override GH_Exposure Exposure => GH_Exposure.octonary;
        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("170bf094-b8b4-45fa-abb2-8111117c7e6d"); }
        }
    }

}
