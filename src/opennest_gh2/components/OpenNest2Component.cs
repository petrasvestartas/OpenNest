using System;
using System.Collections.Generic;
using System.Threading;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;
using opennest_2;            // NestOption
using opennest_gh2.attributes;

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "OpenNest2" component (NFP + genetic algorithm, nfp_nest.dll via nest_lib.rhino_example).
    // Same inputs (Sheets, Geometry, Iterations) and the nine embedded options drawn by NestAttributes.
    [IoId("55f51cca-4e86-4498-8fce-38abbe131c8c")]
    public class OpenNest2Component : NestComponentBase
    {
        private readonly List<NestOption> _options = BuildOptions();

        // Serialize all native nfp solves across instances (process-global engine state).
        private static readonly object s_engineLock = new object();

        // The solver of the in-flight solve, so Stop/ESC/cancel can ask it to halt (keeps best-so-far).
        private volatile nest_lib.rhino_example _activeNest;

        public OpenNest2Component()
            : base(new Nomen("OpenNest2", "NFP + genetic algorithm nesting (nfp_nest).", "OpenNest", "Nest")) { }

        public OpenNest2Component(IReader reader) : base(reader) { }

        public override IReadOnlyList<NestOption> Options => _options;

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("opennest_2.svg");

        protected override void RequestCancel() { var n = _activeNest; if (n != null) try { n.StopRequested = true; } catch { } }

        // Exact GH1 option set (component_nest2.cs BuildOptions).
        private static List<NestOption> BuildOptions() => new List<NestOption>
        {
            NestOption.Number("num_of_rotations", "Rotations", 8, 1, 3600, 0, "Orientations each part may try (360/n)."),
            NestOption.Choice("placement_type", "Packing", new[] { "Box", "Gravity", "Squeeze", "Bottom Left" }, new[] { "0", "1", "2", "3" }, 1, "Placement strategy. Bottom Left = pack each part into the lowest-then-leftmost feasible spot."),
            NestOption.Number("spacing", "Spacing", 0.0, 0, 1000, 2, "Gap kept between parts (model units)."),
            NestOption.Number("seed", "Seed", 30, 0, 100000, 0, "Random seed (same seed = same result)."),
            NestOption.Number("mutation", "Mutation", 10, 0, 100, 0, "GA mutation rate."),
            NestOption.Number("population", "Population", 10, 1, 100000, 0, "GA population size."),
            NestOption.Choice("all_rotations", "All Rotations", new[] { "Off", "On" }, new[] { "0", "1" }, 1, "Try every orientation per part for tightest packing (capped at 8)."),
            NestOption.Choice("element_holes", "Element Holes", new[] { "Off", "Fill" }, new[] { "0", "1" }, 1, "Nest smaller parts INSIDE larger parts' holes."),
            NestOption.Text("font", "Sheet Font", "MecSoft_Font-1 1", "Sheet-number label: font name + text size."),
        };

        private double OptNum(string key, double def) { foreach (var o in _options) if (o.Key == key) return o.Value; return def; }
        private int OptToken(string key, int def)
        { foreach (var o in _options) if (o.Key == key) { int i = Math.Min(Math.Max(o.SelectedIndex, 0), o.ChoiceTokens.Count - 1); if (int.TryParse(o.ChoiceTokens[i], out int t)) return t; } return def; }

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Sheets", "Sheets", "From OpenNest tab, use component Sheets.");
            inputs.AddGeneric("Geometry", "Geometry", "From OpenNest tab, use component Geometry.");
            inputs.AddInteger("Iterations", "Iterations", "GA generations to evolve (~10-40 typical).", Access.Item, Requirement.MayBeMissing).Set(10);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Sheets", "Sheets", "Polylines representing sheets.", Access.Twig);
            outputs.AddCurve("Borders", "Borders", "Placed part outline curves.", Access.Twig);
            outputs.AddGeneric("All Geometry", "All Geo", "All placed geometry, grouped per sheet.", Access.Twig);
            outputs.AddTransform("Transforms", "Transforms", "Move/rotate transform placing each part.", Access.Twig);
            outputs.AddInteger("Sheet Id", "Sheet Id", "Sheet index each part landed on.", Access.Twig);
            outputs.AddCurve("Sheet Txt", "Sheet Txt", "Sheet-number labels as text curves.", Access.Twig);
            outputs.AddGeneric("Attributes", "Attributes", "Attribute geometry carried with each part.", Access.Twig);
        }

        // snapshotted solve state (Prepare on UI thread -> SolveCore on worker -> Assemble on UI thread)
        private nest_sheets _sheets;
        private nest_geo _geo;

        // PASS 1 (UI thread): read inputs, build the GA solver, wire the live-preview poll.
        protected override bool Prepare(IDataAccess access)
        {
            if (!access.GetItem(0, out nest_sheets sheets) || sheets == null)
            { access.AddError("No sheets", "Connect the OpenNest Sheets output."); return false; }
            if (!access.GetItem(1, out nest_geo geo) || geo == null)
            { access.AddError("No geometry", "Connect the OpenNest Geometry output."); return false; }
            access.GetItem(2, out int iterations);
            int totalGen = iterations < 1 ? 1 : iterations;

            // rhino_example parameters[0..8]: rotations, wiggle, placement, spacing, seed, curveTol, mutation, population, time.
            var parameters = new List<double>
            {
                OptNum("num_of_rotations", 8), 0, OptToken("placement_type", 1), OptNum("spacing", 0),
                OptNum("seed", 30), 1.0, OptNum("mutation", 10), OptNum("population", 10), 0
            };

            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, totalGen);
            nest.Engine = "cpp";
            nest.ExactNfp = 1;
            nest.TryAllRotations = OptToken("all_rotations", 1);
            nest.UseHoles = OptToken("element_holes", 1);
            _sheets = sheets; _geo = geo; _activeNest = nest;
            // Live per-generation progress + tightening preview (orange borders) from the managed GA solver.
            SetPoll(() => new NestTick
            {
                Done = nest.CurrentGeneration,
                Total = totalGen,
                Status = "gen " + nest.CurrentGeneration + " / " + totalGen + "   fit " + nest.CurrentFitness.ToString("F3") + "   (ESC = stop)",
                Borders = nest.LiveBorders,
                Sheets = nest.LiveSheets
            });
            return true;
        }

        // BACKGROUND THREAD: the managed GA solve (process-global engine -> serialize across instances).
        protected override void SolveCore()
        {
            var geo = _geo;
            lock (s_engineLock) _activeNest.static_solver(ref geo);   // writes geo.xforms
            _geo = geo;
        }

        // PASS 2 (UI thread): assemble + publish placed geometry, cache it for no-solve re-emits.
        protected override void Assemble(IDataAccess access)
        {
            var geo = _geo; var sheets = _sheets;
            if (_activeNest == null || geo == null || sheets == null) return;

            // sheet world bboxes (for sheet-id assignment)
            int nsheet = 0;
            while (nsheet < sheets.sheets.Length && sheets.sheets[nsheet] != null && sheets.sheets[nsheet].Length > 0) nsheet++;
            var sheetBox = new BoundingBox[nsheet];
            for (int s = 0; s < nsheet; s++) sheetBox[s] = sheets.sheets[s][0].BoundingBox;

            var sheetCurves = new List<Curve>();
            for (int s = 0; s < nsheet; s++)
                foreach (var pl in sheets.sheets[s])
                    if (pl != null && pl.Count >= 2) sheetCurves.Add(pl.ToNurbsCurve());

            var borders = new List<Curve>();
            var placed = new List<GeometryBase>();
            var xforms = new List<Transform>();
            var ids = new List<int>();
            var attributes = new List<GeometryBase>();

            int nGroups = geo.boundary_sorted.Count;
            for (int i = 0; i < nGroups; i++)
            {
                var tlist = (geo.xforms != null && i < geo.xforms.Count) ? geo.xforms[i] : null;
                if (tlist == null) continue;
                for (int j = 0; j < tlist.Count; j++)
                {
                    Transform x = tlist[j];
                    xforms.Add(x);
                    Curve firstBorder = null;
                    foreach (var tup in geo.boundary_sorted[i])
                    {
                        // Borders = the ORIGINAL outline (Item4), transformed — never the simplified collision
                        // boundary (Item2). Placed result is always the true input shape (Simplify, default 0,
                        // only affects internal collision speed). Fall back to the boundary polyline if null.
                        Curve crv = tup.Item4 != null ? tup.Item4.DuplicateCurve() : tup.Item2.ToNurbsCurve();
                        if (crv == null) continue;
                        crv.Transform(x); borders.Add(crv);
                        if (firstBorder == null) firstBorder = crv;
                    }
                    ids.Add(SheetOf(firstBorder, sheetBox));
                    foreach (var gi in geo.geometry_sorted[i])
                    {
                        var g = geo.geometry[gi].Duplicate(); g.Transform(x); placed.Add(g);
                        if (geo.geometry_attributes != null && gi < geo.geometry_attributes.Count && geo.geometry_attributes[gi] != null)
                            foreach (var att in geo.geometry_attributes[gi]) { var a = att.Duplicate(); a.Transform(x); attributes.Add(a); }
                    }
                }
            }

            var sheetArr = sheetCurves.ToArray();
            var borderArr = borders.ToArray();
            var placedArr = placed.ToArray();
            var xformArr = xforms.ToArray();
            var idArr = ids.ToArray();
            var attrArr = attributes.ToArray();
            Action<IDataAccess> emit = a =>
            {
                a.SetTwig(0, sheetArr);
                a.SetTwig(1, borderArr);
                a.SetTwig(2, placedArr);
                a.SetTwig(3, xformArr);
                a.SetTwig(4, idArr);
                a.SetTwig(5, new Curve[0]);
                a.SetTwig(6, attrArr);
            };
            emit(access);
            CacheResult(emit);
            _activeNest = null;

            var finalWires = new List<Curve>(sheetArr.Length + borderArr.Length);
            finalWires.AddRange(sheetArr); finalWires.AddRange(borderArr);
            SetFinalWires(finalWires);
        }

        private static int SheetOf(Curve border, BoundingBox[] sheetBox)
        {
            if (border == null) return -1;
            Point3d c = border.GetBoundingBox(false).Center;
            for (int s = 0; s < sheetBox.Length; s++)
                if (sheetBox[s].IsValid && sheetBox[s].Contains(c)) return s;
            return -1;
        }
    }
}
