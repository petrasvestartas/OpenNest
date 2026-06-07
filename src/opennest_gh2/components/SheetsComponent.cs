using System.Collections.Generic;
using Grasshopper2.Components;
using Grasshopper2.Data;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "Sheets" component. Builds an OpenNest nest_sheets from closed sheet outline
    // curves (one sheet per curve in v1) and arrays copies. Reuses nest_sheets verbatim.
    [IoId("7a1e9c20-1d44-4b8f-9c33-2e6a5b0d1f01")]
    public class SheetsComponent : Component
    {
        public SheetsComponent()
            : base(new Nomen("Sheets", "Define nesting sheets from closed outline curves", "OpenNest", "Input")) { }

        public SheetsComponent(IReader reader) : base(reader) { }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("sheet.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            // Inputs mirror the current GH1 Sheets component 1:1 (so the upgrader maps directly). All Access.Tree
            // so the component runs ONCE and builds exactly ONE nest_sheets (each branch = one sheet: outer + holes).
            inputs.AddCurve("Polylines", "P", "Closed sheet outline polylines; one branch per sheet (outer + optional holes).", Access.Tree);
            inputs.AddNumber("Gap", "G", "Gap between arrayed sheets.", Access.Tree, Requirement.MayBeMissing).Set(0.1);
            inputs.AddInteger("Rows", "R", "Sheets per row before partitioning (0/empty = one row).", Access.Tree, Requirement.MayBeMissing);
            inputs.AddInteger("Copies", "C", "Total sheets to array from the input.", Access.Tree, Requirement.MayBeMissing).Set(100);
            inputs.AddNumber("Offset", "O", "Inward MARGIN for nesting (model units; 0 = off). Parts keep this setback from the sheet edge; any sheet holes grow by it.", Access.Tree, Requirement.MayBeMissing).Set(0.0);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Sheets", "S", "OpenNest sheets (feed into a solver).");
            outputs.AddCurve("Polylines", "P", "Generated sheet outline polylines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetTree(0, out Tree<Curve> tree) || tree == null || tree.LeafCount == 0)
            { access.AddWarning("No sheets", "Connect closed sheet outline curves."); return; }
            access.GetTree(1, out Tree<double> gapT); double gap = NestGh2Util.First(gapT, 0.1);
            access.GetTree(2, out Tree<int> rowsT); var rows = NestGh2Util.AllOr(rowsT, 0); if (rows.Count == 1 && rows[0] == 0) rows = new List<int>();
            access.GetTree(3, out Tree<int> copiesT); int copies = NestGh2Util.First(copiesT, 100);
            access.GetTree(4, out Tree<double> offsetT); double offset = NestGh2Util.First(offsetT, 0.0);

            // One branch = one sheet (outer + holes). Use the input polylines DIRECTLY (no resampling),
            // exactly like GH1 component_sheets (TryGetPolyline). Build ONE nest_sheets from all branches.
            tree.ToArrays(out Curve[][] branches);
            var plinesList = new List<List<Polyline>>();
            foreach (var branch in branches)
            {
                if (branch == null || branch.Length == 0) continue;
                var plines = new List<Polyline>();
                foreach (var c in branch)
                {
                    if (c == null) continue;
                    if (c.TryGetPolyline(out Polyline pline) && pline != null && pline.Count >= 2)
                        plines.Add(pline);
                    else if (c.IsClosed)
                    {
                        // closed non-polyline curve -> fall back to a fine polyline approximation
                        var pc = c.ToPolyline(0, 1, 0.01, 0.01, 0, 0, 0, 0, true);
                        if (pc != null && pc.TryGetPolyline(out Polyline ap) && ap != null && ap.Count >= 2) plines.Add(ap);
                    }
                }
                if (plines.Count > 0) plinesList.Add(plines);
            }
            if (plinesList.Count == 0) { access.AddWarning("No closed sheet outlines", "Sheets need closed polyline curves."); return; }

            var sheets = new nest_sheets(plinesList, new List<double> { gap, gap }, rows, copies < 1 ? 1 : copies);
            // Inward MARGIN: per sheet, the OUTER loop (index 0) shrinks and holes grow, so placed parts keep the
            // setback (GH1 parity via the public nest_sheets helper, which uses the same offset_closed_polyline).
            if (offset != 0) sheets.offset_sheet_boundary(offset);

            access.SetItem(0, sheets);

            var outPolys = new List<Curve>();
            foreach (var s in sheets.sheets)
            {
                if (s == null) continue;
                foreach (var pl in s)
                    if (pl != null && pl.Count >= 2) outPolys.Add(pl.ToNurbsCurve());
            }
            access.SetTwig(1, outPolys.ToArray());
        }
    }
}
