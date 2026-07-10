using System;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using GH_IO.Serialization;
using System.Drawing;
using Rhino;
using Rhino.DocObjects;
using System.Linq;

namespace opennest_2
{
    // OpenNest2: the NFP/SvgNest genetic-algorithm nester. The solve now runs on a BACKGROUND thread with
    // a live per-generation preview (orange borders tighten as the GA improves) and ESC-to-stop, mirroring
    // the OpenNestCollision component. "Iterations" == GA generations: each generation evaluates the whole
    // population and evolves it (single elitism), so the result measurably improves with more generations.
    public class component2_nest : NestOptionsHostComponent, IGH_BakeAwareObject
    {
        public override GH_Exposure Exposure
        {
        get { return GH_Exposure.primary; }
        }

        private BoundingBox bbox = new BoundingBox();
        private List<TextEntity> text = new List<TextEntity>();
        private List<Curve> geometry = new List<Curve>();
        private List<System.Drawing.Color> geometry_colors = new List<System.Drawing.Color>();
        private List<List<Polyline>> sheets_display = new List<List<Polyline>>();
        private List<Curve> sheets_number_display = new List<Curve>();
        private List<Polyline> simplified_borders = new List<Polyline>();
        public override BoundingBox ClippingBox => bbox;

        // ---- async state machine (solve runs on a background thread; UI redraws between generations) ----
        private enum Phase { Idle, Computing, Ready }
        private volatile Phase _phase = Phase.Idle;
        private System.Threading.Tasks.Task _task;
        private nest_lib.rhino_example _pendingNest;            // managed GA solver, spans the two passes
        private nest_rhino_lib.nest_geo _pendingNestGeo;
        private string _pendingFont = "MecSoft_Font-1 1";
        private int _totalGenerations = 1;
        private volatile bool _cancelled = false;
        private volatile List<Polyline> _previewBorders = new List<Polyline>();  // live tightening preview (swapped, never mutated)
        private List<Polyline> _previewSheets = new List<Polyline>();            // sheet backdrop during compute

        // Cached last-completed result so a no-Run re-expire (e.g. fed by another OpenNest whose output settled)
        // RE-EMITS it instead of wiping — fixes a downstream OpenNest "results erase after the solve" in A->B chains.
        private bool _hasResult = false;
        private List<Polyline> _out_sheets;
        private GH_Structure<GH_Curve> _out_borders, _out_sheettxt;
        private GH_Structure<IGH_GeometricGoo> _out_allgeo, _out_geomattr;
        private GH_Structure<GH_Transform> _out_xforms;
        private GH_Structure<GH_Integer> _out_sheetid;
        private readonly System.Timers.Timer _timer;            // ~8 fps clock: live label + live geometry
        // Engine arbitration is SHARED with OpenNest1 (both call nfp_nest.dll) via EngineGate.Nfp — only one
        // nfp_nest solve runs at a time; the rest queue and are woken in order.
        private Action _wake;   // stable wake delegate for the engine queue (set in the ctor)

        // ---- Live mode: auto-restart on input change (opt-in via the "Live" option, default Off) ----
        private readonly System.Timers.Timer _debounce;         // one-shot; coalesces rapid input changes (slider drags)
        private volatile bool _restartRequested = false;        // a change arrived mid-solve -> restart once the solve unwinds
        private string _solvedSig = null;                       // input signature of the solve we last launched
        private int _solvedIter = -1;
        private string _pendingSig = null;                      // signature computed by the most recent InputsChanged()
        private int _pendingIter = -1;
        private const int DEBOUNCE_EDIT_MS = 250, DEBOUNCE_ITER_MS = 350;
        // Run is a standard input port (a Boolean Toggle): TRUE = a live session (solve now + auto-restart on
        // every input change); FALSE = idle (nothing runs, the last result is held). _prevRunInput edge-detects.
        private volatile bool _runActive = false;
        private bool _prevRunInput = false;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lineWeight = args.DefaultCurveThickness;

            // While the background solve runs, draw the LIVE tightening preview (snapshot the volatile refs
            // into locals first so a concurrent swap can't tear the iteration).
            if (_phase == Phase.Computing)
            {
                var psheets = _previewSheets;
                var pborders = _previewBorders;
                if (psheets != null) foreach (var pl in psheets) args.Display.DrawPolyline(pl, col);
                if (pborders != null) foreach (var pl in pborders) args.Display.DrawPolyline(pl, col, lineWeight);
                return;
            }

            for (int i = 0; i < sheets_number_display.Count; i++)
                args.Display.DrawCurve(sheets_number_display[i], col);

            for (int i = 0; i < sheets_display.Count; i++)
                for (int j = 0; j < sheets_display[i].Count; j++)
                    args.Display.DrawPolyline(sheets_display[i][j], col);

            for (int i = 0; i < simplified_borders.Count; i++)
                args.Display.DrawPolyline(simplified_borders[i], col, lineWeight);

            for (int i = 0; i < geometry.Count; i++)
                args.Display.DrawCurve(geometry[i], col); // geometry_colors[i]
        }

        private List<nest_rhino_lib.nest_geo> nest_geos = new List<nest_rhino_lib.nest_geo>();

