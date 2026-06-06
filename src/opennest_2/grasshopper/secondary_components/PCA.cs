using Grasshopper.Kernel;
using Minkowski;
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

            double[] xyz = new double[points.Count * 3];
            for (int i = 0; i < points.Count; i++)
            {
                xyz[i * 3 + 0] = points[i].X;
                xyz[i * 3 + 1] = points[i].Y;
                xyz[i * 3 + 2] = points[i].Z;
            }



            double[] results = new double[8*3];
            MinkowskiWrapper.pinvoke_compute_oriented_box(points.Count, xyz, results);

            var points_oob = new List<Point3d>();
            for (int i = 0; i < results.Length; i += 3)
            {
                points_oob.Add(new Point3d(results[i], results[i + 1], results[i + 2]));
            }

            
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