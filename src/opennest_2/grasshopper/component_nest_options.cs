using System;
using System.Collections.Generic;
using System.Globalization;
using GH_IO.Serialization;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;

namespace opennest_2
{
    // Builds the "key value" option strings for the wired "Options" input of the solver components.
    // Input 0 ("Solver") picks which solver the options are for, and the component REBUILDS its remaining
    // inputs on the canvas to that solver's option set (the two solvers expose different options):
    //   0 = OpenNest2 (NFP + genetic algorithm)  -> Rotations, Packing, Seed, Mutation, Population,
    //       All Rotations, Element Holes, Sheet Font
    //   1 = OpenNestCollision (physics solver)   -> Rotations, Seed, Starts, Element Holes, Poles, Compact,
    //       Fit, Sheet Font
    // DEFAULT is Auto (-1): the component follows whichever solver its output is wired into and swaps the
    // inputs to match; setting Solver to 0/1 explicitly overrides. Auto re-checks on SolutionEnd so it
    // adapts the moment a wire is connected (Grasshopper does not re-solve an upstream component when only
    // its output is wired, so the on-wire switch needs that hook). When Auto is unwired or wired to BOTH
    // solvers it falls back to OpenNest2.
    // Every input is optional and documented from NestOptionCatalog (one source of truth with the on-canvas
    // option rows); choice options are integers whose VALUE is the emitted token (named values are attached,
    // so right-clicking the input shows the meanings). The output is wired into OpenNest2/OpenNestCollision's
    // "Options" input, where it overrides the matching on-canvas rows.
    public class component_nest_options : GH_Component
    {
        public const int SOLVER_AUTO = -1, SOLVER_OPENNEST2 = 0, SOLVER_COLLISION = 1;
        private int _solver = SOLVER_OPENNEST2;   // the solver whose option inputs are currently registered (never Auto)

        public component_nest_options()
            : base("NestOptions", "NestOptions",
                "Builds the option strings for a nesting solver. Solver = -1 (Auto, default) follows the solver this is "
                + "wired into; 0 (OpenNest2) / 1 (OpenNestCollision) forces it. The remaining inputs swap to that "
                + "solver's option set. Wire the output into the solver component's Options input.",
                "Params", "OpenNest2")
        { }

        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override System.Drawing.Bitmap Icon => Properties.Resources.nest_options;
        public override Guid ComponentGuid => new Guid("9C4E1B27-6A8F-4D3B-8E52-71A0D2C5B9F4");

        private static List<NestOption> OptionsFor(int solver)
            => solver == SOLVER_COLLISION ? NestOptionCatalog.Collision() : NestOptionCatalog.OpenNest2();

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            var solver = new Param_Integer
            {
                Name = "Solver", NickName = "Solver",
                Description = "Which solver these options are for. -1 = Auto (follow the solver this output is wired into); "
                            + "0 = OpenNest2 (NFP + genetic algorithm); 1 = OpenNestCollision (physics). "
                            + "Changing it swaps the inputs below to that solver's option set.",
                Access = GH_ParamAccess.item, Optional = true,
            };
            solver.AddNamedValue("Auto (follow wired solver)", SOLVER_AUTO);
            solver.AddNamedValue("OpenNest2 (NFP-GA)", SOLVER_OPENNEST2);
            solver.AddNamedValue("OpenNestCollision (physics)", SOLVER_COLLISION);
            solver.SetPersistentData(SOLVER_AUTO);
            pManager.AddParameter(solver);
            foreach (var p in BuildOptionInputs(_solver)) pManager.AddParameter(p);
        }

        // ---- Auto mode: follow the downstream solver ----
        // Inspect the output's recipients and report which solver component(s) they belong to. Returns the
        // detected solver; `both` is true when wired to BOTH (ambiguous -> caller falls back to OpenNest2).
        private int DetectDownstreamSolver(out bool both)
        {
            bool s2 = false, sc = false;
            var recipients = Params.Output[0].Recipients;
            if (recipients != null)
                foreach (var r in recipients)
                {
                    var owner = r?.Attributes?.GetTopLevel?.DocObject;
                    if (owner is component2_nest) s2 = true;
                    else if (owner is component_nest) sc = true;
                }
            both = s2 && sc;
            return (sc && !s2) ? SOLVER_COLLISION : SOLVER_OPENNEST2;   // OpenNest2 also covers none/both
        }