        public override void BakeGeometry(RhinoDoc doc, List<Guid> obj_ids)
        {
            foreach (var nest_geo in nest_geos)
            {

                obj_ids.AddRange(nest_geo.bake_with_transforms());

            }

            for (int i = 0; i < this.sheets_display.Count; i++)
                for (int j = 0; j < this.sheets_display[i].Count; j++)
                    obj_ids.Add(RhinoDoc.ActiveDoc.Objects.AddPolyline(this.sheets_display[i][j]));

            for (int i = 0; i < this.sheets_number_display.Count; i++)
                obj_ids.Add(RhinoDoc.ActiveDoc.Objects.AddCurve(this.sheets_number_display[i]));

            var view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
            view.Redraw();
        }

        public component2_nest()
              : base("OpenNest2", "OpenNest2",
                "Nests parts onto sheets with the no-fit-polygon genetic solver, keeping each part's attributes. Feed it the Sheets and Geometry components.",
                "Params", "OpenNest2")
        {
            // One UI clock (~8 fps). AutoReset=false + re-arm avoids tick pile-up if a frame is slow.
            _timer = new System.Timers.Timer(120) { AutoReset = false };
            _timer.Elapsed += OnTick;
            _debounce = new System.Timers.Timer(DEBOUNCE_EDIT_MS) { AutoReset = false };
            _debounce.Elapsed += OnDebounceElapsed;
            _wake = WakeForRetry;   // stable delegate so the engine queue can enqueue/dequeue by reference
            BuildOptions();
        }

        // The settings shown as on-canvas controls (dropdowns + type-in boxes), replacing the old Options
        // string. ORDER IS LOAD-BEARING: the first 9 numeric rows map to rhino_example's parameters[0..8]
        // by index; all_rotations/exact_nfp are parsed by name; the font row must stay LAST.
        // The settings shown as on-canvas controls (dropdowns + type-in boxes). Defined once in
        // NestOptionCatalog (shared with the GH2 port and the Nest Options component); see the catalog for the
        // load-bearing ordering notes. 'wiggle'/'time'/'spacing' are not exposed (the GA uses discrete
        // rotations; run_cpp drives the solve by Iterations; gaps are set upstream in Geometry/Sheets) but are
        // still emitted as fixed values in BuildOptionStrings to keep the downstream positional indices intact.
        private void BuildOptions()
        {
            _options.Clear();
            _options.AddRange(NestOptionCatalog.OpenNest2());
        }

        // Rebuild the "name value" token list the existing pipeline consumes. Order is LOAD-BEARING: the C# ctor
        // (rhino_example) reads parameters[0..8] BY INDEX, so wiggle (idx 1) and time (idx 8) are emitted as fixed
        // 0 even though they aren't UI controls; all_rotations/exact_nfp are parsed by name; font stays last.
        private List<string> BuildOptionStrings()
        {
            string Tok(string key, string fallback) { var o = Opt(key); return o != null ? o.EmitToken() : key + " " + fallback; }
            return new List<string>
            {
                Tok("num_of_rotations", "4"),
                "wiggle 0",                       // not exposed (unused by the discrete GA)
                Tok("placement_type", "1"),
                "spacing 0",                      // not exposed: gaps are set upstream in the Geometry (part offset)
                                                  // and Sheets (gap) components; only OpenNest1 takes explicit spacing.
                Tok("seed", "30"),
                "simplify_tolerance 1",           // not exposed (no simplification; engine always runs exact NFP)
                Tok("mutation", "10"),
                Tok("population", "10"),
                "time 0",                         // not exposed (run_cpp uses Iterations; timeBudgetSecs = 0)
                Tok("all_rotations", "1"),
                "exact_nfp 1",                    // not exposed: ALWAYS exact NFP (parts touch, no gap, no overlap)
                Tok("font", "MecSoft_Font-1 1"),  // font row: EmitToken returns the raw token; must be LAST
            };
        }

