using System;
using System.Collections.Generic;
using System.Globalization;

namespace opennest_2
{
    // Single source of truth for the two solvers' option sets. The solver components (GH1 + GH2) build their
    // on-canvas option rows from here, and the "Nest Options" components (GH1 + GH2) build their input lists
    // from here, so a new/changed option only ever has to be added in this file. ApplyTokens() lets a wired
    // "Options" input (list of "key value" strings — the exact format EmitToken() produces and the legacy
    // Options string input used) override the on-canvas values.
    public static class NestOptionCatalog
    {
        // OpenNest2 (NFP + genetic algorithm, nfp_nest.dll). ORDER IS LOAD-BEARING: component_nest2's
        // BuildOptionStrings() maps the numeric rows to rhino_example's parameters[0..8] by index;
        // all_rotations/element_holes are parsed by name; the font row must stay LAST.
        // 'spacing' is intentionally NOT here: gaps between parts are set upstream in the Geometry (part
        // offset) and Sheets (gap) components; only OpenNest1 takes an explicit spacing input. The engine's
        // spacing parameter is still emitted as a fixed 0 in BuildOptionStrings to keep the indices intact.
        public static List<NestOption> OpenNest2() => new List<NestOption>
        {
            NestOption.Number("num_of_rotations", "Rotations", 4, 1, 3600, 0, "Orientations each part may try (360/n). Default 4 = faster; raise it (e.g. 8) for tighter packing."),
            NestOption.Choice("placement_type", "Packing", new[] { "Box", "Gravity", "Squeeze", "Bottom Left" }, new[] { "0", "1", "2", "3" }, 1, "Placement strategy. Bottom Left = pack each part into the lowest-then-leftmost feasible spot."),
            NestOption.Number("seed", "Seed", 30, 0, 100000, 0, "Random seed (same seed = same result)."),
            NestOption.Number("mutation", "Mutation", 10, 0, 100, 0, "GA mutation rate."),
            NestOption.Number("population", "Population", 10, 1, 100000, 0, "GA population size."),
            NestOption.Choice("all_rotations", "All Rotations", new[] { "Off", "On" }, new[] { "0", "1" }, 1, "Try every orientation per part for tightest packing — C++ engine only (the C# engine ignores it; it uses 'Rotations'). Default ON; capped at 8 orientations so a large 'Rotations' value can't hang the solver."),
            NestOption.Choice("element_holes", "Element Holes", new[] { "Off", "Fill" }, new[] { "0", "1" }, 1, "Nest smaller parts INSIDE larger parts' holes."),
            NestOption.Text("font", "Sheet Font", "MecSoft_Font-1 1", "Sheet-number label: font name + text size."),
        };

        // OpenNestCollision (physics / penetration-depth solver, nest_physics.dll). ORDER IS LOAD-BEARING:
        // NpRun.Flatten maps the first numeric rows to GetP(0..2) by index (rotations, seed, starts);
        // element_holes/poles/compact/fit are parsed by name; the font row must stay LAST.
        public static List<NestOption> Collision() => new List<NestOption>
        {
            NestOption.Number("num_of_rotations", "Rotations", 3600, 1, 3600, 0, "Orientations each part may try; more = tighter, slower."),
            NestOption.Number("seed", "Seed", 100, 0, 100000, 0, "Random seed; change it if a run spills to a 2nd sheet."),
            NestOption.Number("starts", "Starts", 1, 1, 64, 0, "Multi-start seeds; keeps densest, stops at first single-sheet."),
            NestOption.Choice("element_holes", "Element Holes", new[] { "Off", "Fill", "Fill First" }, new[] { "0", "1", "2" }, 1, "Nest small ELEMENTS into larger elements' holes. Fill = after the nest; Fill First = pre-pack into holes before nesting (sometimes tighter). (Sheet holes are always kept out automatically.)"),
            NestOption.Number("poles", "Poles", 48, 4, 64, 0, "Inscribed circles per part for collision tests; more = more accurate (cleaner pack), fewer = faster but can pack worse."),
            NestOption.Choice("compact", "Compact", new[] { "Off", "Bottom-Left", "Multi" }, new[] { "0", "1", "2" }, 1, "Post-pack tightening slide."),
            NestOption.Choice("fit", "Fit", new[] { "One sheet (max fill)", "All parts (fewest sheets)" }, new[] { "1", "0" }, 1, "One sheet = fill a SINGLE sheet as full as possible; parts that don't fit are placed OUTSIDE. All parts = use as many sheets as needed so nothing is left off."),
            NestOption.Choice("batch", "Batch", new[] { "Off", "On" }, new[] { "0", "1" }, 0, "Keep related parts together for sorting: nest each batch into its own TIGHT BLOCK, then tile the blocks across the sheets (a batch never spreads across the sheet). A batch = one Grasshopper data-tree branch of the Geometry input, OR - if the input has no branches - every 'Batch Size' parts. Off = nest everything combined (densest)."),
            NestOption.Number("batch_size", "Batch Size", 0, 0, 100000, 0, "Batch mode, no branches: split the flat part list into consecutive groups of this many parts (e.g. 20). 0 = batch only by tree branch. Ignored when Batch is Off or the Geometry already has branches."),
            NestOption.Text("font", "Sheet Font", "MecSoft_Font-1 1", "Sheet-number label: font name + text size."),
        };

