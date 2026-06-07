using System;
using System.Collections.Generic;
using Grasshopper2.Components;
using Grasshopper2.Doc;
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
    public class OpenNest2Component : Component, attributes.INestOptionsHost
    {
        private readonly List<NestOption> _options = BuildOptions();

        public OpenNest2Component()
            : base(new Nomen("OpenNest2", "NFP + genetic algorithm nesting (nfp_nest).", "OpenNest", "Nest"))
        {
            // nfp_nest engine keeps process-global state; SingleThreaded + a static lock serialize solves.
            Threading = ThreadingState.SingleThreaded;
        }

        // Serialize all native nfp solves across instances (process-global engine state).
        private static readonly object s_engineLock = new object();

        public OpenNest2Component(IReader reader) : base(reader) { }

        public bool Run { get; set; } = true;
        public bool OptionsExpanded { get; set; } = false;
        public IReadOnlyList<NestOption> Options => _options;

        protected override IAttributes CreateAttributes() => new NestAttributes(this);
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("opennest_2.svg");

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

        protected override void Process(IDataAccess access)
        {
            if (!Run) { access.AddRemark("Stopped", "Press Run on the component to nest."); return; }
            if (!access.GetItem(0, out nest_sheets sheets) || sheets == null)
            { access.AddError("No sheets", "Connect the OpenNest Sheets output."); return; }
            if (!access.GetItem(1, out nest_geo geo) || geo == null)
            { access.AddError("No geometry", "Connect the OpenNest Geometry output."); return; }
            access.GetItem(2, out int iterations);

            // rhino_example parameters[0..8]: rotations, wiggle, placement, spacing, seed, curveTol, mutation, population, time.
            var parameters = new List<double>
            {
                OptNum("num_of_rotations", 8), 0, OptToken("placement_type", 1), OptNum("spacing", 0),
                OptNum("seed", 30), 1.0, OptNum("mutation", 10), OptNum("population", 10), 0
            };

            var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, iterations < 1 ? 1 : iterations);
            nest.Engine = "cpp";
            nest.ExactNfp = 1;
            nest.TryAllRotations = OptToken("all_rotations", 1);
            nest.UseHoles = OptToken("element_holes", 1);
            lock (s_engineLock)
                nest.static_solver(ref geo);   // writes geo.xforms (one transform list per part group)

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
                        var pl = new Polyline(tup.Item2); pl.Transform(x);
                        var crv = pl.ToNurbsCurve(); borders.Add(crv);
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

            access.SetTwig(0, sheetCurves.ToArray());
            access.SetTwig(1, borders.ToArray());
            access.SetTwig(2, placed.ToArray());
            access.SetTwig(3, xforms.ToArray());
            access.SetTwig(4, ids.ToArray());
            access.SetTwig(5, new Curve[0]);
            access.SetTwig(6, attributes.ToArray());
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
