using System;
using System.Collections.Generic;
using System.Globalization;

namespace opennest_2
{
    // One editable option drawn directly on a nest component's body. A list of these replaces the old
    // "Options" string input: the embedded UI edits them, and BuildOptionStrings() turns them back into the
    // exact "name value" token list the existing solve pipeline already consumes (so nothing downstream changes).
    public enum NestOptionKind { Choice, Number, Text }

    public class NestOption
    {
        public string Key;            // serialization key AND emitted token name, e.g. "engine", "num_of_rotations"
        public string Label;          // UI label drawn on the component, e.g. "Engine"
        public NestOptionKind Kind;
        public string Tooltip;

        // CHOICE: parallel lists. ChoiceLabels[i] is shown; ChoiceTokens[i] is the value EmitToken() appends.
        public List<string> ChoiceLabels;   // e.g. {"C++ (fast)","C# (managed)"}
        public List<string> ChoiceTokens;   // e.g. {"cpp","cs"}
        public int SelectedIndex;

        // NUMBER: persisted as double; displayed/emitted with Decimals; clamped to [Min,Max].
        public double Value;
        public double Min = double.NegativeInfinity;
        public double Max = double.PositiveInfinity;
        public int Decimals;                 // 0 => integer display

        // TEXT (e.g. the sheet-number font line): the whole token is free text, emitted verbatim.
        public string TextValue;

        // ---- factory helpers ----
        public static NestOption Choice(string key, string label, string[] labels, string[] tokens, int sel, string tip = null)
            => new NestOption
            {
                Key = key, Label = label, Kind = NestOptionKind.Choice, Tooltip = tip,
                ChoiceLabels = new List<string>(labels), ChoiceTokens = new List<string>(tokens),
                SelectedIndex = ClampI(sel, 0, tokens.Length - 1)
            };

        public static NestOption Number(string key, string label, double value, double min, double max, int dec, string tip = null)
            => new NestOption
            {
                Key = key, Label = label, Kind = NestOptionKind.Number, Tooltip = tip,
                Value = ClampD(value, min, max), Min = min, Max = max, Decimals = dec
            };

        public static NestOption Text(string key, string label, string value, string tip = null)
            => new NestOption { Key = key, Label = label, Kind = NestOptionKind.Text, Tooltip = tip, TextValue = value };

        // The exact "name value" token expected by the existing parsers (rhino_example ctor / NpRun.Flatten /
        // the by-name loops). InvariantCulture so a comma decimal separator never corrupts the value.
        public string EmitToken()
        {
            switch (Kind)
            {
                case NestOptionKind.Choice:
                    return Key + " " + ChoiceTokens[ClampI(SelectedIndex, 0, ChoiceTokens.Count - 1)];
                case NestOptionKind.Number:
                    return Key + " " + Value.ToString(Decimals > 0 ? "F" + Decimals : "0", CultureInfo.InvariantCulture);
                default: // Text: the value already is the full token (e.g. "MecSoft_Font-1 1")
                    return TextValue ?? "";
            }
        }

        // Short string shown inside the control box on the canvas.
        public string Display()
        {
            switch (Kind)
            {
                case NestOptionKind.Choice:
                    return ChoiceLabels[ClampI(SelectedIndex, 0, ChoiceLabels.Count - 1)];
                case NestOptionKind.Number:
                    return Value.ToString(Decimals > 0 ? "F" + Decimals : "0", CultureInfo.InvariantCulture);
                default:
                    return TextValue ?? "";
            }
        }

        // The text the type-in editor starts with (numbers: bare value; text: the raw token).
        public string EditText()
            => Kind == NestOptionKind.Number
                ? Value.ToString(Decimals > 0 ? "F" + Decimals : "0", CultureInfo.InvariantCulture)
                : (TextValue ?? "");

        // Commit a typed string back into the option (numbers parse + clamp; text stored verbatim).
        public void SetFromText(string s)
        {
            if (Kind == NestOptionKind.Number)
            {
                if (double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out double v)
                 || double.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out v))
                    Value = ClampD(v, Min, Max);
            }
            else if (Kind == NestOptionKind.Text)
            {
                TextValue = s ?? "";
            }
        }

        static int ClampI(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
        static double ClampD(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);
    }

    // Implemented by the nest components; lets the shared attributes class read the option list, toggle the
    // collapsed state, and request a re-solve after an edit.
    public interface INestOptionsHost
    {
        IReadOnlyList<NestOption> Options { get; }
        void OnOptionChanged(NestOption opt);
        // Collapsible options panel. Collapsed by default so the component stays compact.
        bool OptionsExpanded { get; set; }
        void OnOptionsToggled();
        // On-component Run button (so no boolean toggle needs wiring). IsBusy => a solve is running
        // (button shows "Stop"); OnRunClicked starts a solve, or cancels one if already running.
        bool IsBusy { get; }
        void OnRunClicked();
    }
}
