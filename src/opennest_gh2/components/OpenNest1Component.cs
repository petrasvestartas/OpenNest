using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;

namespace opennest_gh2.components
{
    // GH2 port of GH1 "OpenNest1" (raw-input NFP). Synchronous v1: takes closed curves for sheets + parts,
    // builds nest_sheets/nest_geo, runs nest_lib.rhino_example, and outputs placed geometry/transforms.
    [IoId("30400898-3a5a-434f-a703-864b9309d79e")]
    public class OpenNest1Component : Component
    {
        private static readonly object s_engineLock = new object();   // serialize the process-global nfp engine
        public OpenNest1Component() : base(new Nomen("OpenNest1", "No-fit-polygon nesting from raw closed curves.", "OpenNest", "Nest"))
        {
            Threading = ThreadingState.SingleThreaded;
        }
        public OpenNest1Component(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("opennest.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddCurve("Sheets", "Sheets", "Closed sheet outline curves.", Access.Twig);
            inputs.AddCurve("Geo", "Geo", "Closed part curves to nest.", Access.Twig);
            inputs.AddNumber("Spacing", "Spacing", "Gap kept between placed parts.", Access.Item, Requirement.MayBeMissing).Set(1.0);
            inputs.AddInteger("Placement", "Placement", "0 Box, 1 Gravity, 2 Squeeze, 3 Bottom-Left.", Access.Item, Requirement.MayBeMissing).Set(1);
            inputs.AddNumber("Tolerance", "Tolerance", "Curve simplification tolerance.", Access.Item, Requirement.MayBeMissing).Set(0.1);
            inputs.AddInteger("Rotations", "Rotations", "Orientation angles per part.", Access.Item, Requirement.MayBeMissing).Set(4);
            inputs.AddInteger("Iterations", "Iterations", "GA generations to evolve.", Access.Item, Requirement.MayBeMissing).Set(6);
            inputs.AddInteger("Seed", "Seed", "Random seed.", Access.Item, Requirement.MayBeMissing).Set(1);
            inputs.AddBoolean("Reset", "Reset", "Set true to clear the outputs.", Access.Item, Requirement.MayBeMissing).Set(false);
            inputs.AddBoolean("Run", "Run", "Set true to nest.", Access.Item, Requirement.MayBeMissing).Set(true);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Sheets", "Sheets", "Sheet outlines used for nesting.", Access.Twig);
            outputs.AddGeneric("Geo", "Geo", "Nested parts placed on sheets.", Access.Twig);
            outputs.AddInteger("ID", "ID", "Part (group) id.", Access.Twig);
            outputs.AddTransform("Transform", "Transform", "Placement transform per part.", Access.Twig);
            outputs.AddInteger("IDS", "IDS", "Sheet id per part.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            access.GetItem(8, out bool reset);
            access.GetItem(9, out bool run);
            if (reset) { access.AddRemark("Reset", "Cleared. Set Reset = false."); return; }
            if (!run) { access.AddRemark("Stopped", "Set Run = true."); return; }
            if (!access.GetItemArray(0, out Curve[] sheetCrv) || sheetCrv == null || sheetCrv.Length == 0) { access.AddError("No sheets", "Connect closed sheet curves."); return; }
            if (!access.GetItemArray(1, out Curve[] partCrv) || partCrv == null || partCrv.Length == 0) { access.AddError("No parts", "Connect closed part curves."); return; }
            access.GetItem(2, out double spacing); access.GetItem(3, out int placement);
            access.GetItem(4, out double tol); access.GetItem(5, out int rotations);
            access.GetItem(6, out int iterations); access.GetItem(7, out int seed);

            var helper = new nest_geo();
            double diag = NestGh2Util.Diagonal(partCrv.Concat(sheetCrv).Where(c => c != null)); double seg = diag > 0 ? diag * 0.01 : 1.0;

            var plinesList = sheetCrv.Where(c => c != null && c.IsClosed).Select(c => helper.curve_to_polyline(c, seg)).Where(p => p != null && p.Count >= 4).Select(p => new List<Polyline> { p }).ToList();
            if (plinesList.Count == 0) { access.AddError("No sheets", "Sheets must be closed."); return; }
            double g = diag * 0.03;
            // AUTO-OVERFLOW: duplicate the sheet set to the right so parts that don't fit spill onto copies,
            // sized to the part area (min 3, max 40) — matches the OpenNest Rhino command. No need to array
            // sheets by hand.
            {
                var setBB = BoundingBox.Empty;
                foreach (var s in plinesList) foreach (var pl in s) setBB.Union(pl.BoundingBox);
                double setW = (setBB.IsValid ? (setBB.Max.X - setBB.Min.X) : 0) + g;
                double partAreaSum = 0;
                foreach (var c in partCrv) { if (c == null) continue; var b = c.GetBoundingBox(false); if (b.IsValid) partAreaSum += (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y); }
                double sheetAreaSum = 0;
                foreach (var s in plinesList) if (s.Count > 0) { var b = s[0].BoundingBox; sheetAreaSum += (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y); }
                int sheetCopies = Math.Min(40, Math.Max(3, (int)Math.Ceiling(partAreaSum * 1.8 / Math.Max(1.0, sheetAreaSum)) + 2));
                var baseSheets = plinesList.ToList();
                for (int copy = 1; copy < sheetCopies && setW > 1e-6; copy++)
                    foreach (var s in baseSheets)
                        plinesList.Add(s.Select(pl => { var p = new Polyline(pl); p.Transform(Transform.Translation(copy * setW, 0, 0)); return p; }).ToList());
            }
            var sheets = new nest_sheets(plinesList, new List<double> { g, g }, new List<int>(), plinesList.Count);

            var grouped = partCrv.Where(c => c != null && c.IsClosed).Select(c => new[] { c }).ToList();
            // hard_coded_input: TRUE — each input curve is its OWN no-hole part (Curve[1]); do NOT run the global
            // containment pass that would auto-pair a curve sitting inside another as a hole. (This component takes
            // curves only, so it never makes holes — holes come from surface input elsewhere.) Also fixes the wrong
            // transforms that resulted when a separate curve was swallowed as a hole and lost its own placement.
            var geo = nest_geo_util.geo_to_nest_geo(grouped, Enumerable.Repeat(1, grouped.Count).ToList(), new List<double> { seg, 0 }, null, hard_coded_input: true);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0) { access.AddError("No parts", "Could not extract part boundaries."); return; }

            // rhino_example parameters[0..8]: rotations, wiggle, placement, spacing, seed, curveTol, mutation, population, time.
            var parameters = new List<double> { rotations, 0, placement, spacing, seed, tol, 10, 10, 0 };
            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, iterations < 1 ? 1 : iterations);
            nest.ExactNfp = 1;
            lock (s_engineLock)
                nest.static_solver(ref geo);

            int nsheet = 0; while (nsheet < sheets.sheets.Length && sheets.sheets[nsheet] != null && sheets.sheets[nsheet].Length > 0) nsheet++;
            var sheetOut = new List<Curve>();
            for (int s = 0; s < nsheet; s++) foreach (var pl in sheets.sheets[s]) if (pl != null) sheetOut.Add(pl.ToNurbsCurve());

            var placed = new List<GeometryBase>(); var xforms = new List<Transform>(); var ids = new List<int>(); var sids = new List<int>();
            for (int i = 0; i < geo.boundary_sorted.Count; i++)
            {
                var tlist = (geo.xforms != null && i < geo.xforms.Count) ? geo.xforms[i] : null; if (tlist == null) continue;
                var slist = (nest.output_polygon_sheet_ids != null && i < nest.output_polygon_sheet_ids.Count) ? nest.output_polygon_sheet_ids[i] : null;
                for (int k = 0; k < tlist.Count; k++)
                {
                    var x = tlist[k];
                    xforms.Add(x); ids.Add(i);
                    // Sheet id from the SOLVER's actual placement (-1 = did not fit), NOT a bbox-containment guess —
                    // so an unplaced part left at its original location (identity transform) is reported -1 even if
                    // it happens to sit inside a sheet's bounding box.
                    sids.Add((slist != null && k < slist.Count) ? slist[k] : -1);
                    foreach (var gi in geo.geometry_sorted[i]) { var ge = geo.geometry[gi].Duplicate(); ge.Transform(x); placed.Add(ge); }
                }
            }

            access.SetTwig(0, sheetOut.ToArray());
            access.SetTwig(1, placed.ToArray());
            access.SetTwig(2, ids.ToArray());
            access.SetTwig(3, xforms.ToArray());
            access.SetTwig(4, sids.ToArray());
        }
    }
}
