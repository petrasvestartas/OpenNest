using System;
using System.Collections.Generic;
using System.Globalization;
using Grasshopper2.Components;
using Grasshopper2.Parameters;
using Grasshopper2.Parameters.Standard;
using Grasshopper2.UI;
using GrasshopperIO;
using opennest_2;   // NestOption + NestOptionCatalog (compiled into opennest_2.gha, bundled next to this .rhp)

namespace opennest_gh2.components
{
    // GH2 port of the GH1 "NestOptions" component (component_nest_options.cs): builds the "key value" option
    // strings for the wired "Options" input of OpenNest2 / OpenNestCollision. Input 0 ("Solver") picks the
    // solver, and the component REBUILDS its remaining inputs on the canvas to that solver's option set
    // (0 = OpenNest2 / NFP-GA, 1 = OpenNestCollision / physics — they expose different options). Inputs are
    // optional and documented from NestOptionCatalog (one source of truth with the on-canvas option rows);
    // choice options are integers whose VALUE is the emitted token. The dynamic swap uses GH2's programmatic
    // parameter API (Parameters.RemoveInput/AddInput), marshalled OUTSIDE the solution pass.
    [IoId("a7f3d2c8-91b4-4e6a-b05d-3c8e7f2a1d96")]
    public class NestOptionsComponent : Component
    {
        public const int SOLVER_AUTO = -1, SOLVER_OPENNEST2 = 0, SOLVER_COLLISION = 1;
        private int _solver = SOLVER_OPENNEST2;   // the solver whose option inputs are currently registered (never Auto)
        private volatile bool _swapPending;       // a layout swap is queued on the UI thread
        // The last Solver INPUT value seen by Process. Defaults to Auto so a freshly dropped OR copy-pasted
        // component auto-follows the solver it gets wired to BEFORE Process ever runs (a pasted component
        // wired to an orange solver never re-processes, so a flag set only inside Process would stay stale and
        // the layout would never swap — the copy-paste bug). Process overwrites it with the real value, so an
        // explicit Solver = 0/1 still wins once the component actually runs.
        private int _lastSolverInput = SOLVER_AUTO;
        private Grasshopper2.Doc.SolutionServer _hookedSolution;   // the solution server we listen to

        public NestOptionsComponent()
            : base(new Nomen("NestOptions",
                "Builds the option strings for a nesting solver. Solver = -1 (Auto, default) follows the solver this is "
                + "wired into; 0 (OpenNest2) / 1 (OpenNestCollision) forces it. The remaining inputs swap to that "
                + "solver's option set. Wire the output into the solver component's Options input.",
                "OpenNest", "Nest"))
        { Threading = ThreadingState.UiSingleThreaded; try { DocumentChanged += OnDocumentChanged; HookSolution(); } catch { } }

        // Restored components get their parameters straight from the archive (whatever solver layout was
        // saved), so AddInputs is NOT re-run; only _solver must be brought back in sync with that layout.
        public NestOptionsComponent(IReader reader) : base(reader)
        {
            Threading = ThreadingState.UiSingleThreaded;
            try { if (reader.HasItem("nest_options_solver")) _solver = reader.Integer32("nest_options_solver"); } catch { }
            try { DocumentChanged += OnDocumentChanged; HookSolution(); } catch { }
        }

        // ---- Auto mode: follow the downstream solver ----
        // Listen to the document's solution end so Auto re-detects the moment the output is wired (GH2 does not
        // re-process an upstream component when only its output is connected). Re-hooked whenever Document changes.
        private void OnDocumentChanged(object sender, Grasshopper2.Doc.ObjectDocumentEventArgs e) => HookSolution();

        private void HookSolution()
        {
            var sol = Document?.Solution;
            if (ReferenceEquals(sol, _hookedSolution)) return;
            if (_hookedSolution != null) { try { _hookedSolution.SolutionCompleted -= OnSolutionCompleted; } catch { } }
            _hookedSolution = sol;
            if (_hookedSolution != null) { try { _hookedSolution.SolutionCompleted += OnSolutionCompleted; } catch { } }
        }