        // Apply wired "key value" token strings onto an option list (the wired Options input overrides the
        // on-canvas rows). Accepted per line: "<key> <value>" where value is a choice token (e.g.
        // "placement_type 1"), a choice label (e.g. "placement_type Gravity"), or a number; "font <name> <size>";
        // or the legacy bare font line "<name> <size>" (a line whose first word is no known key and whose last
        // word is a number — the exact shape the old Options string input used). Returns the unknown lines so
        // the caller can warn. Whitespace-tolerant; keys case-insensitive; InvariantCulture numbers.
        public static List<string> ApplyTokens(IReadOnlyList<NestOption> options, IEnumerable<string> tokens)
        {
            var unknown = new List<string>();
            if (options == null || tokens == null) return unknown;
            foreach (var raw in tokens)
            {
                string line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

                NestOption opt = null;
                foreach (var o in options)
                    if (string.Equals(o.Key, parts[0], StringComparison.OrdinalIgnoreCase)) { opt = o; break; }

                if (opt == null)
                {
                    // Legacy bare font line ("MecSoft_Font-1 1"): no known key, last word numeric.
                    NestOption font = null;
                    foreach (var o in options) if (o.Kind == NestOptionKind.Text) { font = o; break; }
                    if (font != null && parts.Length >= 2
                        && double.TryParse(parts[parts.Length - 1], NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        font.TextValue = line;
                    else
                        unknown.Add(line);
                    continue;
                }

                string value = parts.Length > 1 ? line.Substring(parts[0].Length).Trim() : "";
                switch (opt.Kind)
                {
                    case NestOptionKind.Choice:
                        int sel = -1;
                        for (int i = 0; i < opt.ChoiceTokens.Count && sel < 0; i++)
                            if (string.Equals(opt.ChoiceTokens[i], value, StringComparison.OrdinalIgnoreCase)) sel = i;
                        for (int i = 0; i < opt.ChoiceLabels.Count && sel < 0; i++)
                            if (string.Equals(opt.ChoiceLabels[i], value, StringComparison.OrdinalIgnoreCase)) sel = i;
                        if (sel >= 0) opt.SelectedIndex = sel; else unknown.Add(line);
                        break;
                    case NestOptionKind.Number:
                        opt.SetFromText(value);   // parses + clamps to [Min,Max]
                        break;
                    default: // Text (font): the value after the key is the whole token
                        if (value.Length > 0) opt.TextValue = value;
                        break;
                }
            }
            return unknown;
        }

        // One-line documentation for an option, used as the input description on the Nest Options components
        // and echoed in the solver components' Options-input description. For choices the value MEANINGS are
        // enumerated from the catalog ("0=Off, 1=Fill, …") so they can never drift from the actual tokens.
        public static string Describe(NestOption o)
        {
            switch (o.Kind)
            {
                case NestOptionKind.Choice:
                    var vals = new List<string>(o.ChoiceTokens.Count);
                    for (int i = 0; i < o.ChoiceTokens.Count; i++) vals.Add(o.ChoiceTokens[i] + "=" + o.ChoiceLabels[i]);
                    return o.Tooltip + " Values: " + string.Join(", ", vals) + ". Default " + o.ChoiceTokens[o.SelectedIndex] + ".";
                case NestOptionKind.Number:
                    return o.Tooltip + " Range " + o.Min.ToString("0.###", CultureInfo.InvariantCulture)
                         + "-" + o.Max.ToString("0.###", CultureInfo.InvariantCulture)
                         + ". Default " + o.Value.ToString(o.Decimals > 0 ? "F" + o.Decimals : "0", CultureInfo.InvariantCulture) + ".";
                default:
                    return o.Tooltip + " Default \"" + (o.TextValue ?? "") + "\".";
            }
        }

        // The shared description for the solver components' wired "Options" input (GH1 + GH2).
        public const string OptionsInputDescription =
            "Optional list of \"key value\" option strings — wire the Nest Options component here. Each line "
            + "overrides the matching on-canvas option row (the rows below show the values actually used); "
            + "unwired keys keep their on-canvas value. Examples: \"num_of_rotations 8\", \"placement_type 1\", "
            + "\"spacing 0.5\", \"MecSoft_Font-1 1\" (sheet font: name + size). Unknown lines raise a warning.";
    }
}