        public override void AddedToDocument(GH_Document document)
        {
            base.AddedToDocument(document);
            if (document != null)
            {
                document.SolutionEnd -= OnSolutionEnd;   // avoid a double subscription on re-add
                document.SolutionEnd += OnSolutionEnd;
                // A COPY-PASTED (or loaded) component carries its saved solver layout and may land already
                // wired to a solver — sync once now so it matches what it's connected to, not what it was
                // copied from. (A fresh drag-drop starts at the default, so it adapts on the first wire.)
                document.ScheduleSolution(10, d => { try { SyncToDownstream(); } catch { } });
            }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            if (document != null) document.SolutionEnd -= OnSolutionEnd;
            base.RemovedFromDocument(document);
        }

        // The effective Solver input value WITHOUT a SolveInstance pass: the wired (volatile) value if any,
        // else the on-canvas (persistent) value, else Auto. Lets OnSolutionEnd decide whether to auto-follow
        // even when the solver is orange and Grasshopper never pulled the Options input (so NestOptions never
        // re-solved this pass) — the case where the layout used to stay stuck on the wrong solver.
        private int CurrentSolverInput()
        {
            try
            {
                if (Params.Input[0] is Param_Integer p)
                {
                    if (p.VolatileDataCount > 0)
                    {
                        var b = p.VolatileData.get_Branch(0);
                        if (b != null && b.Count > 0 && b[0] is Grasshopper.Kernel.Types.GH_Integer gi) return gi.Value;
                    }
                    if (p.PersistentData != null && p.PersistentData.DataCount > 0)
                    {
                        var pd = p.PersistentData.get_FirstItem(true);
                        if (pd != null) return pd.Value;
                    }
                }
            }
            catch { }
            return SOLVER_AUTO;
        }

        // After each solution, re-sync to whatever the output is wired into. Runs for EVERY component
        // instance (fresh, pasted or loaded), so a copy-pasted Options component reconnected to a different
        // solver swaps its inputs just like a freshly dropped one.
        private void OnSolutionEnd(object sender, GH_SolutionEventArgs e) => SyncToDownstream();

        // If Solver is Auto and the downstream solver no longer matches the registered layout (output just
        // wired to a solver — possibly an orange one, or a pasted component still showing the copied layout),
        // commit the new solver and schedule the input swap + recompute. Reads the Solver input directly (not
        // a cached flag), so it fires even when NestOptions itself wasn't re-solved this pass. Self-converging:
        // _solver is committed immediately, so the next solution sees detected == _solver and does nothing —
        // no re-entry latch that could stick and freeze future swaps (the copy-paste failure mode).
        private void SyncToDownstream()
        {
            int solverInput = CurrentSolverInput();
            if (solverInput == SOLVER_OPENNEST2 || solverInput == SOLVER_COLLISION) return;  // explicit override; don't auto-follow
            int detected = DetectDownstreamSolver(out _);
            if (detected == _solver) return;
            _solver = detected;   // commit now; the scheduled callback below brings the params in line
            OnPingDocument()?.ScheduleSolution(5, d => { RebuildOptionInputs(); ExpireSolution(false); });
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter("Options", "Options",
                "Option strings (\"key value\" per line) for the selected solver — wire into the solver component's Options input.",
                GH_ParamAccess.list);
        }