        private void OnSolutionCompleted(object sender, Grasshopper2.Doc.SolutionEventArgs e)
        {
            if (_swapPending) return;
            if (_lastSolverInput == SOLVER_OPENNEST2 || _lastSolverInput == SOLVER_COLLISION) return;  // explicit override
            int detected = DetectDownstreamSolver(out _);
            if (detected == _solver) return;
            _solver = detected;       // commit now -> the next SolutionCompleted sees detected == _solver (no loop, no sticky latch)
            _swapPending = true;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { RebuildOptionInputs(); Document?.Solution.DelayedExpire(this); }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
                finally { _swapPending = false; }
            }));
        }

        // Inspect the output's recipients and report which solver component(s) they belong to. Returns the
        // detected solver; `both` is true when wired to BOTH (ambiguous -> caller falls back to OpenNest2).
        private int DetectDownstreamSolver(out bool both)
        {
            bool s2 = false, sc = false;
            var doc = Document;
            var outParam = Parameters.Output(0) as Grasshopper2.Parameters.AbstractParameter;
            if (doc != null && outParam != null && outParam.Connectivity != null)
                foreach (var g in outParam.Connectivity.Outputs)   // recipient parameter ids
                {
                    Grasshopper2.Doc.IDocumentObject owner = doc.Objects.FindParameter(g)?.ParentObject ?? doc.Objects.Find(g);
                    if (owner is OpenNest2Component) s2 = true;
                    else if (owner is OpenNestCollisionComponent) sc = true;
                }
            both = s2 && sc;
            return (sc && !s2) ? SOLVER_COLLISION : SOLVER_OPENNEST2;   // OpenNest2 also covers none/both
        }

        public override void Store(IWriter writer)
        {
            base.Store(writer);
            writer.Integer32("nest_options_solver", _solver);
        }

        protected override Grasshopper2.UI.Icon.IIcon IconInternal => opennest_gh2.icons.SvgVectorIcon.Load("nest_options.svg");

        private static List<NestOption> OptionsFor(int solver)
            => solver == SOLVER_COLLISION ? NestOptionCatalog.Collision() : NestOptionCatalog.OpenNest2();

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddInteger("Solver", "Solver",
                "Which solver these options are for. -1 = Auto (follow the solver this output is wired into); "
                + "0 = OpenNest2 (NFP + genetic algorithm); 1 = OpenNestCollision (physics). "
                + "Changing it swaps the inputs below to that solver's option set.",
                Access.Item, Requirement.MayBeMissing).Set(SOLVER_AUTO);
            // Register the option inputs THROUGH the InputAdder (the canonical path). Do NOT call
            // Parameters.AddInput here — mutating Parameters during AddInputs breaks GH2's registration
            // ("initialization failed"). The dynamic solver swap uses Parameters.AddInput later, outside AddInputs.
            AddOptionInputs(inputs, _solver);
        }

        // Add one InputAdder entry per catalog option (used during registration / AddInputs).
        private static void AddOptionInputs(InputAdder inputs, int solver)
        {
            foreach (var o in OptionsFor(solver))
            {
                string info = NestOptionCatalog.Describe(o);
                switch (o.Kind)
                {
                    case NestOptionKind.Choice:
                        int cd = 0; int.TryParse(o.ChoiceTokens[o.SelectedIndex], out cd);
                        inputs.AddInteger(o.Label, o.Label, info, Access.Item, Requirement.MayBeMissing).Set(cd);
                        break;
                    case NestOptionKind.Number when o.Decimals == 0:
                        inputs.AddInteger(o.Label, o.Label, info, Access.Item, Requirement.MayBeMissing).Set((int)Math.Round(o.Value));
                        break;
                    case NestOptionKind.Number:
                        inputs.AddNumber(o.Label, o.Label, info, Access.Item, Requirement.MayBeMissing).Set(o.Value);
                        break;
                    default: // Text (font)
                        inputs.AddText(o.Label, o.Label, info, Access.Item, Requirement.MayBeMissing).Set(o.TextValue ?? "");
                        break;
                }
            }
        }

        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddText("Options", "Options",
                "Option strings (\"key value\" per line) for the selected solver — wire into the solver component's Options input.",
                Access.Twig);
        }

        // One GH input per catalog option (same value semantics as GH1: Number -> the number, clamped;
        // Choice -> the TOKEN value, e.g. Fit: 1 = one sheet, 0 = all parts; Text -> the raw text).
        private static List<IParameter> BuildOptionInputs(int solver)
        {
            var list = new List<IParameter>();
            foreach (var o in OptionsFor(solver))
            {
                switch (o.Kind)
                {
                    case NestOptionKind.Choice:
                        var cp = new IntegerParameter(o.Label, o.Label, NestOptionCatalog.Describe(o), Access.Item)
                        { Requirement = Requirement.MayBeMissing };
                        if (int.TryParse(o.ChoiceTokens[o.SelectedIndex], out int dflt)) cp.Set(dflt);
                        list.Add(cp);
                        break;
                    case NestOptionKind.Number when o.Decimals == 0:
                        var ip = new IntegerParameter(o.Label, o.Label, NestOptionCatalog.Describe(o), Access.Item)
                        { Requirement = Requirement.MayBeMissing };
                        ip.Set((int)Math.Round(o.Value));
                        list.Add(ip);
                        break;
                    case NestOptionKind.Number:
                        var np = new NumberParameter(o.Label, o.Label, NestOptionCatalog.Describe(o), Access.Item)
                        { Requirement = Requirement.MayBeMissing };
                        np.Set(o.Value);
                        list.Add(np);
                        break;
                    default: // Text (font)
                        var tp = new TextParameter(o.Label, o.Label, NestOptionCatalog.Describe(o), Access.Item)
                        { Requirement = Requirement.MayBeMissing };
                        tp.Set(o.TextValue ?? "");
                        list.Add(tp);
                        break;
                }
            }
            return list;
        }

        protected override void Process(IDataAccess access)
        {
            int solverInput = SOLVER_AUTO;
            access.GetItem(0, out solverInput);
            _lastSolverInput = solverInput;     // record for the SolutionCompleted hook (auto-follow vs explicit)

            int desired;
            if (solverInput == SOLVER_OPENNEST2 || solverInput == SOLVER_COLLISION)
            {
                desired = solverInput;          // explicit override
            }
            else
            {
                if (solverInput != SOLVER_AUTO)
                    access.AddWarning("Invalid solver", "Solver must be -1 (Auto), 0 (OpenNest2) or 1 (OpenNestCollision); using Auto.");
                desired = DetectDownstreamSolver(out bool both);
                if (both) access.AddRemark("Auto", "Wired to both solvers; showing OpenNest2 options. Set Solver to 0 or 1 to choose.");
            }

            // The registered inputs belong to the OTHER solver. Parameters cannot be mutated during a
            // solution pass, so queue the swap on the UI message loop (runs after this pass unwinds) and
            // re-expire; this pass emits nothing (the swap is imminent).
            if (desired != _solver && !_swapPending)
            {
                _solver = desired;
                _swapPending = true;
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        RebuildOptionInputs();
                        Document?.Solution.DelayedExpire(this);
                    }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
                    finally { _swapPending = false; }
                }));
                return;
            }
            if (_swapPending) return;

            var opts = OptionsFor(_solver);
            for (int i = 0; i < opts.Count; i++) ReadOptionInput(access, i + 1, opts[i]);
            var tokens = new List<string>(opts.Count);
            foreach (var o in opts) tokens.Add(o.EmitToken());
            access.SetTwig(0, tokens.ToArray());
        }

        // Read input i into the option (unconnected inputs keep the catalog default via the param's Set value).
        private static void ReadOptionInput(IDataAccess access, int i, NestOption o)
        {
            switch (o.Kind)
            {
                case NestOptionKind.Choice:
                    if (!access.GetItem(i, out int tok)) return;
                    int sel = o.ChoiceTokens.IndexOf(tok.ToString(CultureInfo.InvariantCulture));
                    if (sel >= 0) o.SelectedIndex = sel;
                    else access.AddWarning("Invalid option value", o.Label + ": " + tok + " is not a valid value. " + NestOptionCatalog.Describe(o));
                    break;
                case NestOptionKind.Number:
                    // Match the registered parameter type (integer options use IntegerParameter): GH2 does
                    // NOT cast Integer » Double on GetItem, so reading the wrong type raises a cast error.
                    if (o.Decimals == 0)
                    { if (access.GetItem(i, out int iv)) o.SetFromText(iv.ToString(CultureInfo.InvariantCulture)); }
                    else
                    { if (access.GetItem(i, out double v)) o.SetFromText(v.ToString(CultureInfo.InvariantCulture)); }   // clamps to [Min,Max]
                    break;
                default: // Text
                    if (access.GetItem(i, out string s) && !string.IsNullOrWhiteSpace(s)) o.TextValue = s;
                    break;
            }
        }

        // Replace inputs 1..N with the current solver's option set. Wires on removed inputs are dropped
        // (the option sets differ, so carrying them over would silently misroute values). Runs on the UI
        // thread BETWEEN solutions (queued from Process).
        private void RebuildOptionInputs()
        {
            while (Parameters.InputCount > 1)
                Parameters.RemoveInput(Parameters.InputCount - 1);
            int at = 1;
            foreach (var p in BuildOptionInputs(_solver)) Parameters.AddInput(p, at++);
        }
    }
}
