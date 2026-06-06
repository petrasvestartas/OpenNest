using System;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using GH_IO.Serialization;
using System.Drawing;

namespace opennest_2
{
    public class geo_rhino_component : GH_Component, IGH_VariableParameterComponent
    {
        public override GH_Exposure Exposure
        {
        get { return GH_Exposure.tertiary; }
        }
        private BoundingBox bbox = new BoundingBox();
        private List<TextEntity> text = new List<TextEntity>();

        private List<Curve> geometry = new List<Curve>();

        private List<System.Drawing.Color> geometry_colors = new List<System.Drawing.Color>();
        private List<Polyline> borders_display = new List<Polyline>();
        public override BoundingBox ClippingBox => bbox;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            foreach (var crv in borders_display)
                args.Display.DrawPolyline(crv, Color.Black, 4);

            for (int i = 0; i < geometry.Count; i++)
            {
                args.Display.DrawCurve(geometry[i], geometry_colors[i]);
                args.Display.DrawPoint(geometry[i].PointAtStart, Rhino.Display.PointStyle.RoundSimple, 2, System.Drawing.Color.Black);
            }

            foreach (var t in text)
                args.Display.Draw3dText(t.PlainText, System.Drawing.Color.Black, t.Plane, t.TextHeight, "centre", true, true, Rhino.DocObjects.TextHorizontalAlignment.Center, Rhino.DocObjects.TextVerticalAlignment.Middle);
        }

        public int non_changing_inputs = 5;

