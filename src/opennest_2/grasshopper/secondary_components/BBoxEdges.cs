using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using Grasshopper.Kernel;
using Rhino.Geometry;

namespace opennest_2
{
    public class BBoxEdges : GH_Component {
        /// <summary>
        /// Initializes a new instance of the ComponentText class.
        /// </summary>
        public BBoxEdges()
          : base("Bounding Box Edges", "BBoxEdges",
              "Returns the two base edges and their lengths for an object's oriented bounding box.",
               "Params", "OpenNest2") {
        }

        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddGeometryParameter("G", "Geo", "Geometry to bound", GH_ParamAccess.item);
            pManager.AddPlaneParameter("P", "Plane", "Orientation plane for the box", GH_ParamAccess.item);
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddLineParameter("L0", "Line0", "First bounding box base edge", GH_ParamAccess.item);
            pManager.AddLineParameter("L1", "Line1", "Second bounding box base edge", GH_ParamAccess.item);
            pManager.AddNumberParameter("D0", "Dist0", "Length of the first base edge", GH_ParamAccess.item);
            pManager.AddNumberParameter("D1", "Dist1", "Length of the second base edge", GH_ParamAccess.item);
            pManager.AddBoxParameter("B", "Box", "Oriented bounding box", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA) {

            GeometryBase G = null;
                DA.GetData(0, ref G);

            Plane plane = Plane.Unset;
            DA.GetData(1,ref plane);

            BoundingBox bbox = G.GetBoundingBox(false);

            if(plane != Plane.Unset)
                bbox = G.GetBoundingBox(plane);

            Point3d p0 = bbox.PointAt(0, 0, 0);
            Point3d p1 = bbox.PointAt(0, 1, 0);
            Point3d p2 = bbox.PointAt(1, 0, 0);

            Line l0 = new Line(p0, p1);
            Line l1 = new Line(p0, p2);
            //L0 = l0;
            //L1 = l1;
            //D0 = l0.Length;
            //D1 = l1.Length;

            DA.SetData(0, l0);
            DA.SetData(1, l1);
            DA.SetData(2, l0.Length);
            DA.SetData(3, l1.Length);
            DA.SetData(4, new Box(bbox));

        }




    protected override System.Drawing.Bitmap Icon {
        get {
            return Properties.Resources.bbox_edges;
        }
    }



        /// <summary>
        /// Gets the unique ID for this component. Do not change this ID after release.
        /// </summary>
        public override Guid ComponentGuid => new Guid("e47a4beb-a453-4915-90a7-a18adc13f91c"); 
    }
}