using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Types.Transforms;
using Rhino.Geometry;

namespace opennest_2
{
    public class Project : GH_Component {

        public Project()
          : base("Project to Plane", "Project",
              "Projects a polyline onto its best-fit plane and returns that plane.",
              "Params", "OpenNest2") {
        }
        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddCurveParameter("Polyline", "P", "Polyline to project", GH_ParamAccess.item);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddCurveParameter("Polyline", "P", "Projected polyline", GH_ParamAccess.item);
            pManager.AddPlaneParameter("Plane", "Pl", "Best-fit plane", GH_ParamAccess.item);
        }

        public Polyline ToPolyline(Curve curve, bool collapseShortSegments = true)
        {
            curve.TryGetPolyline(out Polyline polyline);
            if (collapseShortSegments)
                polyline.CollapseShortSegments(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
            return polyline;
        }

        public Vector3d AverageNormal(Polyline p)
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

        public Tuple<Polyline, Plane> FitPolylineToPlane2(Polyline polyline)
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

        protected override void SolveInstance(IGH_DataAccess DA) {

            Curve curve = null;
            DA.GetData(0, ref curve);

            Polyline polyline = ToPolyline(curve, true);
            var fitPoly = FitPolylineToPlane2(polyline);
 
            DA.SetData(0, fitPoly.Item1);
            DA.SetData(1, fitPoly.Item2);

        }




        protected override System.Drawing.Bitmap Icon {
            get {
                return Properties.Resources.project;
            }
        }


        public override Guid ComponentGuid {
            get { return new Guid("{3278ccf2-1258-1478-ae14-1b125e166bbd}"); }
        }


    }
}
