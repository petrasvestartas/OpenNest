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
using NestPhysics;

namespace opennest_2
{
    // Same Grasshopper UI as OpenNest2 (component_nest2.cs), but the solver is the C++ nest_physics
    // library (the sparrow port + penetration-depth overlap proxy) reached via the P/Invoke binding
    // NestPhysicsWrapper. Sheets-with-holes and parts-with-holes are supported with KEEP-EMPTY
    // semantics: parts avoid sheet holes; each part's own holes ride along via its placement transform.
    public class component_nest : NestOptionsHostComponent, IGH_BakeAwareObject
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

        // ---- async state machine (solve runs on a background thread; UI redraws between solves) ----
        private enum Phase { Idle, Computing, Ready }
        private volatile Phase _phase = Phase.Idle;
        private System.Threading.Tasks.Task _task;
        private System.Threading.CancellationTokenSource _cts;
        private NpRun _pending;                              // flattened inputs + result, spans the two passes
        private nest_rhino_lib.nest_geo _pendingNestGeo;
        private int[] _pendingGroupBatch;                    // merged group i -> batch index (null in combined mode)
        private int[] _pendingGroupLocal;                    // merged group i -> its index WITHIN its batch
        private string _pendingFont = "MecSoft_Font-1 1";
        private long _totalBudget = 1;
        private volatile bool _cancelled = false;            // set on ESC / Run-off
        private volatile List<Polyline> _previewBorders = new List<Polyline>();  // live tightening preview (swapped, never mutated)
        private List<Polyline> _previewSheets = new List<Polyline>();            // static sheet backdrop during compute
        private readonly System.Timers.Timer _timer;         // ~8 fps clock: live label + live geometry

        // ---- Live mode: the Run button is a LATCH (ON = solve + auto-restart on any input change; OFF = idle).
        // Engine arbitration is via EngineGate.Physics (nest_physics.dll); independent of the nfp_nest gate, so
        // an OpenNestCollision can run alongside an OpenNest1/OpenNest2. ----
        private readonly System.Timers.Timer _debounce;      // one-shot; coalesces rapid input changes (slider drags)
        private volatile bool _restartRequested = false;     // a change arrived mid-solve -> restart once it unwinds
        private volatile bool _runActive = false;            // latched live session on/off
        private bool _prevRunInput = false;                  // previous value of the Run input port (edge detect)
        private string _solvedSig = null, _pendingSig = null;
        private int _solvedIter = -1, _pendingIter = -1;
        private const int DEBOUNCE_EDIT_MS = 250, DEBOUNCE_ITER_MS = 350;
        private Action _wake;                                 // stable wake delegate for the engine queue (set in ctor)

        // Cached last completed result so a no-Run / watching re-expire RE-EMITS it instead of wiping.
        private bool _hasResult = false;
        private List<Polyline> _out_sheets;
        private GH_Structure<GH_Curve> _out_borders, _out_sheettxt;
        private GH_Structure<IGH_GeometricGoo> _out_allgeo, _out_geomattr;
        private GH_Structure<GH_Transform> _out_xforms;
        private GH_Structure<GH_Integer> _out_sheetid;
        // Quality messages for the cached result. Grasshopper clears runtime messages on every solution, and
        // a "live — watching" re-expire republishes the cached outputs WITHOUT re-solving — so the overlap /
        // unplaced warnings have to be re-emitted alongside them or they vanish while the layout stays.
        private List<Tuple<GH_RuntimeMessageLevel, string>> _out_messages;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lineWeight = args.DefaultCurveThickness;