        // Fires off the UI thread while computing: refresh the label and marshal the geometry preview +
        // repaint onto the UI thread (RhinoCommon document/display calls are UI-thread-only).
        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            try
            {
                var nest = _pendingNest;
                int gen = nest != null ? nest.CurrentGeneration : 0;
                double fit = nest != null ? nest.CurrentFitness : 0.0;
                this.Message = _cancelled
                    ? ("stopping…  gen " + gen)
                    : ("gen " + gen + " / " + _totalGenerations + "   fit " + fit.ToString("F3") + "   (ESC = stop)");
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        if (_phase == Phase.Computing && nest != null)
                        {
                            _previewSheets = nest.LiveSheets;
                            _previewBorders = nest.LiveBorders;
                            OnDisplayExpired(true);                    // repaint the capsule/label
                            ExpirePreview(true);                       // drop cached preview -> DrawViewportWires re-runs
                            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();  // repaint the 3D viewport
                        }
                    }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
                }));
            }
            catch { }
            if (_phase == Phase.Computing) _timer.Start();   // re-arm
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter("Sheets", "Sheets", "From OpenNest tab, use component Sheets.", GH_ParamAccess.tree);
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry. Multiple data-tree branches are combined into one nest (for clustered batch nesting, use OpenNestCollision).", GH_ParamAccess.tree);

            // Optional wired options ("key value" strings, e.g. from the Nest Options component); they override
            // the matching on-canvas option rows. Inserted BEFORE Iterations. MakeOptionsInput is shared with the
            // old-file fixup in NestOptionsHostComponent.Read.
            pManager.AddParameter(MakeOptionsInput());
            pManager.AddIntegerParameter("Iterations", "Iterations", "GA generations to evolve. Each generation evaluates the whole population and keeps the best, so the result improves over generations. ~10-40 typical (a live orange preview tightens as it runs; press ESC to stop and keep the best). Pair with the 'population' option (default 10).", GH_ParamAccess.item, 10);
            // Standard Run input (replaces the old on-canvas Run button). Wire a Boolean Toggle.
            pManager.AddBooleanParameter("Run", "Run", "Wire a Boolean Toggle. TRUE = solve now and re-solve when an input changes (background thread, live preview); FALSE = hold the last result. (Options are still edited on the component body; ESC also stops a running solve.)", GH_ParamAccess.item, false);
            pManager[2].Optional = true;   // Options
            pManager[3].Optional = true;   // Iterations (has a default)
            pManager[4].Optional = true;   // Run
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Sheets", "Sheets", "Polylines representing sheets.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Borders", "Borders", "Placed part outline curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("All Geo", "All Geometry", "All placed geometry, grouped per sheet.", GH_ParamAccess.tree);
            pManager.AddTransformParameter("Transforms", "Transforms", "Move/rotate transform placing each part.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Sheet Id", "Sheet Id", "Sheet index each part landed on.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Sheet Txt", "Sheet Txt", "Sheet-number labels as text curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Attributes", "Attributes", "Attribute geometry carried with each part.", GH_ParamAccess.tree);
            for (int i = 0; i < pManager.ParamCount; i++)
                pManager.HideParameter(i);
        }

        protected override void BeforeSolveInstance()
        {
            // Only reset when a genuinely fresh solve (a Run click) is about to start. During Computing the
            // preview must persist; on the Ready (publish) pass SolveInstance does its own reset + rebuild; and a
            // no-Run re-expire (fed by another OpenNest whose output settled) must KEEP the last result, not wipe it.
            if (_phase != Phase.Idle) return;
            if (!_runButtonRequested) return;
            nest_geos = new List<nest_rhino_lib.nest_geo>();
            bbox = new BoundingBox();
            ResetDisplayLists();
        }

        private void ResetDisplayLists()
        {
            text = new List<TextEntity>();
            geometry = new List<Curve>();
            geometry_colors = new List<System.Drawing.Color>();
            sheets_display = new List<List<Polyline>>();
            sheets_number_display = new List<Curve>();
            simplified_borders = new List<Polyline>();
        }

        // Re-emit the last completed result's outputs (used on a no-Run re-expire so a chained/downstream OpenNest
        // holds its data and the viewport keeps showing the layout instead of erasing it).
        private void EmitCachedOutputs(IGH_DataAccess DA)
        {
            if (!_hasResult) return;
            if (_out_sheets   != null) DA.SetDataList(0, _out_sheets);
            if (_out_borders  != null) DA.SetDataTree(1, _out_borders);
            if (_out_allgeo   != null) DA.SetDataTree(2, _out_allgeo);
            if (_out_xforms   != null) DA.SetDataTree(3, _out_xforms);
            if (_out_sheetid  != null) DA.SetDataTree(4, _out_sheetid);
            if (_out_sheettxt != null) DA.SetDataTree(5, _out_sheettxt);
            if (_out_geomattr != null) DA.SetDataTree(6, _out_geomattr);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Sheets + Geometry are TREE inputs read whole in one pass; the async state machine must advance
            // exactly ONCE per solution. Ignore any extra iterations forced by a tree on another input — this
            // is also the backstop against the multi-branch infinite re-solve loop.
            if (DA.Iteration > 0) return;

            if (nest_geos == null) nest_geos = new List<nest_rhino_lib.nest_geo>();

            // Wired options (if any) override the on-canvas rows BEFORE anything reads/signatures them, so both
            // the launch below and InputsChanged see the values actually in effect.
            ApplyWiredOptions(DA);   // Options input at index 2

            // Run is a standard input port now (was the on-canvas Run button). Edge-drive the existing latch:
            // a rising edge launches a live session; a falling edge stops it and holds the last result. Using the
            // INPUT's previous value (not _runActive) means an ESC-stop mid-solve isn't immediately relaunched
            // just because Run is still held TRUE.
            bool runInput = false; DA.GetData(4, ref runInput);
            if (runInput && !_prevRunInput) { _runActive = true; _runButtonRequested = true; }
            else if (!runInput && _runActive)
            {
                _runActive = false; CancelSolve();
                try { _debounce.Stop(); } catch { }
                try { EngineGate.Nfp.Dequeue(_wake); } catch { }
            }
            _prevRunInput = runInput;

            // ===== phases before the result is ready: gate on Run, launch the solve, or ignore re-solves
            if (_phase != Phase.Ready)
            {
                if (_phase == Phase.Computing)
                {
                    if (!_runActive) { CancelSolve(); return; }     // Run toggled off -> stop the running solve
                    // Live (Run on): an input changed vs the solve in flight -> debounce -> cancel + restart.
                    if (InputsChanged(DA)) ArmDebounce();
                    return;
                }

                // Idle. A "launch now" request comes from the Run click that turned the session on, or from a
                // debounced auto-restart.
                bool launch = _runButtonRequested;
                _runButtonRequested = false;

                if (!_runActive)
                {
                    // Run is OFF: hold the last completed result; do not solve and do not auto-restart.
                    this.Message = null;
                    EmitCachedOutputs(DA);
                    return;
                }

                if (!launch)
                {
                    // Run is ON but this is a plain re-expire (an input/option/canvas change while the session
                    // is live): keep showing the last result and debounce an auto-restart on a real change.
                    if (InputsChanged(DA)) { ArmDebounce(); this.Message = "live — restarting…"; }
                    else this.Message = "live — watching";
                    EmitCachedOutputs(DA);
                    return;
                }

                // Launch (Run just turned on, or a debounced restart fired): fresh solve.
                // Acquire the process-global engine lock FIRST — the nfp_nest engine uses process-global state,
                // so only one solve runs at a time across the whole document. If ANOTHER OpenNest holds it,
                // QUEUE: keep this launch pending, hold the current result, and retry shortly. That lets several
                // nesting components in one file all solve, one after another, with no manual retry.
                if (!TryAcquireEngine())
                {
                    this.Message = "queued — waiting for engine…";
                    EmitCachedOutputs(DA);   // hold the last result while queued; woken when the engine frees
                    ArmDebounce();           // backup poll in case a wake is ever missed
                    return;
                }

                // We hold the engine lock. Record the input signature so a later change is detected as new.
                InputsChanged(DA);
                _solvedSig = _pendingSig; _solvedIter = _pendingIter;

                // Fresh solve — clear the old result + preview, prep the solver, launch.
                ResetDisplayLists();
                this.nest_geos = new List<nest_rhino_lib.nest_geo>();   // fresh (BeforeSolveInstance no longer resets on a button click)
                this.bbox = new BoundingBox();
                _previewBorders = new List<Polyline>();
                _previewSheets = new List<Polyline>();
                _hasResult = false;

                // Read the Sheets + Geometry TREES; all Geometry branches are COMBINED into one nest (this
                // solver has no batch mode). Duplicated so the shared upstream geometry is never mutated.
                if (!ReadCombinedInputs(DA, out var nest_sheets, out var nest_geo_dup))
                {
                    ReleaseEngine();   // release — no solve was started; wake the next queued component
                    this.Message = "missing Sheets/Geometry";
                    return;
                }
                // nest_sheets is already a combined duplicate (see CombineSheets); nest_geo_dup is a dup too.
                this.nest_geos.Add(nest_geo_dup);

                // Options now come from the on-canvas controls, not a wired string input.
                List<string> parameters_text = BuildOptionStrings();
                List<double> parameters = new List<double>();
                for (int i = 0; i < parameters_text.Count - 1; i++)
                {
                    string[] opts = parameters_text[i].Split(' ');
                    int id = opts.Length > 1 ? 1 : 0;
                    if (double.TryParse(opts[id], out double r)) parameters.Add(r);
                }
                _pendingFont = parameters_text.Count > 0 ? parameters_text[parameters_text.Count - 1] : "MecSoft_Font-1 1";

                int allRotations = 0;
                int exactNfp = 1;   // default: exact NFP — parts touch with no gap and no overlap
                int useHoles = 1;   // default: nest smaller parts into larger parts' holes ("element_holes")
                foreach (var line in parameters_text)
                {
                    var t = line.Split(' ');
                    if (t.Length >= 2 && t[0].Equals("all_rotations", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(t[1], out int ar)) allRotations = ar;
                    if (t.Length >= 2 && t[0].Equals("exact_nfp", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(t[1], out int en)) exactNfp = en;
                    if (t.Length >= 2 && t[0].Equals("element_holes", StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(t[1], out int eh)) useHoles = eh;
                }

                int max_iterations = 10;
                DA.GetData(3, ref max_iterations);   // Iterations shifted to index 3 (Options inserted at 2)
                if (max_iterations < 1) max_iterations = 1;

                try
                {
                    _pendingNest = new nest_lib.rhino_example(ref nest_sheets, ref nest_geo_dup, parameters, max_iterations);
                    _pendingNest.TryAllRotations = allRotations;
                    _pendingNest.ExactNfp = exactNfp;
                    _pendingNest.UseHoles = useHoles;
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine(ex.ToString());
                    ReleaseEngine();
                    this.Message = "input error";
                    return;
                }

                _pendingNestGeo = nest_geo_dup;
                _totalGenerations = max_iterations;
                _cancelled = false;
                _phase = Phase.Computing;
                this.Message = "starting…  (ESC = stop)";
                _task = new System.Threading.Tasks.Task(() => RunSolve());
                return;   // the task is started in AfterSolveInstance, once this iteration has unwound
            }

            // ===== PASS 2: background solve finished -> assemble outputs, publish, stop the clock =====

            // Live: a change arrived while solving. The (now-cancelled) solve has unwound and freed the global
            // lock, so DON'T publish its partial result — keep the previous good result and relaunch with the
            // current inputs. ScheduleSolution defers the re-expire safely to after this solution completes.
            if (_restartRequested)
            {
                _restartRequested = false;
                _phase = Phase.Idle;
                StopClocks();
                EmitCachedOutputs(DA);   // hold the previous result so downstream/viewport don't blank
                if (_runActive) ArmDebounce();   // relaunch shortly via the proven timer -> ExpireSolution path
                return;
            }

            ResetDisplayLists();
            try
            {
                nest_lib.rhino_example nest = _pendingNest;
                var nest_geo = _pendingNestGeo;
                string font_and_size = _pendingFont;
                this.simplified_borders.AddRange(nest.simplified_polygons);
                this.Message = (_cancelled ? ("stopped @ gen " + nest.CurrentGeneration) : (nest.CurrentGeneration + " gens"))
                             + "   →   " + nest.output_sheets.Count + " sheet(s)";

                ///////////////////////////////////////////////////////////////////////////////////
                //output
                ///////////////////////////////////////////////////////////////////////////////////
                List<Polyline> output_sheets = new List<Polyline>();
                for (int i = 0; i < nest.output_sheets.Count; i++)
                    for (int j = 0; j < nest.output_sheets[i].Count; j++)
                        output_sheets.Add(new Polyline(nest.output_sheets[i][j]));
                DA.SetDataList(0, output_sheets);
                this.sheets_display.AddRange(nest.output_sheets);

                GH_Structure<GH_Curve> borders = new GH_Structure<GH_Curve>();
                GH_Structure<IGH_GeometricGoo> all_geo_groups = new GH_Structure<IGH_GeometricGoo>();
                GH_Structure<GH_Transform> xforms = new GH_Structure<GH_Transform>();
                GH_Structure<GH_Integer> sheet_id = new GH_Structure<GH_Integer>();
                GH_Structure<GH_Curve> sheet_txt = new GH_Structure<GH_Curve>();
                GH_Structure<IGH_GeometricGoo> geometry_attributes = new GH_Structure<IGH_GeometricGoo>();
                var doc = Rhino.RhinoDoc.ActiveDoc;

                for (int i = 0; i < nest.output_transforms.Count; i++)
                {
                    for (int j = 0; j < nest_geo.boundary_sorted[i].Count; j++)
                    {
                        for (int k = 0; k < nest_geo.xforms[i].Count; k++)
                        {
                            Curve crv_temp = nest_geo.boundary_sorted[i][j].Item2.Duplicate().ToNurbsCurve();
                            crv_temp.Transform(nest_geo.xforms[i][k]);
                            borders.Append(new GH_Curve(crv_temp), new GH_Path(i, k));
                        }
                    }

                    for (int j = 0; j < nest_geo.geometry_sorted[i].Count; j++)
                    {
                        string object_type = nest_geo.geometry[nest_geo.geometry_sorted[i][j]].ObjectType.ToString();

                        for (int k = 0; k < nest_geo.xforms[i].Count; k++)
                        {
                            GeometryBase geo_temp = nest_geo.geometry[nest_geo.geometry_sorted[i][j]].Duplicate();
                            geo_temp.Transform(nest_geo.xforms[i][k]);
                            all_geo_groups.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(geo_temp), new GH_Path(i));

                            int gidx_attr = nest_geo.geometry_sorted[i][j];
                            if (nest_geo.geometry_attributes.Count > gidx_attr)
                            {
                                var attrArr = nest_geo.geometry_attributes[gidx_attr];
                                var portArr = (nest_geo.geometry_attribute_ports != null && gidx_attr < nest_geo.geometry_attribute_ports.Count) ? nest_geo.geometry_attribute_ports[gidx_attr] : null;
                                bool useSub = nest_geo.attribute_port_count >= 2;   // >=2 ports => {part; port} sub-branches
                                for (int a = 0; a < attrArr.Length; a++)
                                {
                                    GeometryBase geometry_attibute_temp = attrArr[a].Duplicate();
                                    geometry_attibute_temp.Transform(nest_geo.xforms[i][k]);
                                    int port = (portArr != null && a < portArr.Length) ? portArr[a] : 0;
                                    GH_Path apath = useSub ? new GH_Path(i, port) : new GH_Path(i);
                                    geometry_attributes.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(geometry_attibute_temp), apath);
                                }
                            }

                            //display
                            if (object_type == "Curve")
                            {
                                bbox.Union(geo_temp.GetBoundingBox(false));
                                this.geometry.Add(geo_temp as Curve);
                                this.geometry_colors.Add(nest_geo.attributes[nest_geo.geometry_sorted[i][j]].ObjectColor);//ComputeObjectColor
                            }
                        }
                    }

                    for (int k = 0; k < nest_geo.xforms[i].Count; k++)
                    {
                        xforms.Append(new GH_Transform(nest_geo.xforms[i][k]), new GH_Path(i));
                        sheet_id.Append(new GH_Integer(nest.output_polygon_sheet_ids[i][k]), new GH_Path(i));
                    }
                }
                DA.SetDataTree(1, borders);
                DA.SetDataTree(2, all_geo_groups);
                DA.SetDataTree(3, xforms);
                DA.SetDataTree(4, sheet_id);
                DA.SetDataTree(6, geometry_attributes);

                ///////////////////////////////////////////////////////////////////////////////////
                //Circles in the empty space | collect sheets and boundaries
                ///////////////////////////////////////////////////////////////////////////////////
                List<Circle> sheets_numbering_centers = new List<Circle>(nest.output_sheets.Count);
                string[] options = font_and_size.Split(' ');
                if (options.Length > 1)
                {
                    if (double.TryParse(options[1], out double sheet_radius))
                    {
                        if (sheet_radius != 0)
                        {

                            if (sheet_radius > 0)
                            {
                                for (int i = 0; i < nest.output_sheets.Count; i++)
                                {
                                    Point3d center = nest.output_sheets[i][0].BoundingBox.Max - (new Vector3d(sheet_radius, sheet_radius, 0));
                                    Circle circle = new Circle(center, sheet_radius);
                                    sheets_numbering_centers.Add(circle);
                                }
                            }
                            else
                            {
                                List<List<Polyline>> sheets_with_borders = new List<List<Polyline>>();
                                for (int i = 0; i < nest.output_sheets.Count; i++)
                                {
                                    List<Polyline> sheet_with_borders = new List<Polyline>();
                                    for (int j = 0; j < nest.output_sheets[i].Count; j++)
                                    {
                                        Polyline border = new Polyline(nest.output_sheets[i][j]);
                                        sheet_with_borders.Add(border);
                                    }
                                    sheets_with_borders.Add(sheet_with_borders);
                                }



                                for (int i = 0; i < nest.output_polygon_sheet_ids.Count; i++)
                                    for (int j = 0; j < nest.output_polygon_sheet_ids[i].Count; j++)
                                    {
                                        if (nest.output_polygon_sheet_ids[i][j] == -1)
                                            continue;
                                        foreach (var pline_id in nest_geo.boundary_sorted[i])
                                        {
                                            Polyline border = new Polyline(pline_id.Item2);
                                            border.Transform(nest.output_transforms[i][j]);
                                            sheets_with_borders[nest.output_polygon_sheet_ids[i][j]].Add(border);
                                        }
                                    }

                                for (int i = 0; i < sheets_with_borders.Count; i++)
                                {
                                    Circle circle = nest_rhino_lib.InscribeCircle.Compute(sheets_with_borders[i]);
                                    sheets_numbering_centers.Add(new Circle(circle.Center, -sheet_radius));
                                }
                            }

                            for (int i = 0; i < sheets_numbering_centers.Count; i++)
                            {
                                Curve[] crvs = nest_rhino_lib.ToRhino.get_text(i.ToString(), sheets_numbering_centers[i], options[0]);

                                this.sheets_number_display.AddRange(crvs);
                                foreach (var crv in crvs)
                                {
                                    sheet_txt.Append(new GH_Curve(crv), new GH_Path(i));
                                }
                            }
                        }
                    }
                }

                DA.SetDataTree(5, sheet_txt);

                // Cache this completed result so a later no-Run re-expire re-emits it instead of clearing.
                _out_sheets = output_sheets; _out_borders = borders; _out_allgeo = all_geo_groups;
                _out_xforms = xforms; _out_sheetid = sheet_id; _out_sheettxt = sheet_txt; _out_geomattr = geometry_attributes;
                _hasResult = true;
            }
            catch (Exception ex)
            {
                Rhino.RhinoApp.WriteLine(ex.ToString());
            }
            _phase = Phase.Idle;
            StopClocks();
        }

        // Start the background solve only after the launch iteration has fully unwound.
        protected override void AfterSolveInstance()
        {
            if (_phase == Phase.Computing && _task != null && _task.Status == System.Threading.Tasks.TaskStatus.Created)
            {
                StartClocks();
                _task.Start(System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        // BACKGROUND THREAD: run the managed GA solve. No GH document calls here; the solver only builds
        // plain geometry snapshots (LiveBorders) that the UI thread reads.
        private void RunSolve()
        {
            try { _pendingNest.static_solver(ref _pendingNestGeo); }
            catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            finally { ReleaseEngine(); }   // frees the engine AND wakes the next queued component
            _phase = Phase.Ready;
            // marshal the re-solve onto the UI thread; NEVER call ExpireSolution from a worker thread.
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => { try { ExpireSolution(true); } catch { } }));
        }

        private void StartClocks()
        {
            Rhino.RhinoApp.EscapeKeyPressed += OnEscape;   // ESC fires now that the UI thread is free
            _timer.Start();
        }

        private void StopClocks()
        {
            try { Rhino.RhinoApp.EscapeKeyPressed -= OnEscape; } catch { }
            try { _timer.Stop(); } catch { }
        }

        private void OnEscape(object sender, EventArgs e)
        {
            if (_phase == Phase.Computing && _pendingNest != null)
            {
                _runActive = false;   // ESC ends the live session (no auto-restart afterwards)
                _pendingNest.StopRequested = true; _cancelled = true; this.Message = "stopping…";
            }
        }

        private void CancelSolve()
        {
            if (_phase == Phase.Computing && _pendingNest != null) { _pendingNest.StopRequested = true; _cancelled = true; }
        }

        // ---- engine arbitration: one nfp_nest solve at a time across the process (process-global engine
        // state). Components that can't get in QUEUE; whoever releases WAKES the next waiter, so several
        // nesting components in one file run one after another with no manual retry. ----
        private bool TryAcquireEngine() => EngineGate.Nfp.TryAcquire(_wake);
        private void ReleaseEngine() => EngineGate.Nfp.Release();
        private void WakeForRetry()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { if (_runActive && _phase == Phase.Idle) { _runButtonRequested = true; ExpireSolution(true); } } catch { }
            }));
        }

        // ---- Live mode helpers -------------------------------------------------------------------------
        // Cheap, stable signature of the meaningful inputs: iterations + the engine option tokens + every
        // nesting-boundary point + every sheet point (rounded). Lets us tell a real edit apart from an
        // unrelated re-expire (or our own completion re-expire, which leaves the signature unchanged).
        private static string SigOf(nest_rhino_lib.nest_sheets sheets, nest_rhino_lib.nest_geo geo,
                                    int iters, List<string> optTokens)
        {
            long[] h = { unchecked((long)1469598103934665603) };   // FNV-1a-ish
            void MixI(long v) { unchecked { h[0] = (h[0] ^ v) * 1099511628211L; } }
            void MixD(double d) { MixI(BitConverter.DoubleToInt64Bits(Math.Round(d, 6))); }
            MixI(iters);
            if (optTokens != null) foreach (var t in optTokens) if (t != null) foreach (char c in t) MixI(c);
            try
            {
                if (geo != null && geo.boundary_sorted != null)
                    foreach (var part in geo.boundary_sorted)
                        foreach (var loop in part)
                        {
                            var pl = loop.Item2;
                            if (pl == null) continue;
                            for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                        }
                if (sheets != null && sheets.sheets != null)
                    foreach (var arr in sheets.sheets)
                        if (arr != null)
                            foreach (var pl in arr)
                            {
                                if (pl == null) continue;
                                for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                            }
            }
            catch { }
            return h[0].ToString();
        }

        // Reads the inputs, computes the signature (cached into _pendingSig/_pendingIter), and reports whether
        // it differs from the solve we last launched. Reads the TREES (all branches) so a change in any branch
        // is caught; branches are combined so branch structure itself doesn't affect the result.
        private bool InputsChanged(IGH_DataAccess DA)
        {
            int it = 10; DA.GetData(3, ref it); if (it < 1) it = 1;   // Iterations at index 3 (Options at 2)
            _pendingSig = SigOfTrees(DA, it, BuildOptionStrings());
            _pendingIter = it;
            return _pendingSig != _solvedSig;
        }

        // Unwrap a generic GH goo into a native OpenNest type (Sheets/Geometry carry these as GH_ObjectWrapper).
        private static T Unwrap<T>(Grasshopper.Kernel.Types.IGH_Goo goo) where T : class
        {
            if (goo == null) return null;
            if (goo is T direct) return direct;
            try { if (goo.ScriptVariable() is T sv) return sv; } catch { }
            if (goo is Grasshopper.Kernel.Types.GH_ObjectWrapper w) return w.Value as T;
            return null;
        }

        // Read the Sheets + Geometry TREES: first nest_sheets found + all Geometry branches COMBINED into one
        // nest_geo (each DUPLICATED so the upstream is never mutated). Returns false if a sheet/geometry missing.
        private bool ReadCombinedInputs(IGH_DataAccess DA, out nest_rhino_lib.nest_sheets sheets, out nest_rhino_lib.nest_geo merged)
        {
            sheets = null; merged = null;
            var geos = new List<nest_rhino_lib.nest_geo>();
            // ALL nest_sheets objects across the tree are combined into one (wiring 2+ sheet objects makes every
            // sheet available, not just the first). CombineSheets returns a duplicate — safe to mutate.
            var sheetList = new List<nest_rhino_lib.nest_sheets>();
            if (DA.GetDataTree(0, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> stree) && stree != null)
                foreach (var branch in stree.Branches)
                {
                    if (branch == null) continue;
                    foreach (var goo in branch) { var s = Unwrap<nest_rhino_lib.nest_sheets>(goo); if (s != null) sheetList.Add(s); }
                }
            sheets = CombineSheets(sheetList);
            if (DA.GetDataTree(1, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> gtree) && gtree != null)
                foreach (var branch in gtree.Branches)
                {
                    if (branch == null) continue;
                    foreach (var goo in branch) { var g = Unwrap<nest_rhino_lib.nest_geo>(goo); if (g != null) geos.Add(g.duplicate()); }
                }
            if (geos.Count == 0 || sheets == null) return false;
            merged = geos.Count == 1 ? geos[0] : nest_rhino_lib.nest_geo.Merge(geos);
            return true;
        }

        // Combine several nest_sheets objects into ONE (concatenate every non-empty sheet) so wiring multiple
        // sheet objects makes ALL their sheets available — not just the first. Each source is DUPLICATED first
        // (the solver offsets/transforms sheets in place), so the result is safe to mutate.
        private static nest_rhino_lib.nest_sheets CombineSheets(List<nest_rhino_lib.nest_sheets> list)
        {
            if (list == null || list.Count == 0) return null;
            var dups = new List<nest_rhino_lib.nest_sheets>(list.Count);
            foreach (var ns in list) if (ns != null) dups.Add(ns.duplicate());
            if (dups.Count == 0) return null;
            if (dups.Count == 1) return dups[0];
            var all = new List<Polyline[]>();
            foreach (var ns in dups)
                if (ns.sheets != null)
                    foreach (var sh in ns.sheets)
                        if (sh != null && sh.Length > 0) all.Add(sh);
            var combined = dups[0];
            combined.sheets = all.ToArray();
            return combined;
        }

        // Signature over the TREES (iterations + option tokens + every part-boundary and sheet point). No merge
        // in this hot path; branch structure is irrelevant to the combined result so it isn't mixed in.
        private string SigOfTrees(IGH_DataAccess DA, int iters, List<string> optTokens)
        {
            long[] h = { unchecked((long)1469598103934665603) };
            void MixI(long v) { unchecked { h[0] = (h[0] ^ v) * 1099511628211L; } }
            void MixD(double d) { MixI(BitConverter.DoubleToInt64Bits(Math.Round(d, 6))); }
            MixI(iters);
            if (optTokens != null) foreach (var t in optTokens) if (t != null) foreach (char c in t) MixI(c);
            try
            {
                if (DA.GetDataTree(1, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> gtree) && gtree != null)
                    foreach (var branch in gtree.Branches)
                    {
                        if (branch == null) continue;
                        foreach (var goo in branch)
                        {
                            var g = Unwrap<nest_rhino_lib.nest_geo>(goo);
                            if (g == null || g.boundary_sorted == null) continue;
                            foreach (var part in g.boundary_sorted)
                                foreach (var loop in part)
                                {
                                    var pl = loop.Item2; if (pl == null) continue;
                                    for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                                }
                        }
                    }
                if (DA.GetDataTree(0, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> stree) && stree != null)
                    foreach (var branch in stree.Branches)
                    {
                        if (branch == null) continue;
                        foreach (var goo in branch)
                        {
                            var s = Unwrap<nest_rhino_lib.nest_sheets>(goo);
                            if (s == null || s.sheets == null) continue;
                            foreach (var arr in s.sheets)
                                if (arr != null)
                                    foreach (var pl in arr)
                                    {
                                        if (pl == null) continue;
                                        for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                                    }
                        }
                    }
            }
            catch { }
            return h[0].ToString();
        }

        // (Re-)arm the one-shot debounce. The Iterations slider gets a slightly longer wait since each tick is
        // a full restart of a heavier solve.
        private void ArmDebounce()
        {
            try { _debounce.Stop(); } catch { }
            _debounce.Interval = (_pendingIter != _solvedIter) ? DEBOUNCE_ITER_MS : DEBOUNCE_EDIT_MS;
            _debounce.Start();
        }

        // Debounce fired: inputs have settled. Idle -> launch now; Computing -> cancel and let PASS 2 restart.
        private void OnDebounceElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_runActive) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (_phase == Phase.Computing) { CancelSolve(); _restartRequested = true; }
                    else if (_phase == Phase.Idle) { _runButtonRequested = true; ExpireSolution(true); }
                    // Phase.Ready (publishing): the PASS 2 restart hook handles it.
                }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            }));
        }

        // On-component Run button: shows "Stop" while solving.
        public override bool IsBusy => _runActive;   // button shows "Stop" the whole time the session is live
        public override void OnRunClicked()
        {
            if (_runActive || _phase == Phase.Computing)
            {
                // Stop the live session: cancel any running solve, drop out of the queue, stop auto-restarting.
                _runActive = false;
                CancelSolve();
                try { _debounce.Stop(); } catch { }
                try { EngineGate.Nfp.Dequeue(_wake); } catch { }
                this.Message = "stopped";
                if (_phase != Phase.Computing) ExpireSolution(true);   // refresh to the held/idle state
            }
            else
            {
                // Start the live session: solve now, then auto-restart on any input change until Stop.
                _runActive = true;
                _runButtonRequested = true;
                ExpireSolution(true);
            }
        }

        // Don't expire downstream while computing (no outputs yet) — avoids a flash of null at consumers.
        protected override void ExpireDownStreamObjects()
        {
            if (_phase != Phase.Computing) base.ExpireDownStreamObjects();
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            try { if (_phase == Phase.Computing && _pendingNest != null) _pendingNest.StopRequested = true; } catch { }
            StopClocks();
            try { _debounce.Stop(); } catch { }
            try { EngineGate.Nfp.Dequeue(_wake); } catch { }
            try { if (_phase == Phase.Computing) ReleaseEngine(); } catch { }   // free + wake next if we held it
            base.RemovedFromDocument(document);
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest_2;

        public override Guid ComponentGuid => new Guid("55F51CCA-4E86-4498-8FCE-38ABBE131C8C");

    }
}
