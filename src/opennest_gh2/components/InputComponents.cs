using System;
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
    // Merge: combine several OpenNest Geometry (nest_geo) streams into one.
    [IoId("8eb83827-eb1d-457e-a45e-a29a241d314d")]
    public class MergeComponent : Component
    {
        public MergeComponent() : base(new Nomen("Merge", "Merge several OpenNest Geometry streams.", "OpenNest", "Input")) { }
        public MergeComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("merge.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Geometry A", "A", "OpenNest Geometry.", Access.Tree);
            inputs.AddGeneric("Geometry B", "B", "OpenNest Geometry.", Access.Tree);
        }
        protected override void AddOutputs(OutputAdder outputs) => outputs.AddGeneric("Geometry", "G", "Merged OpenNest Geometry.");

        protected override void Process(IDataAccess access)
        {
            var list = new List<nest_geo>();
            if (access.GetTree(0, out Tree<nest_geo> a) && a != null) list.AddRange(a.NonNullItems.Where(x => x != null));
            if (access.GetTree(1, out Tree<nest_geo> b) && b != null) list.AddRange(b.NonNullItems.Where(x => x != null));
            if (list.Count == 0) { access.AddWarning("No geometry", "Connect Geometry streams."); return; }
            access.SetItem(0, nest_geo.Merge(list));
        }
    }

    // Sheets (Surfaces): build nest_sheets from planar Breps' boundary loops.
    [IoId("11e19ce6-e1a3-47b1-9a91-ecc959d1dfca")]
    public class SheetsSurfacesComponent : Component
    {
        public SheetsSurfacesComponent() : base(new Nomen("Sheets (Surfaces)", "Define nesting sheets from planar surfaces.", "OpenNest", "Input")) { }
        public SheetsSurfacesComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("sheet_surface.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Surfaces", "S", "Planar surfaces/breps (with optional holes); one sheet each.", Access.Tree);
            inputs.AddInteger("Copies", "C", "Total sheets to array.", Access.Tree, Requirement.MayBeMissing).Set(100);
            inputs.AddNumber("Gap", "G", "Gap between arrayed sheets.", Access.Tree, Requirement.MayBeMissing).Set(0.1);
            inputs.AddNumber("Offset", "O", "Inward margin (0 = off).", Access.Tree, Requirement.MayBeMissing).Set(0.0);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Sheets", "S", "OpenNest sheets.");
            outputs.AddCurve("Polylines", "P", "Generated sheet outlines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetTree(0, out Tree<Brep> tree) || tree == null || tree.LeafCount == 0) return;
            var breps = tree.NonNullItems.ToArray();
            if (breps.Length == 0) return;
            access.GetTree(1, out Tree<int> cT); int copies = NestGh2Util.First(cT, 100);
            access.GetTree(2, out Tree<double> gT); double gap = NestGh2Util.First(gT, 0.1);
            access.GetTree(3, out Tree<double> oT); double offset = NestGh2Util.First(oT, 0.0);
            var helper = new nest_geo();
            var plinesList = new List<List<Polyline>>();
            foreach (var brep in breps)
            {
                if (brep == null) continue;
                var loops = ComponentGeoUtil.BrepLoops(brep);
                // sheet_to_polylines, not curve_to_polyline per loop: a SHEET is a container, so its outer ring
                // must be INSCRIBED in the real sheet and its inner loops (voids) must CONTAIN the real void —
                // the opposite roles from a part, and only knowable once one sheet's loops are sorted together.
                var pls = helper.sheet_to_polylines(loops.Where(c => c != null && c.IsClosed), 0, false);
                if (pls.Count > 0) plinesList.Add(pls);
            }
            if (plinesList.Count == 0) return;
            double g = gap;
            var sheets = new nest_sheets(plinesList, new List<double> { g, g }, new List<int>(), copies < 1 ? 1 : copies);
            if (offset != 0) sheets.offset_sheet_boundary(offset);
            access.SetItem(0, sheets);
            var outPolys = new List<Curve>();
            foreach (var s in sheets.sheets) { if (s == null) continue; foreach (var pl in s) if (pl != null) outPolys.Add(pl.ToNurbsCurve()); }
            access.SetTwig(1, outPolys.ToArray());
        }
    }

    // Geometry (Surfaces): build nest_geo from planar Breps' boundary loops.
    [IoId("d5a5685a-5bf3-45a2-1c32-1a54a0d1a10e")]
    public class GeometrySurfacesComponent : Component
    {
        public GeometrySurfacesComponent() : base(new Nomen("Geometry (Surfaces)", "Prepare parts for nesting from planar surfaces.", "OpenNest", "Input")) { }
        public GeometrySurfacesComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("element_surface.svg");

        // First 7 inputs are FIXED (mirror the main Geometry component); +/- only adds extra Attributes ports after.
        private const int FIXED_INPUTS = 7;
        private const int ROTATIONS_INPUT = 5;   // fixed integer port (before Attributes), not an attribute tree

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Surfaces", "S", "Planar surfaces/breps (holes auto-detected); one part each.", Access.Tree);
            inputs.AddNumber("Simplify", "Sm", "0 = keep all vertices (nest the ORIGINAL shape, default); x>0 divide by distance; x<0 merge near-colinear.", Access.Tree, Requirement.MayBeMissing).Set(0.0);
            inputs.AddBoolean("Hull", "H", "Convex hull each part.", Access.Tree, Requirement.MayBeMissing).Set(false);
            inputs.AddInteger("Copies", "C", "Copies per part.", Access.Tree, Requirement.MayBeMissing).Set(1);
            inputs.AddNumber("Offset", "O", "Nesting clearance (model units; 0 = off). Outer grows / holes shrink so placed parts keep this gap — the ORIGINAL curves are still what get placed/output.", Access.Tree, Requirement.MayBeMissing).Set(0.0);
            inputs.AddInteger("Rotations", "R",
                "OPTIONAL per-part rotation constraint (one value, or one per part, repeats like Copies).\n" +
                "Empty / 0 = part inherits the solver's global Rotations setting (default).\n" +
                "N > 0 = THIS part may only use N orientations (360/N degree steps); 1 = fixed, no rotation.",
                Access.Tree, Requirement.MayBeMissing).Set(0);
            inputs.AddGeneric("Attributes", "A", "Extra geometry carried with each part (one branch per part/surface). Use the +/- on the component to add more Attributes inputs.", Access.Tree, Requirement.MayBeMissing);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Geometry", "G", "OpenNest parts.");
            outputs.AddCurve("Borders", "B", "Outline border polylines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetTree(0, out Tree<Brep> tree) || tree == null || tree.LeafCount == 0) return;
            var breps = tree.NonNullItems.ToArray();
            if (breps.Length == 0) return;
            access.GetTree(1, out Tree<double> sT); double simplify = NestGh2Util.First(sT, 0.0);
            access.GetTree(2, out Tree<bool> hT); bool hull = NestGh2Util.First(hT, false);
            access.GetTree(3, out Tree<int> cT); var copiesVals = NestGh2Util.AllOr(cT, 1);
            access.GetTree(4, out Tree<double> oT); double offset = NestGh2Util.First(oT, 0.0);
            access.GetTree(ROTATIONS_INPUT, out Tree<int> rT); var rotVals = NestGh2Util.AllOr(rT, 0);
            // Each brep is ONE part: its boundary loops (outer + holes) are kept together (hard_coded_input).
            var grouped = new List<Curve[]>();
            foreach (var brep in breps)
            {
                if (brep == null) continue;
                var loops = ComponentGeoUtil.BrepLoops(brep).Where(c => c != null && c.IsClosed).ToArray();
                if (loops.Length > 0) grouped.Add(loops);
            }
            if (grouped.Count == 0) return;

            // Every Attributes port (base "Attributes" at index 6 + any +/- ports at 7+); skip Rotations (index 5).
            // Each gets a stable PORT INDEX by position (base = 0, "Attributes 2" = 1, ...) for {part; port} output.
            var attrTrees = new List<Tree<GeometryBase>>();
            var attrPorts = new List<int>();
            int attrPortCount = 0;
            for (int ai = 5; ai < Parameters.InputCount; ai++)
            {
                if (ai == ROTATIONS_INPUT) continue;
                int portIdx = attrPortCount++;
                if (access.GetTree(ai, out Tree<GeometryBase> at) && at != null && at.LeafCount > 0)
                    { attrTrees.Add(at); attrPorts.Add(portIdx); }
            }
            if (attrPortCount < 1) attrPortCount = 1;

            // Breps are a flat LIST, so attributes match by structure (no per-part paths): a flat list of N
            // attributes maps one-per-part, an N-branch tree maps one branch per part, anything else is
            // ignored with a warning (see NestGh2Attr).
            List<GeometryBase[]> attrsPerPart = null;
            List<int[]> attrPortsPerPart = null;
            if (attrTrees.Count > 0)
            {
                attrsPerPart = NestGh2Attr.Match(grouped.Count, null, attrTrees, attrPorts, out attrPortsPerPart, out bool attrMismatch);
                if (attrMismatch)
                    access.AddWarning("Attributes", "Tree Branches don't match, attributes will be ignored.");
            }

            // Per-part copies, cycled like the main Geometry component (one value applies to all; a list maps
            // one-per-part). Was First(cT) before -> only the first value was used, so a copies LIST never
            // duplicated per part.
            var copiesList = new List<int>(grouped.Count);
            for (int i = 0; i < grouped.Count; i++) { int c = copiesVals[i % copiesVals.Count]; copiesList.Add(c < 1 ? 1 : c); }
            var rotationsList = new List<int>(grouped.Count);
            for (int i = 0; i < grouped.Count; i++) { int rv = rotVals[i % rotVals.Count]; rotationsList.Add(rv < 0 ? 0 : rv); }
            var geo = nest_geo_util.geo_to_nest_geo(grouped, copiesList, new List<double> { simplify, hull ? 1.0 : 0.0 }, attrsPerPart, hard_coded_input: true, rotations: rotationsList, attribute_ports: attrPortsPerPart, attribute_port_count: attrPortCount);
            if (offset != 0) geo.offset_nesting_boundary(offset);   // outer grows / holes shrink on the NFP boundary only
            access.SetItem(0, geo);
            var borders = new List<Curve>();
            foreach (var grp in geo.boundary_sorted) foreach (var tup in grp) if (tup.Item2 != null && tup.Item2.Count >= 2) borders.Add(tup.Item2.ToNurbsCurve());
            access.SetTwig(1, borders.ToArray());
        }

        // ---- variable +/- Attributes input ports (same as the main Geometry component) ----
        public override bool CanCreateParameter(Side side, int index) => side == Side.Input && index >= FIXED_INPUTS;
        public override bool CanRemoveParameter(Side side, int index) => side == Side.Input && index >= FIXED_INPUTS;
        public override void DoCreateParameter(Side side, int index, Grasshopper2.Undo.ActionList undo)
        {
            if (side != Side.Input) return;
            var p = new Grasshopper2.Parameters.Standard.GenericParameter(
                "Attributes", "A", "Extra geometry carried with the matching part branch.", Access.Tree);
            try { p.Requirement = Requirement.MayBeMissing; } catch { }
            Parameters.AddInput(p, index);
        }
        public override void VariableParameterMaintenance()
        {
            for (int i = FIXED_INPUTS; i < Parameters.InputCount; i++)
            {
                var p = Parameters.Input(i);
                if (p == null) continue;
                int n = i - FIXED_INPUTS + 2;
                try { p.ModifyNameAndInfo("Attributes " + n, "Extra geometry for the matching part branch (merged with the other Attributes ports)."); } catch { }
            }
        }
    }

    // Shared geometry helper (Brep -> outline curves), mirroring nest_lib.rhino_conversions.BrepLoops behaviour.
    internal static class ComponentGeoUtil
    {
        public static List<Curve> BrepLoops(Brep b)
        {
            var result = new List<Curve>();
            try
            {
                var pls = nest_lib.rhino_conversions.BrepLoops(b);
                if (pls != null) foreach (var pl in pls) if (pl != null && pl.IsValid && pl.Count >= 4) result.Add(pl.ToNurbsCurve());
            }
            catch { }
            return result;
        }
    }
}
