using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Data;


namespace opennest_2
{
    public class component_sheets : GH_Component, IGH_VariableParameterComponent
    {
        public component_sheets()
          : base("Sheets", "Sheets",
            "Defines the sheets to nest onto (with optional holes) from closed polylines; supports gap, rows and copies.",
             "Params", "OpenNest2")
        {
        }

        public override GH_Exposure Exposure
        {
        get { return GH_Exposure.quarternary; }
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Polylines", "Polylines", "Closed sheet polylines, with optional holes.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Gap", "Gap", "Gap between sheets.", GH_ParamAccess.list, new List<double>(){0.1});
            pManager.AddNumberParameter("Rows", "Rows", "Number of sheets per row before partitioning.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Copies", "Copies", "Number of copies of the same sheet.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Offset", "Offset", "Inward MARGIN for nesting (model units; 0 = OFF, fast). Parts keep this setback from the sheet edge; any sheet holes grow by it.", GH_ParamAccess.item, 0);
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Sheets", "Sheets", "OpenNest nest_sheets data type.", GH_ParamAccess.item);
            pManager.AddCurveParameter("Polylines", "Polylines", "Generated sheet outline polylines.", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {

            var gap_xy = new List<double>();
            DA.GetDataList(1, gap_xy);

            var row_count_double = new List<double> ();
            DA.GetDataList(2, row_count_double);
            var row_count = new List<int>();
            foreach (var item in row_count_double)
            {
                row_count.Add((int)item);
            }
            
            double copies = 100;
            DA.GetData(3, ref copies);
            double sheet_margin = 0;
            DA.GetData(4, ref sheet_margin);

            var plines_list = new List<List<Polyline>>();
      
            GH_Structure<GH_Curve> curves = new GH_Structure<GH_Curve>();
            DA.GetDataTree(0, out curves);

            for (int j = 0; j < curves.Branches.Count; j++)
            {
                var branch = curves.Branches[j];
                if (branch.Count == 0)
                    continue;

                var plines = new List<Polyline>();
                for (int k = 0; k < branch.Count; k++)
                {
                    var curve = branch[k].Value;
                    if (curve != null)
                    {
                        if (curve.TryGetPolyline(out Polyline pline) || !curve.IsClosed )
                        {
                            plines.Add(pline);
                        }
                        else
                        {

                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input curves must be closed polylines!");
                            return;
                        }

                    }
                }
                plines_list.Add(plines);
            }
         

            // Offset each sheet's outline ONCE (margin: outer shrinks, holes grow) BEFORE the copies are made,
            // so the copies just follow the offset base (fast — no per-copy offsetting).
            if (sheet_margin != 0)
            {
                double mtol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
                foreach (var sheet in plines_list)
                {
                    int outerIdx = 0; double maxd = -1;
                    for (int k = 0; k < sheet.Count; k++) { double dd = sheet[k].BoundingBox.Diagonal.Length; if (dd > maxd) { maxd = dd; outerIdx = k; } }
                    for (int k = 0; k < sheet.Count; k++)
                    {
                        var off = nest_rhino_lib.nest_geo.offset_closed_polyline(sheet[k], sheet_margin, k == outerIdx, mtol);
                        if (off != null && off.Count >= 4) sheet[k] = off;
                    }
                }
            }

            // Create nest_sheet class
            nest_rhino_lib.nest_sheets nest_sheets = new nest_rhino_lib.nest_sheets(plines_list, gap_xy, row_count, (int)copies);

            // Convert array of arrays to a GH_Structure
            Grasshopper.Kernel.Data.GH_Structure<GH_Curve> output_curves = new Grasshopper.Kernel.Data.GH_Structure<GH_Curve>();
            for (int i = 0; i < nest_sheets.sheets.Length; i++)
            {
                for (int j = 0; j < nest_sheets.sheets[i].Length; j++)
                {
                    output_curves.Append(new GH_Curve(nest_sheets.sheets[i][j].ToNurbsCurve()), new Grasshopper.Kernel.Data.GH_Path(i));
                }

            }

            // Output
            DA.SetData(0, nest_sheets);
            DA.SetDataTree(1, output_curves);





        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.sheet;

        public override Guid ComponentGuid => new Guid("11E19CE6-E1A3-47B1-9A91-ECC987D1DFCA");

        /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
        ///Predefined Inputs and Groups
        /////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////
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
            //Param_Plane param = new Param_Plane();
            //Grasshopper.Kernel.Parameters.Param_String param = new Grasshopper.Kernel.Parameters.Param_String();
            IGH_Param param = new Grasshopper.Kernel.Parameters.Param_Curve();
            param.Access = GH_ParamAccess.tree;
            param.Optional = true;
            param.Name = "polylines";
            param.NickName = param.Name;
            param.Description = "polylines" + (Params.Input.Count + 1);

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