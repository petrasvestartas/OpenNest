using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace opennest_2
{
    public class component_geo_grasshopper_surfaces : GH_Component, IGH_VariableParameterComponent
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
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lineWeight = args.DefaultCurveThickness;

            foreach (var crv in borders_display)
                args.Display.DrawPolyline(crv, col, lineWeight);

            for (int i = 0; i < geometry.Count; i++)
            {
                args.Display.DrawCurve(geometry[i], col); // geometry_colors[i]
                args.Display.DrawPoint(geometry[i].PointAtStart, Rhino.Display.PointStyle.RoundSimple, 2, col);
            }

            foreach (var t in text)
                args.Display.Draw3dText(t.PlainText, col, t.Plane, t.TextHeight, "centre", true, true, Rhino.DocObjects.TextHorizontalAlignment.Center, Rhino.DocObjects.TextVerticalAlignment.Middle);
        }

        public int non_changing_inputs = 0;

        public component_geo_grasshopper_surfaces()
          : base("Geometry (Surfaces)", "Geometry Srf",
            "Builds nesting parts from planar surfaces instead of curves; holes are read from the surface boundaries.",
             "Params", "OpenNest2")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddBrepParameter( "Surfaces", "Surfaces", "Planar boundary surfaces.\n If you want to nest multiple sheets partition surfaces into the data-tree.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Simplify", "Simplify", "segment divisions \n0 takes only ends \nx>0 divides by distance \nx<0 max 3 points per sub-segment)", GH_ParamAccess.item,  -100);
            pManager.AddBooleanParameter("Hull", "Hull", "Use the convex hull of each simplified outline.", GH_ParamAccess.item, false );
            pManager.AddIntegerParameter("Copies", "Copies", "Number of copies per part.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Offset", "Offset", "Clearance offset for NESTING only (model units; 0 = OFF, fast).\nParts: outer grows / holes shrink so placed parts keep this gap.\nThe ORIGINAL curves are still what get placed/output.", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Rotations", "Rotations",
                "OPTIONAL per-part rotation constraint (one value per part, repeats like Copies).\n" +
                "Empty / 0 = part inherits the solver's global Rotations setting (default).\n" +
                "N > 0 = THIS part may only use N orientations (360/N degree steps).\n" +
                "1 = fixed, no rotation (e.g. grain direction).\n" +
                "Lets rectangular parts stay at 4 orientations while freeform parts rotate freely in ONE nest.",
                GH_ParamAccess.list);
            pManager.AddGeometryParameter("Attributes", "Attributes", "Additional geometry: points, lines, surfaces, meshes... \nUse data-tree, one list of additional geometry per branch..", GH_ParamAccess.list);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;   // Rotations
            pManager[6].Optional = true;   // Attributes
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Geometry", "Geometry", "Nesting parts ready for the solver.", GH_ParamAccess.list);
            pManager.AddCurveParameter("Borders", "Borders", "Outline curves of each part.", GH_ParamAccess.list);
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

            double simplify_parameter = 100;
            bool compute_convex_hull = false;

            DA.GetData(1, ref simplify_parameter);
            DA.GetData(2, ref compute_convex_hull);
            double offset_distance = 0;
            DA.GetData(4, ref offset_distance);

            var simplify_parameters = new List<double>{simplify_parameter};
            if (compute_convex_hull)
                simplify_parameters.Add(1);
            else
                simplify_parameters.Add(0);

            var copies = new List<int>();
            DA.GetDataList(3, copies);

            // Per-part rotation constraints + Attributes are read BY NAME so component instances saved before
            // the Rotations input existed (Attributes at index 5) keep working after it was inserted at index 5.
            var rotations = new List<int>();
            int rotIdx = -1;
            for (int pi = 0; pi < Params.Input.Count; pi++)
                if (Params.Input[pi].Name == "Rotations") { rotIdx = pi; break; }
            if (rotIdx >= 0) DA.GetDataList(rotIdx, rotations);

            var sort = 1.0;

            var geo_current = new List<Brep>();
            bool result = DA.GetDataList(0, geo_current);

            var attributes = new List<IGH_GeometricGoo>();
            int attrIdx = -1;
            for (int pi = 0; pi < Params.Input.Count; pi++)
                if (Params.Input[pi].Name == "Attributes") { attrIdx = pi; break; }
            if (attrIdx >= 0) DA.GetDataList(attrIdx, attributes);


            List<Curve[]> curves = new List<Curve[]>();
            List<GeometryBase[]> attributes_geometries = new List<GeometryBase[]>();
            for (int j = 0; j < geo_current.Count; j++)
            {

                if (geo_current[j].Faces.Count != 1)
                    this.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Brep must contain only one face, current face count: " + geo_current[j].Faces.Count.ToString());
                if (geo_current[j].Faces[0].IsPlanar(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance*2) == false)
                    this.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input must be a planar surface");

                List<Curve> boundary_curves_exploded = new List<Curve>();
                foreach (BrepEdge edge in geo_current[j].Edges)
                {
                    // Find only the naked edges 
                    if (edge.Valence == EdgeAdjacency.Naked)
                    {
                        Curve crv = edge.DuplicateCurve();
                        if (null != crv)
                        boundary_curves_exploded.Add(crv);
                    }
                }

                double tol = 2.1 * Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
                Curve[] boundary_curves = Curve.JoinCurves(boundary_curves_exploded, tol);

                curves.Add(boundary_curves);


                if (copies.Count == 0)
                    copies.Add(1);

                var attributes_local = new GeometryBase[0];
                attributes_geometries.Add(attributes_local);
                if (geo_current.Count == attributes.Count)
                    attributes_geometries[attributes_geometries.Count-1] = new GeometryBase[] { GH_Convert.ToGeometryBase(attributes[j]) };

            }


            for (int i = copies.Count; i< curves.Count; i++)
            {
                copies.Add(copies[i%copies.Count]);
            }





            //Solution
            nest_geo = nest_rhino_lib.nest_geo_util.geo_to_nest_geo(curves, copies, simplify_parameters, attributes_geometries, rotations: rotations);
            if (offset_distance != 0) nest_geo.offset_nesting_boundary(offset_distance);   // 0 = skip entirely (fast)

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

            //display
            foreach (var t in nest_geo.disply_texts)
            {
                bbox.Union(t.Plane.Origin);
                text.Add(t);
                //Rhino.RhinoApp.WriteLine(t.PlainText);
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

        protected override System.Drawing.Bitmap Icon => Properties.Resources.element_surface;

        public override Guid ComponentGuid => new Guid("D5A5685A-5BF3-45A2-1C32-1A54A0D1A10E");

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
    }   
}