        public geo_rhino_component()
          : base("Geometry (Rhino)", "Geometry Rhino",
             "Builds nesting parts from referenced Rhino objects, separating outlines from attributes (text, curves) and sorting them automatically.",
             "Params", "OpenNest2")
        {
        }
       
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Layer", "Layer", "Boundary layer in Rhino you need to create to select objects. ", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Copies", "Copies", "Number of copies must be equal to tree branches, number of guids inputs is treated as a separate branch", GH_ParamAccess.list, new List<int> { 1 });
            pManager.AddNumberParameter("Simplify", "Simplify", "Default parameter<0>, parameter<1>, \nsegment divisions (0 takes only ends, x>0 divides by distance,\n x<0 max 3 points per sub-segment), compute only convex-hull from simplified polyline 0/1", GH_ParamAccess.list, new List<double> { 100, 0 });
            pManager.AddNumberParameter("Sort", "Sort", "Sort polylines.", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Offset", "Offset", "Clearance offset for NESTING only (model units; 0 = OFF, fast).\nParts: outer grows / holes shrink so placed parts keep this gap.\nThe ORIGINAL curves are still what get placed/output.", GH_ParamAccess.item, 0);
            IGH_Param geometryAsGuid = new Grasshopper.Kernel.Parameters.Param_Guid();
            pManager.AddParameter(geometryAsGuid, "Guid", "Guid", "Referenced geometry as guid (can be mesh, brep, curves in one list)", GH_ParamAccess.tree);

            for (int i = 2; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
            pManager[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Geometry", "Geometry", "Assembled nesting geometry to feed the solver", GH_ParamAccess.list);
            pManager.AddCurveParameter("Borders", "Borders", "Outline curves grouped per part", GH_ParamAccess.tree);
            pManager.AddCurveParameter("BC", "BC", "Convex-hull border curves per part", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Groups", "Groups", "Object group indices per part", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("All", "All", "All referenced geometry grouped per part", GH_ParamAccess.tree);
            pManager.HideParameter(2);
        }

        protected override void BeforeSolveInstance()
        {
            //nest_geo = new nest_rhino_lib.nest_geo();
            bbox = new BoundingBox();
            text = new List<TextEntity>();
            geometry = new List<Curve>();
            geometry_colors = new List<System.Drawing.Color>();
            borders_display = new List<Polyline>();
        }

        private nest_rhino_lib.nest_geo nest_geo;

        public override void BakeGeometry(Rhino.RhinoDoc doc, List<Guid> obj_ids)
        {
            //var attributes = doc.CreateDefaultAttributes();
            obj_ids.AddRange(nest_geo.bake());
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            //Inputs
            string layer = "";
            DA.GetData(0, ref layer);

            var copies = new List<int>();
            DA.GetDataList(1, copies);

            var simplify_parameters = new List<double>();
            DA.GetDataList(2, simplify_parameters);

            // var extend = 0.0;
            //DA.GetData(3, ref extend);

            var sort = 1.0;
            DA.GetData(3, ref sort);
            double offset_distance = 0;
            DA.GetData(4, ref offset_distance);

            List<Guid[]> guids = new List<Guid[]>();

            
            for (int i = non_changing_inputs; i < Params.Input.Count; i++)
            {
                //get current input
                var guids_current = new GH_Structure<GH_Guid>();
                DA.GetDataTree(i, out guids_current);

                //iterate over branches in a current tree
                for (int j = 0; j < guids_current.PathCount; j++)
                {
                    //get current branch guids
                    Guid[] guids_branch = new Guid[guids_current[guids_current.Paths[j]].Count];
                    for (int k = 0; k < guids_branch.Length; k++)
                        guids_branch[k] = guids_current[guids_current.Paths[j]][k].Value;
                    guids.Add(guids_branch);
                }
            }

            //Solution
            nest_geo = nest_rhino_lib.nest_geo_util.guid_to_nest_geo(guids, layer, copies, simplify_parameters);
            if (offset_distance != 0) nest_geo.offset_nesting_boundary(offset_distance);   // 0 = skip entirely (fast)

            //if (extend != 0)
            // nest_geo.extend_openlines(extend);



            if (sort != 0)
                nest_geo.sort_groups(sort == 1);

            //Output
            DA.SetData(0, nest_geo);

            var xforms = new List<Transform>();
            GH_Structure<GH_Curve> gh_borders = new GH_Structure<GH_Curve>();
            GH_Structure<GH_Curve> gh_borders_c = new GH_Structure<GH_Curve>();
            GH_Structure<GH_Integer> gh_groups = new GH_Structure<GH_Integer>();
            GH_Structure<Grasshopper.Kernel.Types.IGH_GeometricGoo> all_geo_groups = new GH_Structure<IGH_GeometricGoo>();
            double text_scale = 0;
            for (int i = 0; i < nest_geo.boundary_sorted.Count; i++)
            {
                xforms.Add(Transform.Translation(new Vector3d(0, 0, 1000 + 200 * i)));

                foreach (var geo in nest_geo.boundary_sorted[i])
                {
                    gh_borders.Append(new GH_Curve(geo.Item2.ToNurbsCurve()), new GH_Path(i));
                    borders_display.Add(geo.Item2);
                    gh_borders_c.Append(new GH_Curve(geo.Item4), new GH_Path(i));
                    text_scale += geo.Item3.Diagonal.SquareLength;
                }
            }
            text_scale = Math.Sqrt(text_scale) / 25;

            for (int i = 0; i < nest_geo.geometry_sorted.Count; i++)
                foreach (var id in nest_geo.geometry_sorted[i])
                {
                    gh_groups.Append(new GH_Integer(id), new GH_Path(i));

                    //https://discourse.mcneel.com/t/geometrybase-equivalent-in-in-grasshopper-kernel-types/145109
                    all_geo_groups.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(nest_geo.geometry[id]), new GH_Path(i));
                }

            DA.SetDataTree(1, gh_borders);
            DA.SetDataTree(2, gh_borders_c);
            DA.SetDataTree(3, gh_groups);
            DA.SetDataTree(4, all_geo_groups);

            //display
            foreach (var t in nest_geo.disply_texts)
            {
                bbox.Union(t.Plane.Origin);
                text.Add(t);
                text[text.Count - 1].TextHeight = text_scale;
            }

            for (int i = 0; i < nest_geo.geometry.Count; i++)
            {
                if (nest_geo.geometry[i].ObjectType.ToString() == "Curve")
                {
                    Curve curve = nest_geo.geometry[i] as Curve;
                    this.geometry.Add(curve);
                    this.geometry_colors.Add(nest_geo.attributes[i].ObjectColor);
                }
            }
          
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.nest_rhino_geo;


        public override Guid ComponentGuid => new Guid("D5A8325A-5BF3-45A2-8C32-1A54A5D1A10E");

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
            IGH_Param param = new Grasshopper.Kernel.Parameters.Param_Guid();
            param.Access = GH_ParamAccess.tree;
            param.Optional = true;
            param.Name = "guid";
            //param.NickName = param.Name;
            param.Description = "guid" + (Params.Input.Count + 1);

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

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);

            //Add String
            Grasshopper.Kernel.Parameters.Param_String ti = Params.Input[0] as Grasshopper.Kernel.Parameters.Param_String;
            if (ti == null || ti.SourceCount > 0 || ti.PersistentDataCount > 0) return;
            Attributes.PerformLayout();
            int xT = (int)ti.Attributes.Pivot.X - 250;
            int yT = (int)ti.Attributes.Pivot.Y - 10;
            Grasshopper.Kernel.Special.GH_Panel panel = new Grasshopper.Kernel.Special.GH_Panel();
            panel.CreateAttributes();
            panel.SetUserText("Outline");
            panel.Attributes.Bounds = new System.Drawing.RectangleF(xT, yT, 100, 12);
            panel.Attributes.Pivot = new System.Drawing.PointF(xT, yT);
            panel.Attributes.ExpireLayout();
            document.AddObject(panel, false);
            ti.AddSource(panel);

            //var numbers = Params.Input[2] as Grasshopper.Kernel.Parameters.Param_Number;
            ////if (ti == null || ti.SourceCount > 0 || ti.PersistentDataCount > 0) return;
            //Attributes.PerformLayout();
            //xT = (int)numbers.Attributes.Pivot.X - 250;
            //yT = (int)numbers.Attributes.Pivot.Y - 10 + 20;
            //panel = new Grasshopper.Kernel.Special.GH_Panel();
            //panel.CreateAttributes();
            //panel.SetUserText("10\n0");
            //panel.Properties.Multiline = false;
            //panel.Attributes.Bounds = new System.Drawing.RectangleF(xT, yT, 100, 58);
            //panel.Attributes.Pivot = new System.Drawing.PointF(xT, yT);
            //panel.Attributes.ExpireLayout();
            //document.AddObject(panel, false);
            //numbers.AddSource(panel);

            var tinteger = Params.Input[1] as Grasshopper.Kernel.Parameters.Param_Integer;
            //if (ti == null || ti.SourceCount > 0 || ti.PersistentDataCount > 0) return;
            Attributes.PerformLayout();
            xT = (int)tinteger.Attributes.Pivot.X - 250;
            yT = (int)tinteger.Attributes.Pivot.Y - 10;
            panel = new Grasshopper.Kernel.Special.GH_Panel();
            panel.CreateAttributes();
            panel.SetUserText("1");
            panel.Properties.Multiline = false;
            panel.Attributes.Bounds = new System.Drawing.RectangleF(xT, yT, 100, 40);
            panel.Attributes.Pivot = new System.Drawing.PointF(xT, yT);
            panel.Attributes.ExpireLayout();
            document.AddObject(panel, false);
            tinteger.AddSource(panel);

            Grasshopper.Kernel.Parameters.Param_Number sort_ni = Params.Input[3] as Grasshopper.Kernel.Parameters.Param_Number;
            //if (ni == null || ni.SourceCount > 0 || ni.PersistentDataCount > 0) return;

            Attributes.PerformLayout();
            int x = (int)sort_ni.Attributes.Pivot.X - 250;
            int y = (int)sort_ni.Attributes.Pivot.Y - 10;
            Grasshopper.Kernel.Special.GH_NumberSlider sort_slider = new Grasshopper.Kernel.Special.GH_NumberSlider();
            sort_slider.SetInitCode(string.Format("{0}<{1}<{2}", -1, 0, 1));

            sort_slider.CreateAttributes();
            sort_slider.Attributes.Pivot = new System.Drawing.PointF(x, y);
            sort_slider.Attributes.ExpireLayout();
            document.AddObject(sort_slider, false);
            sort_ni.AddSource(sort_slider);

            //Add guid
            Grasshopper.Kernel.Parameters.Param_Guid ri = Params.Input[4] as Grasshopper.Kernel.Parameters.Param_Guid;
            if (ri == null || ri.SourceCount > 0 || ri.PersistentDataCount > 0) return;
            Attributes.PerformLayout();
            x = (int)ri.Attributes.Pivot.X - 225;
            y = (int)ri.Attributes.Pivot.Y +00;
            IGH_Param guid = new Grasshopper.Kernel.Parameters.Param_Guid();
            //rect.AddVolatileData(new Grasshopper.Kernel.Data.GH_Path(0), 0, recValues[i].ToNurbsCurve());
            guid.CreateAttributes();
            guid.Attributes.Pivot = new System.Drawing.PointF(x, y);
            guid.Attributes.ExpireLayout();
            document.AddObject(guid, false);
            ri.AddSource(guid);
        }

        protected override void AfterSolveInstance()
        {
            GH_Document ghdoc = base.OnPingDocument();
            for (int i = 0; i < ghdoc.ObjectCount; i++)
            {
                IGH_DocumentObject obj = ghdoc.Objects[i];
                if (obj.Attributes.DocObject.ToString().Equals("Grasshopper.Kernel.Special.GH_Group"))
                {
                    Grasshopper.Kernel.Special.GH_Group groupp = (Grasshopper.Kernel.Special.GH_Group)obj;
                    if (groupp.ObjectIDs.Contains(this.InstanceGuid))
                        return;
                }
            }

            List<Guid> guids = new List<Guid>() { this.InstanceGuid };

            foreach (var param in base.Params.Input)
                foreach (IGH_Param source in param.Sources)
                    guids.Add(source.InstanceGuid);

            Grasshopper.Kernel.Special.GH_Group g = new Grasshopper.Kernel.Special.GH_Group();
            g.NickName = "";
            g.Colour = System.Drawing.Color.FromArgb(50, 255, 255, 255);

            ghdoc.AddObject(g, false, ghdoc.ObjectCount);
            for (int i = 0; i < guids.Count; i++)
                g.AddObject(guids[i]);
            g.ExpireCaches();
        }
    }
}