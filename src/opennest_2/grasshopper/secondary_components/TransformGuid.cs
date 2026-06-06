using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Linq;

namespace opennest_2
{
    public class TransformGuid : GH_Component {
        public override Guid ComponentGuid {
            get { return new Guid("3718d986-f4f1-40ee-4c0f-84ad2b4e1114"); }
        }
        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override System.Drawing.Bitmap Icon {
            get {

                return Properties.Resources.transform_element_rhino;
            }
        }

        public TransformGuid() : base("Transform Object", "TGuid", "Transforms a referenced Rhino object in place, keeping all of its properties.", "Params", "OpenNest2") {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            IGH_Param geometryAsGuid = new Grasshopper.Kernel.Parameters.Param_Guid();
            pManager.AddParameter(geometryAsGuid, "Guid", "Guid", "Referenced geometry as guid", GH_ParamAccess.item);
            pManager.AddTransformParameter("Transform", "Transform", "Transformation to apply to the object", GH_ParamAccess.item);
            pManager.AddBooleanParameter("Run", "Run", "Set to true to apply the transform", GH_ParamAccess.item);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {

        }

        protected override void SolveInstance(IGH_DataAccess DA) {


            Guid guid = Guid.Empty;
            DA.GetData(0, ref guid);
            Transform transform = Transform.Unset;
            DA.GetData(1, ref transform);
            bool run = false;
            DA.GetData(2, ref run);

            if (run) {
                Rhino.RhinoDoc.ActiveDoc.Objects.Transform(guid, transform, false);//bake objects
            }
        }

    
    }
}