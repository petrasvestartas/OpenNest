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

        // First 7 inputs are FIXED (mirror GH1); +/- only adds/removes extra Attributes ports after them.
        private const int FIXED_INPUTS = 7;
        private const int ROTATIONS_INPUT = 6;   // fixed integer port, not an attribute tree

        protected override void AddInputs(InputAdder inputs)
        {
            // Inputs mirror the current GH1 Geometry component 1:1 (so the upgrader maps directly). All Access.Tree
            // so the component runs ONCE and emits ONE nest_geo. Simplify default 0 = KEEP ALL vertices: the parts
            // are nested at full detail. The placed/output geometry is ALWAYS the original curve — Simplify and
            // Offset only affect the internal NFP collision boundary, never the shape that gets placed/output.
            inputs.AddCurve("Parts", "P", "Closed part curves. Flat list = each curve a part (holes auto-detected); data tree = one branch per part.", Access.Tree);
            inputs.AddNumber("Simplify", "S", "0 = keep all vertices (nest the ORIGINAL shape, default); x>0 divide by distance; x<0 merge near-colinear.", Access.Tree, Requirement.MayBeMissing).Set(0.0);
            inputs.AddBoolean("Hull", "H", "Replace each part's NESTING boundary with its convex hull.", Access.Tree, Requirement.MayBeMissing).Set(false);
            inputs.AddInteger("Copies", "C", "Copies per part (one value, or one per part).", Access.Tree, Requirement.MayBeMissing).Set(1);
            inputs.AddNumber("Offset", "O", "Nesting clearance (model units; 0 = off). Outer grows / holes shrink so placed parts keep this gap — the ORIGINAL curves are still what get placed/output.", Access.Tree, Requirement.MayBeMissing).Set(0.0);
            inputs.AddGeneric("Attributes", "A", "Extra geometry carried with each part (data-tree: one branch per part; flat list: applied to every part). Use the +/- on the component to add more Attributes inputs.", Access.Tree, Requirement.MayBeMissing);
            inputs.AddInteger("Rotations", "R",
                "OPTIONAL per-part rotation constraint (one value, or one per part, repeats like Copies).\n" +
                "Empty / 0 = part inherits the solver's global Rotations setting (default).\n" +
                "N > 0 = THIS part may only use N orientations (360/N degree steps); 1 = fixed, no rotation.\n" +
                "Lets rectangular parts stay at 4 orientations while freeform parts rotate freely in ONE nest.",
                Access.Tree, Requirement.MayBeMissing).Set(0);
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
            access.GetTree(1, out Tree<double> simpT); double simplify = NestGh2Util.First(simpT, 0.0);
            access.GetTree(2, out Tree<bool> hullT); bool hull = NestGh2Util.First(hullT, false);
            access.GetTree(3, out Tree<int> copiesT); var copiesVals = NestGh2Util.AllOr(copiesT, 1);
            access.GetTree(4, out Tree<double> offT); double offset = NestGh2Util.First(offT, 0.0);

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

            // Per-part rotation constraints (fixed integer port — 0 = inherit solver setting).
            access.GetTree(ROTATIONS_INPUT, out Tree<int> rotT);
            var rotVals = NestGh2Util.AllOr(rotT, 0);

            // Every Attributes port (base index 5 + any +/- variable ports) as a tree of geometry, by branch.
            var attrTrees = new List<GeometryBase[][]>();
            for (int ai = 5; ai < Parameters.InputCount; ai++)
            {
                if (ai == ROTATIONS_INPUT) continue;   // integer port, not an attribute tree
                if (access.GetTree(ai, out Tree<GeometryBase> at) && at != null && at.LeafCount > 0)
                { at.ToArrays(out GeometryBase[][] ab); attrTrees.Add(ab); }
            }

            // FLAT LIST (single branch) -> explode to one curve per part so identify_groups auto-pairs each
            // outer ring with the smaller rings it contains. DATA TREE -> keep each branch pre-grouped.
            bool flatList = (curves.Count == 1);
            if (flatList)
            {
                var temp = new List<Curve[]>();
                foreach (var c in curves[0]) temp.Add(new[] { c });
                curves = temp;
            }

            // Attributes per part: data-tree -> positional branch match (part branch i <- attribute branch i);
            // flat list -> all attributes applied to every part (identify_groups regroups, so per-part match is
            // undefined — mirrors GH1).
            List<GeometryBase[]> attrsPerPart = null;
            if (attrTrees.Count > 0)
            {
                attrsPerPart = new List<GeometryBase[]>(curves.Count);
                if (!flatList)
                    for (int i = 0; i < curves.Count; i++)
                    {
                        var l = new List<GeometryBase>();
                        foreach (var ab in attrTrees) if (i < ab.Length && ab[i] != null) foreach (var g in ab[i]) if (g != null) l.Add(g);
                        attrsPerPart.Add(l.ToArray());
                    }
                else
                {
                    var all = new List<GeometryBase>();
                    foreach (var ab in attrTrees) foreach (var b in ab) if (b != null) foreach (var g in b) if (g != null) all.Add(g);
                    for (int i = 0; i < curves.Count; i++) attrsPerPart.Add(all.ToArray());
                }
            }

            // Per-part copies, cycled like GH1 (one value applies to all; a list maps one-per-part).
            var copiesList = new List<int>(curves.Count);
            for (int i = 0; i < curves.Count; i++) { int c = copiesVals[i % copiesVals.Count]; copiesList.Add(c < 1 ? 1 : c); }
            // Per-part rotation overrides, cycled the same way (0 = inherit the solver setting).
            var rotationsList = new List<int>(curves.Count);
            for (int i = 0; i < curves.Count; i++) { int rv = rotVals[i % rotVals.Count]; rotationsList.Add(rv < 0 ? 0 : rv); }
            var simp = new List<double> { simplify, hull ? 1.0 : 0.0 };

            var geo = nest_geo_util.geo_to_nest_geo(curves, copiesList, simp, attrsPerPart, hard_coded_input: !flatList, rotations: rotationsList);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0)
            {
                access.AddWarning("No boundaries detected", "Could not extract closed part boundaries.");
                return;
            }
            // Clearance offset: outer grows / holes shrink on the NFP boundary only (original curves untouched).
            if (offset != 0) geo.offset_nesting_boundary(offset);

            access.SetItem(0, geo);

            var borders = new List<Curve>();
            foreach (var grp in geo.boundary_sorted)
                foreach (var tup in grp)
                    if (tup.Item2 != null && tup.Item2.Count >= 2) borders.Add(tup.Item2.ToNurbsCurve());
            access.SetTwig(1, borders.ToArray());
        }

        // ---- variable +/- Attributes input ports (GH1 IGH_VariableParameterComponent equivalent) ----
        // The 6 fixed inputs are locked; +/- only adds/removes extra generic "Attributes" ports AFTER them, so
        // the fixed indices never shift. Overriding the three Can/Do methods enables the +/- UI.
        public override bool CanCreateParameter(Side side, int index)
            => side == Side.Input && index >= FIXED_INPUTS;

        public override bool CanRemoveParameter(Side side, int index)
            => side == Side.Input && index >= FIXED_INPUTS;

        public override void DoCreateParameter(Side side, int index)
        {
            if (side != Side.Input) return;
            // Access.Tree is fixed via the ctor (the Access setter is non-public).
            var p = new Grasshopper2.Parameters.Standard.GenericParameter(
                "Attributes", "A", "Extra geometry carried with the matching part branch.", Access.Tree);
            try { p.Requirement = Requirement.MayBeMissing; } catch { }
            Parameters.AddInput(p, index);
        }

        // Keep the extra ports consistently named Attributes 2, Attributes 3, … .
        public override void VariableParameterMaintenance()
        {
            for (int i = FIXED_INPUTS; i < Parameters.InputCount; i++)
            {
                var p = Parameters.Input(i);
                if (p == null) continue;
                int n = i - FIXED_INPUTS + 2;   // first extra port = "Attributes 2"
                try { p.ModifyNameAndInfo("Attributes " + n, "Extra geometry for the matching part branch (merged with the other Attributes ports)."); } catch { }
            }
        }
    }
}
