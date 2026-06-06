using Grasshopper.Kernel;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace opennest_2 {
    public class InscribeCircleInPolygon : GH_Component {
        /// <summary>
        /// Initializes a new instance of the InscribeCircle class.
        /// </summary>
        public InscribeCircleInPolygon()
          : base("Inscribed Circle", "InscribeCircle",
              "Finds the largest circle that fits inside each closed polyline.",
          "Params", "OpenNest2") {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {

            pManager.AddCurveParameter("Polylines", "P", "Closed polylines to inscribe circles in",GH_ParamAccess.list);
            pManager.AddNumberParameter("Tolerance","T","Precision of the fit",GH_ParamAccess.item,10);
        }

     
        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddCircleParameter("Circle", "C", "Largest inscribed circle", GH_ParamAccess.list);
        }


        protected override void SolveInstance(IGH_DataAccess DA) {
            double tolerance = 0;
            DA.GetData(1, ref tolerance);
            List<Curve> curves = new List<Curve>();
            DA.GetDataList(0, curves);
            List<Polyline> plines = new List<Polyline>();
            foreach(Curve c in curves) {
                c.TryGetPolyline(out Polyline p);
                plines.Add(p);
            }
           DA.SetData(0, InscribeCircle.Compute(plines, tolerance));
        }

        protected override System.Drawing.Bitmap Icon {
            get {

                return Properties.Resources.inscribe_circle;
            }
        }
        public override GH_Exposure Exposure => GH_Exposure.octonary;
        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid {
            get { return new Guid("170bf094-b8b4-45fa-abb2-8799117c7e6d"); }
        }
    }

    public static class InscribeCircle {
        public static Vector3d AverageNormal(this Polyline p) {
            //PolyFace item = this[index];
            int len = p.Count - 1;
            Vector3d vector3d = new Vector3d();
            int count = checked(len - 1);

            for (int i = 0; i <= count; i++) {
                int num = ((i - 1) + len) % len;
                int item1 = (checked(i + 1) + len) % len;
                Point3d point3d = p[num];
                Point3d point3d1 = p[item1];
                Point3d item2 = p[i];
                vector3d = vector3d + Vector3d.CrossProduct(new Vector3d(item2 - point3d), new Vector3d(point3d1 - item2));
            }

            if (vector3d.X == 0 & vector3d.Y == 0 & vector3d.Z == 0)
                vector3d.Unitize();

            return vector3d;
        }

        public static Vector3d Unit(this Vector3d p) {
            Vector3d v = new Vector3d(p.X, p.Y, p.Z);
            v.Unitize();
            return new Vector3d(v.X, v.Y, v.Z);
        }
        public static Plane GetPlane(this Polyline polyline, bool AveragePlane = true) {

            //In case use default version

            if (!AveragePlane) {
                // in z case z axis may flip from time to time
                Plane plane_;
                Plane.FitPlaneToPoints(polyline, out plane_);
                plane_.Origin = polyline.CenterPoint();
                return plane_;
            } else {

                Vector3d XAxis = polyline.SegmentAt(0).Direction.Unit();

                Vector3d ZAxis = AverageNormal(polyline).Unit();
                Vector3d YAxis = Vector3d.CrossProduct(XAxis, ZAxis);
                //return new Plane(polyline.CenterPoint(), PolylineUtil.AverageNormal(polyline));
                return new Plane(polyline.CenterPoint(), XAxis, YAxis);

            }


        }

        public static Circle Compute(this List<Polyline> input, double precision = 1.0) {

            var pline = new List<Polyline>();
            foreach (var p in input)
                pline.Add(p.Duplicate());
      
            Plane plane = pline[0].GetPlane();




            Transform xform = Transform.PlaneToPlane(plane, Plane.WorldXY);
            Transform xformInv = Transform.PlaneToPlane(Plane.WorldXY, plane);
            for(int i = 0; i < pline.Count;i++)
            pline[i].Transform(xform);

            double scale = precision;


            //Sort Polygons
            double[] len = new double[input.Count];
            Polyline[] sortedPlines = new Polyline[input.Count];
            for (int i = 0; i < input.Count; i++) {
                len[i] = pline[i].BoundingBox.Area;
                sortedPlines[i] = new Polyline(pline[i]);
            }
            Array.Sort(len, sortedPlines);
            pline = sortedPlines.Reverse().ToList();


            //Convert to 2D Polygons
            double[][][] polygon = new double[pline.Count][][];
            for (int i = 0; i < pline.Count; i++) {
                polygon[i] = new double[pline[i].Count - 1][];
                for (int j = 0; j < pline[i].Count - 1; j++) {
                    double[] coord = new double[] { (int)pline[i][j].X * scale, (int)pline[i][j].Y * scale };
                    polygon[i][j] = coord;
                }
            }

            //Convert to circle
            double[] center = Polylabel.GetPolylabel(polygon, 1, false);
            Point3d c = new Point3d(center[0] * (1 / scale), center[1] * (1 / scale), 0);
            Circle circle = new Circle(c, center[2] * (1 / scale));
            circle.Transform(xformInv);
            return circle;
        }


        public static Circle Compute(this Polyline input, double precision = 1.0) {
            return Compute(new List<Polyline> { input }, precision);

        }
    }

    internal class Cell {
        public Cell(double x, double y, double h, double[][][] polygon) {
            X = x;
            Y = y;
            H = h;
            D = PointToPolygonDist(x, y, polygon);
            Max = this.D + this.H * Math.Sqrt(2);
        }

        /// <summary>
        /// Cell center X
        /// </summary>
        public double X { get; }

        /// <summary>
        /// Cell center Y
        /// </summary>
        public double Y { get; }

        /// <summary>
        /// Half the cell size
        /// </summary>
        public double H { get; }

        /// <summary>
        /// Distance from cell center to polygon
        /// </summary>
        public double D { get; }

        /// <summary>
        /// Max distance to polygon within a cell
        /// </summary>
        public double Max { get; }

        /// <summary>
        /// Signed distance from point to polygon outline (negative if point is outside)
        /// </summary>
        /// <param name="x">Cell center x</param>
        /// <param name="y">Cell center y</param>
        /// <param name="polygon">Full GeoJson like Polygon</param>
        private double PointToPolygonDist(double x, double y, double[][][] polygon) {
            var inside = false;
            var minDistSq = double.PositiveInfinity;

            for (var k = 0; k < polygon.Length; k++) {
                var ring = polygon[k];

                var len = ring.Length;
                var j = len - 1;
                for (var i = 0; i < len; j = i++) {
                    var a = ring[i];
                    var b = ring[j];

                    if ((a[1] > y != b[1] > y) &&
                        (x < (b[0] - a[0]) * (y - a[1]) / (b[1] - a[1]) + a[0])) inside = !inside;

                    minDistSq = Math.Min(minDistSq, GetSegDistSq(x, y, a, b));
                }
            }

            return (inside ? 1 : -1) * Math.Sqrt(minDistSq);
        }

        /// <summary>
        /// Get squared distance from a point to a segment
        /// </summary>
        private double GetSegDistSq(double px, double py, double[] a, double[] b) {
            var x = a[0];
            var y = a[1];
            var dx = b[0] - x;
            var dy = b[1] - y;

            if (dx != 0 || dy != 0) {
                var t = ((px - x) * dx + (py - y) * dy) / (dx * dx + dy * dy);

                if (t > 1) {
                    x = b[0];
                    y = b[1];
                } else if (t > 0) {
                    x += dx * t;
                    y += dy * t;
                }
            }

            dx = px - x;
            dy = py - y;

            return dx * dx + dy * dy;
        }
    }
    public class Polylabel {
        /// <summary>
        /// A fast algorithm for finding polygon pole of inaccessibility, the most distant
        /// internal point from the polygon outline (not to be confused with centroid).
        /// Useful for optimal placement of a text label on a polygon.
        /// </summary>
        /// <param name="polygon">GeoJson like format</param>
        /// <param name="precision"></param>
        /// <param name="debug"></param>
        /// <returns></returns>
        public static double[] GetPolylabel(double[][][] polygon, double precision = 1.0, bool debug = false) {
            //get bounding box of the outer ring
            var minX = polygon[0].Min(x => x[0]);
            var minY = polygon[0].Min(x => x[1]);
            var maxX = polygon[0].Max(x => x[0]);
            var maxY = polygon[0].Max(x => x[1]);

            var width = maxX - minX;
            var height = maxY - minY;
            var cellSize = Math.Min(width, height);
            var h = cellSize / 2;

            if (cellSize == 0) return new double[] { minX, minY };

            //a priority queue of cells in order of their "potential" (max distance to polygon)
            var cellQueue = new Queue<Cell>();

            //cover polygon with initial cells
            for (var x = minX; x < maxX; x += cellSize) {
                for (var y = minY; y < maxY; y += cellSize) {
                    cellQueue.Enqueue(new Cell(x + h, y + h, h, polygon));
                }
            }

            //take centroid as the first best guess
            var bestCell = GetCentroidCell(polygon);

            //special case for rectangular polygons
            var bboxCell = new Cell(minX + width / 2, minY + height / 2, 0, polygon);
            if (bboxCell.D > bestCell.D) bestCell = bboxCell;

            var numProbes = cellQueue.Count;

            while (cellQueue.Count > 0) {
                //pick the most promising cell from the queue
                var cell = cellQueue.Dequeue();

                //update the best cell if we found a better one
                if (cell.D > bestCell.D) {
                    bestCell = cell;
                    if (debug) Console.WriteLine($"found best {Math.Round(1e4 * cell.D) / 1e4} after {numProbes}");
                }

                //do not drill down further if there's no chance of a better solution
                if (cell.Max - bestCell.D <= precision) continue;

                //split the cell into four cells
                h = cell.H / 2;
                cellQueue.Enqueue(new Cell(cell.X - h, cell.Y - h, h, polygon));
                cellQueue.Enqueue(new Cell(cell.X + h, cell.Y - h, h, polygon));
                cellQueue.Enqueue(new Cell(cell.X - h, cell.Y + h, h, polygon));
                cellQueue.Enqueue(new Cell(cell.X + h, cell.Y + h, h, polygon));
                numProbes += 4;
            }

            if (debug) {
                Rhino.RhinoApp.WriteLine($"Number probes: {numProbes}");
                Rhino.RhinoApp.WriteLine($"Best distance: {bestCell.D}");
            }

            return new double[] { bestCell.X, bestCell.Y, bestCell.D };
        }

        private static Cell GetCentroidCell(double[][][] polygon) {
            var area = 0.0;
            var x = 0.0;
            var y = 0.0;
            var points = polygon[0];

            var len = points.Length;
            var j = len - 1;
            for (var i = 0; i < len; j = i++) {
                var a = points[i];
                var b = points[j];
                var f = a[0] * b[1] - b[0] * a[1];
                x += (a[0] + b[0]) * f;
                y += (a[1] + b[1]) * f;
                area += f * 3;
            }
            if (area == 0) return new Cell(points[0][0], points[0][1], 0, polygon);
            return new Cell(x / area, y / area, 0, polygon);
        }
    }
}