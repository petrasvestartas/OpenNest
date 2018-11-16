using System;
using System.Collections.Generic;
using DeepNestLib;
using Grasshopper.Kernel;
using Rhino.Geometry;



namespace OpenNest {
    public class OpenNestComponent : GH_Component {

        /// </summary>
        public OpenNestComponent()
          : base("OpenNest", "OpenNest",
              "OpenNest",
              "Params", "Nest") {
        }

     
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddRectangleParameter("Sheets", "Sheets", "Sheets", GH_ParamAccess.list);
            pManager.AddCurveParameter("Polylines", "Polylines", "Polylines", GH_ParamAccess.list);
            pManager.AddNumberParameter("Spacing", "Spacing", "Spacing", GH_ParamAccess.item,1);
            pManager.AddIntegerParameter("Iterations", "Iterations", "Iterations",GH_ParamAccess.item,1);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheets", GH_ParamAccess.list);
            pManager.AddCurveParameter("Polylines", "Polylines", "Polylines", GH_ParamAccess.list);
            pManager.AddIntegerParameter("ID", "ID", "ID", GH_ParamAccess.list);
            pManager.AddTransformParameter("Transform", "Transform", "Transform", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA) {

            //Input
            List<Rectangle3d> sheets = new List<Rectangle3d>();
            List<Polyline> polygonsTranslatedToOrigin = new List<Polyline>();
            List<Polyline> polygons = new List<Polyline>();
            List<Curve> curves = new List<Curve>();
            double spacing = 1;
            int iterations = 1;
            

            DA.GetDataList(0, sheets);
            DA.GetDataList(1, curves);
            DA.GetData(2, ref spacing);
            DA.GetData(3, ref iterations);

            Transform[] orient = new Transform[curves.Count];
            Transform[] translate = new Transform[curves.Count];
            int i = 0;
            foreach (Curve c in curves) {
                c.TryGetPolyline(out Polyline poly);
                Tuple<Polyline, Transform> projectedPolyline = FitPolylineToPlane(poly);
                poly = projectedPolyline.Item1;
                poly.Transform(projectedPolyline.Item2);
                orient[i] = projectedPolyline.Item2;
                polygons.Add(new Polyline(poly));

                Transform translation = Transform.Translation((Vector3d)(-poly[0]));
                Transform translationInv = Transform.Translation((Vector3d)(poly[0]));
                translate[i++] = translation;
                poly.Transform(translation);

                polygonsTranslatedToOrigin.Add(poly);

                //Rhino.RhinoDoc.ActiveDoc.Objects.AddPolyline(poly);
            }

            //Solution
            OpenNestSolver sample = new OpenNestSolver();
            sample.LoadSampleData(sheets, polygonsTranslatedToOrigin);
            var output = sample.Run(spacing,iterations);




            //Output
            DA.SetDataList(0, output.Item1);
            //polygonsTranslatedToOrigin.TransformPolylines(output.Item3)

            Transform[] transforms = output.Item3.MergeTransfoms(translate);
       

            DA.SetDataList(1, polygons.TransformPolylines(transforms));//.MergeTransfoms(translate)
            //DA.SetDataList(1, polygonsTranslatedToOrigin.TransformPolylines(output.Item3));
            DA.SetDataList(2, output.Item2);
            DA.SetDataList(3, transforms.MergeTransfoms(orient) );

        }

        public Tuple<Polyline,Transform> FitPolylineToPlane(Polyline polyline) {



            if (polyline.IsValid) {
                if (polyline.Count > 2 && polyline.IsClosed) {
                    Plane plane = Plane.Unset;
                   
                    Plane.FitPlaneToPoints(polyline, out plane);
                    Polyline projectedPolyline = new Polyline();




                    for (int i = 0; i < polyline.Count - 1; i++) {
                        projectedPolyline.Add(plane.ClosestPoint(polyline[i]));
                    }

                    // Vector3d axis0 = polyline.SegmentAt(0).Direction;
                    // axis0.Unitize();
                    // Vector3d axis1 = polyline.SegmentAt(0).Direction;
                    // axis1.Unitize();

                    //for (int i = 1; i < polyline.SegmentCount; i++) {
                    //   Vector3d vec = polyline.SegmentAt(i).Direction;
                    //     vec.Unitize();

                    //     if (vec.IsParallelTo(axis0) == 0) {
                    //         axis1 = vec;

                    //        break;
                    //     }
                    //}


                    // plane = new Plane(plane.Origin, Vector3d.CrossProduct(axis0, axis1));

                    Mesh mesh = Mesh.CreateFromClosedPolyline(polyline);




                    projectedPolyline.Add(projectedPolyline[0]);
                    plane = new Plane( projectedPolyline.CenterPoint(), mesh.FaceNormals[0]) ;
                    Transform transform = Transform.PlaneToPlane(plane, Plane.WorldXY);

                    Polyline polylineCopy = new Polyline(projectedPolyline);
                    polyline.Transform(transform);

                    if (IsClockwisePolygon(projectedPolyline)) {
                        plane.Flip();
                        transform = Transform.PlaneToPlane(plane, Plane.WorldXY);
                    }

           


                    //transform = Transform.PlaneToPlane(plane, plane);
                    //Rhino.RhinoDoc.ActiveDoc.Objects.AddPolyline(projectedPolyline);

                    //projectedPolyline.Transform(transform);
                    return new Tuple<Polyline, Transform>(projectedPolyline, transform);
                }
            }
            return new Tuple<Polyline, Transform>(polyline, new Transform());




        }

        public  bool IsClockwisePolygon(Polyline polygon) {
            double sum = 0;

            for (int i = 0; i < polygon.Count - 1; i++)
                sum += (polygon[i + 1].X - polygon[i].X) * (polygon[i + 1].Y + polygon[i].Y);

            return sum > 0;
        }

        protected override System.Drawing.Bitmap Icon {
            get {

                return Properties.Resources.NestIcon;
            }
        }


        public override Guid ComponentGuid {
            get { return new Guid("ff729e8d-6e1b-4b19-9dd6-d1c136996c86"); }
        }
    }
}
