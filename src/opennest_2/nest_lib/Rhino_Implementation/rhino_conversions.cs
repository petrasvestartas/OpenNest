using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;

namespace nest_lib
{
    public static class rhino_conversions
    {
        public static Polyline CleanPolyline(Polyline x, double t = 0.01)
        {
            Polyline polyline = new Polyline(x);
            //polyline.CollapseShortSegments( t);
            polyline.DeleteShortSegments(t);

            Polyline polylineClean = new Polyline();
            polylineClean.Add(polyline[0]);

            for (int i = 1; i < polyline.Count; i++)
            {
                double dist = polylineClean[i - 1].DistanceTo(polyline[i]);

                if (dist > t)
                {
                    polylineClean.Add(polyline[i]);
                }
            }

            if (!polylineClean.IsValid)
                Rhino.RhinoApp.WriteLine("NotValid");

            //RhinoHelpers.DuplicateRemover dr = new RhinoHelpers.DuplicateRemover(polyline, t);
            //dr.Solve();
            //Polyline polylineClean = new Polyline(dr.UniquePoints);
            //dr.Dispose();
            //polylineClean.Add(polylineClean[0]);

            return polylineClean;
        }

        public static double FastDistance(Point3d p1, Point3d p2)
        {
            return (p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y) + (p2.Z - p1.Z) * (p2.Z - p1.Z);
        }

        public static Polyline[] BrepLoops(Brep B)
        {
            if (B.Faces.Count == 1 && B.Faces[0].IsPlanar())
            {
                Polyline[] polylines = new Polyline[B.Loops.Count];
                double[] len = new double[B.Loops.Count];

                for (int i = 0; i < B.Loops.Count; i++)
                {
                    Curve curve = B.Loops[i].To3dCurve();
                    Polyline polyline;
                    curve.TryGetPolyline(out polyline);
                    //Polyline cleanPoly = CleanPolyline(polyline);
                    polylines[i] = polyline;

                    //len[i] = polyline.Length;
                    len[i] = polyline.BoundingBox.Diagonal.Length;
                }

                Array.Sort(len, polylines);

                int n = polylines.Length - 1;
                if (polylines[n].IsValid && polylines[n].Count > 2 && FastDistance(polylines[n][0], polylines[n][polylines[n].Count - 1]) < 0.01)
                {
                    return polylines;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                Mesh[] meshes = Mesh.CreateFromBrep(B);

                //Rhino.RhinoApp.WriteLine(meshes.Length.ToString());

                Mesh mesh = new Mesh();
                foreach (Mesh m in meshes)
                    mesh.Append(m);

                //Rhino.RhinoDoc.ActiveDoc.Objects.AddMesh(mesh);
                Polyline[] polylines = mesh.GetOutlines(Plane.WorldXY);

                double[] len = new double[polylines.Length];

                for (int i = 0; i < polylines.Length; i++)
                {
                    len[i] = polylines[i].Length;
                }
                Array.Sort(len, polylines);

                //Rhino.RhinoDoc.ActiveDoc.Objects.AddPolyline(polylines[polylines.Length - 1]);

                //return null;

                return new Polyline[] { polylines[polylines.Length - 1] };
            }
        }

        public static Polyline[] MeshLoops(Mesh mesh)
        {
            //Rhino.RhinoDoc.ActiveDoc.Objects.AddMesh(mesh);
            Polyline[] polylines = mesh.GetOutlines(Plane.WorldXY);

            double[] len = new double[polylines.Length];

            for (int i = 0; i < polylines.Length; i++)
            {
                len[i] = polylines[i].Length;
            }
            Array.Sort(len, polylines);

            //Rhino.RhinoDoc.ActiveDoc.Objects.AddPolyline(polylines[polylines.Length - 1]);

            //return null;

            return new Polyline[] { polylines[polylines.Length - 1] };
        }

        public static Tuple<Polyline, Transform> FitPolylineToPlane(Polyline polyline)
        {
            //Fit polyline to plane
            Plane plane = Plane.Unset;
            Plane.FitPlaneToPoints(polyline, out plane);

            //Project polyline to plane
            Polyline projectedPolyline = new Polyline();

            for (int i = 0; i < polyline.Count - 1; i++)
                projectedPolyline.Add(plane.ClosestPoint(polyline[i]));

            projectedPolyline.Add(projectedPolyline[0]);

            plane = new Plane(projectedPolyline[0], AverageNormal(projectedPolyline));
            Transform transform = Transform.Translation((Vector3d)(-projectedPolyline[0]));

            projectedPolyline.Transform(transform);

            return new Tuple<Polyline, Transform>(projectedPolyline, transform);
        }

        public static Vector3d AverageNormal(Polyline p)
        {
            int len = p.Count - 1;
            Vector3d vector3d = new Vector3d();

            for (int i = 0; i < len; i++)
            {
                int num = ((i - 1) + len) % len;
                int item1 = ((i + 1) + len) % len;
                Point3d point3d = p[num];
                Point3d point3d1 = p[item1];
                Point3d item2 = p[i];
                vector3d = vector3d + Vector3d.CrossProduct(new Vector3d(item2 - point3d), new Vector3d(point3d1 - item2));
            }

            if (vector3d.X == 0 & vector3d.Y == 0 & vector3d.Z == 0)
                vector3d.Unitize();

            return vector3d;
        }

        public static bool IsClockwisePolygon(Polyline polygon)
        {
            double sum = 0;

            for (int i = 0; i < polygon.Count - 1; i++)
                sum += (polygon[i + 1].X - polygon[i].X) * (polygon[i + 1].Y + polygon[i].Y);

            return sum > 0;
        }

        public static Tuple<Brep[], Dictionary<int, List<Guid>>> SortGuidsByPlanarCurves(List<Rhino.DocObjects.RhinoObject> guids, double tol)
        {
            var TxtInput = new Dictionary<int, Tuple<Point3d, string>>();
            var crvsInput = new Dictionary<int, Curve>();
            var ptsInput = new Dictionary<int, Point3d>();
            var meshesInput = new Dictionary<int, Mesh>();
            var brepsInput = new Dictionary<int, Brep>();

            //Identify object types
            int id = 0;
            foreach (var g in guids)
            {
                GeometryBase rhino_obj = g.Geometry;
                string objectType = g.ObjectType.ToString();

                switch (objectType)
                {
                    case ("Annotation"):
                        TextEntity textObj = rhino_obj as TextEntity;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textObj.Plane.Origin, textObj.Text));
                        break;

                    case ("TextDot"):
                        TextDot textDotObj = rhino_obj as TextDot;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textDotObj.Point, textDotObj.Text));

