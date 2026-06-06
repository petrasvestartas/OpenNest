using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace nest_rhino_lib
{
    public static class ToRhino
    {
        public static Curve[] get_text(string text, Circle circle, string font = "", bool bold = false)
        {
            var location = circle.Plane;
            double size = circle.Radius;
            var hAlign = 0;
            var vAlign = 0;

            string[] options = font.Split(' ');
            bool Bold = false;
            bool Italic = false;

            if (options.Length == 3)
            {
                font = options[0];
                Bold = options[1] == "True" || options[1] == "true" || options[1] == "1" ? true : false;
                Italic = options[2] == "True" || options[2] == "true" || options[2] == "1" ? true : false;
            }
            else if (options.Length == 1)
            {
                font = options[0];
            }

            if (font != "" && font != "default" && font != "none" && font != "x" && font != "0" && font != " ")
            {
                if (size == 0)
                    size = 1;

                if (!string.IsNullOrEmpty(font) && size > 0 && !string.IsNullOrEmpty(text) && location.IsValid)
                {
                    var te = Rhino.RhinoDoc.ActiveDoc.Objects.AddText(text, location, size, font, Bold, Italic, TextJustification.MiddleCenter);
                    Rhino.DocObjects.TextObject txt = Rhino.RhinoDoc.ActiveDoc.Objects.Find(te) as Rhino.DocObjects.TextObject;

                    if (txt != null)
                    {
                        Rhino.RhinoDoc.ActiveDoc.Objects.Delete(te, true);
                        var tt = txt.Geometry as Rhino.Geometry.TextEntity;
                        return tt.Explode();
                        //DA.SetDataList(0, tt.Explode());
                    }
                }
            }
            else
            {
                //if (!DA.GetData(3, ref bold)) return;
                //if (!DA.GetData(3, ref hAlign)) return;
                // if (!DA.GetData(4, ref vAlign)) return;

                var typeWriter = bold ? Typewriter.Bold : Typewriter.Regular;

                var position = location.Origin;
                var unitX = location.XAxis * size;
                var unitZ = location.YAxis * size;

                var curves = typeWriter.Write(text, position, unitX, unitZ, hAlign, vAlign);

                return curves.ToArray();
                //DA.SetDataList(0, curves);
            }

            //FontWriter.Save(@"C:\Users\Thomas\Desktop\Test.xml", new[] { Typewriter.Regular, Typewriter.Bold });
            return new Curve[0];
        }

        public static Box Box(this Mesh mesh, Plane plane)
        {
            Box box;
            mesh.GetBoundingBox(plane, out box);
            return box;
        }

        public static Point2d Map2D(this Point3d point, Plane plane)
        {
            var p = point - plane.Origin;

            var x = p * plane.XAxis;
            var y = p * plane.YAxis;

            return new Point2d(x, y);
        }

        public static Point2d Map2D(this Vector3d vector, Plane plane)
        {
            var p = vector - (Vector3d)plane.Origin;

            var x = p * plane.XAxis;
            var y = p * plane.YAxis;

            return new Point2d(x, y);
        }

        public static List<List<IntPoint>> Section(this Mesh mesh, Plane plane, double tolerance, List<Rhino.Geometry.Curve> curves = null)
        {
            var sections = Intersection.MeshPlane(mesh, plane);

            if (sections == null)
                return new List<List<IntPoint>>();

            var joined = Curve.JoinCurves(sections.Select(o => new PolylineCurve(o)));

            if (curves != null)
                curves.AddRange(joined);

            return sections.ToPolygons(plane, tolerance).ToList();
        }

        public static List<List<IntPoint>> ToPolygons(this IEnumerable<Curve> curves, ref Plane? plane, double unit)
        {
            var polygons = new List<List<IntPoint>>();

            foreach (var curve in curves)
            {
                Plane curvePlane;

                if (!curve.TryGetPlane(out curvePlane))
                    continue;

                if (!plane.HasValue)
                    plane = curvePlane;

                var polygon = curve.ToPolygon(plane.Value, unit);

                if (polygon == null)
                    continue;

                polygons.Add(polygon);
            }

            return polygons;
        }

        public static List<List<IntPoint>> ToPolygons(this IEnumerable<Curve> curves, Plane plane, double unit)
        {
            return curves.Select(curve => curve.ToPolygon(plane, unit))
                         .Where(polygon => polygon != null)
                         .ToList();
        }

        public static List<List<IntPoint>> ToPolygons(this IEnumerable<Polyline> polylines, Plane plane, double unit)
        {
            return polylines.Select(polyline => polyline.ToPolygon(plane, unit))
                            .Where(polygon => polygon != null)
                            .ToList();
        }

        public static List<IntPoint> ToPolygon(this Curve curve, Plane plane, double unit)
        {
            Polyline polyline;

            if (!curve.TryGetPolyline(out polyline))
                return null;

            return polyline.ToPolygon(plane, unit);
        }

        public static List<IntPoint> ToPolygon(this IEnumerable<Point3d> polyline, Plane plane, double unit)
        {
            return polyline.Select(o => o - plane.Origin)
                           .Select(o => new IntPoint((o * plane.XAxis) / unit, o * (plane.YAxis) / unit))
                           .ToList();
        }

        public static Curve ToCurve(this List<IntPoint> polygon, Plane plane, double unit)
        {
            var polyline = polygon.ToPolyline(plane, unit);

            return new PolylineCurve(polyline);
        }

        public static Curve ToCurve(this List<IntPoint> polylineInt, Plane plane, Point2d origin, double unit)
        {
            var points = polylineInt.Select(p => plane.Origin + (p.X * unit + origin.X) * plane.XAxis + (p.Y * unit + origin.Y) * plane.YAxis)
                                    .ToList();

            if (points.Count > 0 && points.First() != points.Last())
                points.Add(points[0]);

            return new PolylineCurve(points);
        }

        public static Polyline ToPolyline(this List<IntPoint> polygon, Plane plane, double unit)
        {
            var points = polygon.Select(o => plane.Origin + o.X * unit * plane.XAxis + o.Y * unit * plane.YAxis)
                                .ToList();

            if (points.Count > 0 && points.First() != points.Last())
                points.Add(points[0]);

            return new Polyline(points);
        }

        public static List<Curve> ToCurves(this List<List<IntPoint>> polygons, Plane plane, double unit)
        {
            return polygons.Select(o => o.ToCurve(plane, unit)).ToList();
        }
    }
}