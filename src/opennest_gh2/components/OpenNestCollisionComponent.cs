using System;
using System.Collections.Generic;
using Grasshopper2.Components;
using Grasshopper2.Doc;          // IAttributes
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using nest_rhino_lib;
using opennest_2;   // NpRun + NestOption (compiled into opennest_2.gha, bundled next to this .rhp)
using opennest_gh2.attributes;

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "OpenNestCollision" component (penetration-depth nesting, nest_physics.dll / np_nest).
    // Inputs match GH1 (Sheets, Geometry, Iterations); the eight options are drawn ON THE COMPONENT BODY by
    // NestAttributes (collapsible dropdown/type-in panel + Run button) using the exact GH1 NestOption model.
    [IoId("c3d5f9b7-4a28-4e03-9b16-2d8c0a1e3b03")]
    public class OpenNestCollisionComponent : Component, attributes.INestOptionsHost
    {
        // Exact GH1 option set (component_nest.cs BuildOptions), reused via the shared NestOption model.
        private readonly List<NestOption> _options = BuildCollisionOptions();

        public OpenNestCollisionComponent()
            : base(new Nomen("OpenNestCollision", "Penetration-depth nesting (nest_physics).", "OpenNest", "Nest")) { }

        public OpenNestCollisionComponent(IReader reader) : base(reader) { }

        // INestOptionsHost (on-canvas UI state)
        public bool Run { get; set; } = true;
        public bool OptionsExpanded { get; set; } = false;
        public IReadOnlyList<NestOption> Options => _options;

        protected override IAttributes CreateAttributes() => new NestAttributes(this);
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => NestIcons.Load("nest_collision.svg");

        private static List<NestOption> BuildCollisionOptions() => new List<NestOption>
        {
            NestOption.Number("num_of_rotations", "Rotations", 3600, 1, 3600, 0, "Orientations each part may try; more = tighter, slower."),
            NestOption.Number("seed", "Seed", 100, 0, 100000, 0, "Random seed; change it if a run spills to a 2nd sheet."),
            NestOption.Number("starts", "Starts", 1, 1, 64, 0, "Multi-start seeds; keeps densest, stops at first single-sheet."),
            NestOption.Choice("element_holes", "Element Holes", new[] { "Off", "Fill", "Fill First" }, new[] { "0", "1", "2" }, 1, "Nest small ELEMENTS into larger elements' holes. Fill = after the nest; Fill First = pre-pack into holes before nesting. (Sheet holes are always kept out automatically.)"),
            NestOption.Number("poles", "Poles", 48, 4, 64, 0, "Inscribed circles per part for collision tests; more = more accurate (cleaner pack), fewer = faster but can pack worse."),
            NestOption.Choice("compact", "Compact", new[] { "Off", "Bottom-Left", "Multi" }, new[] { "0", "1", "2" }, 1, "Post-pack tightening slide."),
            NestOption.Choice("fit", "Fit", new[] { "One sheet (max fill)", "All parts (fewest sheets)" }, new[] { "1", "0" }, 0, "One sheet = fill a SINGLE sheet as full as possible; parts that don't fit are placed OUTSIDE. All parts = use as many sheets as needed so nothing is left off."),
            NestOption.Text("font", "Sheet Font", "MecSoft_Font-1 1", "Sheet-number label: font name + text size."),
        };

        private int OptInt(string key, int def)
        { foreach (var o in _options) if (o.Key == key) return (int)Math.Round(o.Value); return def; }
        private int OptToken(string key, int def)
        {
            foreach (var o in _options) if (o.Key == key)
            { int i = Math.Min(Math.Max(o.SelectedIndex, 0), o.ChoiceTokens.Count - 1); if (int.TryParse(o.ChoiceTokens[i], out int t)) return t; }
            return def;
        }
        private string OptText(string key, string def)
        { foreach (var o in _options) if (o.Key == key) return o.TextValue ?? def; return def; }

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Sheets", "Sheets", "From OpenNest tab, use component Sheets.");
            inputs.AddGeneric("Geometry", "Geometry", "From OpenNest tab, use component Geometry.");
            inputs.AddInteger("Iterations", "Iterations", "Relaxation rounds; higher packs tighter but is slower. ~4000 = the tight all-on-one-sheet pack; lower for a quick rough preview.", Access.Item, Requirement.MayBeMissing).Set(4000);
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddCurve("Sheets", "Sheets", "Sheet outline polylines used for nesting.", Access.Twig);
            outputs.AddCurve("Borders", "Borders", "Placed part outline curves.", Access.Twig);
            outputs.AddGeneric("All Geometry", "All Geo", "All placed part geometry.", Access.Twig);
            outputs.AddTransform("Transforms", "Transforms", "Placement transforms for each part.", Access.Twig);
            outputs.AddInteger("Sheet Id", "Sheet Id", "Index of the sheet each part landed on.", Access.Twig);
            outputs.AddCurve("Sheet Txt", "Sheet Txt", "Sheet-number label curves.", Access.Twig);
            outputs.AddGeneric("Attributes", "Attributes", "Per-part attribute geometry.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!Run) { access.AddRemark("Stopped", "Press Run on the component to nest."); return; }

            if (!access.GetItem(0, out nest_sheets sheets) || sheets == null)
            { access.AddError("No sheets", "Connect the OpenNest Sheets output."); return; }
            if (!access.GetItem(1, out nest_geo geo) || geo == null)
            { access.AddError("No geometry", "Connect the OpenNest Geometry output."); return; }
            access.GetItem(2, out int iterations);

            int rotations = OptInt("num_of_rotations", 3600);
            int seed = OptInt("seed", 100);
            int starts = OptInt("starts", 1);
            int partHolesMode = OptToken("element_holes", 1);   // 0=Off 1=Fill 2=Fill First
            int poles = OptInt("poles", 48);
            bool compactOn = OptToken("compact", 1) != 0;        // Off=>false, Bottom-Left/Multi=>true
            int fitMode = OptToken("fit", 1);                    // One sheet=>1, All parts=>0

            var run = new NpRun();
            // NpRun.Flatten reads parameters positionally: [0]=rotations [1]=seed [2]=starts.
            run.Flatten(sheets, geo, new List<double> { rotations, seed, starts < 1 ? 1 : starts },
                        iterations < 1 ? 1 : iterations, partHolesMode, poles, compactOn, fitMode);
            run.Solve();
            run.Assemble();

            var sheetCurves = new List<Curve>();
            foreach (var s in run.output_sheets)
                foreach (var pl in s)
                    if (pl != null && pl.Count >= 2) sheetCurves.Add(pl.ToNurbsCurve());

            var borders = new List<Curve>();
            var placed = new List<GeometryBase>();
            var xforms = new List<Transform>();
            var ids = new List<int>();
            var attributes = new List<GeometryBase>();

            int nGroups = geo.boundary_sorted.Count;
            for (int i = 0; i < nGroups; i++)
            {
                var tlist = i < run.output_transforms.Count ? run.output_transforms[i] : null;
                var slist = i < run.output_polygon_sheet_ids.Count ? run.output_polygon_sheet_ids[i] : null;
                if (tlist == null) continue;
                for (int j = 0; j < tlist.Count; j++)
                {
                    Transform x = tlist[j];
                    xforms.Add(x);
                    ids.Add(slist != null && j < slist.Count ? slist[j] : -1);
                    foreach (var tup in geo.boundary_sorted[i])
                    {
                        var pl = new Polyline(tup.Item2); pl.Transform(x); borders.Add(pl.ToNurbsCurve());
                    }
                    foreach (var gi in geo.geometry_sorted[i])
                    {
                        var g = geo.geometry[gi].Duplicate(); g.Transform(x); placed.Add(g);
                        if (geo.geometry_attributes != null && gi < geo.geometry_attributes.Count && geo.geometry_attributes[gi] != null)
                            foreach (var att in geo.geometry_attributes[gi])
                            { var a = att.Duplicate(); a.Transform(x); attributes.Add(a); }
                    }
                }
            }

            access.SetTwig(0, sheetCurves.ToArray());
            access.SetTwig(1, borders.ToArray());
            access.SetTwig(2, placed.ToArray());
            access.SetTwig(3, xforms.ToArray());
            access.SetTwig(4, ids.ToArray());
            access.SetTwig(5, new Curve[0]);                 // Sheet Txt (font-rendered labels) — parity placeholder
            access.SetTwig(6, attributes.ToArray());
        }
    }
}