            // While the background solve runs, draw the LIVE tightening preview (snapshot the volatile
            // refs into locals first so a concurrent swap can't tear the iteration).
            if (_phase == Phase.Computing)
            {
                var psheets = _previewSheets;
                var pborders = _previewBorders;
                if (psheets != null) foreach (var pl in psheets) args.Display.DrawPolyline(pl, col);
                if (pborders != null) foreach (var pl in pborders) args.Display.DrawPolyline(pl, System.Drawing.Color.OrangeRed, lineWeight);
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

        public component_nest()
              : base("OpenNestCollision", "OpenNestCollision",
                "Nests parts onto sheets with the physics collision solver - packs tightly and can fill part cavities. Feed it the Sheets and Geometry components.",
                "Params", "OpenNest2")
        {
            // One UI clock (~8 fps). AutoReset=false + re-arm avoids tick pile-up if a frame is slow.
            _timer = new System.Timers.Timer(120) { AutoReset = false };
            _timer.Elapsed += OnTick;
            _debounce = new System.Timers.Timer(DEBOUNCE_EDIT_MS) { AutoReset = false };
            _debounce.Elapsed += OnDebounceElapsed;
            _wake = WakeForRetry;
            BuildOptions();
        }

        // Settings shown as on-canvas controls (dropdowns + type-in boxes). Defined once in NestOptionCatalog
        // (shared with the GH2 port and the Nest Options component); see the catalog for the load-bearing
        // ordering notes and for why 'spacing'/'simplify_tolerance' are not exposed.
        private void BuildOptions()
        {
            _options.Clear();
            _options.AddRange(NestOptionCatalog.Collision());
        }

        // Rebuild the "name value" token list the existing solve pipeline consumes (font emitted last).
        private List<string> BuildOptionStrings()
        {
            var list = new List<string>(_options.Count);
            foreach (var o in _options) list.Add(o.EmitToken());
            return list;
        }

        // Fires off the UI thread while computing: refresh the label (safe to set Message anywhere) and
        // marshal the geometry preview + repaints onto the UI thread (RhinoCommon is UI-thread-only).
        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            // User disabled/"blocked" the component mid-solve. A Locked component is skipped by SolveInstance,
            // so this ~8fps timer is the only place that can notice and stop the native solve. np_cancel makes
            // it return at the next round; RunSolve's finally then frees the single-solve lock. (The timer
            // re-arms only while Computing, so it self-stops once the cancelled solve leaves that phase.)
            if (this.Locked && !_cancelled)
            {
                try { NestPhysicsWrapper.np_cancel(); } catch { }
                _cancelled = true;
                try { this.Message = "stopping… (disabled)"; } catch { }
            }
            try
            {
                long done = (_pending != null) ? _pending.ProgressRounds() : NestPhysicsWrapper.np_progress();
                string batchTag = (_pending != null && _pending.BatchMode) ? ("  · batch " + (_pending.CurBatch + 1) + "/" + _pending.BatchCount) : "";
                this.Message = _cancelled ? ("stopping…  " + done + " rounds")
                                          : (done + " / ~" + _totalBudget + " rounds" + batchTag + "  (ESC = stop)");
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        if (_phase == Phase.Computing && _pending != null)
                        {
                            var b = _pending.BuildPreviewBorders();   // polls np_poll_layout + builds polylines on the UI thread
                            if (b != null) _previewBorders = b;
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
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry. When the Batch option is ON, each data-tree BRANCH is nested as its own clustered batch (kept together as one block, one batch per sheet — shape the tree upstream, e.g. Partition List, to control grouping); a flat input (one branch) is one combined nest.", GH_ParamAccess.tree);

            // Optional wired options ("key value" strings, e.g. from the Nest Options component); they override
            // the matching on-canvas option rows. Inserted BEFORE Iterations. MakeOptionsInput is shared with the
            // old-file fixup in NestOptionsHostComponent.Read.
            pManager.AddParameter(MakeOptionsInput());
            pManager.AddIntegerParameter("Iterations", "Iterations", "Relaxation rounds; higher packs tighter but is slower. ~4000 = the tight all-on-one-sheet pack; lower for a quick rough preview.", GH_ParamAccess.item, 4000);
            // Standard Run input (replaces the old on-canvas Run button). Wire a Boolean Toggle.
            pManager.AddBooleanParameter("Run", "Run", "Wire a Boolean Toggle. TRUE = solve now and re-solve when an input changes (background thread, live preview); FALSE = output nothing and clear the previous result (blank outputs + cleared preview). (Options are still edited on the component body; ESC stops a running solve but keeps the partial result while Run is held TRUE.)", GH_ParamAccess.item, false);
            pManager[2].Optional = true;   // Options
            pManager[3].Optional = true;   // Iterations (has a default)
            pManager[4].Optional = true;   // Run
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheet outline polylines used for nesting.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Borders", "Borders", "Placed part outline curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("All Geo", "All Geometry", "All placed part geometry.", GH_ParamAccess.tree);
            pManager.AddTransformParameter("Transforms", "Transforms", "Placement transforms for each part.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Sheet Id", "Sheet Id", "Index of the sheet each part landed on.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("Sheet Txt", "Sheet Txt", "Sheet-number label curves.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Attributes", "Attributes", "Per-part attribute geometry.", GH_ParamAccess.tree);
            for (int i = 0; i < pManager.ParamCount; i++)
                pManager.HideParameter(i);
        }

        protected override void BeforeSolveInstance()
        {
            // Only reset when a genuinely fresh solve is about to start (Idle). During Computing the
            // preview must persist; on the Ready (publish) pass SolveInstance does its own reset + rebuild.
            if (_phase != Phase.Idle) return;
            if (!_runButtonRequested) return;   // only a fresh launch resets; watching/queued keeps the result
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

        // Re-emit the last completed result (used on a no-Run / watching / queued re-expire so downstream and
        // the viewport keep the layout instead of blanking).
        private void EmitCachedOutputs(IGH_DataAccess DA)
        {
            if (!_hasResult) return;
            if (_out_messages != null)
                foreach (var m in _out_messages) AddRuntimeMessage(m.Item1, m.Item2);
            if (_out_sheets   != null) DA.SetDataList(0, _out_sheets);
            if (_out_borders  != null) DA.SetDataTree(1, _out_borders);
            if (_out_allgeo   != null) DA.SetDataTree(2, _out_allgeo);
            if (_out_xforms   != null) DA.SetDataTree(3, _out_xforms);
            if (_out_sheetid  != null) DA.SetDataTree(4, _out_sheetid);
            if (_out_sheettxt != null) DA.SetDataTree(5, _out_sheettxt);
            if (_out_geomattr != null) DA.SetDataTree(6, _out_geomattr);
        }

        // Run OFF: drop the cached result and ALL preview/display geometry so every output goes blank and the
        // viewport preview clears (nothing is re-emitted). Idempotent — safe to call on every no-Run solution.
        private void ClearOutputsAndPreview()
        {
            _hasResult = false;
            _out_sheets = null; _out_borders = null; _out_allgeo = null;
            _out_xforms = null; _out_sheetid = null; _out_sheettxt = null; _out_geomattr = null;
            _out_messages = null;
            nest_geos = new List<nest_rhino_lib.nest_geo>();
            bbox = new BoundingBox();
            _previewBorders = new List<Polyline>();
            _previewSheets = new List<Polyline>();
            ResetDisplayLists();
            try { ExpirePreview(true); } catch { }                       // GH rebuilds DrawViewportWires (now empty)
            try { Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); } catch { }   // clear the stale layout from the viewport now
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Sheets + Geometry are TREE inputs read whole in one pass (GetDataTree), so the async state
            // machine must advance exactly ONCE per solution. If any OTHER input is fed a tree it would drive
            // extra iterations; ignore them (the single result is emitted on iteration 0). This is also the
            // backstop against the multi-branch infinite re-solve loop.
            if (DA.Iteration > 0) return;

            // Wired options (if any) override the on-canvas rows BEFORE anything reads/signatures them, so both
            // the launch below and InputsChanged see the values actually in effect.
            ApplyWiredOptions(DA);   // Options input at index 2

            // Run is a standard input port now (was the on-canvas Run button). Edge-drive the existing latch:
            // a rising edge launches a live session; a falling edge stops it and holds the last result. Using the
            // INPUT's previous value (not _runActive) means an ESC-stop mid-solve isn't immediately relaunched
            // just because Run is still held TRUE.
            bool runInput = false; DA.GetData(4, ref runInput);

            // Run OFF (input FALSE): output NOTHING and clear any previous result + viewport preview, instead of
            // holding the last layout. Cancel a running/queued solve first; if one is still unwinding this same
            // block clears on the follow-up Ready pass (once _phase leaves Computing). ESC is separate: it stops
            // while Run is held TRUE and DOES keep the partial result.
            if (!runInput)
            {
                bool wasBusy = _runActive || _phase == Phase.Computing;
                _runActive = false;
                _prevRunInput = false;
                _restartRequested = false;   // don't let a mid-solve restart survive into the next Run-on cycle
                if (wasBusy)
                {
                    CancelSolve();
                    try { _debounce.Stop(); } catch { }
                    try { EngineGate.Physics.Dequeue(_wake); } catch { }
                }
                if (_phase == Phase.Computing) { this.Message = null; return; }   // solve still unwinding -> clears on the Ready pass
                _phase = Phase.Idle;
                StopClocks();
                ClearOutputsAndPreview();
                this.Message = null;
                return;
            }

            // Run ON: a rising edge launches a fresh live session (ESC can still stop it while Run is held TRUE).
            if (!_prevRunInput) { _runActive = true; _runButtonRequested = true; }
            _prevRunInput = true;

            // ===== phases before the result is ready: gate on Run, launch the solve, or ignore re-solves
            if (_phase != Phase.Ready)
            {
                if (_phase == Phase.Computing)
                {
                    if (!_runActive) { CancelSolve(); return; }     // ESC pressed (Run still held TRUE) -> stop the running solve
                    if (InputsChanged(DA)) ArmDebounce();           // live: input changed -> debounce -> restart
                    return;
                }

                // Idle. A "launch now" comes from the Run click that started the session, or a debounced restart.
                bool launch = _runButtonRequested;
                _runButtonRequested = false;

                // Reached with Run held TRUE but the session stopped by ESC: keep the partial result on screen
                // (toggle Run off to clear it). Run=FALSE is handled at the top of SolveInstance, never here.
                if (!_runActive) { this.Message = _hasResult ? "stopped (Esc)" : null; EmitCachedOutputs(DA); return; }

                if (!launch)
                {
                    // Run is ON but this is a plain re-expire (input/option/canvas change while live).
                    if (InputsChanged(DA)) { ArmDebounce(); this.Message = "live — restarting…"; }
                    else this.Message = "live — watching";
                    EmitCachedOutputs(DA);
                    return;
                }

                // Launch: acquire the engine, or queue behind whoever holds it (woken when it frees).
                if (!TryAcquireEngine())
                {
                    this.Message = "queued — waiting for engine…";
                    EmitCachedOutputs(DA);
                    ArmDebounce();   // backup poll in case a wake is missed
                    return;
                }
                InputsChanged(DA); _solvedSig = _pendingSig; _solvedIter = _pendingIter;

                // read inputs on the UI thread, flatten, and launch the background solve.
                ResetDisplayLists();
                this.nest_geos = new List<nest_rhino_lib.nest_geo>();   // fresh (BeforeSolveInstance no longer resets on a button click)
                this.bbox = new BoundingBox();
                _previewBorders = new List<Polyline>();
                _hasResult = false;

                // Read the Sheets + Geometry TREES (each Geometry branch is a potential batch). Everything is
                // DUPLICATED so the shared upstream geometry is never mutated. `merged` is the combined nest_geo
                // that PASS 2 consumes; `batchGeos` are the per-branch pieces (for the batch partition).
                if (!ReadNestInputs(DA, out var nest_sheets, out var batchSheets, out var batchGeos, out var merged))
                { ReleaseEngine(); this.Message = "missing Sheets/Geometry"; return; }
                // nest_sheets is already a combined duplicate (see CombineSheets); merged/batchGeos are dups too.
                this.nest_geos.Add(merged);

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

                // element_holes / poles / compact / fit / batch: parse BY NAME (robust to dropdown order). Sheet
                // holes are ALWAYS kept out when the sheet has them. element_holes: 0=off,1=fill,2=fill-first.
                int partHolesMode = 1; int poles = 48; bool compact = true; int fitMode = 0;   // 0 = all parts / fewest sheets (default)
                int batchOn = 0;
                foreach (var ln in parameters_text)
                {
                    string t = (ln ?? "").Trim();
                    var tk = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length < 2) continue;
                    if (t.StartsWith("element_holes", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int em)) partHolesMode = em;
                    else if (t.StartsWith("poles", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int pv)) poles = pv;
                    else if (t.StartsWith("compact", StringComparison.OrdinalIgnoreCase) && double.TryParse(tk[tk.Length - 1], out double cv)) compact = (cv != 0.0);
                    else if (t.StartsWith("batch", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int bo)) batchOn = bo;
                    else if (t.StartsWith("fit", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int fm)) fitMode = fm;
                }

                int max_iterations = 1;
                DA.GetData(3, ref max_iterations);   // Iterations shifted to index 3 (Options inserted at 2)

                // Decide the batch partition: OFF or a single branch -> one combined nest; ON with >= 2 Geometry
                // branches -> one batch per branch. null / <2 = combined.
                List<List<int>> partition = BuildBatchPartition(batchOn != 0, batchGeos, merged);

                // group -> (batch, local index) map so PASS 2 can key outputs batch-first: {b; localGroup} instead
                // of the flat {globalGroup}. Null in combined (non-batch) mode -> PASS 2 keeps the flat paths.
                _pendingGroupBatch = null; _pendingGroupLocal = null;
                if (partition != null && partition.Count >= 2)
                {
                    int _G = merged.boundary_sorted != null ? merged.boundary_sorted.Count : 0;
                    _pendingGroupBatch = new int[_G];
                    _pendingGroupLocal = new int[_G];
                    for (int _g = 0; _g < _G; _g++) { _pendingGroupBatch[_g] = 0; _pendingGroupLocal[_g] = _g; }
                    for (int _b = 0; _b < partition.Count; _b++)
                        for (int _li = 0; _li < partition[_b].Count; _li++)
                        {
                            int _g = partition[_b][_li];
                            if (_g >= 0 && _g < _G) { _pendingGroupBatch[_g] = _b; _pendingGroupLocal[_g] = _li; }
                        }
                }

                // Per-batch mode pairs geometry-branch b with sheet-branch b. Warn if the counts don't line up
                // (they get clamped: the last sheet branch is reused, so extra batches overlap on shared sheets).
                if (batchOn != 0 && partition != null && partition.Count >= 2 && batchSheets.Count > 1 && batchSheets.Count != partition.Count)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Sheet branches (" + batchSheets.Count + ") != Geometry batches (" + partition.Count + "). Paired by clamping; for one dedicated sheet set per batch, match the Sheets tree to the Geometry tree.");

                NpRun pending = new NpRun();
                try
                {
                    if (partition != null && partition.Count >= 2)
                        pending.SetupBatches(batchSheets, merged, partition, parameters, max_iterations, partHolesMode, poles, compact, fitMode);
                    else
                        pending.Flatten(nest_sheets, merged, parameters, max_iterations, partHolesMode, poles, compact, fitMode);
                }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); ReleaseEngine(); this.Message = "input error"; return; }
                if (!pending.Ready) { ReleaseEngine(); this.Message = "no geometry / sheet"; return; }

                _pending = pending;
                _pendingNestGeo = merged;
                _totalBudget = pending.TotalBudget;
                _cancelled = false;
                _previewSheets = pending.SheetOutlines();
                this.bbox = pending.SheetsBBox();
                _phase = Phase.Computing;
                this.Message = "starting…  (ESC = stop)";
                _cts = new System.Threading.CancellationTokenSource();
                _task = new System.Threading.Tasks.Task(() => RunSolve());
                return;   // the task is started in AfterSolveInstance, once this iteration has unwound
            }

            // Live: a change arrived while solving. The cancelled solve has unwound + freed the engine; don't
            // publish its partial result — keep the previous one and relaunch with the current inputs.
            if (_restartRequested)
            {
                _restartRequested = false;
                _phase = Phase.Idle;
                StopClocks();
                EmitCachedOutputs(DA);
                if (_runActive) ArmDebounce();
                return;
            }

            // ===== PASS 2: background solve finished -> assemble outputs, publish, stop the clock =====
            ResetDisplayLists();
            try
            {
                NpRun nest = _pending;
                var nest_geo = _pendingNestGeo;
                string font_and_size = _pendingFont;
                nest.Assemble();
                this.simplified_borders.AddRange(nest.simplified_polygons);
                // status: real round count + sheets; reflects an ESC-stop.
                this.Message = (_cancelled ? ("stopped @ " + nest.rounds_done) : (nest.rounds_done + " rounds"))
                             + "  →  " + nest.output_sheets.Count + " sheet(s)"
                             + (nest.BatchMode ? ("  · " + nest.BatchCount + " batches") : "");

                ReportSolutionQuality(nest);

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
                    // batch-first output paths in Batch mode: {batch; localGroup; ...}; combined mode keeps {group; ...}
                    int pb = (_pendingGroupBatch != null && i < _pendingGroupBatch.Length) ? _pendingGroupBatch[i] : -1;
                    int pl = (_pendingGroupLocal != null && i < _pendingGroupLocal.Length) ? _pendingGroupLocal[i] : i;
                    for (int j = 0; j < nest_geo.boundary_sorted[i].Count; j++)
                    {
                        for (int k = 0; k < nest_geo.xforms[i].Count; k++)
                        {
                            Curve crv_temp = nest_geo.boundary_sorted[i][j].Item2.Duplicate().ToNurbsCurve();
                            crv_temp.Transform(nest_geo.xforms[i][k]);
                            borders.Append(new GH_Curve(crv_temp), pb >= 0 ? new GH_Path(pb, pl) : new GH_Path(i, k));
                        }
                    }

                    for (int j = 0; j < nest_geo.geometry_sorted[i].Count; j++)
                    {
                        string object_type = nest_geo.geometry[nest_geo.geometry_sorted[i][j]].ObjectType.ToString();

                        for (int k = 0; k < nest_geo.xforms[i].Count; k++)
                        {
                            GeometryBase geo_temp = nest_geo.geometry[nest_geo.geometry_sorted[i][j]].Duplicate();
                            geo_temp.Transform(nest_geo.xforms[i][k]);
                            all_geo_groups.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(geo_temp), pb >= 0 ? new GH_Path(pb, pl) : new GH_Path(i));

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
                                    GH_Path apath = pb >= 0 ? (useSub ? new GH_Path(pb, pl, port) : new GH_Path(pb, pl))
                                                             : (useSub ? new GH_Path(i, port) : new GH_Path(i));
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
                        xforms.Append(new GH_Transform(nest_geo.xforms[i][k]), pb >= 0 ? new GH_Path(pb, pl) : new GH_Path(i));
                        sheet_id.Append(new GH_Integer(nest.output_polygon_sheet_ids[i][k]), pb >= 0 ? new GH_Path(pb, pl) : new GH_Path(i));
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

                // Cache this completed result so a later no-Run / watching / queued re-expire re-emits it.
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

        // Start the background solve only after the launch iteration has fully unwound (the Task is
        // created in SolveInstance but started here so SolveInstance has already returned).
        protected override void AfterSolveInstance()
        {
            if (_phase == Phase.Computing && _task != null && _task.Status == System.Threading.Tasks.TaskStatus.Created)
            {
                StartClocks();
                _task.Start(System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        // BACKGROUND THREAD: native solve only — no RhinoCommon / GH document calls here.
        private void RunSolve()
        {
            try { _pending.Solve(); }
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
            if (_phase == Phase.Computing) { _runActive = false; NestPhysicsWrapper.np_cancel(); _cancelled = true; this.Message = "stopping…"; }
        }

        private void CancelSolve()
        {
            if (_phase == Phase.Computing) { NestPhysicsWrapper.np_cancel(); _cancelled = true; }
        }

        // ---- Live mode helpers (mirror OpenNest2; this component's engine queue is EngineGate.Physics) ----
        private bool TryAcquireEngine() => EngineGate.Physics.TryAcquire(_wake);
        private void ReleaseEngine() => EngineGate.Physics.Release();
        private void WakeForRetry()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { if (_runActive && _phase == Phase.Idle) { _runButtonRequested = true; ExpireSolution(true); } } catch { }
            }));
        }

        // Tell the user what actually came out of the solve instead of publishing it silently. Three things
        // were previously invisible:
        //   * parts that fit on NO sheet — laid out in a row to the right with sheet id -1. The count was
        //     tracked (_unplacedTotal) and never read, so "I gave it N sheets and only got M back" looked
        //     like a solver bug when it was simply "your parts need more sheets than you supplied".
        //   * a layout the engine itself verified as OVERLAPPING. The relaxation solver optimises a
        //     penetration-depth proxy over inscribed poles, so an overlap-free result is not guaranteed;
        //     greedy_fill brute-force-checks every sheet and the ESC snapshot is explicitly best-effort.
        //     That verdict now crosses the ABI (np_last_quality) — surface it as an ERROR, because an
        //     overlapping nest cut on a real machine destroys material.
        //   * all-rectangle input, where this solver is simply the wrong tool (measured on the bench:
        //     ~72% sheet utilisation for identical rectangles vs a rectangle packer's ~96%).
        //
        // There is deliberately NO "part sticks out of its sheet" error here. The engine's drop-invalid
        // pass runs immediately before the verification, applies the same bounds predicate, and demotes
        // every violator to unplaced — so such an error could never fire, and a user-facing "please report
        // this as a bug" that cannot fire is worse than no message. The same event reaches the user
        // through the unplaced Warning below, which now says how many parts got there by demotion.
        private void ReportSolutionQuality(NpRun nest)
        {
            var msgs = new List<Tuple<GH_RuntimeMessageLevel, string>>();
            if (nest == null) { _out_messages = msgs; return; }

            if (nest.QAvailable && nest.QOverlapPairs > 0)
                msgs.Add(Tuple.Create(GH_RuntimeMessageLevel.Error,
                    nest.QOverlapPairs + " pair(s) of parts OVERLAP in this layout — do not cut it. "
                    + (nest.QCancelled
                        ? "The solve was stopped early (ESC / Run off), so the result is a best-effort snapshot; let it finish."
                        : "Raise Iterations, or use OpenNest2 (no-fit-polygon), which never publishes an overlapping placement.")));

            int unplaced = nest.UnplacedCount;
            if (unplaced > 0)
            {
                // Parts reach the overflow row two ways, and the difference is what the user should act on:
                // never placed at all (not enough sheet area) vs placed and then taken back off because the
                // placement did not actually fit that sheet (usually a sheet SMALLER than the first one).
                int demoted = nest.QAvailable ? Math.Min(nest.QDemoted, unplaced) : 0;
                msgs.Add(Tuple.Create(GH_RuntimeMessageLevel.Warning,
                    unplaced + " part(s) did not fit on the sheets you supplied and were laid out in a row to the "
                    + "RIGHT of them (Sheet Id = -1). "
                    + (demoted > 0
                        ? demoted + " of them did not fit the specific sheet the solver tried and were taken back off it"
                          + (nest.QCancelled ? " (the solve was stopped early — let it finish for a better layout). " : ". ")
                        : "")
                    + "Add sheets (Copies on the Sheets component), enlarge them, "
                    + "or remove parts. The solver never uses more sheets than you give it."));
            }

            if (AllPartsAreRectangles(_pendingNestGeo))
                msgs.Add(Tuple.Create(GH_RuntimeMessageLevel.Remark,
                    "Every part is a rectangle. OpenNestCollision is a collision/relaxation solver built for "
                    + "irregular shapes — on all-rectangle input OpenNest2 is both faster and packs tighter. "
                    + "Switch to OpenNest2 unless you specifically need this solver's behaviour."));

            _out_messages = msgs;
            foreach (var m in msgs) AddRuntimeMessage(m.Item1, m.Item2);
        }

        // True when EVERY nested outline is a 4-corner rectangle (closing duplicate tolerated, ~90 deg corners).
        // Cheap: one pass over the outer ring of each group, no Rhino native calls.
        private static bool AllPartsAreRectangles(nest_rhino_lib.nest_geo geo)
        {
            if (geo == null || geo.boundary_sorted == null || geo.boundary_sorted.Count == 0) return false;
            foreach (var group in geo.boundary_sorted)
            {
                if (group == null || group.Count == 0) return false;
                if (group.Count > 1) return false;                       // has interior holes -> not a plain rectangle
                Polyline pl = group[0].Item2;
                if (pl == null) return false;
                int n = pl.Count;
                if (n >= 2 && pl[0].DistanceTo(pl[n - 1]) < 1e-9) n--;   // drop the closing duplicate
                if (n != 4) return false;
                for (int i = 0; i < 4; i++)
                {
                    Vector3d a = pl[(i + 1) % 4] - pl[i];
                    Vector3d b = pl[(i + 2) % 4] - pl[(i + 1) % 4];
                    if (a.Length < 1e-9 || b.Length < 1e-9) return false;
                    a.Unitize(); b.Unitize();
                    if (Math.Abs(a * b) > 1e-6) return false;            // corner not square
                }
            }
            return true;
        }

        // Unwrap a generic GH goo into a native OpenNest type (the Sheets/Geometry inputs carry these as
        // GH_ObjectWrapper). Robust to the value arriving directly or wrapped.
        private static T Unwrap<T>(Grasshopper.Kernel.Types.IGH_Goo goo) where T : class
        {
            if (goo == null) return null;
            if (goo is T direct) return direct;
            try { if (goo.ScriptVariable() is T sv) return sv; } catch { }
            if (goo is Grasshopper.Kernel.Types.GH_ObjectWrapper w) return w.Value as T;
            return null;
        }

        // Combine several nest_sheets objects into ONE (concatenate every non-empty sheet) so wiring multiple
        // sheet objects makes ALL their sheets available to the solver — not just the first. Each source is
        // DUPLICATED first (the solver offsets/transforms sheets in place), so the result is safe to mutate.
        internal static nest_rhino_lib.nest_sheets CombineSheets(List<nest_rhino_lib.nest_sheets> list)
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

        // Read the Sheets + Geometry TREES. Sheets = the first nest_sheets found. Geometry = one batch geo per
        // NON-EMPTY branch (a branch's items merged), each DUPLICATED so the upstream is never mutated, plus
        // their overall merge (what PASS 2 consumes). Returns false if a sheet or any geometry is missing.
        private bool ReadNestInputs(IGH_DataAccess DA, out nest_rhino_lib.nest_sheets sheets,
                                    out List<nest_rhino_lib.nest_sheets> batchSheets,
                                    out List<nest_rhino_lib.nest_geo> batchGeos, out nest_rhino_lib.nest_geo merged)
        {
            sheets = null; batchSheets = new List<nest_rhino_lib.nest_sheets>();
            batchGeos = new List<nest_rhino_lib.nest_geo>(); merged = null;

            // Sheets tree: keep ONE nest_sheets per BRANCH (batchSheets, parallel to batchGeos) so a batch can
            // nest onto its OWN sheets; ALSO build the flat COMBINED pool (sheets) for the non-batch path. Each
            // CombineSheets returns fresh duplicates — safe to mutate, upstream untouched.
            var sheetList = new List<nest_rhino_lib.nest_sheets>();
            if (DA.GetDataTree(0, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> stree) && stree != null)
                foreach (var branch in stree.Branches)
                {
                    if (branch == null) continue;
                    var inBranch = new List<nest_rhino_lib.nest_sheets>();
                    foreach (var goo in branch) { var s = Unwrap<nest_rhino_lib.nest_sheets>(goo); if (s != null) { sheetList.Add(s); inBranch.Add(s); } }
                    if (inBranch.Count > 0) { var bs = CombineSheets(inBranch); if (bs != null) batchSheets.Add(bs); }
                }
            sheets = CombineSheets(sheetList);

            // ONE batch per BRANCH: the objects that share a data-tree branch are merged into a single nest_geo
            // for that batch, so the tree structure IS the batch definition (shape it upstream — Partition List,
            // Entwine, …). A single branch -> one batch (combined).
            if (DA.GetDataTree(1, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> gtree) && gtree != null)
                foreach (var branch in gtree.Branches)
                {
                    if (branch == null) continue;
                    var inBranch = new List<nest_rhino_lib.nest_geo>();
                    foreach (var goo in branch) { var g = Unwrap<nest_rhino_lib.nest_geo>(goo); if (g != null) inBranch.Add(g.duplicate()); }
                    if (inBranch.Count == 0) continue;
                    batchGeos.Add(inBranch.Count == 1 ? inBranch[0] : nest_rhino_lib.nest_geo.Merge(inBranch));
                }

            if (batchGeos.Count == 0 || sheets == null) return false;
            merged = batchGeos.Count == 1 ? batchGeos[0] : nest_rhino_lib.nest_geo.Merge(batchGeos);
            return true;
        }

        // Decide the batch partition over the MERGED geo's group indices. OFF or a single branch -> null (one
        // combined nest). ON with >= 2 Geometry branches -> one batch per branch (contiguous group ranges; the
        // merge concatenates branches in order). null when < 2 batches result.
        private static List<List<int>> BuildBatchPartition(bool batchOn, List<nest_rhino_lib.nest_geo> batchGeos,
                                                           nest_rhino_lib.nest_geo merged)
        {
            if (!batchOn || merged == null) return null;
            int G = merged.boundary_sorted != null ? merged.boundary_sorted.Count : 0;
            if (G <= 1 || batchGeos == null || batchGeos.Count < 2) return null;

            var parts = new List<List<int>>();
            int off = 0;
            foreach (var bg in batchGeos)
            {
                int c = bg.boundary_sorted != null ? bg.boundary_sorted.Count : 0;
                if (c > 0)
                {
                    var idx = new List<int>(c);
                    for (int k = 0; k < c && off + k < G; k++) idx.Add(off + k);
                    if (idx.Count > 0) parts.Add(idx);
                }
                off += c;
            }

            return parts.Count >= 2 ? parts : null;
        }

        // Cheap stable signature of the meaningful inputs (iterations + option tokens + branch structure + every
        // part-boundary and sheet point) so a real edit is told apart from an unrelated / self re-expire. Reads
        // the TREES directly (including branch counts, so re-batching is detected) — no merge in this hot path.
        private string SigOfTrees(IGH_DataAccess DA, int iters, List<string> optTokens)
        {
            long[] h = { unchecked((long)1469598103934665603) };
            void MixI(long v) { unchecked { h[0] = (h[0] ^ v) * 1099511628211L; } }
            void MixD(double d) { MixI(BitConverter.DoubleToInt64Bits(Math.Round(d, 6))); }
            void MixGeo(nest_rhino_lib.nest_geo g)
            {
                if (g == null || g.boundary_sorted == null) return;
                foreach (var part in g.boundary_sorted)
                    foreach (var loop in part)
                    {
                        var pl = loop.Item2; if (pl == null) continue;
                        for (int k = 0; k < pl.Count; k++) { var p = pl[k]; MixD(p.X); MixD(p.Y); MixD(p.Z); }
                    }
            }
            MixI(iters);
            if (optTokens != null) foreach (var t in optTokens) if (t != null) foreach (char c in t) MixI(c);
            try
            {
                if (DA.GetDataTree(1, out Grasshopper.Kernel.Data.GH_Structure<Grasshopper.Kernel.Types.IGH_Goo> gtree) && gtree != null)
                {
                    MixI(gtree.PathCount);
                    foreach (var branch in gtree.Branches)
                    {
                        MixI(branch != null ? branch.Count : 0);
                        if (branch == null) continue;
                        foreach (var goo in branch) MixGeo(Unwrap<nest_rhino_lib.nest_geo>(goo));
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

        private bool InputsChanged(IGH_DataAccess DA)
        {
            int it = 1; DA.GetData(3, ref it);   // Iterations at index 3 (Options at 2)
            _pendingSig = SigOfTrees(DA, it, BuildOptionStrings());
            _pendingIter = it;
            return _pendingSig != _solvedSig;
        }

        private void ArmDebounce()
        {
            try { _debounce.Stop(); } catch { }
            _debounce.Interval = (_pendingIter != _solvedIter) ? DEBOUNCE_ITER_MS : DEBOUNCE_EDIT_MS;
            _debounce.Start();
        }

        private void OnDebounceElapsed(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (!_runActive) return;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (_phase == Phase.Computing) { CancelSolve(); _restartRequested = true; }
                    else if (_phase == Phase.Idle) { _runButtonRequested = true; ExpireSolution(true); }
                }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            }));
        }

        // On-component Run button: shows "Stop" while solving.
        public override bool IsBusy => _runActive;   // "Stop" shows the whole time the live session is active
        public override void OnRunClicked()
        {
            if (_runActive || _phase == Phase.Computing)
            {
                // Stop the live session: cancel any running solve, drop out of the queue, stop auto-restarting.
                _runActive = false;
                CancelSolve();
                try { _debounce.Stop(); } catch { }
                try { EngineGate.Physics.Dequeue(_wake); } catch { }
                this.Message = "stopped";
                if (_phase != Phase.Computing) ExpireSolution(true);
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
            try { if (_phase == Phase.Computing) NestPhysicsWrapper.np_cancel(); } catch { }
            StopClocks();
            try { _debounce.Stop(); } catch { }
            try { _cts?.Dispose(); } catch { }
            try { EngineGate.Physics.Dequeue(_wake); } catch { }
            try { if (_phase == Phase.Computing) ReleaseEngine(); } catch { }   // free + wake next if we held it
            base.RemovedFromDocument(document);
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.nest_collision;

        public override Guid ComponentGuid => new Guid("B3D9F2A7-5C41-4E8A-9D6B-2A0F1C3E4D58");

    }

    // Drives the C++ nest_physics solver (via NestPhysicsWrapper / nest_physics.dll) and produces the
    // SAME output structures the existing OpenNest output-assembly expects (output_sheets,
    // output_transforms, output_polygon_sheet_ids), so the component body is reused verbatim.
    public class NpRun
    {
        public List<List<Polyline>> output_sheets = new List<List<Polyline>>();
        public List<List<Transform>> output_transforms = new List<List<Transform>>();
        public List<List<int>> output_polygon_sheet_ids = new List<List<int>>();
        public List<Polyline> simplified_polygons = new List<Polyline>();
        public bool cancelled = false;   // set if the user pressed ESC to stop early
        public long rounds_done = 0;      // relaxation rounds actually completed (for the status label)
        public int rc = 0;

        // ---- SOLUTION QUALITY (read back from the engine after every solve) -------------------------
        // The relaxation solver optimises against a penetration-depth proxy over inscribed poles, not
        // exact polygon geometry, so a returned layout is NOT overlap-free by construction. greedy_fill
        // has always brute-force-verified every sheet it emits, and the ESC snapshot is documented as
        // "positions are as-is, may still overlap" — but none of that used to cross the ABI, so this
        // component published a broken layout exactly like a good one. np_last_quality re-verifies the
        // FINAL placements; PASS 2 turns a non-clean verdict into a Grasshopper message.
        public int QOverlapPairs = 0;    // pairs of parts that genuinely interpenetrate on a sheet. A part
                                         //   nested INSIDE another part's hole is legitimate and excluded;
                                         //   one that pokes back out through the hole wall is counted.
                                         //   "Genuinely" = deeper than a thin contact skin, because TOUCHING
                                         //   is the desired outcome of a tight nest and this drives an Error.
                                         //   The engine's skin used to be computed by scaling each part
                                         //   toward its AREA CENTROID, which is not a shrink at all for a
                                         //   concave part (a U/C centroid sits outside the material), so
                                         //   concave layouts with no overlap anywhere raised this Error:
                                         //   measured 7 of 24 U/C bench runs before the fix, 0 after.
        public int QDemoted = 0;         // parts the solver placed and the engine then took back OFF a
                                         //   sheet (they hung outside it / sat in a sheet hole). Already
                                         //   inside UnplacedCount; reported to explain WHY they are there.
        public bool QCancelled = false;  // the layout is a best-effort snapshot of a stopped solve
        public bool QAvailable = false;  // false when the loaded nest_physics.dll predates np_last_quality

        // NOT surfaced to the user: np_last_quality's out_out_of_bounds ("part is not on usable material of
        // the sheet it is reported on" — off the edge, or on one of that sheet's keep-out holes). It is an
        // internal-consistency diagnostic, and with the engine's default drop-invalid pass on it is 0 BY
        // CONSTRUCTION — that pass applies the identical two predicates immediately before the verification
        // and demotes every violator, so a message driven by it could never fire. Its replacement is
        // QDemoted, which reports the same underlying event (a part that did not really fit the sheet it
        // was put on) via the outcome the user can actually see: the part sitting in the overflow row.
        // It is still read (and discarded) rather than deleted because it is the engine's tripwire for a
        // post-pass moving a part AFTER the demotion — the sheet-id renumbering did exactly that.

        // Parts that got no sheet and were laid out in the overflow row OUTSIDE the sheets. Computed by
        // Assemble/AssembleBatches; PASS 2 reports it. (Was tracked and then never read — the user got no
        // hint that parts had silently been left off the nest.)
        public int UnplacedCount => _batchMode ? _unplacedBatchTotal : _unplacedTotal;

        // Declared here rather than in NestPhysicsWrapper so this diagnostic stays self-contained: an
        // older nest_physics.dll without the export throws EntryPointNotFoundException on first call,
        // which ReadQuality swallows (QAvailable stays false and no message is shown).
        [System.Runtime.InteropServices.DllImport("nest_physics",
            CallingConvention = System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private static extern int np_last_quality(out int overlapPairs, out int outOfBounds,
                                                  out int unplaced, out int cancelled, out int demoted);

        // Accumulate the engine's verdict for the solve that just finished (batch mode sums its batches).
        //
        // np_last_quality reports whichever solve wrote the engine's globals LAST — the verdict is
        // attributed by TIMING, not by solve (see the note on the g_q_* atomics in nest_physics_capi.cpp).
        // That is correct here only because every np_nest call is serialised: this component holds
        // EngineGate.Physics for the whole solve, and batch mode runs its inner NpRuns one at a time, so
        // ReadQuality always runs between "our np_nest returned" and "anyone else's np_nest starts".
        // If that ever changes, the verdict has to become a per-call out-param of np_nest — a lock here
        // would not help, because by then the numbers already belong to a different layout.
        private void ReadQuality()
        {
            try
            {
                np_last_quality(out int ovl, out int _oob, out int _unp, out int can, out int dem);
                QOverlapPairs += ovl; QDemoted += dem; QCancelled |= (can != 0);
                QAvailable = true;
            }
            catch { QAvailable = false; }   // DLL older than the export -> stay silent rather than guess
        }

        // ---- BATCH MODE (Batch option ON): each batch is nested into its own tight block by an inner NpRun,
        // then the blocks are shelf-tiled across the sheets so related parts stay clustered. In combined
        // mode (_batches == null) this whole block is inert and the single-solve path below runs as before. ----
        private List<NpRun> _batches = null;             // one inner NpRun per batch (null = combined mode)
        private List<nest_rhino_lib.nest_sheets> _batchSheets = null;  // each batch's OWN sheets (paired per branch)
        private int[] _batchSheetOffset = null;          // batch b's sheets start at this global sheet index
        private bool _batchMode = false;
        public volatile int CurBatch = 0;                // which batch is solving now (for the progress label)
        public int BatchCount => _batches != null ? _batches.Count : 1;
        public long BatchRoundsDone = 0;                 // rounds completed by finished batches (cosmetic progress)
        private int _unplacedBatchTotal = 0;             // parts that didn't fit their batch block (diagnostic)

        // ---- flattened inputs (built by Flatten on the UI thread; consumed by Solve/Assemble/preview) ----
        private nest_rhino_lib.nest_sheets _sheets;
        private nest_rhino_lib.nest_geo _geo;
        private int _nGroups, _nsheet, _F, _n_sheets_used;
        private Transform[] _to_xy;
        private Point3d[] _sheetOrigin;
        private List<int> _pvc, _flatGroup;
        private int[] _pvcA, _protA, _sovcA, _shcA, _hvcA, _phcA, _phvcA, _out_sheet, _psh;
        private double[] _pxyA, _soxyA, _hxyA, _phxyA, _out_tx, _out_ty, _out_angle, _ptx, _pty, _pang;
        private NpParams _np;

        public NpParams Np => _np;
        public long TotalBudget => _np.iter_budget * Math.Max(1, _np.n_starts) * (_batchMode ? Math.Max(1, BatchCount) : 1);
        public bool Ready => _F > 0 && _nsheet > 0;
        public bool BatchMode => _batchMode;

        // Cumulative rounds across all batches (for the progress label): finished batches + the live one.
        public long ProgressRounds()
        {
            long live = 0; try { live = NestPhysicsWrapper.np_progress(); } catch { }
            return _batchMode ? BatchRoundsDone + live : live;
        }

        // Build the native input arrays + per-part transforms on the UI thread (RhinoCommon geometry).
        private int _partHolesTotal = 0;   // total interior holes sent to the solver (for the diagnostic)
        private int _sheetHolesTotal = 0;  // total sheet keep-out holes sent to the solver (for the diagnostic)
        private int _unplacedTotal = 0;    // parts that didn't fit any sheet -> laid out outside (diagnostic)

        // groupSubset != null limits the flatten to those group indices of nest_geo (used for BATCH mode: the
        // inner solve of one batch against the MERGED geo — _flatGroup then holds GLOBAL group indices, so the
        // batch's placements slot straight back into the merged output). maxSheetsOverride/fitModeOverride let a
        // batch pack into ONE sheet-sized block. Combined mode passes the defaults -> behaviour is unchanged.
        public void Flatten(nest_rhino_lib.nest_sheets nest_sheets, nest_rhino_lib.nest_geo nest_geo,
                            List<double> parameters, int max_iterations, int partHolesMode = 1,
                            int poles = 0, bool compact = false, int fitMode = 0,
                            IReadOnlyList<int> groupSubset = null, int maxSheetsOverride = -1, int fitModeOverride = -1)
        {
            int nGroups = nest_geo.boundary_sorted.Count;
            IReadOnlyList<int> groups = groupSubset ?? (IReadOnlyList<int>)System.Linq.Enumerable.Range(0, nGroups).ToList();

            // per-group orientation onto WorldXY (matches rhino_example.to_xy: the part's first outer
            // vertex becomes the local frame origin). Sized to ALL groups and indexed by group index (so it
            // works when _flatGroup holds global indices); only the flattened subset is filled.
            Transform[] to_xy = new Transform[nGroups];
            for (int i = 0; i < nGroups; i++) to_xy[i] = Transform.Identity;
            foreach (int i in groups)
            {
                Polyline g_outer = nest_geo.boundary_sorted[i][0].Item2;
                Plane plane = new Plane(g_outer[0], Vector3d.XAxis, Vector3d.YAxis);
                to_xy[i] = Transform.PlaneToPlane(plane, Plane.WorldXY);
            }

            // flatten parts: one solver part per copy, OUTER ring. In FILL-HOLES mode each part's interior
            // holes (boundary_sorted[i][1..], same to_xy frame as the outer) are ALSO sent so other parts
            // may nest inside them. In keep-empty mode the holes are still emitted but the solver ignores
            // them (part_holes_mode==0), so they "ride along" as before.
            List<int> pvc = new List<int>();
            List<double> pxy = new List<double>();
            List<int> flatGroup = new List<int>();
            List<int> prot = new List<int>();   // per-flat-part rotation-count override (0 = free continuous)
            List<int> phc = new List<int>();    // part-hole count per flat part
            List<int> phvc = new List<int>();   // vertex count per part-hole (part-major)
            List<double> phxy = new List<double>();
            foreach (int i in groups)
            {
                int ncopies = 1;
                int gi = nest_geo.geometry_sorted[i][0];
                if (gi >= 0 && gi < nest_geo.copies.Count) ncopies = Math.Max(1, nest_geo.copies[gi]);
                int rotOverride = 0;   // 0 = free continuous rotation (the solver default)
                if (nest_geo.rotations != null && gi >= 0 && gi < nest_geo.rotations.Count)
                    rotOverride = Math.Max(0, nest_geo.rotations[gi]);

                Polyline outer = new Polyline(nest_geo.boundary_sorted[i][0].Item2);
                outer.Transform(to_xy[i]);
                int cnt = outer.Count;
                if (cnt >= 2 && outer[0].DistanceTo(outer[cnt - 1]) < 1e-9) cnt--; // drop closing duplicate

                // this group's interior holes (rings [1..]), in the same to_xy frame as the outer ring
                var groupHoles = new List<double[]>();
                for (int h = 1; h < nest_geo.boundary_sorted[i].Count; h++)
                {
                    Polyline hole = new Polyline(nest_geo.boundary_sorted[i][h].Item2);
                    hole.Transform(to_xy[i]);
                    int hcnt = hole.Count;
                    if (hcnt >= 2 && hole[0].DistanceTo(hole[hcnt - 1]) < 1e-9) hcnt--;
                    if (hcnt < 3) continue;
                    var hv = new double[hcnt * 2];
                    for (int k = 0; k < hcnt; k++) { hv[2 * k] = hole[k].X; hv[2 * k + 1] = hole[k].Y; }
                    groupHoles.Add(hv);
                }

                for (int c = 0; c < ncopies; c++)
                {
                    pvc.Add(cnt);
                    for (int k = 0; k < cnt; k++) { pxy.Add(outer[k].X); pxy.Add(outer[k].Y); }
                    flatGroup.Add(i);
                    prot.Add(rotOverride);
                    phc.Add(groupHoles.Count);
                    foreach (var hv in groupHoles) { phvc.Add(hv.Length / 2); phxy.AddRange(hv); }
                }
            }

            // flatten sheets (outer + holes) and record each sheet's world origin (its bbox min).
            int nsheet = 0;
            while (nsheet < nest_sheets.sheets.Length && nest_sheets.sheets[nsheet] != null && nest_sheets.sheets[nsheet].Length > 0)
                nsheet++;
            List<int> sovc = new List<int>();
            List<double> soxy = new List<double>();
            List<int> shc = new List<int>();
            List<int> hvc = new List<int>();
            List<double> hxy = new List<double>();
            Point3d[] sheetOrigin = new Point3d[nsheet];
            for (int s = 0; s < nsheet; s++)
            {
                Polyline outer = nest_sheets.sheets[s][0];
                sheetOrigin[s] = outer.BoundingBox.Min;
                int oc = outer.Count;
                sovc.Add(oc);
                for (int k = 0; k < oc; k++) { soxy.Add(outer[k].X); soxy.Add(outer[k].Y); }

                // NON-RECTANGULAR SHEET SUPPORT. The native solver strip-packs into the outline's
                // BOUNDING BOX and only treats holes as keep-out, so the area between a non-rect outline
                // and its bbox (e.g. the triangle above a slanted top edge) is wrongly usable. Reduce
                // "stay inside the outline" to "stay out of (bbox - outline)" by appending that frame as
                // extra keep-out 'holes' here, reusing the sheet-hole path (relaxer QualityZone + every
                // post-pass). A rectangular outline => empty frame => byte-identical (no engine rebuild).
                List<Polyline> frame = NonRectFrameKeepouts(outer);

                int nh = nest_sheets.sheets[s].Length - 1;
                shc.Add(nh + frame.Count);
                for (int h = 1; h <= nh; h++)
                {
                    Polyline hole = nest_sheets.sheets[s][h];
                    hvc.Add(hole.Count);
                    for (int k = 0; k < hole.Count; k++) { hxy.Add(hole[k].X); hxy.Add(hole[k].Y); }
                }
                foreach (Polyline fk in frame)
                {
                    hvc.Add(fk.Count);
                    for (int k = 0; k < fk.Count; k++) { hxy.Add(fk[k].X); hxy.Add(fk[k].Y); }
                }
            }

            // options -> NpParams. ONLY the knobs nest_physics actually uses are kept; the SvgNest-only
            // wiggle / placement_type / mutation / population / time were removed from the Options.
            // Positional order (spacing + simplify removed): [0]=num_of_rotations [1]=seed [2]=starts.
            Func<int, double, double> GetP = (idx, def) => (idx >= 0 && idx < parameters.Count) ? parameters[idx] : def;
            NpParams np = new NpParams();
            np.num_rotations = Math.Max(1, (int)GetP(0, 32));
            np.spacing = 0.0;   // spacing control removed (offset_shape not ported); real gaps need a polygon-offset port
            np.seed = (int)GetP(1, 100); if (np.seed < 0) np.seed = 0;
            np.simplify_tolerance = 0.0;   // simplify control removed: always full detail
            // The Iterations INPUT is the sole budget, mapped 1:1 to relaxation rounds: 1 = quick rough
            // preview (~sub-second), ~4000 = the tight all-on-one-sheet pack (~2 min).
            np.iter_mode = 1; np.time_budget_secs = 0; np.iter_budget = (long)Math.Max(1, max_iterations);
            // BATCH mode packs each batch into ONE sheet-sized block (maxSheetsOverride=1, fitModeOverride=1);
            // combined mode uses ALL the sheets the user supplied.
            // NOTE: this used to fall back to a hardcoded 6 when nsheet==0. That literal was unreachable (a
            // run with no sheets is stopped by `Ready`) but it hid the one place a cap can silently apply:
            // nest_physics_capi.cpp turns max_sheets<=0 into 6 bins. Pass the real count and never 0, so the
            // solver can only ever be limited by the sheets actually on the canvas. Parts that still don't
            // fit are reported to the user in PASS 2 rather than quietly dropped into the overflow row.
            np.max_sheets = maxSheetsOverride > 0 ? maxSheetsOverride : Math.Max(1, nsheet);
            np.n_starts = Math.Max(1, (int)GetP(2, 1));   // multi-start: run N seeds, keep the densest (positional [2] after spacing+simplify removed)
            np.part_holes_mode = partHolesMode;   // 0=off, 1=post-pass fill, 2=fill-first (from Element Holes dropdown)
            np.pole_max = poles > 0 ? poles : 0;      // surrogate poles/part (16 ≈ 2.7x faster; 0 = default 48)
            np.final_compact = compact ? 1 : 0;       // post-relaxation compaction slide (tighter pack)
            np.fit_mode = fitModeOverride >= 0 ? fitModeOverride : fitMode;   // 0 = all parts / fewest sheets; 1 = ONE sheet, max fill

            int F = flatGroup.Count;
            double[] out_tx = new double[F == 0 ? 1 : F];
            double[] out_ty = new double[F == 0 ? 1 : F];
            double[] out_angle = new double[F == 0 ? 1 : F];
            int[] out_sheet = new int[F == 0 ? 1 : F];
            int n_sheets_used = 0;

            for (int i = 0; i < nGroups; i++)
            {
                output_transforms.Add(new List<Transform>());
                output_polygon_sheet_ids.Add(new List<int>());
            }

            // store the flattened inputs as fields for Solve()/Assemble()/the live preview
            _sheets = nest_sheets; _geo = nest_geo;
            _nGroups = nGroups; _nsheet = nsheet; _F = F; _np = np;
            _to_xy = to_xy; _sheetOrigin = sheetOrigin; _pvc = pvc; _flatGroup = flatGroup;
            _out_tx = out_tx; _out_ty = out_ty; _out_angle = out_angle; _out_sheet = out_sheet;
            _pvcA = pvc.ToArray(); _pxyA = pxy.ToArray();
            _protA = prot.ToArray();
            _sovcA = sovc.ToArray(); _soxyA = soxy.ToArray();
            _shcA = shc.ToArray(); _hvcA = hvc.ToArray(); _hxyA = hxy.ToArray();
            _phcA = phc.ToArray();
            _phvcA = phvc.Count > 0 ? phvc.ToArray() : new int[1];
            _phxyA = phxy.Count > 0 ? phxy.ToArray() : new double[1];
            _partHolesTotal = 0; foreach (int hcount in phc) _partHolesTotal += hcount;
            _sheetHolesTotal = 0; foreach (int hcount in shc) _sheetHolesTotal += hcount;
            _ptx = new double[F == 0 ? 1 : F]; _pty = new double[F == 0 ? 1 : F];
            _pang = new double[F == 0 ? 1 : F]; _psh = new int[F == 0 ? 1 : F];
        }

        // === BATCH MODE ======================================================================
        // Build one inner NpRun per batch. `partition` is a disjoint cover of the MERGED geo's group
        // indices (branch = batch, or count-split); each batch is nested (by its inner NpRun) into ONE
        // sheet-sized block. Solve() then solves every batch and AssembleBatches() shelf-tiles the blocks
        // across the sheets so a batch never spreads out. The merged output slots line up with `merged`,
        // so component PASS 2 (which reads _pendingNestGeo = merged) is unchanged.
        public void SetupBatches(List<nest_rhino_lib.nest_sheets> batchSheets, nest_rhino_lib.nest_geo merged,
                                 List<List<int>> partition, List<double> parameters, int max_iterations,
                                 int partHolesMode, int poles, bool compact, int fitMode)
        {
            _batchMode = true;
            _geo = merged;
            _nGroups = merged.boundary_sorted.Count;

            int nb = partition.Count;

            // Pair geometry-batch b with sheet-branch b (clamp: reuse the last sheet branch if there are fewer).
            var perBatchSheets = new List<nest_rhino_lib.nest_sheets>(nb);
            for (int b = 0; b < nb; b++)
                perBatchSheets.Add(batchSheets[Math.Min(b, batchSheets.Count - 1)]);
            _batchSheets = perBatchSheets;

            // Combined pool = every batch's sheets concatenated in order (batch-major), for the preview backdrop,
            // the output_sheets emission, and the global sheet-id numbering. Batch b's local sheet k = global (k + offset).
            _sheets = component_nest.CombineSheets(perBatchSheets);
            int nsheet = 0;
            while (nsheet < _sheets.sheets.Length && _sheets.sheets[nsheet] != null && _sheets.sheets[nsheet].Length > 0)
                nsheet++;
            _nsheet = nsheet;
            _sheetOrigin = new Point3d[nsheet == 0 ? 1 : nsheet];
            for (int s = 0; s < nsheet; s++) _sheetOrigin[s] = _sheets.sheets[s][0].BoundingBox.Min;

            _batchSheetOffset = new int[nb];
            int off = 0;
            for (int b = 0; b < nb; b++) { _batchSheetOffset[b] = off; off += SheetCount(perBatchSheets[b]); }

            // _np here is only for the progress budget + diagnostic; each batch carries its own solve params.
            Func<int, double, double> GetP = (idx, def) => (idx >= 0 && idx < parameters.Count) ? parameters[idx] : def;
            NpParams np = new NpParams();
            np.num_rotations = Math.Max(1, (int)GetP(0, 32));
            np.seed = (int)GetP(1, 100); if (np.seed < 0) np.seed = 0;
            np.iter_mode = 1; np.time_budget_secs = 0; np.iter_budget = (long)Math.Max(1, max_iterations);
            np.n_starts = Math.Max(1, (int)GetP(2, 1));
            np.max_sheets = Math.Max(1, nsheet);   // progress/diagnostic only; each batch carries its own params
            np.part_holes_mode = partHolesMode; np.pole_max = poles > 0 ? poles : 0;
            np.final_compact = compact ? 1 : 0; np.fit_mode = fitMode;
            _np = np;

            // One inner NpRun per batch: nest that batch's groups (of the merged geo) across its OWN sheets, with
            // fit_mode=0 (all parts / fewest sheets) so parts SPILL across the batch's sheets instead of one block.
            _batches = new List<NpRun>(nb);
            _F = 0;
            for (int b = 0; b < nb; b++)
            {
                var bs = perBatchSheets[b];
                int bsCount = Math.Max(1, SheetCount(bs));
                var inner = new NpRun();
                inner.Flatten(bs, merged, parameters, max_iterations, partHolesMode, poles, compact, fitMode,
                              partition[b], /*maxSheetsOverride*/ bsCount, /*fitModeOverride*/ 0);
                _batches.Add(inner);
                _F += inner._F;
            }

            for (int i = 0; i < _nGroups; i++)
            {
                output_transforms.Add(new List<Transform>());
                output_polygon_sheet_ids.Add(new List<int>());
            }
            _pvc = new List<int>(); _flatGroup = new List<int>();   // unused in batch mode; keep non-null
        }

        // Count of non-empty sheets in a nest_sheets.
        private static int SheetCount(nest_rhino_lib.nest_sheets ns)
        {
            if (ns == null || ns.sheets == null) return 0;
            int n = 0;
            while (n < ns.sheets.Length && ns.sheets[n] != null && ns.sheets[n].Length > 0) n++;
            return n;
        }

        // One placed (or unplaced) instance from a batch's inner solve.
        internal struct PlacedPart { public int group; public Transform xf; public bool placed; public int sid; public BoundingBox bb; }

        // Inner-solve results: per flat part, its world transform (on sheet 0) and outer-loop world bbox.
        internal List<PlacedPart> GetPlacedParts()
        {
            var list = new List<PlacedPart>(_F);
            for (int f = 0; f < _F; f++)
            {
                int g = _flatGroup[f];
                int sid = _out_sheet[f];
                bool placed = sid >= 0 && sid < _nsheet;
                Transform xf = placed ? PartTransform(f, sid, _out_tx[f], _out_ty[f], _out_angle[f]) : Transform.Identity;
                BoundingBox bb = BoundingBox.Empty;
                if (placed)
                {
                    Polyline outer = new Polyline(_geo.boundary_sorted[g][0].Item2);
                    outer.Transform(xf);
                    bb = outer.BoundingBox;
                }
                list.Add(new PlacedPart { group = g, xf = xf, placed = placed, sid = placed ? sid : -1, bb = bb });
            }
            return list;
        }

        // ONE BATCH PER SHEET: batch b is assigned to sheet (b % nsheet), so with at least as many sheets as
        // batches each batch gets its OWN sheet (branch 0 -> sheet 0, branch 1 -> sheet 1, ...). With more
        // batches than sheets the extra ones wrap round-robin and shelf-tile onto the sheets. Each batch stays
        // one contiguous block; parts that didn't fit their block go to an overflow row outside (sheet id -1).
        // Each batch was nested (by its inner NpRun) across its OWN sheets with fit_mode=0, so every placed part
        // already sits at that batch's sheet world coordinates. Here we just collect them, remap each batch's
        // LOCAL sheet id to a GLOBAL one (batch b's sheets start at _batchSheetOffset[b] in the combined pool),
        // send any part that still didn't fit its batch's sheets to an overflow row, and emit every batch's sheets.
        private void AssembleBatches()
        {
            int nb = _batches.Count, nsheet = _nsheet;

            // overflow row (parts that didn't fit even their batch's own sheets) to the RIGHT of all sheets
            BoundingBox sheetsBB = BoundingBox.Empty;
            for (int s = 0; s < nsheet; s++)
                if (_sheets.sheets[s] != null && _sheets.sheets[s].Length > 0) sheetsBB.Union(_sheets.sheets[s][0].BoundingBox);
            double ovGap = sheetsBB.IsValid ? 0.03 * sheetsBB.Diagonal.Length : 1.0;
            double ovX = sheetsBB.IsValid ? sheetsBB.Max.X + ovGap : 0.0;
            double ovY = sheetsBB.IsValid ? sheetsBB.Min.Y : 0.0;
            int unplaced = 0;

            for (int b = 0; b < nb; b++)
            {
                int offset = (_batchSheetOffset != null && b < _batchSheetOffset.Length) ? _batchSheetOffset[b] : 0;
                foreach (var p in _batches[b].GetPlacedParts())
                {
                    Transform full; int sid;
                    if (p.placed)
                    {
                        full = p.xf;                 // already at this batch's own sheet world position
                        sid = offset + p.sid;        // local sheet id -> global sheet id (into the combined pool)
                    }
                    else
                    {
                        Polyline loc = new Polyline(_geo.boundary_sorted[p.group][0].Item2);
                        Transform toxy = Transform.PlaneToPlane(
                            new Plane(_geo.boundary_sorted[p.group][0].Item2[0], Vector3d.XAxis, Vector3d.YAxis), Plane.WorldXY);
                        loc.Transform(toxy);
                        BoundingBox lb = loc.BoundingBox;
                        full = Transform.Translation(ovX - lb.Min.X, ovY - lb.Min.Y, 0.0) * toxy;
                        ovX += (lb.Max.X - lb.Min.X) + ovGap;
                        sid = -1; unplaced++;
                    }
                    output_transforms[p.group].Add(full);
                    output_polygon_sheet_ids[p.group].Add(sid);
                }
            }
            _unplacedBatchTotal = unplaced;

            // Emit EVERY batch's sheets (so all provided sheets show; unused ones are simply empty).
            for (int s = 0; s < nsheet; s++)
                output_sheets.Add(new List<Polyline>(_sheets.sheets[s]));

            _geo.xforms = output_transforms;
        }

        // === Non-rectangular sheet support ===================================================
        // Returns keep-out polygons (world XY) tiling bbox(outer) - outer, so the strip packer (which
        // only respects the outline's BOUNDING BOX) keeps parts inside the true outline. Returns an
        // EMPTY list when the outline already IS its bounding rectangle -> nothing is sent, the strip
        // packing is byte-identical, and the proven engine binary is never touched. Decomposition is a
        // self-contained TRAPEZOIDAL (horizontal-slab) sweep: exact for convex AND concave outlines (it
        // even reproduces the single triangle for a trapezoid), no Rhino native / mesh calls, and it
        // handles the outline touching its own bbox at its extreme vertices (which defeats a Brep hole).
        private static List<Polyline> NonRectFrameKeepouts(Polyline outer)
        {
            var result = new List<Polyline>();
            if (outer == null || outer.Count < 3) return result;

            // distinct vertices (drop closing duplicate + consecutive repeats)
            var v = new List<Point2d>();
            for (int i = 0; i < outer.Count; i++)
            {
                var p = new Point2d(outer[i].X, outer[i].Y);
                if (v.Count > 0 && v[v.Count - 1].DistanceTo(p) < 1e-9) continue;
                v.Add(p);
            }
            if (v.Count > 1 && v[0].DistanceTo(v[v.Count - 1]) < 1e-9) v.RemoveAt(v.Count - 1);
            int n = v.Count;
            if (n < 3) return result;

            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (var p in v) { minx = Math.Min(minx, p.X); miny = Math.Min(miny, p.Y); maxx = Math.Max(maxx, p.X); maxy = Math.Max(maxy, p.Y); }
            double bw = maxx - minx, bh = maxy - miny;
            if (bw <= 1e-9 || bh <= 1e-9) return result;
            double bboxArea = bw * bh;

            // RECTANGULAR OUTLINE: |area| fills its bbox => empty frame => strip packing unchanged.
            double area2 = 0.0;
            for (int i = 0; i < n; i++) { var a = v[i]; var b = v[(i + 1) % n]; area2 += a.X * b.Y - b.X * a.Y; }
            if (Math.Abs(area2) * 0.5 >= bboxArea * (1.0 - 1e-6)) return result;

            double z = outer[0].Z;
            double EPS = Math.Max(1e-7, bh * 1e-9);
            double areaTol = Math.Max(1e-4, bboxArea * 1e-9);

            // Horizontal slab boundaries = all vertex Ys (so no vertex is strictly inside any slab =>
            // every outline edge crosses a slab as a single monotone segment).
            var yset = new SortedSet<double>();
            foreach (var p in v) yset.Add(p.Y);
            var ys = new List<double>(yset);

            for (int sIdx = 0; sIdx < ys.Count - 1; sIdx++)
            {
                double y0 = ys[sIdx], y1 = ys[sIdx + 1];
                if (y1 - y0 <= EPS) continue;
                double ymid = 0.5 * (y0 + y1);

                // x where each spanning outline edge crosses this slab: {x@ymid (for ordering), x@y0, x@y1}
                var cross = new List<double[]>();
                for (int i = 0; i < n; i++)
                {
                    var a = v[i]; var b = v[(i + 1) % n];
                    double lo = Math.Min(a.Y, b.Y), hi = Math.Max(a.Y, b.Y);
                    if (ymid < lo || ymid > hi || hi - lo <= EPS) continue;   // not spanning / horizontal
                    double t0 = (y0 - a.Y) / (b.Y - a.Y), t1 = (y1 - a.Y) / (b.Y - a.Y), tm = (ymid - a.Y) / (b.Y - a.Y);
                    cross.Add(new double[] { a.X + (b.X - a.X) * tm, a.X + (b.X - a.X) * t0, a.X + (b.X - a.X) * t1 });
                }
                int m = cross.Count;
                if (m < 2) continue;
                cross.Sort((p, q) => p[0].CompareTo(q[0]));

                // even-odd: outline INTERIOR is between crossing pairs (0-1),(2-3),...; the FRAME (outside,
                // within the bbox) is [minx, x0], [x1, x2], [x3, x4], ..., [x_{m-1}, maxx].
                EmitTrap(result, minx, minx, cross[0][1], cross[0][2], y0, y1, z, areaTol);
                for (int i = 1; i + 1 < m; i += 2)
                    EmitTrap(result, cross[i][1], cross[i][2], cross[i + 1][1], cross[i + 1][2], y0, y1, z, areaTol);
                EmitTrap(result, cross[m - 1][1], cross[m - 1][2], maxx, maxx, y0, y1, z, areaTol);
            }
            return result;
        }

        // Add a frame trapezoid for one slab: left edge x=(lx0@y0, lx1@y1), right edge x=(rx0@y0, rx1@y1).
        private static void EmitTrap(List<Polyline> result, double lx0, double lx1, double rx0, double rx1,
                                     double y0, double y1, double z, double areaTol)
        {
            var raw = new List<Point2d> {
                new Point2d(lx0, y0), new Point2d(rx0, y0), new Point2d(rx1, y1), new Point2d(lx1, y1)
            };
            var cl = new List<Point2d>();
            foreach (var p in raw)
                if (cl.Count == 0 || cl[cl.Count - 1].DistanceTo(p) > 1e-9) cl.Add(p);
            if (cl.Count > 1 && cl[0].DistanceTo(cl[cl.Count - 1]) < 1e-9) cl.RemoveAt(cl.Count - 1);
            if (cl.Count < 3 || PolyArea(cl) < areaTol) return;
            result.Add(ToPolyline(cl, z));
        }

        private static double PolyArea(List<Point2d> p)
        {
            double a2 = 0.0; int n = p.Count;
            for (int i = 0; i < n; i++) { var u = p[i]; var w = p[(i + 1) % n]; a2 += u.X * w.Y - w.X * u.Y; }
            return Math.Abs(a2) * 0.5;
        }

        private static Polyline ToPolyline(List<Point2d> p, double z)
        {
            var pl = new Polyline();
            foreach (var q in p) pl.Add(new Point3d(q.X, q.Y, z));
            return pl;
        }

        // BACKGROUND THREAD: the blocking native solve. No RhinoCommon calls here.
        public void Solve()
        {
            if (!Ready) return;

            // BATCH mode: reset the cancel flag ONCE, then solve every batch in turn (each into its own
            // block). A per-batch reset would wipe a mid-solve ESC; instead each batch calls SolveCore (no
            // reset), so once np_cancel fires the current batch bails and the rest return immediately.
            if (_batchMode)
            {
                NestPhysicsWrapper.np_cancel_reset();
                BatchRoundsDone = 0; rounds_done = 0;
                for (int b = 0; b < _batches.Count; b++)
                {
                    CurBatch = b;
                    _batches[b].SolveCore();
                    long r = _batches[b].rounds_done;
                    BatchRoundsDone += r; rounds_done += r;
                    // roll each batch's quality verdict up to this (outer) run, which is what PASS 2 reads
                    QOverlapPairs += _batches[b].QOverlapPairs;
                    QDemoted += _batches[b].QDemoted;
                    QCancelled |= _batches[b].QCancelled;
                    QAvailable |= _batches[b].QAvailable;
                }
                return;
            }

            NestPhysicsWrapper.np_cancel_reset();
            SolveCore();
        }

        // The bare native solve, WITHOUT resetting the cancel flag (so a batch loop can share one reset).
        public void SolveCore()
        {
            if (!Ready) return;
            rc = NestPhysicsWrapper.np_nest(
                _F, _pvcA, _pxyA, _protA, _nsheet, _sovcA, _soxyA, _shcA, _hvcA, _hxyA,
                _phcA, _phvcA, _phxyA,
                ref _np, _out_tx, _out_ty, _out_angle, _out_sheet, out _n_sheets_used);
            rounds_done = NestPhysicsWrapper.np_progress();
            ReadQuality();   // engine's verdict on the layout it just returned (see the Q* fields)
            if (rc != 0) Rhino.RhinoApp.WriteLine("nest_physics np_nest returned " + rc.ToString());
        }

        // Compose the world transform for flat part f on sheet sid (the convention Assemble + preview share).
        private Transform PartTransform(int f, int sid, double tx, double ty, double angle)
        {
            Transform tloc = Transform.Translation(tx, ty, 0.0) * Transform.Rotation(angle, Point3d.Origin);
            return Transform.Translation((Vector3d)_sheetOrigin[sid]) * tloc * _to_xy[_flatGroup[f]];
        }

        // UI THREAD: poll the running best layout + build placed border polylines for the live preview.
        public List<Polyline> BuildPreviewBorders()
        {
            var borders = new List<Polyline>();
            if (_batchMode) return borders;   // batch preview would be untiled/misleading; show the sheets only
            if (!Ready) return borders;
            int placed = NestPhysicsWrapper.np_poll_layout(_F, _ptx, _pty, _pang, _psh, out int pns);
            if (placed <= 0) return borders;
            for (int f = 0; f < _F; f++)
            {
                int sid = _psh[f];
                if (sid < 0 || sid >= _nsheet) continue;
                int g = _flatGroup[f];
                Transform full = PartTransform(f, sid, _ptx[f], _pty[f], _pang[f]);
                foreach (var tup in _geo.boundary_sorted[g])
                {
                    Polyline bl = new Polyline(tup.Item2);
                    bl.Transform(full);
                    borders.Add(bl);
                }
            }
            return borders;
        }

        // Static sheet outlines (world polylines) for the preview backdrop while computing.
        public List<Polyline> SheetOutlines()
        {
            var outlines = new List<Polyline>();
            for (int s = 0; s < _nsheet; s++)
                foreach (var pl in _sheets.sheets[s])
                    if (pl != null) outlines.Add(new Polyline(pl));
            return outlines;
        }

        public BoundingBox SheetsBBox()
        {
            BoundingBox bb = BoundingBox.Empty;
            for (int s = 0; s < _nsheet; s++)
                foreach (var pl in _sheets.sheets[s])
                    if (pl != null) bb.Union(pl.BoundingBox);
            return bb;
        }

        // UI THREAD: build the final per-group transforms and emit sheets (after Solve).
        public void Assemble()
        {
            if (_batchMode) { AssembleBatches(); return; }
            int F = _F, nsheet = _nsheet;
            _unplacedTotal = 0; for (int f = 0; f < F; f++) if (_out_sheet[f] < 0) _unplacedTotal++;

            // Overflow layout for parts that did NOT fit on any sheet (sid == -1): instead of dropping them
            // at the origin (on top of everything), lay them out in a single row just to the RIGHT of all the
            // sheets so they're clearly visible as "didn't fit". They keep their input orientation and sheet
            // id -1 (so downstream can tell they're unplaced).
            BoundingBox sheetsBB = BoundingBox.Empty;
            for (int s = 0; s < nsheet; s++)
                if (_sheets.sheets[s] != null && _sheets.sheets[s].Length > 0)
                    sheetsBB.Union(_sheets.sheets[s][0].BoundingBox);
            double ovGap = sheetsBB.IsValid ? 0.03 * sheetsBB.Diagonal.Length : 1.0;
            double ovX = sheetsBB.IsValid ? sheetsBB.Max.X + ovGap : 0.0;
            double ovY = sheetsBB.IsValid ? sheetsBB.Min.Y : 0.0;
            int unplaced = 0;

            for (int f = 0; f < F; f++)
            {
                int g = _flatGroup[f];
                int sid = _out_sheet[f];
                Transform full;
                if (sid >= 0 && sid < nsheet)
                {
                    full = PartTransform(f, sid, _out_tx[f], _out_ty[f], _out_angle[f]);
                }
                else
                {
                    // unplaced -> next slot in the overflow row outside the sheets
                    Polyline loc = new Polyline(_geo.boundary_sorted[g][0].Item2);
                    loc.Transform(_to_xy[g]);                       // into the part's local frame
                    BoundingBox lb = loc.BoundingBox;
                    full = Transform.Translation(ovX - lb.Min.X, ovY - lb.Min.Y, 0.0) * _to_xy[g];
                    ovX += (lb.Max.X - lb.Min.X) + ovGap;
                    unplaced++;
                }
                output_transforms[g].Add(full);
                output_polygon_sheet_ids[g].Add(sid);
            }
            _unplacedTotal = unplaced;

            int emit = _n_sheets_used > 0 ? _n_sheets_used : (nsheet > 0 ? 1 : 0);
            if (emit > nsheet) emit = nsheet;
            for (int s = 0; s < emit; s++)
                output_sheets.Add(new List<Polyline>(_sheets.sheets[s]));

            _geo.xforms = output_transforms;   // the output-assembly reads nest_geo.xforms
        }
    }
}
