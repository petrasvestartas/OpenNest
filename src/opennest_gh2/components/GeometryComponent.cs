using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Data;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "Geometry" component. Prepares parts for nesting from closed curves; holes vs
    // borders are auto-detected by nest_geo.identify_groups (containment + winding). Reuses geo_to_nest_geo.
    [IoId("b2c4e8a6-3f17-4d92-8a05-1c7b9e0d2a02")]
    public class GeometryComponent : Component
    {
        public GeometryComponent()
            : base(new Nomen("Geometry", "Prepare parts for nesting (holes auto-detected)", "OpenNest", "Input")) { }

        public GeometryComponent(IReader reader) : base(reader) { }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("element.svg");

        // Explicit viewport preview (GH1 DrawViewportWires equivalent) so the prepared part borders show
        // without baking. Cached from the last Process.
        private volatile List<Curve> _previewWires;
        private BoundingBox _previewBox = BoundingBox.Empty;
        public override bool DisplayCapable => true;
        public override BoundingBox DisplayBounds() => _previewBox.IsValid ? _previewBox : base.DisplayBounds();
        public override void DisplayWires(Rhino.Display.DisplayPipeline pipeline, Grasshopper2.Display.Guises guises, ref BoundingBox region)
        {
            var w = _previewWires; if (w == null) return;
            var col = System.Drawing.Color.FromArgb(40, 40, 40);
            foreach (var c in w) if (c != null) { pipeline.DrawCurve(c, col); region.Union(c.GetBoundingBox(false)); }
        }

        protected override void AddInputs(InputAdder inputs)
        {
            // Whole TREE (like GH1 GH_ParamAccess.tree). FLAT LIST (one branch) -> each curve is its own part
            // and holes are auto-detected by containment. DATA TREE (many branches) -> each branch is one
            // pre-grouped part (outer + holes). Either way it builds ONE nest_geo (not one per branch).
            // ALL inputs are whole-TREE so NO input can drive GH2 per-branch iteration: the component runs once
            // and emits exactly ONE nest_geo regardless of the input tree shape (matches GH1 GH_ParamAccess.tree).
            inputs.AddCurve("Parts", "P", "Closed part curves. Flat list = each curve a part (holes auto-detected); data tree = one branch per part.", Access.Tree);
            inputs.AddNumber("Simplify", "S", "Segment divisions: 0 = keep all vertices; x>0 divide by distance; x<0 merge near-colinear (default).", Access.Tree, Requirement.MayBeMissing).Set(-100.0);
            inputs.AddBoolean("Hull", "H", "Replace each part with its convex hull.", Access.Tree, Requirement.MayBeMissing).Set(false);
            inputs.AddInteger("Copies", "C", "Copies per part (one value, or one per part).", Access.Tree, Requirement.MayBeMissing).Set(1);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geometry", "G", "OpenNest parts (feed into a solver).");
            outputs.AddCurve("Borders", "B", "Outline border polylines per part.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetTree(0, out Tree<Curve> tree) || tree == null || tree.LeafCount == 0)
            { access.AddWarning("No parts", "Connect closed part curves."); return; }
            access.GetTree(1, out Tree<double> simpT); double simplify = NestGh2Util.First(simpT, -100.0);
            access.GetTree(2, out Tree<bool> hullT); bool hull = NestGh2Util.First(hullT, false);
            access.GetTree(3, out Tree<int> copiesT); var copiesVals = NestGh2Util.AllOr(copiesT, 1);

            // One List<Curve[]> entry per input BRANCH (closed curves only), mirroring GH1.
            tree.ToArrays(out Curve[][] branches);
            var curves = new List<Curve[]>();
            foreach (var br in branches)
            {
                if (br == null) continue;
                var cl = new List<Curve>();
                foreach (var c in br) if (c != null && c.IsClosed) cl.Add(c);
                if (cl.Count > 0) curves.Add(cl.ToArray());
            }
            if (curves.Count == 0) { access.AddWarning("No closed parts", "Parts must be closed curves."); return; }

            // FLAT LIST (single branch) -> explode to one curve per part so identify_groups auto-pairs each
            // outer ring with the smaller rings it contains. DATA TREE -> keep each branch pre-grouped.
            bool flatList = (curves.Count == 1);
            if (flatList)
            {
                var temp = new List<Curve[]>();
                foreach (var c in curves[0]) temp.Add(new[] { c });
                curves = temp;
            }

            // Per-part copies, cycled like GH1 (one value applies to all; a list maps one-per-part).
            var copiesList = new List<int>(curves.Count);
            for (int i = 0; i < curves.Count; i++) { int c = copiesVals[i % copiesVals.Count]; copiesList.Add(c < 1 ? 1 : c); }
            // Pass the simplify VALUE through exactly like GH1 (NOT a computed segment length).
            var simp = new List<double> { simplify, hull ? 1.0 : 0.0 };

            var geo = nest_geo_util.geo_to_nest_geo(curves, copiesList, simp, null, hard_coded_input: !flatList);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0)
            {
                access.AddWarning("No boundaries detected", "Could not extract closed part boundaries.");
                return;
            }

            access.SetItem(0, geo);

            var borders = new List<Curve>();
            var bb = BoundingBox.Empty;
            foreach (var grp in geo.boundary_sorted)
                foreach (var tup in grp)
                    if (tup.Item2 != null && tup.Item2.Count >= 2) { var c = tup.Item2.ToNurbsCurve(); borders.Add(c); bb.Union(c.GetBoundingBox(false)); }
            access.SetTwig(1, borders.ToArray());
            _previewWires = borders; _previewBox = bb;
        }
    }
}
