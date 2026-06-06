using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace opennest_2
{
    public class component_geo_merge : GH_Component, IGH_VariableParameterComponent
    {

        public override GH_Exposure Exposure
        {
        get { return GH_Exposure.tertiary; }
        }


        public component_geo_merge()
          : base("Merge", "Merge",
            "Combines two OpenNest Geometry streams into one.",
             "Params", "OpenNest2")
        {
        }



        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddGenericParameter("Geometry", "G", "OpenNest Geometry.", GH_ParamAccess.list);
            pManager.AddGenericParameter("Geometry", "G", "OpenNest Geometry.", GH_ParamAccess.list);

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Geometry", "G", "Merged Geometry.", GH_ParamAccess.item);
        }


        protected override void SolveInstance(IGH_DataAccess DA)
        {

            

            List<nest_rhino_lib.nest_geo> nest_geo_list = new List<nest_rhino_lib.nest_geo>();

            for (int i = 0; i< this.Params.Input.Count; i++)
            {

                List<nest_rhino_lib.nest_geo> nest_geo_list_temp = new List<nest_rhino_lib.nest_geo>();
                DA.GetDataList(i, nest_geo_list_temp);
                foreach (var geo in nest_geo_list_temp)
                {
                    if (geo != null)
                        nest_geo_list.Add(geo);
                }

            }

            DA.SetData(0, nest_rhino_lib.nest_geo.Merge(nest_geo_list));
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.merge;

        public override Guid ComponentGuid => new Guid("8EB83827-EB1D-457E-A45E-A29A241D314D");

        //////////////////////////////////////////////////////////////////////////////////////////ZoomableComponent

        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index)
        {
            //We only let input parameters to be added (output number is fixed at one)
            if (side == GH_ParameterSide.Input)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index)
        {
            //We can only remove from the input
            if (side == GH_ParameterSide.Input && Params.Input.Count > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index)
        {
            IGH_Param param = new Grasshopper.Kernel.Parameters.Param_GenericObject();
            param.Access = GH_ParamAccess.list;
            param.Optional = true;
            param.Name = "G";
            param.NickName = "G";
            param.Description = "Geometry" + (Params.Input.Count + 1);

            return param;
        }

        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index)
        {
            //Nothing to do here by the moment
            return true;
        }

        void IGH_VariableParameterComponent.VariableParameterMaintenance()
        {
            //Nothing to do here by the moment
        }
    }
}