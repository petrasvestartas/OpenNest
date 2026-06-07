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
            inputs.AddInteger("Rotations", "Rotations", "Orientation angles per part.", Access.Item, Requirement.MayBeMissing).Set(2);
            inputs.AddInteger("Iterations", "Iterations", "GA generations to evolve.", Access.Item, Requirement.MayBeMissing).Set(6);
            inputs.AddInteger("Seed", "Seed", "Random seed.", Access.Item, Requirement.MayBeMissing).Set(1);
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
            access.GetItem(8, out bool run);
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
            var sheets = new nest_sheets(plinesList, new List<double> { g, g }, new List<int>(), plinesList.Count);

            var grouped = partCrv.Where(c => c != null && c.IsClosed).Select(c => new[] { c }).ToList();
            var geo = nest_geo_util.geo_to_nest_geo(grouped, Enumerable.Repeat(1, grouped.Count).ToList(), new List<double> { seg, 0 }, null, hard_coded_input: false);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0) { access.AddError("No parts", "Could not extract part boundaries."); return; }

            // rhino_example parameters[0..8]: rotations, wiggle, placement, spacing, seed, curveTol, mutation, population, time.
            var parameters = new List<double> { rotations, 0, placement, spacing, seed, tol, 10, 10, 0 };
            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, iterations < 1 ? 1 : iterations);
            nest.Engine = "cpp"; nest.ExactNfp = 1;
            lock (s_engineLock)
                nest.static_solver(ref geo);

            int nsheet = 0; while (nsheet < sheets.sheets.Length && sheets.sheets[nsheet] != null && sheets.sheets[nsheet].Length > 0) nsheet++;
            var sheetBox = new BoundingBox[nsheet];
            var sheetOut = new List<Curve>();
            for (int s = 0; s < nsheet; s++) { sheetBox[s] = sheets.sheets[s][0].BoundingBox; foreach (var pl in sheets.sheets[s]) if (pl != null) sheetOut.Add(pl.ToNurbsCurve()); }

            var placed = new List<GeometryBase>(); var xforms = new List<Transform>(); var ids = new List<int>(); var sids = new List<int>();
            for (int i = 0; i < geo.boundary_sorted.Count; i++)
            {
                var tlist = (geo.xforms != null && i < geo.xforms.Count) ? geo.xforms[i] : null; if (tlist == null) continue;
                foreach (var x in tlist)
                {
                    xforms.Add(x); ids.Add(i);
                    var first = new Polyline(geo.boundary_sorted[i][0].Item2); first.Transform(x);
                    Point3d c = first.BoundingBox.Center; int sid = -1;
                    for (int s = 0; s < nsheet; s++) if (sheetBox[s].IsValid && sheetBox[s].Contains(c)) { sid = s; break; }
                    sids.Add(sid);
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
