using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Rhino.Geometry;

namespace nest_rhino_lib.sort_2d
{


    public class sort_by_closed_curves
    {
        //input
        public List<Curve> crvs = new List<Curve>();

        //temp
        private List<int> open_crvs_ids = new List<int>();
        private List<int> closed_crvs_ids = new List<int>();
        bool sort_open_and_closed_curves_separately = true;


        //result
        public List<List<int>> curves_in_curves = new List<List<int>>();//first curve id represents enclosing curve
        public List<int> curves_in_curves_flat_list = new List<int>();




        public sort_by_closed_curves(List<Curve> crvs, bool sort_open_and_closed_curves_separately = true)
        {
            this.crvs = crvs;
            this.sort_open_and_closed_curves_separately = sort_open_and_closed_curves_separately;

            BoundingBox bbox = crvs[0].GetBoundingBox(true);
            for (int i = 1; i < crvs.Count; i++)
                bbox.Union(crvs[i].GetBoundingBox(true));
            bbox.Inflate(0.1);
            Curve curve = (new Polyline(){

        bbox.Corner(true,true,true),
        bbox.Corner(false,true,true),
        bbox.Corner(false,false,true),
        bbox.Corner(true,false,true),
        bbox.Corner(true,true,true),
        }).ToNurbsCurve();
            this.crvs.Add(curve);



            split_into_open_and_closed();
            sort_closed_crvs_by_size();
            open_middle_point_inclusion_in_closed_crvs();
            open_proximity_search_and_alternating_flipping();
            collect_empty_closed_curves_and_assign_to_non_empty_lists();
            flat_list();
            //Rhino.RhinoApp.WriteLine("___________");
            //foreach(var id in curves_in_curves_flat_list)
            //    Rhino.RhinoApp.WriteLine(id.ToString());

        }

        private void split_into_open_and_closed()
        {

            if (!sort_open_and_closed_curves_separately)
            {
                open_crvs_ids.AddRange(Enumerable.Range(0, crvs.Count - 1).ToList());
                closed_crvs_ids.Add(crvs.Count - 1);
            }
            else
            {
                for (int i = 0; i < crvs.Count; i++)
                    if (crvs[i].IsClosed)
                        closed_crvs_ids.Add(i);
                    else
                        open_crvs_ids.Add(i);
            }

        }

        private void sort_closed_crvs_by_size()
        {
            double[] len = new double[closed_crvs_ids.Count];
            int[] closed_crvs_ids_array = closed_crvs_ids.ToArray();

            for (int i = 0; i < closed_crvs_ids.Count; i++)
            {
                len[i] = crvs[closed_crvs_ids[i]].GetBoundingBox(true).Diagonal.SquareLength;
            }

            Array.Sort(len, closed_crvs_ids_array);
            //closed_crvs_ids_array=closed_crvs_ids_array.Reverse().ToArray();
            closed_crvs_ids = closed_crvs_ids_array.ToList();
        }

        private void open_middle_point_inclusion_in_closed_crvs()
        {

            HashSet<int> visited = new HashSet<int>();
            for (int i = 0; i < closed_crvs_ids.Count; i++)
            {
                int cc = closed_crvs_ids[i];
                BoundingBox bbox = crvs[cc].GetBoundingBox(true);
                bbox.Inflate(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 100);


                List<int> cc_and_oc = new List<int>() { cc };

                if (visited.Count != open_crvs_ids.Count)
                {

                    for (int j = 0; j < open_crvs_ids.Count; j++)
                    {
                        int oc = open_crvs_ids[j];
                        if (visited.Contains(oc)) continue;

                        if (!bbox.Contains(crvs[oc].PointAt(crvs[oc].Domain.Mid))) continue;


                        PointContainment result = crvs[cc].Contains(crvs[oc].PointAt(crvs[oc].Domain.Mid), Plane.WorldXY, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 1000);
                        if (result == PointContainment.Inside || result == PointContainment.Coincident)
                        {
                            visited.Add(oc);
                            cc_and_oc.Add(oc);
                        }
                    }
                    //Rhino.RhinoApp.WriteLine(cc_and_oc.Count.ToString());
                }
                //Rhino.RhinoApp.WriteLine(cc_and_oc.Count.ToString());
                curves_in_curves.Add(cc_and_oc);
            }
        }

