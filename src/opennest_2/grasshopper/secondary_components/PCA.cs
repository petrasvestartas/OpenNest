using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Linq;

namespace opennest_2
{
    public class PCA : GH_Component
    {
        /// <summary>
        /// Initializes a new instance of the InscribeCircle class.
        /// </summary>
        public PCA()
          : base("Principal Component Analysis", "PCA",
              "Finds the principal axes of a point set and returns the aligned plane and bounding box.",
          "Params", "OpenNest2")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddPointParameter("Points", "P", "Point set to analyze", GH_ParamAccess.list);
        }


        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddPlaneParameter("P", "Plane", "Principal-axis aligned plane", GH_ParamAccess.item);
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

            // Oriented bounding box via principal component analysis, computed in managed C# (no native
            // dependency). Faithful 3D port of the former minkowski.dll routine: 3x3 population covariance
            // (+1e-5 regularization) -> symmetric eigendecomposition (Jacobi) -> project -> 8 corners in
            // bit order (pt0 = min,min,min ; pt1 = max,min,min ; pt2 = min,max,min ; pt4 = min,min,max ;
            // pt7 = max,max,max), matching what the Plane/Box construction below expects.
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
        /// Principal-component-analysis oriented bounding box, in managed C# (no native dependency).
        /// Returns the 8 corners in bit order: corner i takes the max extent on local axis k iff bit k of i
        /// is set, so index 0 = (min,min,min), 1 = (max,min,min), 2 = (min,max,min), 4 = (min,min,max),
        /// 7 = (max,max,max). Fully 3D (the covariance is the complete 3x3).
        /// </summary>
        private static List<Point3d> ComputeOrientedBoxCorners(IList<Point3d> pts)
        {
            int n = pts.Count;

            // Centroid.
            double cx = 0, cy = 0, cz = 0;
            for (int i = 0; i < n; i++) { cx += pts[i].X; cy += pts[i].Y; cz += pts[i].Z; }
            cx /= n; cy /= n; cz /= n;
            Point3d centroid = new Point3d(cx, cy, cz);

            // 3x3 population covariance of the mean-centered points (divisor N, not N-1).
            double[,] cov = new double[3, 3];
            for (int i = 0; i < n; i++)
            {
                double dx = pts[i].X - cx, dy = pts[i].Y - cy, dz = pts[i].Z - cz;
                cov[0, 0] += dx * dx; cov[0, 1] += dx * dy; cov[0, 2] += dx * dz;
                cov[1, 1] += dy * dy; cov[1, 2] += dy * dz;
                cov[2, 2] += dz * dz;
            }
            for (int i = 0; i < 3; i++)
                for (int j = i; j < 3; j++) { cov[i, j] /= n; cov[j, i] = cov[i, j]; }

            // Regularize so a degenerate (planar / collinear / single) cloud stays solvable.
            cov[0, 0] += 1e-5; cov[1, 1] += 1e-5; cov[2, 2] += 1e-5;

            JacobiEigen3x3(cov, out double[] _eigenvalues, out double[,] V);

            // Eigenvectors are the columns of V (ascending eigenvalues).
            Vector3d e0 = new Vector3d(V[0, 0], V[1, 0], V[2, 0]);
            Vector3d e1 = new Vector3d(V[0, 1], V[1, 1], V[2, 1]);
            Vector3d e2 = new Vector3d(V[0, 2], V[1, 2], V[2, 2]);
            e0.Unitize(); e1.Unitize(); e2.Unitize();
            // Make the frame right-handed for a clean plane (eigenvector signs are otherwise arbitrary).
            if (Vector3d.CrossProduct(e0, e1) * e2 < 0) e2 = -e2;

            // Extents along each principal axis.
            double minx = double.MaxValue, miny = double.MaxValue, minz = double.MaxValue;
            double maxx = double.MinValue, maxy = double.MinValue, maxz = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                Vector3d d = pts[i] - centroid;
                double px = d * e0, py = d * e1, pz = d * e2;
                if (px < minx) minx = px; if (px > maxx) maxx = px;
                if (py < miny) miny = py; if (py > maxy) maxy = py;
                if (pz < minz) minz = pz; if (pz > maxz) maxz = pz;
            }

            var corners = new List<Point3d>(8);
            for (int i = 0; i < 8; i++)
            {
                double ox = ((i & 1) != 0) ? maxx : minx;
                double oy = ((i & 2) != 0) ? maxy : miny;
                double oz = ((i & 4) != 0) ? maxz : minz;
                corners.Add(centroid + e0 * ox + e1 * oy + e2 * oz);
            }
            return corners;
        }

        /// <summary>
        /// Cyclic Jacobi eigensolver for a symmetric 3x3 matrix. Returns eigenvalues in ASCENDING order and
        /// the eigenvectors as the COLUMNS of V (V[*,k] is the eigenvector for eigenvalues[k]) — matching the
        /// convention of Eigen's SelfAdjointEigenSolver that the original native routine used.
        /// </summary>
        private static void JacobiEigen3x3(double[,] A, out double[] eigenvalues, out double[,] V)
        {
            double[,] a = (double[,])A.Clone();
            V = new double[3, 3];
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++) V[i, j] = (i == j) ? 1.0 : 0.0;

            for (int iter = 0; iter < 100; iter++)
            {
                double off = Math.Abs(a[0, 1]) + Math.Abs(a[0, 2]) + Math.Abs(a[1, 2]);
                double diag = Math.Abs(a[0, 0]) + Math.Abs(a[1, 1]) + Math.Abs(a[2, 2]);
                if (off <= 1e-18 * diag) break;

                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        double apq = a[p, q];
                        if (Math.Abs(apq) <= 1e-300) continue;

                        // Rotation angle that zeroes a[p,q].
                        double theta = (a[q, q] - a[p, p]) / (2.0 * apq);
                        double t = (theta == 0.0)
                            ? 1.0
                            : Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0));
                        double c = 1.0 / Math.Sqrt(t * t + 1.0);
                        double s = t * c;

                        double app = a[p, p], aqq = a[q, q];
                        a[p, p] = app - t * apq;
                        a[q, q] = aqq + t * apq;
                        a[p, q] = 0.0; a[q, p] = 0.0;

                        for (int i = 0; i < 3; i++)
                        {
                            if (i == p || i == q) continue;
                            double aip = a[i, p], aiq = a[i, q];
                            a[i, p] = c * aip - s * aiq; a[p, i] = a[i, p];
                            a[i, q] = s * aip + c * aiq; a[q, i] = a[i, q];
                        }
                        for (int i = 0; i < 3; i++)
                        {
                            double vip = V[i, p], viq = V[i, q];
                            V[i, p] = c * vip - s * viq;
                            V[i, q] = s * vip + c * viq;
                        }
                    }
                }
            }

            // Sort ascending, carrying the eigenvector columns along. (Sort against a local — an `out`
            // parameter can't be captured in the comparison lambda.)
            double[] w = new double[] { a[0, 0], a[1, 1], a[2, 2] };
            int[] idx = { 0, 1, 2 };
            Array.Sort(idx, (x, y) => w[x].CompareTo(w[y]));
            double[] ev = new double[3];
            double[,] Vs = new double[3, 3];
            for (int k = 0; k < 3; k++)
            {
                ev[k] = w[idx[k]];
                for (int i = 0; i < 3; i++) Vs[i, k] = V[i, idx[k]];
            }
            eigenvalues = ev;
            V = Vs;
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