using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Drawing;

namespace opennest_2
{
    public class component_geo_grasshopper : GH_Component, IGH_VariableParameterComponent
    {
        public component_geo_grasshopper()
          : base("Geometry", "Geometry",
            "Prepares parts for nesting: separates outlines (with holes) from attached attributes; optional simplify, convex hull and copies.",
             "Params", "OpenNest2")
        {
        }

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

        // True if `full` is the same path as `prefix` OR a sub-branch of it (prefix is an ancestor),
        // e.g. {0} matches {0}, {0;0}, {0;1;2}. Lets a user park one outline's attributes across
        // sub-branches and have them all collected for that outline.
        private static bool PathStartsWith(Grasshopper.Kernel.Data.GH_Path full, Grasshopper.Kernel.Data.GH_Path prefix)
        {
            if (full == null || prefix == null || full.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (full.Indices[i] != prefix.Indices[i]) return false;
            return true;
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {

            pManager.AddGeometryParameter( "Outlines", "Outlines", "Closed curves OR planar surfaces (cast per type internally).\nIf geometries have holes, create a data-tree first. \nPlace each element into indivdual branch. \nOtherwise the algoritm will try to order polylines by checking possible holes.", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Simplify", "Simplify", "segment divisions \n0 = KEEP ALL vertices (no simplification - best for nesting fine outlines) \nx>0 divides by distance \nx<0 max 3 points per sub-segment (merges colinear within 10deg)", GH_ParamAccess.item,  -100);
            pManager.AddBooleanParameter("Hull", "Hull", "Replace each outline with its convex hull", GH_ParamAccess.item, false );
            pManager.AddIntegerParameter("Copies", "Copies", "Number of copies per part", GH_ParamAccess.list);
            pManager.AddNumberParameter("Offset", "Offset", "Clearance offset for NESTING only (model units; 0 = OFF, fast).\nParts: outer grows / holes shrink so placed parts keep this gap.\nThe ORIGINAL curves are still what get placed/output.", GH_ParamAccess.item, 0);
            pManager.AddIntegerParameter("Rotations", "Rotations",
                "OPTIONAL per-part rotation constraint (one value per part, repeats like Copies).\n" +
                "Empty / 0 = part inherits the solver's global Rotations setting (default).\n" +
                "N > 0 = THIS part may only use N orientations (360/N degree steps).\n" +
                "1 = fixed, no rotation (e.g. grain direction).\n" +
                "Lets rectangular parts stay at 4 orientations while freeform parts rotate freely in ONE nest.",
                GH_ParamAccess.list);
            pManager.AddGeometryParameter("Attributes", "Attributes", "Additional geometry: points, lines, surfaces, meshes... \nUse data-tree, one list of additional geometry per branch..", GH_ParamAccess.tree);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            pManager[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Geometry", "Geometry", "Prepared parts ready for nesting", GH_ParamAccess.list);
            pManager.AddCurveParameter("Borders", "Borders", "Outline border curves per part", GH_ParamAccess.tree);
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

        // Cast one Grasshopper geometry item to its nesting boundary curve(s): a Curve is used as-is; a
        // Brep/Surface yields its boundary loops (naked edges joined -> outer + holes); a Mesh yields its
        // XY outline. Lets the parts input accept curves AND planar surfaces in the same port (no separate
        // "Geometry Srf" needed, no "Data conversion failed from Surface to Curve").
        private static IEnumerable<Curve> GooToCurves(IGH_GeometricGoo goo)
        {
            var outc = new List<Curve>();
            if (goo == null) return outc;
            string tn = goo.TypeName;

            if (tn == "Brep" || tn == "Surface")
            {
                if (goo.CastTo<Brep>(out Brep brep) && brep != null && brep.IsValid)
                {
                    double mtol = (Rhino.RhinoDoc.ActiveDoc != null) ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
                    if (mtol <= 0) mtol = 0.001;
                    var naked = new List<Curve>();
                    foreach (BrepEdge edge in brep.Edges)
                        if (edge.Valence == EdgeAdjacency.Naked)
                        {
                            Curve c = edge.DuplicateCurve();
                            if (c != null) naked.Add(c);
                        }
                    if (naked.Count > 0)
                    {
                        Curve[] joined = Curve.JoinCurves(naked, 2.1 * mtol);
                        if (joined != null) outc.AddRange(joined);
                    }
                }
            }
            else if (tn == "Mesh")
            {
                if (goo.CastTo<Mesh>(out Mesh mesh) && mesh != null && mesh.IsValid)
                {
                    Polyline[] outlines = mesh.GetOutlines(Plane.WorldXY);
                    if (outlines != null)
                        foreach (var pl in outlines)
                            if (pl != null && pl.IsValid && pl.Count >= 3) outc.Add(pl.ToNurbsCurve());
                }
            }
            else
            {
                if (goo.CastTo<Curve>(out Curve crv) && crv != null) outc.Add(crv);
            }
            return outc;
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

            // Per-part rotation constraints — located BY NAME so component instances saved
            // before this input existed (no "Rotations" port) keep working unchanged.
            var rotations = new List<int>();
            {
                int rotIdx = -1;
                for (int pi = 0; pi < Params.Input.Count; pi++)
                    if (Params.Input[pi].Name == "Rotations") { rotIdx = pi; break; }
                if (rotIdx >= 0) DA.GetDataList(rotIdx, rotations);
            }

            var sort = 1.0;


            List<Curve[]> curves = new List<Curve[]>();


            List<GeometryBase[]> attributes_geometries = new List<GeometryBase[]>();
            var geo_current = new GH_Structure<IGH_GeometricGoo>();
            bool result = DA.GetDataTree<IGH_GeometricGoo>(0, out geo_current);

            // Read the base "Attributes" tree PLUS every extra "Attributes N" port added via the
            // component's +/- zoom. Each port is an independent attribute source; a part collects from
            // ALL of them (by path + sub-branches). Scan from the first fixed attribute slot (index 5)
            // and skip the scalar "Rotations" port by name, so this is order-independent (Rotations may
            // sit before or after the base Attributes port depending on when the instance was saved).
            var attrTrees = new List<GH_Structure<IGH_GeometricGoo>>();
            for (int ai = 5; ai < Params.Input.Count; ai++)
            {
                if (Params.Input[ai].Name == "Rotations") continue;   // integer port, not an attribute tree
                GH_Structure<IGH_GeometricGoo> t;
                if (DA.GetDataTree(ai, out t) && t != null && t.DataCount > 0) attrTrees.Add(t);
            }

            //Rhino.RhinoApp.WriteLine("geo_current.PathCount: " + geo_current.PathCount.ToString());


            //iterate over branches in a current tree
            for (int j = 0; j < geo_current.PathCount; j++)
            {
                //get current branch items; cast each by TYPE (curve as-is, surface/brep -> boundary loops,
                //mesh -> outline) so the parts input accepts curves AND planar surfaces in one port.
                var outline_branch = geo_current[geo_current.Paths[j]];
                var curve_list = new List<Curve>(outline_branch.Count);
                for (int k = 0; k < outline_branch.Count; k++)
                {
                    if (outline_branch[k] == null) continue;
                    curve_list.AddRange(GooToCurves(outline_branch[k]));
                }
                curves.Add(curve_list.ToArray());


                if (copies.Count == 0)
                    copies.Add(1);

                // Attributes: gather every attribute branch at THIS outline's path OR any SUB-branch of
                // it ({i}, {i;0}, {i;1}, ...), so a user can organise one part's attributes across
                // sub-branches. Fall back to POSITIONAL pairing only when NO path/sub-path matched AND
                // the two trees have equal branch counts (keeps already-aligned trees byte-identical).
                // NULL / unconvertible items are SKIPPED; an exact/empty matched branch is honoured (that
                // outline gets no attributes). attributes_geometries stays index-aligned with curves.
                attributes_geometries.Add(new GeometryBase[0]);
                Grasshopper.Kernel.Data.GH_Path opath = geo_current.Paths[j];
                var attrs = new List<GeometryBase>();
                foreach (var atree in attrTrees)   // gather from every attribute port
                {
                    bool matchedPath = false;
                    for (int b = 0; b < atree.PathCount; b++)
                    {
                        if (!PathStartsWith(atree.Paths[b], opath)) continue;     // exact path or a sub-branch of it
                        matchedPath = true;
                        var br = atree.Branches[b];
                        for (int k = 0; k < br.Count; k++)
                        {
                            if (br[k] == null) continue;                          // skip null item
                            var gb = GH_Convert.ToGeometryBase(br[k]);
                            if (gb != null) attrs.Add(gb);                        // skip unconvertible item
                        }
                    }
                    if (!matchedPath && geo_current.PathCount == atree.PathCount && j < atree.Branches.Count)
                    {
                        var br = atree.Branches[j];                              // positional fallback (equal-count, differently-pathed)
                        for (int k = 0; k < br.Count; k++)
                        {
                            if (br[k] == null) continue;
                            var gb = GH_Convert.ToGeometryBase(br[k]);
                            if (gb != null) attrs.Add(gb);
                        }
                    }
                }
                if (attrs.Count > 0)
                    attributes_geometries[attributes_geometries.Count - 1] = attrs.ToArray();

            }

            // If user gives a FLAT LIST of curves, each curve becomes its own item AND holes are
            // auto-detected by containment below (identify_groups pairs a big ring with the smaller
            // rings inside it => one part with a hole). A DATA-TREE input keeps each branch pre-grouped.
            bool flatList = (curves.Count == 1);
            if (flatList)
            {

                var temp_curves = new List<Curve[]>();
                foreach (var curve in curves[0])
                    temp_curves.Add(new Curve[] { curve });
                curves = temp_curves;
                attributes_geometries.Clear();
                for (int i = 0; i < curves.Count; i++)
                {
               
                    var attributes_local = new List<GeometryBase>();
                    foreach (var atree in attrTrees)                  // flatten every attribute port onto each part
                        foreach (var attribute in atree.AllData(true))
                        {
                            if (attribute == null) continue;
                            var gb = GH_Convert.ToGeometryBase(attribute);
                            if (gb != null) attributes_local.Add(gb);   // skip null/unconvertible
                        }

                    attributes_geometries.Add(attributes_local.ToArray());
                }
            }



            for (int i = copies.Count; i< curves.Count; i++)
            {
                copies.Add(copies[i%copies.Count]);
            }

            // Extend rotations like copies: repeat the pattern across all parts; empty = all 0 (inherit).
            if (rotations.Count == 0)
                for (int i = 0; i < curves.Count; i++) rotations.Add(0);
            else
                for (int i = rotations.Count; i < curves.Count; i++) rotations.Add(rotations[i % rotations.Count]);
            if (rotations.Count > curves.Count) rotations.RemoveRange(curves.Count, rotations.Count - curves.Count);





            //Solution. Flat list => hard_coded_input:false => identify_groups auto-pairs outer rings with
            //the smaller rings they contain (big rect + smaller rect inside = one part with a hole).
            nest_geo = nest_rhino_lib.nest_geo_util.geo_to_nest_geo(curves, copies, simplify_parameters, attributes_geometries, !flatList, rotations);
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
                    bbox.Union(geo.Item3);   // include the (possibly offset) nesting outline in the preview ClippingBox
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

        protected override System.Drawing.Bitmap Icon => Properties.Resources.element;

        public override Guid ComponentGuid => new Guid("D5A8325A-5BF3-45A2-1C32-1A54A0D1A10E");

        //////////////////////////////////////////////////////////////////////////////////////////ZoomableComponent

        // The fixed inputs (Outlines, Simplify, Hull, Copies, Offset, Rotations, Attributes) are locked;
        // +/- only ever adds/removes EXTRA attribute ports AFTER them. Extras always follow the BASE
        // "Attributes" port, found BY NAME so this is robust to component instances saved by older builds:
        //   - before Rotations existed        -> ...Offset, Attributes(5), extras(6+)
        //   - Rotations AFTER Attributes       -> ...Attributes(5), Rotations(6), extras(7+)
        //   - current: Rotations BEFORE Attrs  -> ...Rotations(5), Attributes(6), extras(7+)
        private int FirstExtraIndex()
        {
            int baseAttr = -1;
            for (int i = 0; i < Params.Input.Count; i++)
                if (Params.Input[i].Name == "Attributes") { baseAttr = i; break; }
            if (baseAttr < 0) return Params.Input.Count;          // no base port (shouldn't happen)
            int j = baseAttr + 1;
            if (j < Params.Input.Count && Params.Input[j].Name == "Rotations") j++;   // legacy: Rotations sits after Attributes
            return j;
        }

        // + is only offered at the end (index >= first extra), so inserting can't shift fixed inputs.
        bool IGH_VariableParameterComponent.CanInsertParameter(GH_ParameterSide side, int index)
            => side == GH_ParameterSide.Input && index >= FirstExtraIndex();

        // - is only offered for the extra attribute ports, never the fixed inputs.
        bool IGH_VariableParameterComponent.CanRemoveParameter(GH_ParameterSide side, int index)
            => side == GH_ParameterSide.Input && index >= FirstExtraIndex();

        // Each added port is an extra Attributes geometry tree (named in VariableParameterMaintenance).
        IGH_Param IGH_VariableParameterComponent.CreateParameter(GH_ParameterSide side, int index)
        {
            var param = new Grasshopper.Kernel.Parameters.Param_Geometry();
            param.Access = GH_ParamAccess.tree;
            param.Optional = true;
            param.Name = "Attributes";
            param.NickName = "Attributes";
            return param;
        }

        bool IGH_VariableParameterComponent.DestroyParameter(GH_ParameterSide side, int index)
            => side == GH_ParameterSide.Input && index >= FirstExtraIndex();

        // Keep the extra ports consistently named/typed: Attributes 2, Attributes 3, ... (tree, optional).
        void IGH_VariableParameterComponent.VariableParameterMaintenance()
        {
            int firstExtra = FirstExtraIndex();
            for (int i = firstExtra; i < Params.Input.Count; i++)
            {
                var p = Params.Input[i];
                int n = i - firstExtra + 2;   // first extra port = "Attributes 2"
                p.Name = "Attributes " + n;
                p.NickName = "Attr" + n;
                p.Description = "Extra attribute geometry tree. Branch {i} (and its sub-branches) attaches to part i, merged with the other Attributes ports.";
                p.Access = GH_ParamAccess.tree;
                p.Optional = true;
                p.MutableNickName = false;
            }
        }
    }
}