                        break;

                    case ("Curve"):
                        Curve curve = rhino_obj as Curve;
                        //if (curve.IsPolyline()) {
                        crvsInput.Add(id, curve);
                        //}
                        break;

                    case ("Point"):
                        Rhino.Geometry.Point p = rhino_obj as Rhino.Geometry.Point;
                        ptsInput.Add(id, p.Location);
                        break;

                    case ("Mesh"):
                        Mesh m = rhino_obj as Mesh;
                        meshesInput.Add(id, m);
                        break;

                    case ("Brep"):
                        Brep b = rhino_obj as Brep;
                        brepsInput.Add(id, b);
                        break;

                    case ("Extrusion"):
                        Extrusion e = rhino_obj as Extrusion;
                        brepsInput.Add(id, e.ToBrep());
                        break;

                    default:
                        Rhino.RhinoApp.WriteLine("Uknown type: " + objectType.ToString());
                        break;
                }

                id++;
            }

            //Joints Curves
            var crvClosed = new List<Curve>();

            foreach (var c in crvsInput)
            {
                c.Value.Domain = new Interval(0, 1);
                if (c.Value.IsClosed)
                {
                    if (c.Value.IsPolyline())
                    {
                        crvClosed.Add(c.Value);
                    }
                }
            }

            //Create planar breps
            Brep[] breps = Brep.CreatePlanarBreps(crvClosed);// Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

            HashSet<int> hash = new HashSet<int>();
            Dictionary<int, List<Guid>> guidsTree = new Dictionary<int, List<Guid>>();
            for (int i = 0; i < breps.Length; i++)
            {
                guidsTree.Add(i, new List<Guid>());
            }

            //Check input

            //Crvs
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in crvsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.PointAt(0.5);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key].Id);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Txt
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in TxtInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.Item1;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key].Id);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Points
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in ptsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key].Id);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Meshes
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in meshesInput)
                {
                    if (!hash.Contains(j))
                    {
                        //Rhino.RhinoApp.WriteLine("Hi");
                        // Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        Point3d p = c.Value.Vertices[0];
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key].Id);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Breps
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in brepsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key].Id);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            return new Tuple<Brep[], Dictionary<int, List<Guid>>>(breps, guidsTree);
        }

        public static Tuple<Brep[], Dictionary<int, List<Guid>>> SortGuidsByPlanarCurves(List<Guid> guids, double tol)
        {
            var TxtInput = new Dictionary<int, Tuple<Point3d, string>>();
            var crvsInput = new Dictionary<int, Curve>();
            var ptsInput = new Dictionary<int, Point3d>();
            var meshesInput = new Dictionary<int, Mesh>();
            var brepsInput = new Dictionary<int, Brep>();

            //Identify object types
            int id = 0;
            foreach (var g in guids)
            {
                GeometryBase rhino_obj = Rhino.RhinoDoc.ActiveDoc.Objects.Find(g).Geometry;
                string objectType = rhino_obj.ObjectType.ToString();

                //Rhino.RhinoApp.WriteLine("Uknown type: " + objectType.ToString());

                switch (objectType)
                {
                    case ("Annotation"):
                        TextEntity textObj = rhino_obj as TextEntity;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textObj.Plane.Origin, textObj.Text));
                        break;

                    case ("TextDot"):
                        TextDot textDotObj = rhino_obj as TextDot;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textDotObj.Point, textDotObj.Text));

                        break;

                    case ("Curve"):
                        Curve curve = rhino_obj as Curve;
                        //if (curve.IsPolyline()) {
                        crvsInput.Add(id, curve);
                        //}
                        break;

                    case ("Point"):
                        Rhino.Geometry.Point p = rhino_obj as Rhino.Geometry.Point;
                        ptsInput.Add(id, p.Location);
                        break;

                    case ("Mesh"):
                        Mesh m = rhino_obj as Mesh;
                        meshesInput.Add(id, m);
                        break;

                    case ("Brep"):
                        Brep b = rhino_obj as Brep;
                        brepsInput.Add(id, b);
                        break;

                    case ("Extrusion"):
                        Extrusion e = rhino_obj as Extrusion;
                        brepsInput.Add(id, e.ToBrep());
                        break;

                    default:
                        Rhino.RhinoApp.WriteLine("Uknown type: " + objectType.ToString());
                        break;
                }

                id++;
            }

            //Joints Curves
            var crvClosed = new List<Curve>();

            foreach (var c in crvsInput)
            {
                c.Value.Domain = new Interval(0, 1);
                if (c.Value.IsClosed)
                {
                    if (c.Value.IsPolyline())
                    {
                        crvClosed.Add(c.Value);
                    }
                }
            }

            //Create planar breps
            Brep[] breps = Brep.CreatePlanarBreps(crvClosed);// Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

            HashSet<int> hash = new HashSet<int>();
            Dictionary<int, List<Guid>> guidsTree = new Dictionary<int, List<Guid>>();
            for (int i = 0; i < breps.Length; i++)
            {
                guidsTree.Add(i, new List<Guid>());
            }

            //Check input

            //Crvs
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in crvsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.PointAt(0.5);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Txt
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in TxtInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.Item1;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Points
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in ptsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Meshes
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in meshesInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        p = c.Value.Vertices[0];


                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Breps
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in brepsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            return new Tuple<Brep[], Dictionary<int, List<Guid>>>(breps, guidsTree);
        }

        public static Tuple<Polyline, Plane> FitPolylineToPlane2(Polyline polyline)
        {
            //Fit polyline to plane
            Plane plane = Plane.Unset;
            Plane.FitPlaneToPoints(polyline, out plane);

            //Project polyline to plane
            Polyline projectedPolyline = new Polyline();

            for (int i = 0; i < polyline.Count - 1; i++)
                projectedPolyline.Add(plane.ClosestPoint(polyline[i]));

            projectedPolyline.Add(projectedPolyline[0]);

            //Get average plane for loop of cross product
            plane = new Plane(projectedPolyline[0], AverageNormal(projectedPolyline));


            return new Tuple<Polyline, Plane>(projectedPolyline, plane);
        }

        public static List<Polyline> ToPolylines(this IEnumerable<Curve> nurbsCurves, bool collapseShortSegments = true)
        {
            List<Polyline> p = new List<Polyline>(nurbsCurves.Count());

            for (int i = 0; i < nurbsCurves.Count(); i++)
            {
                nurbsCurves.ElementAt(i).TryGetPolyline(out Polyline polyline);
                p.Add(nurbsCurves.ElementAt(i).ToPolyline(collapseShortSegments));
            }

            return p;
        }

        public static Polyline ToPolyline(this Curve curve, bool collapseShortSegments = true)
        {
            curve.TryGetPolyline(out Polyline polyline);
            if (collapseShortSegments)
                polyline.CollapseShortSegments(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
            return polyline;
        }

        public static Transform[] MergeTransfoms(this Transform[] t0, Transform[] t1)
        {
            Transform[] t2 = new Transform[t0.Length];

            for (int i = 0; i < t0.Length; i++)
            {
                t2[i] = t0[i] * t1[i];
            }

            return t2;
        }

        public static Polyline TransformPolyline(this Polyline polyline, Transform transform)
        {
            Polyline newPolyline = new Polyline(polyline);
            newPolyline.Transform(transform);
            return newPolyline;
        }

        public static Polyline[] TransformPolylines(this List<Polyline> polylines, Transform[] transform)
        {
            Polyline[] polylinesNew = new Polyline[polylines.Count];

            for (int i = 0; i < polylines.Count; i++)
            {
                polylinesNew[i] = polylines[i].TransformPolyline(transform[i]);
            }

            return polylinesNew;
        }

        public static Polyline[] TransformPolylines(this Polyline[] polylines, Transform[] transform)
        {
            Polyline[] polylinesNew = new Polyline[polylines.Length];

            for (int i = 0; i < polylines.Length; i++)
            {
                polylinesNew[i] = polylines[i].TransformPolyline(transform[i]);
            }

            return polylinesNew;
        }

        public static Transform GetTransform(this NFP nfp)
        {
            //Transformation matrix from translation and rotation
            Transform translate = Transform.Translation(new Vector3d(nfp.x, nfp.y, 0));
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            translate *= rotation;

            return translate;
        }

        public static Transform[] GetTransforms(this List<NFP> nfps)
        {
            //Rhino.RhinoApp.WriteLine("nfps " + nfps.Count.ToString());
            Transform[] transforms = new Transform[nfps.Count];
            for (int i = 0; i < nfps.Count; i++)
            {
                transforms[i] = nfps[i].GetTransform();
                //int sheetId = nfps[i].;
            }
            return transforms;
        }

        public static Transform GetRotation(this NFP nfp)
        {
            //Transformation matrix from translation and rotation
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            return rotation;
        }

        public static Transform[] GetRotations(this List<NFP> nfps)
        {
            Transform[] transforms = new Transform[nfps.Count];
            for (int i = 0; i < nfps.Count; i++)
            {
                transforms[i] = nfps[i].GetRotation();
            }
            return transforms;
        }

        public static Polyline ToPolyline(this NFP nfp)
        {
            Polyline polyline = new Polyline();
            foreach (SvgPoint p in nfp.Points)
            {
                polyline.Add(p.x, p.y, 0);
            }


            polyline.Add(polyline[0]);

            //Transformation

            Transform translate = Transform.Translation(new Vector3d(nfp.x, nfp.y, 0));
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            translate *= rotation;

            polyline.Transform(translate);

            return polyline;
        }

        public static List<Polyline> ToPolylines(this NFP nfp)
        {
            Polyline polyline = new Polyline();
            foreach (SvgPoint p in nfp.Points)
            {
                polyline.Add(p.x, p.y, 0);
            }

            List<Polyline> holes = new List<Polyline>();
            foreach (NFP hole in nfp.children)
            {
                Polyline holePolyline = new Polyline();
                foreach (SvgPoint p in hole.Points)
                {
                    holePolyline.Add(p.x, p.y, 0);
                }
                holes.Add(holePolyline);
            }

            polyline.Add(polyline[0]);

            //Transformation

            Transform translate = Transform.Translation(new Vector3d(nfp.x, nfp.y, 0));
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            translate *= rotation;

            polyline.Transform(translate);

            foreach (Polyline hole in holes)
            {
                hole.Add(hole[0]);
                hole.Transform(translate);
            }

            List<Polyline> polylines = new List<Polyline>();
            polylines.Add(polyline);
            polylines.AddRange(holes);



            return polylines;
        }

        public static Polyline[] ToPolylines(this List<NFP> nfps)
        {
            Polyline[] polylines = new Polyline[nfps.Count];

            for (int i = 0; i < nfps.Count; i++)
            {
                polylines[i] = nfps[i].ToPolyline();
            }

            return polylines;
        }

        public static int[] ToIDArray(this List<NFP> nfps)
        {
            int[] id = new int[nfps.Count];

            for (int i = 0; i < nfps.Count; i++)
                id[i] = nfps[i].id;

            return id;
        }

        public static int[] ToSourceArray(this List<NFP> nfps)
        {
            int[] id = new int[nfps.Count];

            for (int i = 0; i < nfps.Count; i++)
                id[i] = (int)nfps[i].source;

            return id;
        }
    }
}