        // One GH input per catalog option. The input NAME is the option's UI label; the value semantics are:
        // Number -> the number (clamped to the catalog range); Choice -> the TOKEN value (named values attached,
        // e.g. Fit: 1 = one sheet, 0 = all parts — note tokens are not always index-ordered); Text -> the raw text.
        private static List<IGH_Param> BuildOptionInputs(int solver)
        {
            var inputs = new List<IGH_Param>();
            foreach (var o in OptionsFor(solver))
            {
                IGH_Param p;
                switch (o.Kind)
                {
                    case NestOptionKind.Choice:
                        var ip = new Param_Integer();
                        for (int i = 0; i < o.ChoiceTokens.Count; i++)
                            if (int.TryParse(o.ChoiceTokens[i], out int tok)) ip.AddNamedValue(o.ChoiceLabels[i], tok);
                        if (int.TryParse(o.ChoiceTokens[o.SelectedIndex], out int dflt)) ip.SetPersistentData(dflt);
                        p = ip;
                        break;
                    case NestOptionKind.Number when o.Decimals == 0:
                        var np = new Param_Integer();
                        np.SetPersistentData((int)Math.Round(o.Value));
                        p = np;
                        break;
                    case NestOptionKind.Number:
                        var dp = new Param_Number();
                        dp.SetPersistentData(o.Value);
                        p = dp;
                        break;
                    default: // Text (font)
                        var tp = new Param_String();
                        tp.SetPersistentData(o.TextValue ?? "");
                        p = tp;
                        break;
                }
                p.Name = o.Label;
                p.NickName = o.Label;
                p.Description = NestOptionCatalog.Describe(o);
                p.Access = GH_ParamAccess.item;
                p.Optional = true;
                inputs.Add(p);
            }
            return inputs;
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int solverInput = SOLVER_AUTO;
            DA.GetData(0, ref solverInput);

            int desired;
            if (solverInput == SOLVER_OPENNEST2 || solverInput == SOLVER_COLLISION)
            {
                desired = solverInput;          // explicit override
            }
            else
            {
                if (solverInput != SOLVER_AUTO)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Solver must be -1 (Auto), 0 (OpenNest2) or 1 (OpenNestCollision); using Auto.");
                desired = DetectDownstreamSolver(out bool both);
                if (both) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Auto: wired to both solvers; showing OpenNest2 options. Set Solver to 0 or 1 to choose.");
            }

            // The registered inputs belong to the OTHER solver: params can't be changed mid-solve, so swap
            // them in a scheduled solution and recompute. This pass emits nothing (the swap is imminent).
            if (desired != _solver)
            {
                _solver = desired;
                var doc = OnPingDocument();
                doc?.ScheduleSolution(5, d => { RecordUndoEvent("Nest Options solver"); RebuildOptionInputs(); ExpireSolution(false); });
                return;
            }

            var opts = OptionsFor(_solver);
            for (int i = 0; i < opts.Count; i++) ReadOptionInput(DA, i + 1, opts[i]);
            var tokens = new List<string>(opts.Count);
            foreach (var o in opts) tokens.Add(o.EmitToken());
            DA.SetDataList(0, tokens);
        }

        // Read input i into the option (unconnected inputs keep the catalog default via persistent data).
        private void ReadOptionInput(IGH_DataAccess DA, int i, NestOption o)
        {
            switch (o.Kind)
            {
                case NestOptionKind.Choice:
                    int tok = 0;
                    if (!DA.GetData(i, ref tok)) return;
                    int sel = o.ChoiceTokens.IndexOf(tok.ToString(CultureInfo.InvariantCulture));
                    if (sel >= 0) o.SelectedIndex = sel;
                    else AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        o.Label + ": " + tok + " is not a valid value (" + NestOptionCatalog.Describe(o) + ")");
                    break;
                case NestOptionKind.Number:
                    // Match the registered parameter type (integer options use Param_Integer): reading a
                    // double from a Param_Integer throws "Invalid cast: Integer » Double".
                    if (o.Decimals == 0)
                    {
                        int iv = 0;
                        if (DA.GetData(i, ref iv)) o.SetFromText(iv.ToString(CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        double v = 0;
                        if (DA.GetData(i, ref v)) o.SetFromText(v.ToString(CultureInfo.InvariantCulture));   // clamps to [Min,Max]
                    }
                    break;
                default: // Text
                    string s = null;
                    if (DA.GetData(i, ref s) && !string.IsNullOrWhiteSpace(s)) o.TextValue = s;
                    break;
            }
        }

        // Replace inputs 1..N with the current solver's option set. Wires on removed inputs are dropped
        // (the option sets differ, so carrying them over would silently misroute values).
        private void RebuildOptionInputs()
        {
            while (Params.Input.Count > 1)
                Params.UnregisterInputParameter(Params.Input[Params.Input.Count - 1], true);
            foreach (var p in BuildOptionInputs(_solver)) Params.RegisterInputParam(p);
            Params.OnParametersChanged();
        }

        // Persist the layout and rebuild it BEFORE base.Read, so the archived param chunks (which GH1 binds
        // by index) line up with the registered inputs of the saved solver.
        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32("nest_options_solver", _solver);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            try
            {
                if (reader.ItemExists("nest_options_solver"))
                {
                    int s = reader.GetInt32("nest_options_solver");
                    if (s != _solver && (s == SOLVER_OPENNEST2 || s == SOLVER_COLLISION))
                    {
                        _solver = s;
                        RebuildOptionInputs();
                    }
                }
            }
            catch { /* fall through: default layout */ }
            return base.Read(reader);
        }
    }
}