        private void open_proximity_search_and_alternating_flipping()
        {


            for (int i = 0; i < curves_in_curves.Count; i++)
            {
                int n = curves_in_curves[i].Count - 1;
                if (n < 2)
                {
                    //curves_in_curves[i] = { 1};
                    //Rhino.RhinoApp.WriteLine("Simple" + curves_in_curves[i].Count.ToString());
                    continue;
                }

                //sort curves by proximity to the closed curve
                double[] cpt = new double[n];
                //Tuple<int, double>[] oc_id_oc_t = new Tuple<int, double>[n];

                //change curve starting point at the bottom left
                //Point3d bottom_left = crvs[curves_in_curves[i][0]].GetBoundingBox(false).Min;
                //Curve guide_curve = crvs[curves_in_curves[i][0]].DuplicateCurve();
                //guide_curve.ClosestPoint(bottom_left, out double t_bottom_left);
                //guide_curve.ChangeClosedCurveSeam(t_bottom_left);



                //sort by closest point to curve
                //for (int j = 0; j < n; j++)
                //{
                //    int oc = curves_in_curves[i][j + 1];

                //    guide_curve.ClosestPoints(crvs[oc], out Point3d point_on_this_curve, out Point3d point_on_other_curve);
                //    guide_curve.ClosestPoint(point_on_this_curve, out double t0);
                //    crvs[oc].ClosestPoint(point_on_other_curve, out double t1);
                //    cpt[j] = t0;
                //    oc_id_oc_t[j] = Tuple.Create(oc, t1);
                //}

                //Array.Sort(cpt, oc_id_oc_t);

                //sort by coordinates
                Tuple<int, Point3d>[] oc_id_oc_t = new Tuple<int, Point3d>[n];
                for (int j = 0; j < n; j++)
                {
                    int oc = curves_in_curves[i][j + 1];
                    oc_id_oc_t[j] = Tuple.Create(oc, crvs[oc].PointAt(crvs[oc].Domain.Mid));

                }
                oc_id_oc_t = oc_id_oc_t.OrderBy(p => p.Item2.X).ThenBy(p => p.Item2.Y).ToArray();

                //oc_id_oc_t = oc_id_oc_t.OrderBy(p => p.Item2.DistanceToSquared(crvs.Last().PointAtStart)).ToArray();
                //Rhino.RhinoDoc.ActiveDoc.Objects.AddPoint(oc_id_oc_t[0].Item2);

                for (int j = 0; j < n; j++)
                {
                    //assign sorted order id
                    curves_in_curves[i][j + 1] = oc_id_oc_t[j].Item1;
                }



                //flip by closest point pattern
                Point3d last_point = crvs[curves_in_curves[i][0]].PointAtEnd;


                for (int j = 0; j < n; j++)
                {
                    if (!crvs[curves_in_curves[i][j + 1]].IsClosed)
                    {
                        Point3d p0 = crvs[curves_in_curves[i][j + 1]].PointAtStart;
                        Point3d p1 = crvs[curves_in_curves[i][j + 1]].PointAtEnd;
                        if (last_point.DistanceToSquared(p0) > last_point.DistanceToSquared(p1))
                            crvs[curves_in_curves[i][j + 1]].Reverse();
                        last_point = crvs[curves_in_curves[i][j + 1]].PointAtEnd;
                    }
                }


                //Sort by division points
                int divisions = this.sort_open_and_closed_curves_separately ? 1 : 2;
                Rhino.Collections.Point3dList[] clouds = new Rhino.Collections.Point3dList[divisions + 1];
                List<int> ids = curves_in_curves[i].GetRange(1, n).ToList();

                for (int j = 0; j < clouds.Length; j++)
                    clouds[j] = new Rhino.Collections.Point3dList();


                for (int j = 0; j < n; j++)
                {
                    crvs[curves_in_curves[i][j + 1]].DivideByCount(divisions, false, out Point3d[] p);

                    clouds[0].Add(crvs[curves_in_curves[i][j + 1]].PointAtStart);
                    for (int k = 0; k < p.Length; k++)
                        clouds[k + 1].Add(p[k]);
                    clouds.Last().Add(crvs[curves_in_curves[i][j + 1]].PointAtEnd);
                }

                //Rhino.RhinoApp.WriteLine(curves_in_curves[i][0].ToString());
                //Rhino.RhinoApp.WriteLine(curves_in_curves[i][1].ToString());
                //Rhino.RhinoApp.WriteLine(curves_in_curves[i][2].ToString());

                //first point
                List<int> sorted_ids = new List<int>() { ids[0] };

                Point3d search_point = clouds.Last()[0];
                for (int j = 0; j < clouds.Length; j++)
                    clouds[j].RemoveAt(0);
                ids.RemoveAt(0);

                //the rest of points
                for (int j = 0; j < 1000; j++)
                {
                    if (clouds[0].Count == 0) break;

                    double[] dists = new double[clouds.Length];
                    Tuple<int, int>[] cloudid_pointid = new Tuple<int, int>[clouds.Length];
                    //int[] p_ids = Enumerable.Range(0,clouds[0].Count).ToArray();

                    //find closest distance to division points
                    for (int k = 0; k < clouds.Length; k++)
                    {
                        int closest_index = clouds[k].ClosestIndex(search_point);
                        dists[k] = clouds[k][closest_index].DistanceToSquared(search_point);
                        cloudid_pointid[k] = Tuple.Create(k, closest_index);
                    }

                    Array.Sort(dists, cloudid_pointid);

                    //addition and preparation for the next step
                    search_point = clouds[cloudid_pointid[0].Item1][cloudid_pointid[0].Item2];
                    sorted_ids.Add(ids[cloudid_pointid[0].Item2]);
                    for (int k = 0; k < clouds.Length; k++)
                        clouds[k].RemoveAt(cloudid_pointid[0].Item2);
                    ids.RemoveAt(cloudid_pointid[0].Item2);

                }

                curves_in_curves[i] = new List<int> { curves_in_curves[i][0] };
                curves_in_curves[i].AddRange(sorted_ids);

                //flip
                for (int j = 2; j < n; j++)
                {
                    if (crvs[curves_in_curves[i][j - 1]].PointAtEnd.DistanceToSquared(crvs[curves_in_curves[i][j]].PointAtStart) >
                        crvs[curves_in_curves[i][j - 1]].PointAtEnd.DistanceToSquared(crvs[curves_in_curves[i][j]].PointAtEnd)
                        )
                        crvs[curves_in_curves[i][j]].Reverse();
                }



            }



        }

