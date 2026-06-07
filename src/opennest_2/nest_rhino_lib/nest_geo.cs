using Ed.Eto;
using nest_rhino_lib.sort_2d;
using Rhino;
using Rhino.Collections;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Collections;
using Rhino.Geometry.Intersect;
using Rhino.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace nest_rhino_lib
{
    public class nest_geo
    {
        public List<int> indices;

        public List<int> copies;

        public List<GeometryBase> geometry;

        public List<GeometryBase[]> geometry_attributes;

        public List<ObjectAttributes> attributes;

        public List<List<int>> geometry_sorted;

        public List<int> boudary_indices_non_sorted;

        public List<Curve> boundary_curves_non_sorted;

        public List<List<Tuple<int, Polyline, BoundingBox, Curve>>> boundary_sorted;

        public List<TextEntity> disply_texts;

        public List<List<Transform>> xforms;

        private HashSet<int> visited = new HashSet<int>();

        private List<int> ids = new List<int>();

        private int current_id = -1;

        public nest_geo()
        {
            this.indices = new List<int>();
            this.copies = new List<int>();
            this.geometry = new List<GeometryBase>();
            this.geometry_attributes = new List<GeometryBase[]>();
            this.attributes = new List<ObjectAttributes>();
            this.geometry_sorted = new List<List<int>>();
            this.boudary_indices_non_sorted = new List<int>();
            this.boundary_curves_non_sorted = new List<Curve>();
            this.boundary_sorted = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();
            this.disply_texts = new List<TextEntity>();
            this.xforms = new List<List<Transform>>();
        }

        public List<Guid> bake()
        {
          
            List<Guid> guids = new List<Guid>();
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<Guid> guids1 = new List<Guid>();
                foreach (int item in this.geometry_sorted[i])
                {
                    this.attributes[item].RemoveFromAllGroups();
                    this.attributes[item].RemoveDisplayModeOverride();
                    GeometryBase geometryBase = this.geometry[item].Duplicate();
                    geometryBase.Translate(new Vector3d(0, 0, (double)1000));
                    guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(geometryBase, this.attributes[item]));
                }
                var group = RhinoDoc.ActiveDoc.Groups.FindName(string.Concat("nest_geo_", i.ToString()));
                if (group != null)
                {
                    RhinoDoc.ActiveDoc.Groups.Delete(group);
                }
                int num = RhinoDoc.ActiveDoc.Groups.Add(string.Concat("nest_geo_", i.ToString()), guids1);
                RhinoDoc.ActiveDoc.Groups.Show(num);
                RhinoDoc.ActiveDoc.Groups.Unlock(num);
                guids.AddRange(guids1);
            }
            return guids;
        }

        public List<Guid> bake_with_transforms()
        {
            RhinoApp.WriteLine("bake with transforms");
            List<Guid> guids = new List<Guid>();
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                for (int j = 0; j < this.xforms[i].Count; j++)
                {
                    List<Guid> guids1 = new List<Guid>();
                    foreach (int item in this.geometry_sorted[i])
                    {
                        this.attributes[item].RemoveFromAllGroups();
                        this.attributes[item].RemoveDisplayModeOverride();
                        GeometryBase geometryBase = this.geometry[item].Duplicate();
                        geometryBase.Transform(this.xforms[i][j]);
                        guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(geometryBase, this.attributes[item]));

                        var attribute_copy_for_geometry_attributes = this.attributes[item].Duplicate();
                        attribute_copy_for_geometry_attributes.ObjectColor = System.Drawing.Color.FromArgb(255, 0, 0);
                        attribute_copy_for_geometry_attributes.ColorSource = ObjectColorSource.ColorFromObject;
                        attribute_copy_for_geometry_attributes.PlotColor = System.Drawing.Color.FromArgb(255, 0, 0);
                        attribute_copy_for_geometry_attributes.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;

                        if (this.geometry_attributes.Count > item)
                        {
                            foreach (var geometry_attribute in this.geometry_attributes[item])
                            {
                                var copy = geometry_attribute.Duplicate();
                                copy.Transform(this.xforms[i][j]);
                                guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(copy, attribute_copy_for_geometry_attributes));
                            }
                        }
                    }
                    var group = RhinoDoc.ActiveDoc.Groups.FindName(string.Concat("nest_geo_", j.ToString(), "_", j.ToString()));
                    if (group != null)
                    {
                        RhinoDoc.ActiveDoc.Groups.Delete(group);
                    }
                    int num = RhinoDoc.ActiveDoc.Groups.Add(string.Concat("nest_geo_", i.ToString()), guids1);
                    RhinoDoc.ActiveDoc.Groups.Show(num);
                    RhinoDoc.ActiveDoc.Groups.Unlock(num);
                    guids.AddRange(guids1);
                }
            }
            return guids;
        }

        private void BoundingBoxCallback(object sender, RTreeEventArgs e)
        {
            if (e.Id > this.current_id)
            {
                this.ids.Add(e.Id);
            }
        }

        public bool ccw(Point3d a, Point3d b, Point3d c)
        {
            bool x = (b.X - a.X) * (c.Y - a.Y) > (b.Y - a.Y) * (c.X - a.X);
            return x;
        }

        public List<Point3d> convex_hull(List<Point3d> p)
        {
            List<Point3d> point3ds;
            if (p.Count != 0)
            {
                p.Sort();
                List<Point3d> point3ds1 = new List<Point3d>();
                foreach (Point3d point3d in p)
                {
                    while (true)
                    {
                        if ((point3ds1.Count < 2 ? true : this.ccw(point3ds1[point3ds1.Count - 2], point3ds1[point3ds1.Count - 1], point3d)))
                        {
                            break;
                        }
                        point3ds1.RemoveAt(point3ds1.Count - 1);
                    }
                    point3ds1.Add(point3d);
                }
                int count = point3ds1.Count + 1;
                for (int i = p.Count - 1; i >= 0; i--)
                {
                    Point3d item = p[i];
                    while (true)
                    {
                        if ((point3ds1.Count < count ? true : this.ccw(point3ds1[point3ds1.Count - 2], point3ds1[point3ds1.Count - 1], item)))
                        {
                            break;
                        }
                        point3ds1.RemoveAt(point3ds1.Count - 1);
                    }
                    point3ds1.Add(item);
                }
                point3ds = point3ds1;
            }
            else
            {
                point3ds = new List<Point3d>();
            }
            return point3ds;
        }

        public Polyline curve_to_polyline(Curve curve, double segment_division_length = 0, bool hull = false, bool keep_all = false)
        {
            Curve[] segments = curve_explode.GetSegments(curve, true, 1);
            Polyline polyline = new Polyline((int)segments.Length + 1);
            if ((int)segments.Length >= 3)
            {
                Curve[] curveArray = segments;
                for (int i = 0; i < (int)curveArray.Length; i++)
                {
                    Curve curve1 = curveArray[i];
                    double length = curve1.GetLength();
                    if ((segment_division_length == 0 || curve1.IsLinear(RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 10) ? true : length < Math.Abs(segment_division_length)))
                    {
                        polyline.Add(curve1.PointAtStart);
                    }
                    else if (segment_division_length >= 0)
                    {
                        polyline.AddRange(curve1.ToPolyline(segment_division_length, 0, 0, 0).ToPolyline());
                        polyline.RemoveAt(polyline.Count() - 1);
                    }
                    else
                    {
                        polyline.Add(curve1.PointAtStart);
                        Interval domain = curve1.Domain;
                        polyline.Add(curve1.PointAt(domain.Mid));
                    }
                }
            }
            else
            {
                Point3d[] point3dArray = null;
                segments[0].DivideByCount(4, true, out point3dArray);
                polyline.AddRange(point3dArray);
            }
            if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
            {
                polyline.Add(polyline[0]);
            }
            // keep_all (Simplify==0): preserve EVERY input vertex - skip the colinear merge that
            // otherwise collapses fine curve detail (e.g. 67-pt ribbons -> ~15) and loosens nesting.
            if (!keep_all)
                polyline.MergeColinearSegments(RhinoMath.ToRadians(10), true);
            if (hull)
            {
                polyline = new Polyline(this.convex_hull(polyline.ToList<Point3d>()));
            }
            return polyline;
        }

        public Polyline curve_to_polyline_unfillet(Curve curve, double segment_division_length = 0, bool hull = false)
        {
            Point3d[] point3dArray = null;
            Arc arc = new Arc();
            double num = 0;
            double num1 = 0;
            Point3d[] point3dArray1 = null;
            Point3d[] point3dArray2 = null;
            Point3d[] point3dArray3 = null;
            double modelAbsoluteTolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            double modelAngleToleranceRadians = RhinoDoc.ActiveDoc.ModelAngleToleranceRadians;
            Curve curve1 = curve.Simplify(CurveSimplifyOptions.All, modelAbsoluteTolerance * 10, modelAngleToleranceRadians * 2) ?? curve;
            Curve[] segments = curve_explode.GetSegments(curve1, true, 1);
            Polyline polyline = new Polyline((int)segments.Length + 1);

            if ((int)segments.Length >= 4)
            {
                bool flag = false;
                bool flag1 = false;
                for (int i = 0; i < (int)segments.Length; i++)
                {
                    int length = ((i - 1) % (int)segments.Length + (int)segments.Length) % (int)segments.Length;
                    int length1 = ((i + 1) % (int)segments.Length + (int)segments.Length) % (int)segments.Length;
                    if (!segments[i].TryGetArc(out arc, modelAbsoluteTolerance))
                    {
                        if (i == 0)
                        {
                            polyline.Add(segments[i].PointAtStart);
                        }
                        if (!segments[i].IsLinear(modelAbsoluteTolerance))
                        {
                            segments[i].DivideByCount(Math.Min(15, Math.Max(2, (int)(segments[i].GetLength() / Math.Abs(segment_division_length)))), false, out point3dArray1);
                            polyline.AddRange(point3dArray1);
                        }
                        polyline.Add(segments[i].PointAtEnd);
                    }
                    else if (arc.EndAngleDegrees <= 270)
                    {
                        Line line = new Line(segments[length].PointAtEnd, segments[length].PointAtStart);
                        Line line1 = new Line(segments[length1].PointAtStart, segments[length1].PointAtEnd);
                        if (arc.Length > Math.Abs(segment_division_length))
                        {
                            if (i == 0)
                                polyline.Add(segments[i].PointAtStart);

                            Curve curve2 = segments[i];
                            //Interval domain = segments[i].Domain;
                            //polyline.Add(curve2.PointAt(domain.Mid));

                            if (segment_division_length != 0)
                            {
                                int divisions = Math.Min(20, Math.Max(2, (int)Math.Floor(curve2.GetLength() / segment_division_length)));
                                curve2.DivideByCount(divisions, true, out Point3d[] division_points);
                                polyline.AddRange(division_points);
                            }



                            polyline.Add(segments[i].PointAtEnd);
                        }
                        else if ((!segments[length].IsLinear(modelAbsoluteTolerance) ? false : segments[length1].IsLinear(modelAbsoluteTolerance)))
                        {
                            if (i == 0)
                            {
                                flag = true;
                            }
                            if (i == (int)segments.Length - 1)
                            {
                                flag1 = true;
                            }
                            Intersection.LineLine(line, line1, out num, out num1, modelAbsoluteTolerance, false);
                            if (Math.Abs(3.14159265358979 - Vector3d.VectorAngle(line.Direction, line1.Direction, Plane.WorldXY)) < modelAngleToleranceRadians)
                            {
                                polyline.Add(segments[i].PointAtEnd);
                            }
                            else if (polyline.Count() <= 0)
                            {
                                polyline.Add(line.PointAt(num));
                            }
                            else
                            {
                                polyline[polyline.Count-1]=(line.PointAt(num));
                            }
                        }
                        else
                        {
                            polyline.Add(segments[i].PointAtEnd);
                        }
                    }
                    else
                    {
                        if (i == 0)
                        {
                            polyline.Add(segments[i].PointAtStart);
                        }
                        if (!segments[i].IsLinear(modelAbsoluteTolerance))
                        {
                            segments[i].DivideByCount(Math.Min(8, Math.Max(3, (int)(segments[i].GetLength() / Math.Abs(segment_division_length)))), false, out point3dArray2);
                            polyline.AddRange(point3dArray2);
                        }
                        polyline.Add(segments[i].PointAtEnd);
                    }
                }
                Line line2 = new Line(polyline[0], polyline[1]);
                Line line3 = new Line(polyline.Last(), polyline[polyline.Count() - 2]);
                if (flag1 & flag)
                {
                    polyline[polyline.Count-1]=(polyline[0]);
                }
                polyline.Add(polyline[0]);
            }
            else
            {
                curve.DivideByCount(Math.Min(30, Math.Max(3, (int)(curve.GetLength() / Math.Abs(segment_division_length)))), true, out point3dArray);
                polyline.AddRange(point3dArray);
                if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
                {
                    polyline.Add(polyline[0]);
                }
            }
            polyline.MergeColinearSegments(modelAngleToleranceRadians * 1.5, true);
            polyline.CollapseShortSegments(modelAbsoluteTolerance * 10);
            if ((!polyline.IsValid ? true : polyline.Count() == 2))
            {
                polyline.Clear();
                curve.DivideByCount(Math.Min(8, Math.Max(4, (int)(curve.GetLength() / Math.Abs(segment_division_length * 0.5)))), true, out point3dArray3);
                polyline.AddRange(point3dArray3);
                if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
                {
                    polyline.Add(polyline[0]);
                }
            }
            if (hull)
            {
                polyline = new Polyline(this.convex_hull(polyline.ToList<Point3d>()));
            }
            return polyline;
        }

        public nest_geo duplicate()
        {
            nest_geo nestGeo = new nest_geo()
            {
                indices = this.indices,
                copies = this.copies,
                attributes = this.attributes,
                geometry_sorted = this.geometry_sorted,
                boudary_indices_non_sorted = this.boudary_indices_non_sorted
            };
            nestGeo.attributes = new List<ObjectAttributes>();
            for (int i = 0; i < this.attributes.Count; i++)
            {
                nestGeo.attributes.Add(this.attributes[i].Duplicate());
            }
            nestGeo.geometry = new List<GeometryBase>();
            for (int j = 0; j < this.geometry.Count; j++)
            {
                nestGeo.geometry.Add(this.geometry[j].Duplicate());
            }

            nestGeo.geometry_attributes = new List<GeometryBase[]>();

            for (int j = 0; j < this.geometry_attributes.Count; j++)
            {
                var copy = new GeometryBase[this.geometry_attributes[j].Length];
                for (int k = 0; k < copy.Length; k++)
                {
                    copy[k] = this.geometry_attributes[j][k].Duplicate();
                }
                nestGeo.geometry_attributes.Add(copy);



            }


            nestGeo.boundary_curves_non_sorted = new List<Curve>();
            for (int k = 0; k < this.boundary_curves_non_sorted.Count; k++)
            {
                nestGeo.boundary_curves_non_sorted.Add(this.boundary_curves_non_sorted[k].DuplicateCurve());
            }
            nestGeo.boundary_sorted = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();
            for (int l = 0; l < this.boundary_sorted.Count; l++)
            {
                List<Tuple<int, Polyline, BoundingBox, Curve>> tuples = new List<Tuple<int, Polyline, BoundingBox, Curve>>();
                nestGeo.boundary_sorted.Add(tuples);
                for (int m = 0; m < this.boundary_sorted[l].Count; m++)
                {
                    nestGeo.boundary_sorted.Last<List<Tuple<int, Polyline, BoundingBox, Curve>>>().Add(Tuple.Create<int, Polyline, BoundingBox, Curve>(this.boundary_sorted[l][m].Item1, this.boundary_sorted[l][m].Item2.Duplicate(), this.boundary_sorted[l][m].Item3, this.boundary_sorted[l][m].Item4.DuplicateCurve()));
                }
            }
            nestGeo.disply_texts = new List<TextEntity>();
            for (int n = 0; n < this.disply_texts.Count; n++)
            {
                nestGeo.disply_texts.Add(this.disply_texts[n]);
            }
            return nestGeo;
        }

        public void duplicate_openlines_and_flip(string layer_name = "Planks")
        {
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<int> nums = new List<int>();
                foreach (int item in this.geometry_sorted[i])
                {
                    nums.Add(item);
                    if (this.geometry[item].ObjectType.ToString() == "Curve")
                    {
                        Curve curve = this.geometry[item] as Curve;
                        if (RhinoDoc.ActiveDoc.Layers.FindIndex(this.attributes[i].LayerIndex).Name == layer_name)
                        {
                            Curve curve1 = curve.DuplicateCurve();
                            curve1.Reverse();
                            this.geometry.Add(curve1);
                            this.attributes.Add(this.attributes[item]);
                            nums.Add(this.geometry.Count - 1);
                        }
                    }
                }
                this.geometry_sorted[i] = nums;
            }
        }

        public void extend_openlines(double distance)
        {
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<int> nums = new List<int>();
                foreach (int item in this.geometry_sorted[i])
                {
                    nums.Add(item);
                    if (this.geometry[item].ObjectType.ToString() == "Curve")
                    {
                        Curve curve = this.geometry[item] as Curve;
                        if (curve.PointAtStart.DistanceToSquared(curve.PointAtEnd) > RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * RhinoDoc.ActiveDoc.ModelAbsoluteTolerance)
                        {
                            Curve curve1 = distance < 0 ? curve.Trim(3, -distance) : curve.Extend(CurveEnd.Both, distance, CurveExtensionStyle.Smooth);

                            if (curve1 != null && curve1.IsValid)
                            {
                                this.geometry[item] = curve1;
                            }
                        }
                    }
                }
                this.geometry_sorted[i] = nums;
            }
        }

        public void hard_coded_input(List<int> ids, double segment_division_length = 0, bool hull = false)
        {

            var double_lengths = new double[ids.Count];
            var plines_simplified = new Tuple<int, Polyline, BoundingBox, Curve>[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                Polyline pline = segment_division_length == 0 ?
                    this.curve_to_polyline(boundary_curves_non_sorted[ids[i]], 0, hull, true) :   // 0 = keep all vertices
                    segment_division_length < 0 ?
                    this.curve_to_polyline(boundary_curves_non_sorted[ids[i]], segment_division_length, hull) :
                    this.curve_to_polyline_unfillet(boundary_curves_non_sorted[ids[i]], segment_division_length, hull);
                BoundingBox bbox = pline.Count == 2 ? new BoundingBox(pline[0], pline[1]) : pline.BoundingBox;
                bbox.Inflate(RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 2);
                // Use the GLOBAL index ids[i] (not the local loop index i) for the source index and the original
                // curve — matching the curve read above (boundary_curves_non_sorted[ids[i]]). With local i, every
                // group's rings took the first global curves' index/curve, corrupting per-part source/Item4.
                plines_simplified[i] = Tuple.Create(boudary_indices_non_sorted[ids[i]], pline, bbox, boundary_curves_non_sorted[ids[i]]);
                double_lengths[i] = (bbox.Diagonal.Length);


            }

            Array.Sort(double_lengths, plines_simplified);
            Array.Reverse(double_lengths);
            Array.Reverse(plines_simplified);


            this.geometry_sorted.Add(ids);
            this.boundary_sorted.Add(plines_simplified.ToList());


     

        }

        public void identify_groups(double segment_division_length = 0, bool hull = false)
        {
            ////////////////////////////////////////////////////////////////////////////////////////
            ///Identify groups in boundaries
            ////////////////////////////////////////////////////////////////////////////////////////
            int n = boundary_curves_non_sorted.Count;

            var groups = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();

            //Sort by bbox diagonal, from largest to smallest
            var double_lengths = new double[n];
            var plines_simplified = new Tuple<int, Polyline, BoundingBox, Curve>[n];

            for (int i = 0; i < n; i++)
            {
                Polyline pline = segment_division_length == 0 ?
                    curve_to_polyline(boundary_curves_non_sorted[i], 0, hull, true) :   // 0 = keep all vertices
                    segment_division_length < 0 ?
                    curve_to_polyline(boundary_curves_non_sorted[i], segment_division_length, hull) :
                    curve_to_polyline_unfillet(boundary_curves_non_sorted[i], segment_division_length, hull);
                BoundingBox bbox = pline.Count == 2 ? new BoundingBox(pline[0], pline[1]) : pline.BoundingBox;
                bbox.Inflate(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 2);
                plines_simplified[i] = (Tuple.Create(boudary_indices_non_sorted[i], pline, bbox, boundary_curves_non_sorted[i]));
                double_lengths[i] = (bbox.Diagonal.Length);
            }


            Array.Sort(double_lengths, plines_simplified);
            Array.Reverse(double_lengths);
            Array.Reverse(plines_simplified);
       

            //RTree
            RTree tree = new RTree();

            for (int i = 0; i < n; i++)
                tree.Insert(plines_simplified[i].Item3, i);

            for (int i = 0; i < n; i++)
            {
                //skip if visited
                if (!visited.Add(i)) continue;

                // Geometry may sit on a plane offset from WorldXY (e.g. imported at z != 0). Use an XY plane
                // AT THIS CURVE'S ELEVATION for the winding + containment tests so outer+hole pairing still
                // works off the world origin (Curve.Contains against WorldXY can miss curves far from z=0).
                Plane testPlane = plines_simplified[i].Item2.Count > 0
                    ? new Plane(new Point3d(0, 0, plines_simplified[i].Item2[0].Z), Vector3d.ZAxis)
                    : Plane.WorldXY;

                //check winding
                if (plines_simplified[i].Item4.ClosedCurveOrientation(testPlane) == CurveOrientation.Clockwise)
                {
                    Polyline polyline_temp = new Polyline(plines_simplified[i].Item2);
                    polyline_temp.Reverse();
                    Curve curve_temp = plines_simplified[i].Item4.DuplicateCurve();
                    curve_temp.Reverse();
                    plines_simplified[i] = (Tuple.Create(plines_simplified[i].Item1, polyline_temp, plines_simplified[i].Item3, curve_temp));
                }

                //create local group
                var group_local = new List<Tuple<int, Polyline, BoundingBox, Curve>>() { plines_simplified[i] };

                //overwrite last search
                ids.Clear();
                current_id = i;

                //convert to curve for point inclusion test
                Curve temp_curve = plines_simplified[i].Item2.ToNurbsCurve();

                //search
                tree.Search(plines_simplified[i].Item3, BoundingBoxCallback);

                foreach (var id in ids)
                {
                    //iterate found polylines points while checking point inclusion in XY plane
                    for (int j = 0; j < plines_simplified[i].Item2.Count; j++)
                    {
                        if (temp_curve.Contains(plines_simplified[id].Item2[j], testPlane, 0.01) == PointContainment.Inside)
                        {
                            visited.Add(id);

                            if (plines_simplified[id].Item4.ClosedCurveOrientation(testPlane) == CurveOrientation.CounterClockwise)
                            {
                                Polyline polyline_temp = new Polyline(plines_simplified[id].Item2);
                                polyline_temp.Reverse();
                                Curve curve_temp = plines_simplified[id].Item4.DuplicateCurve();
                                curve_temp.Reverse();
                                plines_simplified[id] = (Tuple.Create(plines_simplified[id].Item1, polyline_temp, plines_simplified[id].Item3, curve_temp));
                            }
                            group_local.Add(plines_simplified[id]);
                            break;
                        }

                        //check just two points
                        if (j == 2)
                            break;
                    }//iterate collision points
                }//collision


                this.boundary_sorted.Add(group_local);

                //add text to display
                var text = new TextEntity();
                text.PlainText = copies[group_local[0].Item1].ToString();
                text.TextHeight = 100;
                text.Plane = new Plane(group_local[0].Item2.CenterPoint(), Vector3d.ZAxis);

                this.disply_texts.Add(text);
            }

            ///////////////////////////////////////////////////////////////////////////////////////
            //Identify groups in all geometries
            ///////////////////////////////////////////////////////////////////////////////////////
            current_id = -1;
            RTree tree_all_geo = new RTree();

            for (int i = 0; i < geometry.Count; i++)
                tree_all_geo.Insert(geometry[i].GetBoundingBox(false), i);

            for (int i = 0; i < boundary_sorted.Count; i++)
            {
                //overwrite last search
                ids.Clear();

                //search by check the boundary groups
                tree_all_geo.Search(boundary_sorted[i][0].Item3, BoundingBoxCallback);

                List<int> group = new List<int>(boundary_sorted.Count)
                {
                    boundary_sorted[i][0].Item1
                };

                foreach (int id in ids)
                {
                    string object_type = geometry[id].ObjectType.ToString();

                    Point3d check_point = Point3d.Unset;

                    switch (object_type)
                    {
                        case ("Annotation"):
                            TextEntity textObj = geometry[id] as TextEntity;
                            check_point = textObj.Plane.Origin;
                            break;

                        case ("TextDot"):
                            TextDot textDotObj = geometry[id] as TextDot;
                            check_point = textDotObj.Point;
                            break;

                        case ("Curve"):
                            Curve curve = geometry[id] as Curve;
                            check_point = curve.PointAt(curve.Domain.Mid);
                            break;

                        case ("Point"):
                            Point p = geometry[id] as Point;
                            check_point = new Point3d(p.Location);
                            break;

                        case ("Mesh"):
                            Mesh m = geometry[id] as Mesh;
                            check_point = m.Vertices[i];
                            break;

                        case ("Brep"):
                            Brep b = geometry[id] as Brep;
                            check_point = b.Vertices[0].Location;
                            break;

                        case ("Extrusion"):
                            Extrusion e = geometry[id] as Extrusion;
                            check_point = e.PointAt(0, 0);
                            break;

                        default:
                            RhinoApp.WriteLine("Unknown type: " + object_type);
                            break;
                    }

                    if (check_point == Point3d.Unset)
                        continue;

                    if (boundary_sorted[i][0].Item4.Contains(check_point, Plane.WorldXY, 0.01) == PointContainment.Inside)
                        group.Add(indices[id]);
                }

                this.geometry_sorted.Add(group);
            }

        }

        public void offset_boundaries(double distance, bool keep_original_curve = true, bool flip = true)
        {
            Polyline polyline = null;
            for (int i = 0; i < this.boundary_sorted.Count; i++)
            {
                for (int j = 0; j < this.boundary_sorted[i].Count; j++)
                {
                    Curve curve = this.boundary_sorted[i][j].Item4.DuplicateCurve();
                    Curve[] curveArray = curve.Offset(Plane.WorldXY, distance, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Round);
                    Curve[] curveArray1 = this.boundary_sorted[i][j].Item2.ToNurbsCurve().Offset(Plane.WorldXY, distance, RhinoDoc.ActiveDoc.ModelAbsoluteTolerance, CurveOffsetCornerStyle.Sharp);
                    if ((curveArray == null ? true : curveArray1 == null))
                    {
                        RhinoApp.WriteLine("Could not offset in nest_geo.cs");
                    }
                    else if (!curveArray1[0].TryGetPolyline(out polyline))
                    {
                        RhinoApp.WriteLine("Could not offset in nest_geo.cs");
                    }
                    else
                    {
                        this.boundary_sorted[i][j] = Tuple.Create<int, Polyline, BoundingBox, Curve>(this.boundary_sorted[i][j].Item1, polyline, polyline.BoundingBox, curveArray[0]);
                        this.geometry[this.boundary_sorted[i][j].Item1] = curveArray[0];
                        if (keep_original_curve)
                        {
                            this.geometry.Add(curve);
                            this.attributes.Add(this.attributes[this.boundary_sorted[i][j].Item1]);
                            this.geometry_sorted[i].Add(this.geometry.Count - 1);
                            this.copies.Add(this.copies[this.boundary_sorted[i][j].Item1]);
                        }
                    }
                }
            }
        }

        // Offset ONLY the NESTING polyline (boundary_sorted Item2, what the solver collides on): each OUTER loop
        // OUTWARD, each HOLE INWARD, so placed parts keep `distance` of clearance — while Item4 and geometry[]
        // stay the ORIGINAL curve, so the OUTPUT is the original geometry (the offset is nesting-only). Direction
        // is chosen by AREA (orientation-agnostic): an outer keeps the offset that GREW, a hole the one that
        // SHRANK. Collinear-merged so no points are added. On any failure the original polyline is kept (a part
        // is never dropped).
        public void offset_nesting_boundary(double distance)
        {
            if (System.Math.Abs(distance) < 1e-9) return;
            double tol = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            for (int i = 0; i < this.boundary_sorted.Count; i++)
                for (int j = 0; j < this.boundary_sorted[i].Count; j++)
                {
                    var tup = this.boundary_sorted[i][j];
                    if (tup.Item2 == null || tup.Item2.Count < 4) continue;
                    Polyline off = offset_closed_polyline(tup.Item2, distance, j > 0 /*isHole*/, tol);
                    if (off == null || off.Count < 4) continue;            // keep original on failure
                    this.boundary_sorted[i][j] = Tuple.Create<int, Polyline, BoundingBox, Curve>(
                        tup.Item1, off, off.BoundingBox, tup.Item4);
                }
        }

        // Offset a closed polyline by `distance`: outer => grow (keep the larger-area result), hole => shrink
        // (smaller). Tries both signs (orientation-agnostic), keeps closed results, merges collinear (no points
        // added). Returns null if no usable offset in the right direction was produced.
        internal static Polyline offset_closed_polyline(Polyline src, double distance, bool isHole, double tol)
        {
            double srcA = poly_area(src);
            Curve crv = src.ToPolylineCurve();
            Polyline pick = null; double pickA = isHole ? double.MaxValue : -1.0;
            foreach (double d in new double[] { distance, -distance })
            {
                Curve[] offs = crv.Offset(Plane.WorldXY, d, tol, CurveOffsetCornerStyle.Sharp);
                if (offs == null) continue;
                foreach (Curve oc in offs)
                {
                    Polyline pl;
                    if (oc == null || !oc.IsClosed || !oc.TryGetPolyline(out pl)) continue;
                    double a = poly_area(pl);
                    if (isHole) { if (a > tol && a < pickA) { pickA = a; pick = pl; } }
                    else        { if (a > pickA)            { pickA = a; pick = pl; } }
                }
            }
            if (pick == null) return null;
            if (isHole ? (pickA >= srcA) : (pickA <= srcA)) return null;   // wrong direction -> keep original
            pick.MergeColinearSegments(RhinoMath.ToRadians(1.0), true);
            pick.CollapseShortSegments(tol);
            return pick;
        }

        private static double poly_area(Polyline p)
        {
            int n = p.Count; if (n < 3) return 0.0;
            int m = (n > 1 && p[0].DistanceTo(p[n - 1]) < 1e-9) ? n - 1 : n;   // drop closing duplicate
            double a = 0.0;
            for (int k = 0; k < m; k++) { Point3d c = p[k], d = p[(k + 1) % m]; a += c.X * d.Y - d.X * c.Y; }
            return System.Math.Abs(a) * 0.5;
        }

        public void sort_groups(bool split_into_open_and_closed = true)
        {
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<Curve> curves = new List<Curve>(this.geometry_sorted.Count);
                List<int> nums = new List<int>(this.geometry_sorted.Count);
                List<int> nums1 = new List<int>(this.geometry_sorted.Count);
                for (int j = 0; j < this.geometry_sorted[i].Count; j++)
                {
                    if (this.geometry[this.geometry_sorted[i][j]].ObjectType.ToString() != "Curve")
                    {
                        nums1.Add(this.geometry_sorted[i][j]);
                    }
                    else
                    {
                        Curve item = this.geometry[this.geometry_sorted[i][j]] as Curve;
                        curves.Add(item);
                        nums.Add(this.geometry_sorted[i][j]);
                    }
                }
                List<int> curvesInCurvesFlatList = (new sort_by_closed_curves(curves, split_into_open_and_closed)).curves_in_curves_flat_list;
                this.geometry_sorted[i].Clear();
                Dictionary<int, int> nums2 = new Dictionary<int, int>(this.geometry_sorted[i].Count);
                for (int k = 0; k < curvesInCurvesFlatList.Count; k++)
                {
                    int num = curvesInCurvesFlatList[k];
                    this.geometry_sorted[i].Add(nums[num]);
                    nums2.Add(nums[k], nums[num]);
                    this.geometry[nums[num]] = curves[num];
                }
                this.geometry_sorted[i].AddRange(nums1);
                for (int l = 0; l < this.boundary_sorted[i].Count; l++)
                {
                    this.boundary_sorted[i][l] = Tuple.Create<int, Polyline, BoundingBox, Curve>(nums2[this.boundary_sorted[i][l].Item1], this.boundary_sorted[i][l].Item2, this.boundary_sorted[i][l].Item3, this.boundary_sorted[i][l].Item4);
                }
            }
        }

        /// <summary>
        /// Merges multiple nest_geo instances into a single combined nest_geo instance
        /// </summary>
        /// <param name="nestGeos">List of nest_geo instances to merge</param>
        /// <returns>A new nest_geo instance that contains all elements from the input instances</returns>
        public static nest_geo Merge(List<nest_geo> nestGeos)
        {
            if (nestGeos == null || nestGeos.Count == 0)
                return new nest_geo();
                
            if (nestGeos.Count == 1)
                return nestGeos[0];
                
            // Create a new combined nest_geo
            nest_geo combined = new nest_geo();
            
            // Track the offset needed for indices when merging
            int geometryIndexOffset = 0;
            
            // Process each nest_geo instance
            foreach (nest_geo source in nestGeos)
            {
                if (source == null) 
                    continue;
                
                // Add basic elements that can be directly merged
                combined.geometry.AddRange(source.geometry);
                combined.geometry_attributes.AddRange(source.geometry_attributes);
                combined.attributes.AddRange(source.attributes);
                combined.disply_texts.AddRange(source.disply_texts);
                combined.boundary_curves_non_sorted.AddRange(source.boundary_curves_non_sorted);
                combined.copies.AddRange(source.copies);
                
                // Handle indices with offset
                foreach (int index in source.indices)
                {
                    combined.indices.Add(index + geometryIndexOffset);
                }
                
                // Handle boundary indices with offset
                foreach (int index in source.boudary_indices_non_sorted)
                {
                    combined.boudary_indices_non_sorted.Add(index + geometryIndexOffset);
                }
                
                // Handle geometry_sorted with offset
                foreach (List<int> indexGroup in source.geometry_sorted)
                {
                    List<int> adjustedGroup = new List<int>();
                    foreach (int index in indexGroup)
                    {
                        adjustedGroup.Add(index + geometryIndexOffset);
                    }
                    combined.geometry_sorted.Add(adjustedGroup);
                }
                
                // Handle boundary_sorted with offset
                foreach (var boundaryGroup in source.boundary_sorted)
                {
                    var adjustedBoundaryGroup = new List<Tuple<int, Polyline, BoundingBox, Curve>>();
                    foreach (var tuple in boundaryGroup)
                    {
                        adjustedBoundaryGroup.Add(new Tuple<int, Polyline, BoundingBox, Curve>(
                            tuple.Item1 + geometryIndexOffset, 
                            tuple.Item2, 
                            tuple.Item3, 
                            tuple.Item4));
                    }
                    combined.boundary_sorted.Add(adjustedBoundaryGroup);
                }
                
                // Handle transforms
                if (source.xforms != null)
                {
                    combined.xforms.AddRange(source.xforms);
                }
                
                // Update offset for the next nest_geo
                geometryIndexOffset += source.geometry.Count;
            }
            
            return combined;
        }
        
        /// <summary>
        /// Merges this nest_geo instance with another one
        /// </summary>
        /// <param name="other">Other nest_geo instance to merge with</param>
        /// <returns>A new nest_geo instance that contains elements from both instances</returns>
        public nest_geo MergeWith(nest_geo other)
        {
            return Merge(new List<nest_geo> { this, other });
        }
    }
}