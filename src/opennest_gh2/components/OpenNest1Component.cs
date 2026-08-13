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
            inputs.AddNumber("Spacing", "Spacing", "Gap to keep between placed parts AND from the sheet edge.\nApplied as a nesting offset directly to the parts and sheets (this component takes raw curves, not nest_geo/nest_sheets), so it stands in for the Geometry/Sheets Offset inputs. The ORIGINAL geometry is still what gets output.", Access.Item, Requirement.MayBeMissing).Set(1.0);
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
            outputs.AddCurve("Sheets", "Sheets", "The sheets as supplied — Spacing insets the nesting container internally but never the reported outline.", Access.Twig);
            outputs.AddGeneric("Geo", "Geo", "The ORIGINAL parts moved onto the sheets (same curves in, same curves out — only the position changes). Spacing is applied to a SEPARATE nesting outline, so it shows up as the gap BETWEEN parts and never as a fatter curve.", Access.Twig);
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
            // Spacing is a GAP, so a negative value has no meaning: shrinking the parts / growing the sheet
            // would let placements overlap. Clamp before either offset call (GH1 parity, process_inputs).
            if (spacing < 0) spacing = 0;

            var helper = new nest_geo();
            double diag = NestGh2Util.Diagonal(partCrv.Concat(sheetCrv).Where(c => c != null)); double seg = diag > 0 ? diag * 0.01 : 1.0;

            // sheet_to_polylines, not curve_to_polyline: a SHEET is a container, so its ring must be INSCRIBED
            // in the real sheet (a chord ring that bulges out over a concave stretch would let parts be placed
            // off the material). One curve per sheet here, so each call returns exactly one outer ring.
            var plinesList = sheetCrv.Where(c => c != null && c.IsClosed).Select(c => helper.sheet_to_polylines(new Curve[] { c }, seg, false)).Where(l => l.Count > 0).ToList();
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

            // The sheet outlines EXACTLY as supplied, captured off sheets.sheets BEFORE the Spacing inset
            // below (the ctor sorts each set largest-ring-first and lays the auto-overflow copies out, so this
            // is 1:1 with the solver's sheet indices). The inset is an INTERNAL nesting container: letting it
            // reach this output would hand the user back a sheet Spacing/2 smaller than the one they drew.
            // (GH1 parity: component_nest1.sheets_unoffset.)
            var sheetOut = new List<Curve>();
            for (int s = 0; s < sheets.sheets.Length; s++)
            {
                if (sheets.sheets[s] == null || sheets.sheets[s].Length == 0) break;
                foreach (var pl in sheets.sheets[s]) if (pl != null) sheetOut.Add(pl.ToNurbsCurve());
            }

            // OpenNest1 takes RAW curves — it has no upstream Geometry/Sheets component to offset for it (the
            // ONE exception to "offsetting happens at the geometry and sheet level", which is how OpenNest2 and
            // OpenNestCollision work). So Spacing is applied HERE, exactly as GH1's component_nest1 does:
            // the sheet boundary insets by Spacing/2 (outer shrinks, holes grow) and each part's NESTING
            // outline grows by Spacing/2, which leaves a full Spacing gap between parts AND from the sheet
            // edge. The native solver's own spacing is then forced to 0 (see `parameters` below) so the gap is
            // applied ONCE — NestingContext::init would otherwise dilate the already-grown outlines a second
            // time via offsetTree.
            //
            // A ring the offset cannot handle keeps its ORIGINAL size, i.e. that sheet silently contributes no
            // setback at all. Since the offset is now the ONLY source of spacing, that is an invisible
            // zero-gap — the McNeel 221208 "spacing wont transform" symptom — so say it out loud.
            if (spacing > 0)
            {
                sheets.offset_sheet_boundary(spacing * 0.5);
                if (sheets.last_offset_failures > 0)
                    access.AddWarning("Spacing", sheets.last_offset_failures + " of " + sheets.last_offset_rings +
                        " sheet outline(s) could not be inset by Spacing/2 — those sheets give parts NO setback from the edge. " +
                        "Usually a degenerate ring, or an inset that consumes it; try a smaller Spacing or a cleaner sheet. " +
                        "(The sheet set is auto-copied for overflow, so one bad sheet is counted once per copy.)");
            }

            var grouped = partCrv.Where(c => c != null && c.IsClosed).Select(c => new[] { c }).ToList();
            // hard_coded_input: TRUE — each input curve is its OWN no-hole part (Curve[1]); do NOT run the global
            // containment pass that would auto-pair a curve sitting inside another as a hole. (This component takes
            // curves only, so it never makes holes — holes come from surface input elsewhere.) Also fixes the wrong
            // transforms that resulted when a separate curve was swallowed as a hole and lost its own placement.
            //
            // `seg` is POSITIVE, so parts take nest_geo's curve_to_polyline_unfillet branch (fillets replaced by the
            // sharp corner they were cut from, arcs sampled by length). That branch used to SKIP apply_ring_roles,
            // which made this component's outlines inscribed with no allowance at all: at the seg an R=100 circle
            // gets here (14.14 for a 1414-diagonal input set) it came out a 30-gon that the true circle escapes by
            // 0.547810, i.e. 1.0956 of interpenetration between two copies at Spacing 0. nest_geo now applies the
            // role allowance to every branch, so this is covered — see the comment on nest_geo.apply_ring_roles.
            var geo = nest_geo_util.geo_to_nest_geo(grouped, Enumerable.Repeat(1, grouped.Count).ToList(), new List<double> { seg, 0 }, null, hard_coded_input: true);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0) { access.AddError("No parts", "Could not extract part boundaries."); return; }

            // Grow each part's NESTING outline (boundary_sorted Item2) by Spacing/2 — paired with the sheet
            // inset above. geometry[] / Item4 stay the ORIGINAL curve, so the Geo output below is the user's
            // own geometry and the gap shows up as space BETWEEN parts, never as a fatter curve.
            if (spacing > 0)
            {
                geo.offset_nesting_boundary(spacing * 0.5);
                if (geo.last_offset_failures > 0)
                    access.AddWarning("Spacing", geo.last_offset_failures + " of " + geo.last_offset_rings +
                        " part outline(s) could not be grown by Spacing/2 — those parts get NO gap. " +
                        "Usually a ring with fewer than 3 corners, or a hole small enough that Spacing/2 closes it; " +
                        "try a smaller Spacing or clean up the outline.");
            }

            // rhino_example parameters[0..8]: rotations, wiggle, placement, spacing, seed, curveTol, mutation, population, time.
            // spacing is 0 ON PURPOSE — the gap is already baked into the part/sheet outlines above.
            var parameters = new List<double> { rotations, 0, placement, 0, seed, tol, 10, 10, 0 };
            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, iterations < 1 ? 1 : iterations);
            nest.ExactNfp = 1;
            lock (s_engineLock)
                nest.static_solver(ref geo);

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