        private void collect_empty_closed_curves_and_assign_to_non_empty_lists()
        {

            //separate empty curves and non-empty lists
            List<int> cc = new List<int>();
            List<List<int>> curves_in_curves_cleaned = new List<List<int>>();
            List<List<int>> curves_in_curves_empty = new List<List<int>>();

            for (int i = 0; i < curves_in_curves.Count; i++)
                if (curves_in_curves[i].Count == 1)
                    cc.Add(curves_in_curves[i][0]);
                else
                {
                    curves_in_curves_cleaned.Add(curves_in_curves[i]);
                    curves_in_curves_empty.Add(new List<int>() { });
                }

            if (curves_in_curves_cleaned.Count == 0 && curves_in_curves_empty.Count == 0)
            {
                curves_in_curves.Clear();
                List<int> ids = new List<int>();
                for (int i = 0; i < cc.Count - 1; i++)
                    ids.Add(cc[i]);
                curves_in_curves.Add(ids);
                // curves_in_curves.AddRange(cc.GetRange(0,cc.Count-1).ToList());
                return;
                //Rhino.RhinoApp.WriteLine(curves_in_curves_cleaned.Count.ToString() + " " + curves_in_curves_empty.Count.ToString() + " " + curves_in_curves[2].Count.ToString());
            }

            //Assign closed curves to holes
            HashSet<int> visited = new HashSet<int>();
            for (int i = 0; i < curves_in_curves_cleaned.Count; i++)
            {
                if (curves_in_curves_cleaned.Count < 1) continue;
                int hole = curves_in_curves_cleaned[i][0];
                BoundingBox bbox = crvs[hole].GetBoundingBox(true);
                bbox.Inflate(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 100);
                if (visited.Count == cc.Count) break;

                //check if curves are inside holes
                for (int j = 0; j < cc.Count; j++)
                {
                    if (visited.Contains(cc[j])) continue;
                    if (!bbox.Contains(crvs[cc[j]].PointAt(crvs[cc[j]].Domain.Mid))) continue;

                    PointContainment result = crvs[hole].Contains(crvs[cc[j]].PointAt(crvs[cc[j]].Domain.Mid), Plane.WorldXY, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

                    if (result == PointContainment.Inside || result == PointContainment.Coincident)
                    {
                        visited.Add(cc[j]);
                        curves_in_curves_empty[i].Add(cc[j]);
                    }
                }

                if (curves_in_curves_empty[i].Count == 0) continue;

                ////////////////////////////////////////////////////////////////////////////////
                //Sort closed curves
                ////////////////////////////////////////////////////////////////////////////////
                int[] ids = curves_in_curves_empty[i].ToArray();
                double[] t = new double[ids.Length];
                for (int j = 0; j < curves_in_curves_empty[i].Count; j++)
                {
                    int oc = curves_in_curves_empty[i][j];
                    crvs[hole].ClosestPoints(crvs[oc], out Point3d point_on_this_curve, out Point3d point_on_other_curve);
                    crvs[hole].ClosestPoint(point_on_this_curve, out t[j]);

                }
                Array.Sort(t, ids);
                curves_in_curves_empty[i] = ids.ToList();

                ////////////////////////////////////////////////////////////////////////////////
                //sort by searching end points
                ////////////////////////////////////////////////////////////////////////////////
                Rhino.Collections.Point3dList cloud = new Rhino.Collections.Point3dList();

                List<int> ids_list = ids.ToList();

                for (int j = 0; j < ids_list.Count; j++)
                    cloud.Add(crvs[ids_list[j]].PointAtStart);


                int start_id = cloud.ClosestIndex(crvs.Last().PointAtStart);
                List<int> sorted_ids = new List<int>() { ids_list[start_id] };

                Point3d search_point = cloud[start_id];
                cloud.RemoveAt(start_id);
                ids_list.RemoveAt(start_id);

                //the rest of points
                for (int j = 0; j < 1000; j++)
                {
                    if (cloud.Count == 0) break;

                    start_id = cloud.ClosestIndex(search_point);
                    sorted_ids.Add(ids_list[start_id]);

                    search_point = cloud[start_id];
                    cloud.RemoveAt(start_id);
                    ids_list.RemoveAt(start_id);

                }
                curves_in_curves_empty[i] = sorted_ids;

            }


            //combine and replace
            for (int i = 0; i < curves_in_curves_cleaned.Count; i++)
            {
                curves_in_curves_cleaned[i].AddRange(curves_in_curves_empty[i]);

                //remove boundingbox rectangle
                if (curves_in_curves_cleaned[i][0] == this.crvs.Count - 1)
                    curves_in_curves_cleaned[i].RemoveAt(0);
            }
            curves_in_curves = curves_in_curves_cleaned;
        }

        private void flat_list()
        {
            foreach (var list in curves_in_curves)
                curves_in_curves_flat_list.AddRange(list);

            //remove boundingbox rectangle
            this.crvs.RemoveAt(this.crvs.Count - 1);
        }
    }
}
