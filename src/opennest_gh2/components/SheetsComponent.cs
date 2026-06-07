using System.Collections.Generic;
using Grasshopper2.Components;
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
            : base(new Nomen("Sheets", "Define nesting sheets from closed outline curves", "OpenNest", "Nest")) { }

        public SheetsComponent(IReader reader) : base(reader) { }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("sheet.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Sheets", "S", "Closed sheet outline curves (one sheet each).", Access.Twig);
            inputs.AddInteger("Copies", "C", "Total sheets to array from the input.", Access.Item, Requirement.MayBeMissing).Set(100);
            inputs.AddNumber("Gap", "G", "Gap between arrayed sheets.", Access.Item, Requirement.MayBeMissing).Set(0.1);
            inputs.AddNumber("Offset", "O", "Inward margin for nesting (0 = off).", Access.Item, Requirement.MayBeMissing).Set(0.0);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Sheets", "S", "OpenNest sheets (feed into a solver).");
            outputs.AddCurve("Polylines", "P", "Generated sheet outline polylines.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItemArray(0, out Curve[] curves) || curves == null || curves.Length == 0) return;
            access.GetItem(1, out int copies);
            access.GetItem(2, out double gap);
            access.GetItem(3, out double offset);

            double diag = NestGh2Util.Diagonal(curves);
            double seg = diag > 0 ? diag * 0.01 : 1.0;

            var helper = new nest_geo();
            var plinesList = new List<List<Polyline>>();
            foreach (var c in curves)
            {
                if (c == null || !c.IsClosed) continue;
                var pl = helper.curve_to_polyline(c, seg);
                if (pl != null && pl.Count >= 4) plinesList.Add(new List<Polyline> { pl });
            }
            if (plinesList.Count == 0) { access.AddWarning("No closed sheet outlines", "Sheets need closed curves."); return; }

            var sheets = new nest_sheets(plinesList, new List<double> { gap, gap }, new List<int>(), copies < 1 ? 1 : copies);
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
