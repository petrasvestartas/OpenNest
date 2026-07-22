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
    // OpenNest1: the no-fit-polygon nester that takes RAW curves/surfaces directly (no Geometry component, no
    // attributes). PLAIN input-port component (no on-canvas options). The solve runs on a BACKGROUND thread so
    // Rhino never freezes, with a LIVE per-generation preview (orange tightening + "gen X / Y" label). Run is a
    // boolean toggle: TRUE runs and auto re-runs when an input changes; FALSE holds the last result. Reset
    // (a boolean Button) clears the whole component INSTANTLY. There is NO ESC / Stop — to stop, flip Run off
    // or press Reset. Supports multi-start Tries (run N seeds, keep the tightest).
    public class component_nest1 : GH_Component, IGH_BakeAwareObject
    {
        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest;

        public override Guid ComponentGuid => new Guid("30400898-3A5A-434F-A703-864B9309D79E");

        public override GH_Exposure Exposure { get { return GH_Exposure.primary; } }

        private BoundingBox bbox = new BoundingBox();
        private List<TextEntity> text = new List<TextEntity>();
        private List<Curve> geometry = new List<Curve>();
        private List<System.Drawing.Color> geometry_colors = new List<System.Drawing.Color>();
        private List<List<Polyline>> sheets_display = new List<List<Polyline>>();
        private List<Curve> sheets_number_display = new List<Curve>();
        private List<Polyline> simplified_borders = new List<Polyline>();
        public override BoundingBox ClippingBox => bbox;

        // ---- background solve state machine ----
        private enum Phase { Idle, Computing, Ready }
        private volatile Phase _phase = Phase.Idle;
        private System.Threading.Tasks.Task _task;
        private volatile bool _restartRequested = false;   // input changed mid-solve -> relaunch once it unwinds
        private volatile bool _haveEngine = false;
        private readonly Action _wake;                      // stable engine-queue wake delegate

        // Solve-generation token: bumped on every launch AND on every Reset/removal. The worker captures the
        // token it launched under and publishes ONLY if it still matches — so a Reset (or a newer launch) makes
        // the in-flight worker drop its result with no publish race. Instant, full reset in every phase.
        private volatile int _solveGen = 0;
        private volatile int _readyGen = -1;

        // ---- live preview (~8 fps timer reading the in-flight solver) ----
        private readonly System.Timers.Timer _timer;
        private volatile nest_lib.rhino_example _liveNest;        // the currently-solving try
        private volatile List<Polyline> _previewBorders = new List<Polyline>();
        private List<Polyline> _previewSheets = new List<Polyline>();
        private int _totalGenerations = 1;
        private volatile int _currentTry = 0;

        // ---- worker input snapshot (captured on the UI thread; the worker only reads these) ----
        private nest_rhino_lib.nest_sheets _snapSheets;
        private nest_rhino_lib.nest_geo _snapTemplate;
        private List<double> _snapParams;
        private int _snapIterations = 6;
        private int _snapTries = 1;
        private int _snapBaseSeed = 1;

        // ---- best result of the multi-start sweep (published in PASS 2) ----
        private nest_lib.rhino_example _bestNest;
        private nest_rhino_lib.nest_geo _bestGeo;
        private double _bestFitness = 0;

        // ---- published result (read by AssembleOutputs / DrawViewportWires / Bake) ----
        private List<nest_rhino_lib.nest_geo> nest_geos;
        nest_lib.rhino_example nest = null;
        nest_rhino_lib.nest_geo nest_geo = null;

        // ---- cached outputs so a no-Run / still-solving re-expire re-emits instead of blanking ----
        private bool _hasResult = false;
        private List<Polyline> _out_sheets;
        private GH_Structure<GH_Curve> _out_borders;
        private GH_Structure<GH_Integer> _out_elemid;
        private GH_Structure<GH_Transform> _out_xforms;
        private GH_Structure<GH_Integer> _out_sheetid;

        // ---- launch gating (only solve on a real trigger; prevents the restart loop) ----
        private bool _prevRun = false;
        private string _lastSig = null;

        // ---- scratch read from the input ports ----
        private double x = 0;
        private double spacing = 1;
        private int placement = 1;
        private double tolerance = 0.1;
        private int rotations = 2;
        private int iterations = 6;
        private int seed = 1;
        private int tries = 1;
        private bool run = false;

        public component_nest1()
              : base("OpenNest1", "OpenNest1",
                "Nests RAW curves/surfaces onto sheets with the no-fit-polygon solver (no Geometry component, no attributes). Runs on a background thread with a live preview, so Rhino stays responsive. Run = TRUE solves and re-solves on input change; Reset clears. Supports multi-start Tries.",
                "Params", "OpenNest2")
        {
            _timer = new System.Timers.Timer(120) { AutoReset = false };   // ~8 fps; re-armed in OnTick
            _timer.Elapsed += OnTick;
            _wake = WakeForRetry;
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("Sheets", "Sheets", "Sheets — closed planar surfaces (outer + holes) or closed curves. A single sheet is auto-copied so parts can overflow onto more.", GH_ParamAccess.tree);
            pManager.AddGeometryParameter("Geo", "Geo", "Parts to nest — closed curves or planar surfaces. Multiple data-tree branches are combined into one nest (for clustered batch nesting — one branch per sheet — use OpenNest2 or OpenNestCollision, which have a Batch option).", GH_ParamAccess.tree);
            pManager.AddNumberParameter("Spacing", "Spacing", "Gap to keep between placed parts AND from the sheet edge.\nApplied as a nesting offset directly to the parts and sheets (this component takes raw polylines, not nest_geo/nest_sheets), so it stands in for the Geometry/Sheets Offset inputs. The ORIGINAL geometry is still what gets output.", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Placement", "Placement", "Placement strategy index (0 Box, 1 Gravity, 2 Squeeze, 3 Bottom-Left).", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Tolerance", "Tolerance", "Curve simplification tolerance.", GH_ParamAccess.item, 0.1);
            pManager.AddIntegerParameter("Rotations", "Rotations", "Orientation angles per part. Fewer = much faster on large sets (the cold NFP cache grows with rotations); more = slightly tighter.", GH_ParamAccess.item, 4);
            pManager.AddIntegerParameter("Iterations", "Iterations", "Solver generations to evolve. You watch each one tighten in the preview; higher = tighter but slower. ~4–10 typical.", GH_ParamAccess.item, 6);
            pManager.AddIntegerParameter("Seed", "Seed", "Random seed for reproducible results.", GH_ParamAccess.item, 1);
            pManager.AddBooleanParameter("Reset", "Reset", "Set TRUE (wire a Button) to clear the whole component instantly and drop any running solve.", GH_ParamAccess.item, false);
            pManager.AddBooleanParameter("Run", "Run", "Wire a Boolean Toggle. TRUE = solve now and re-solve automatically when an input changes (background thread, Rhino stays responsive, live preview). FALSE = output nothing and clear the previous result (blank outputs + cleared preview).", GH_ParamAccess.item, false);

            for (int i = 2; i < pManager.ParamCount; i++) pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheet outlines used for nesting.", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "Geo", "Nested parts placed on the sheets.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("ID", "ID", "Polygon id number", GH_ParamAccess.list);
            pManager.AddTransformParameter("Transform", "Transform", "Placement transform per part.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("IDS", "IDS", "Sheet id number", GH_ParamAccess.list);
        }

        // Read the scalar param ports (2..7) into the positional list the rhino_example ctor reads BY INDEX.
        // Tries is a fixed default (single run) — no longer exposed as an input.
        private List<double> process_inputs(IGH_DataAccess DA)
        {
            spacing = 1; placement = 1; tolerance = 0.1; rotations = 4; iterations = 6; seed = 1;
            DA.GetData(2, ref spacing);
            DA.GetData(3, ref placement);
            DA.GetData(4, ref tolerance);
            DA.GetData(5, ref rotations);
            DA.GetData(6, ref iterations);
            DA.GetData(7, ref seed);
            if (iterations < 1) iterations = 1;
            if (rotations < 1) rotations = 1;

            return new List<double>
            {
                rotations, // [0] num_of_rotations
                0,         // [1] wiggle
                placement, // [2] placement_type
                spacing,   // [3] spacing
                seed,      // [4] seed  (varied per try by the sweep)
                tolerance, // [5] simplify_tolerance
                10,        // [6] mutation
                10,        // [7] population (canonical SVGnest/DeepNest default)
                0,         // [8] time
                10         // [9]
            };
        }

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lineWeight = args.DefaultCurveThickness;

            // While the background solve runs, draw the LIVE tightening preview (orange) and return early.
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
                args.Display.DrawCurve(geometry[i], col);
        }

        public override void BakeGeometry(RhinoDoc doc, List<Guid> obj_ids)
        {
            if (nest_geos != null)
                foreach (var ng in nest_geos)
                    obj_ids.AddRange(ng.bake_with_transforms());

            for (int i = 0; i < this.sheets_display.Count; i++)
                for (int j = 0; j < this.sheets_display[i].Count; j++)
                    obj_ids.Add(RhinoDoc.ActiveDoc.Objects.AddPolyline(this.sheets_display[i][j]));

            for (int i = 0; i < this.sheets_number_display.Count; i++)
                obj_ids.Add(RhinoDoc.ActiveDoc.Objects.AddCurve(this.sheets_number_display[i]));

            var view = Rhino.RhinoDoc.ActiveDoc.Views.ActiveView;
            view.Redraw();
        }

        private void ResetDisplayLists()
        {
            nest_geos = new List<nest_rhino_lib.nest_geo>();
            bbox = new BoundingBox();
            text = new List<TextEntity>();
            geometry = new List<Curve>();
            geometry_colors = new List<System.Drawing.Color>();
            sheets_display = new List<List<Polyline>>();
            sheets_number_display = new List<Curve>();
            simplified_borders = new List<Polyline>();
        }

        // Wipe the whole runtime state (used by Reset).
        private void HardClear()
        {
            nest = null; nest_geo = null;
            _hasResult = false; _lastSig = null;
            _out_sheets = null; _out_borders = null; _out_elemid = null; _out_xforms = null; _out_sheetid = null;
            _previewBorders = new List<Polyline>();
            _previewSheets = new List<Polyline>();
            _liveNest = null;
            ResetDisplayLists();
        }

        // Re-emit the last completed result so a re-expire (paused / still-solving / queued) doesn't blank outputs.
        private void EmitCachedOutputs(IGH_DataAccess DA)
        {
            if (!_hasResult) return;
            if (_out_sheets  != null) DA.SetDataList(0, _out_sheets);
            if (_out_borders != null) DA.SetDataTree(1, _out_borders);
            if (_out_elemid  != null) DA.SetDataTree(2, _out_elemid);
            if (_out_xforms  != null) DA.SetDataTree(3, _out_xforms);
            if (_out_sheetid != null) DA.SetDataTree(4, _out_sheetid);
        }

        private void ReleaseEngineIfHeld()
        {
            if (_haveEngine) { _haveEngine = false; EngineGate.Nfp.Release(); }
        }

        private void StartClocks() { try { _timer.Start(); } catch { } }
        private void StopClocks() { try { _timer.Stop(); } catch { } }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Sheets + Geo are TREE inputs read whole (all branches combined) in one pass; the async state
            // machine must advance exactly ONCE per solution. Ignore extra iterations forced by a tree on
            // another input — also the backstop against the multi-branch infinite re-solve loop.
            if (DA.Iteration > 0) return;

            bool reset = false;
            this.run = false; this.tries = 1;
            DA.GetData(8, ref reset);
            DA.GetData(9, ref this.run);
            var parameters = process_inputs(DA);   // reads ports 2..7

            // ===== RESET (any phase): clear the whole component INSTANTLY =====
            if (reset)
            {
                _prevRun = false;
                _solveGen++;                                       // invalidate any in-flight worker's token
                EngineGate.Nfp.Dequeue(_wake);
                if (_phase != Phase.Computing) ReleaseEngineIfHeld();   // a Computing worker frees the gate in its finally
                _phase = Phase.Idle;
                StopClocks();
                HardClear();
                this.Message = "reset";
                ExpireSolution(true);
                return;
            }

            // ===== RUN OFF (input FALSE): output NOTHING and clear any previous result + preview =====
            // Mirrors Reset but silent: users asked for a clean blank when Run is off, not the held last layout.
            // A solve in flight is invalidated via _solveGen (its worker drops the result); it frees the gate in
            // its own finally, so only release here when NOT computing.
            if (!this.run)
            {
                _prevRun = false;
                _solveGen++;
                EngineGate.Nfp.Dequeue(_wake);
                if (_phase != Phase.Computing) ReleaseEngineIfHeld();
                _phase = Phase.Idle;
                StopClocks();
                HardClear();
                this.Message = null;
                try { ExpirePreview(true); } catch { }                       // GH rebuilds DrawViewportWires (now empty)
                try { Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); } catch { }   // clear the stale layout from the viewport now
                return;
            }

            // ===== PASS 2: a finished background solve is waiting to publish =====
            if (_phase == Phase.Ready)
            {
                StopClocks();
                _phase = Phase.Idle;

                // Superseded by a Reset (or removal) after the worker committed -> drop it, stay cleared.
                if (_readyGen != _solveGen) return;

                nest = _bestNest; nest_geo = _bestGeo;
                ResetDisplayLists();
                if (nest != null && nest_geo != null)
                {
                    nest_geos.Add(nest_geo);   // so BakeGeometry can bake the placed parts
                    try { AssembleOutputs(DA); _hasResult = true; }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); _hasResult = false; }
                    this.Message = (_snapTries > 1 ? ("best of " + _snapTries + "   ") : "") + "fit " + _bestFitness.ToString("F3");
                }
                else { _hasResult = false; this.Message = "no result"; }

                // An input changed while we were solving -> re-solve with the new inputs.
                if (_restartRequested && this.run)
                {
                    _restartRequested = false;
                    Rhino.RhinoApp.InvokeOnUiThread((Action)(() => { try { ExpireSolution(true); } catch { } }));
                }
                return;
            }

            // ===== COMPUTING: hold the last result; note a mid-solve input change for a follow-up re-solve.
            // (The live label/preview are driven by the timer; don't touch Message here.) =====
            if (_phase == Phase.Computing)
            {
                if (this.run)
                {
                    try { if (ComputeSig(DA, parameters) != _lastSig) _restartRequested = true; } catch { }
                }
                EmitCachedOutputs(DA);
                return;
            }

            // ===== IDLE, Run ON (Run OFF is handled above). Build the inputs (needed to compute the change
            // signature and to solve). =====
            var sheets = process_sheets(DA);
            var template = process_geometry(DA);
            if (sheets == null || template == null)
            {
                _hasResult = false; ResetDisplayLists(); this.Message = "missing Sheets / Geo"; return;
            }

            string sig = ComputeSig(DA, parameters);
            bool rising = !_prevRun;   // Run just turned on
            _prevRun = true;

            // No real trigger (Run already on, inputs unchanged) -> re-emit, do NOT relaunch (breaks the loop).
            if (!rising && _hasResult && sig == _lastSig)
            {
                EmitCachedOutputs(DA);
                this.Message = (_snapTries > 1 ? ("best of " + _snapTries + "   ") : "") + "fit " + _bestFitness.ToString("F3");
                return;
            }

            // Launch a fresh background solve. Serialize against OpenNest2 (shared nfp_nest.dll); if busy, queue.
            if (!EngineGate.Nfp.TryAcquire(_wake))
            {
                this.Message = "waiting for engine…";
                EmitCachedOutputs(DA);
                return;
            }
            _haveEngine = true;

            _snapSheets = sheets;
            _snapTemplate = template;
            _snapParams = parameters;
            _snapIterations = Math.Max(1, iterations);
            _snapTries = Math.Max(1, this.tries);
            _snapBaseSeed = (parameters != null && parameters.Count > 4) ? (int)parameters[4] : 1;
            _lastSig = sig;
            _restartRequested = false;
            _bestNest = null; _bestGeo = null; _bestFitness = 0;
            _totalGenerations = _snapIterations;
            _previewBorders = new List<Polyline>();
            _previewSheets = new List<Polyline>();
            _liveNest = null;
            _currentTry = 0;

            int myGen = ++_solveGen;
            _phase = Phase.Computing;
            this.Message = (_snapTries > 1 ? ("solving… " + _snapTries + " tries") : "solving…");
            _task = new System.Threading.Tasks.Task(() => RunSolve(myGen));   // started in AfterSolveInstance
        }

        protected override void AfterSolveInstance()
        {
            if (_phase == Phase.Computing && _task != null && _task.Status == System.Threading.Tasks.TaskStatus.Created)
            {
                var t = _task; _task = null;
                StartClocks();
                t.Start(System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        // BACKGROUND THREAD: multi-start sweep — run _snapTries seeds, keep the tightest. Publishes only if its
        // generation token is still current (so a Reset / newer launch makes it drop silently). When done, flip
        // to Ready and re-expire on the UI thread (never call ExpireSolution off-thread).
        private void RunSolve(int myGen)
        {
            try
            {
                double bestFit = double.MaxValue;
                nest_lib.rhino_example bestNest = null;
                nest_rhino_lib.nest_geo bestGeo = null;

                for (int t = 0; t < _snapTries; t++)
                {
                    if (myGen != _solveGen) break;   // superseded (Reset / newer launch) -> stop the sweep
                    _currentTry = t + 1;

                    var geoTry = _snapTemplate.duplicate();
                    var sheetsRef = _snapSheets;
                    var paramsTry = new List<double>(_snapParams);
                    if (paramsTry.Count > 4) paramsTry[4] = _snapBaseSeed + t;
                    if (paramsTry.Count > 3) paramsTry[3] = 0.0;   // spacing applied UPSTREAM (geo+sheet offset) — don't double it in the solver

                    var nestTry = new nest_lib.rhino_example(ref sheetsRef, ref geoTry, paramsTry, _snapIterations);
                    nestTry.TryAllRotations = 0;   // first valid orientation per placement = far faster; GA still varies rotations

                    _liveNest = nestTry;   // expose the in-flight try to the live preview
                    nestTry.static_solver(ref geoTry);

                    double fit = nestTry.CurrentFitness;
                    if (bestNest == null || fit < bestFit) { bestFit = fit; bestNest = nestTry; bestGeo = geoTry; }
                }

                _bestNest = bestNest; _bestGeo = bestGeo; _bestFitness = bestFit;
            }
            catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            finally { ReleaseEngineIfHeld(); }   // free the engine + wake the next queued component (even if dropped)

            if (myGen != _solveGen) return;       // superseded -> never publish
            _readyGen = myGen;
            _phase = Phase.Ready;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => { if (myGen == _solveGen) { try { ExpireSolution(true); } catch { } } }));
        }

        // ~8 fps: refresh the "gen X / Y   fit Z" label and repaint the live tightening layout the solver
        // publishes in LiveSheets/LiveBorders. NO ESC, NO "stop" wording.
        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            try
            {
                var ln = _liveNest;
                int gen = ln != null ? ln.CurrentGeneration : 0;
                double fit = ln != null ? ln.CurrentFitness : 0.0;
                string tryTag = _snapTries > 1 ? ("   try " + _currentTry + "/" + _snapTries) : "";
                this.Message = "gen " + gen + " / " + _totalGenerations + "   fit " + fit.ToString("F3") + tryTag;
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        if (_phase == Phase.Computing && ln != null)
                        {
                            _previewSheets = ln.LiveSheets;
                            _previewBorders = ln.LiveBorders;
                            OnDisplayExpired(true);
                            ExpirePreview(true);
                            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
            if (_phase == Phase.Computing) _timer.Start();   // re-arm
        }

        // ---- engine arbitration: one nfp_nest solve at a time across the process (shared with OpenNest2) ----
        private void WakeForRetry()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { if (this.run && _phase == Phase.Idle) ExpireSolution(true); } catch { }
            }));
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _solveGen++;   // invalidate any in-flight worker so it never marshals onto a dead component
            StopClocks();
            try { EngineGate.Nfp.Dequeue(_wake); } catch { }
            try { if (_phase != Phase.Computing) ReleaseEngineIfHeld(); } catch { }
            base.RemovedFromDocument(document);
        }

        // ---- change signature (raw goo bounding boxes + curve samples + the scalar params + iterations/tries) ----
        // Read a geometry TREE input flattened into one list — all branches combined (this solver has no batch
        // mode, so branch structure is irrelevant to the result; combining them also stops the multi-branch
        // per-iteration re-solve loop, since a tree is now consumed in a single SolveInstance pass).
        private static List<IGH_GeometricGoo> ReadAllGoo(IGH_DataAccess DA, int index)
        {
            var flat = new List<IGH_GeometricGoo>();
            if (DA.GetDataTree(index, out Grasshopper.Kernel.Data.GH_Structure<IGH_GeometricGoo> tree) && tree != null)
                foreach (var branch in tree.Branches)
                    if (branch != null)
                        foreach (var g in branch) if (g != null) flat.Add(g);
            return flat;
        }

        private string ComputeSig(IGH_DataAccess DA, List<double> parameters)
        {
            var s = ReadAllGoo(DA, 0);
            var g = ReadAllGoo(DA, 1);
            return SigOf(s, g, this.iterations, this.tries, parameters);
        }

        private static string SigOf(List<IGH_GeometricGoo> sheets, List<IGH_GeometricGoo> geo,
                                    int iters, int tries, List<double> parameters)
        {
            long[] h = { unchecked((long)1469598103934665603) };   // FNV-1a-ish
            void MixI(long v) { unchecked { h[0] = (h[0] ^ v) * 1099511628211L; } }
            void MixD(double d) { MixI(BitConverter.DoubleToInt64Bits(Math.Round(d, 6))); }
            void MixGoo(IGH_GeometricGoo gg)
            {
                if (gg == null) { MixI(0); return; }
                try
                {
                    var bb = gg.Boundingbox;
                    MixD(bb.Min.X); MixD(bb.Min.Y); MixD(bb.Min.Z);
                    MixD(bb.Max.X); MixD(bb.Max.Y); MixD(bb.Max.Z);
                }
                catch { }
                try
                {
                    if (gg.CastTo<Curve>(out Curve c) && c != null)
                        for (int sgn = 0; sgn <= 8; sgn++)
                        {
                            try { var p = c.PointAtNormalizedLength(sgn / 8.0); MixD(p.X); MixD(p.Y); MixD(p.Z); }
                            catch { }
                        }
                }
                catch { }
            }
            MixI(iters); MixI(tries);
            if (parameters != null) foreach (var v in parameters) MixD(v);
            if (sheets != null) { MixI(sheets.Count); foreach (var gg in sheets) MixGoo(gg); }
            if (geo != null) { MixI(geo.Count); foreach (var gg in geo) MixGoo(gg); }
            return h[0].ToString();
        }

        // ====================================================================================================
        // Intake helpers: raw Goo -> nest_sheets / nest_geo.
        // ====================================================================================================

        private nest_rhino_lib.nest_sheets process_sheets(IGH_DataAccess DA)
        {
            var sheetGoo = ReadAllGoo(DA, 0);

            List<List<Polyline>> sheetSets = new List<List<Polyline>>();
            foreach (var goo in sheetGoo)
            {
                if (goo == null || !goo.IsValid) continue;
                var loops = new List<Polyline>();
                string tn = goo.TypeName;
                if (tn == "Brep" || tn == "Surface")
                {
                    if (goo.CastTo<Brep>(out Brep brep) && brep != null)
                    {
                        Curve[] rings = BrepBoundaryCurves(brep);
                        if (rings != null)
                            foreach (var c in rings) { var pl = CurveToClosedPolyline(c); if (pl != null) loops.Add(pl); }
                    }
                }
                else if (tn == "Mesh")
                {
                    if (goo.CastTo<Mesh>(out Mesh mesh) && mesh != null && mesh.IsValid)
                        foreach (var pl in MeshLoops(mesh)) if (pl != null && pl.IsValid && pl.Count > 2) loops.Add(pl);
                }
                else
                {
                    if (goo.CastTo<Curve>(out Curve crv) && crv != null) { var pl = CurveToClosedPolyline(crv); if (pl != null) loops.Add(pl); }
                }
                if (loops.Count > 0) sheetSets.Add(loops);
            }

            if (sheetSets.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No valid sheets from the Sheets input. Feed a closed PLANAR surface (outer + optional holes) or a closed curve.");
                return null;
            }

            this.x = 0;
            // AUTO-OVERFLOW: duplicate the sheet SET to the right so parts that don't fit spill onto copies.
            // Size the number of copies to the part area (min 3, max 40) — matches the OpenNest Rhino command,
            // and works whether the user supplies one sheet or several (the whole set is duplicated).
            {
                var setBB = BoundingBox.Empty;
                foreach (var s in sheetSets) foreach (var pl in s) setBB.Union(pl.BoundingBox);
                double setW = (setBB.IsValid ? (setBB.Max.X - setBB.Min.X) : 0) + 0.01;
                var partGoo = ReadAllGoo(DA, 1);
                double partAreaSum = 0;
                foreach (var pg in partGoo) { if (pg == null) continue; var b = pg.Boundingbox; if (b.IsValid) partAreaSum += (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y); }
                double sheetAreaSum = 0;
                foreach (var s in sheetSets) if (s.Count > 0) { var b = s[0].BoundingBox; sheetAreaSum += (b.Max.X - b.Min.X) * (b.Max.Y - b.Min.Y); }
                int sheetCopies = Math.Min(40, Math.Max(3, (int)Math.Ceiling(partAreaSum * 1.8 / Math.Max(1.0, sheetAreaSum)) + 2));
                var baseSheets = sheetSets.ToList();
                for (int copy = 1; copy < sheetCopies && setW > 1e-6; copy++)
                    foreach (var s in baseSheets)
                    {
                        var dup = new List<Polyline>();
                        foreach (var pl in s) { var p = new Polyline(pl); p.Transform(Transform.Translation(copy * setW, 0, 0)); dup.Add(p); }
                        sheetSets.Add(dup);
                    }
            }

            var gaps = new List<double> { 0.0 };
            var rots = new List<int> { 4 };
            int place = 0;
            var ns = new nest_rhino_lib.nest_sheets(sheetSets, gaps, rots, place);
            // Spacing also insets the sheet boundary by spacing/2 (outer shrinks, holes grow) so parts keep the
            // same clearance from the sheet edge as from each other — mirrors the Sheets component's offset.
            if (this.spacing > 0) ns.offset_sheet_boundary(this.spacing * 0.5);
            return ns;
        }

        private Polyline CurveToClosedPolyline(Curve curve)
        {
            if (curve == null) return null;
            Polyline polyline;
            if (curve.TryGetPolyline(out polyline) && polyline != null && polyline.IsValid && polyline.Count > 2)
                return polyline;
            PolylineCurve pc = curve.ToPolyline(20, 1, 0, 0, 0, 0.01, 0, 0, true);
            if (pc != null && pc.TryGetPolyline(out polyline) && polyline != null && polyline.IsValid && polyline.Count > 2)
                return polyline;
            return null;
        }

        private Polyline[] MeshLoops(Mesh mesh)
        {
            Polyline[] outlines = mesh.GetOutlines(Plane.WorldXY);
            double[] length = new double[(int)outlines.Length];
            for (int i = 0; i < (int)outlines.Length; i++) length[i] = outlines[i].Length;
            Array.Sort<double, Polyline>(length, outlines);
            return new Polyline[] { outlines[(int)outlines.Length - 1] };
        }

        private Curve[] BrepBoundaryCurves(Brep B)
        {
            if (B == null || !B.IsValid) return null;
            double mtol = (Rhino.RhinoDoc.ActiveDoc != null) ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
            if (mtol <= 0) mtol = 0.001;

            if (B.Faces.Count == 1 && B.Faces[0].IsPlanar(mtol * 2.0))
            {
                var naked = new List<Curve>();
                foreach (BrepEdge edge in B.Edges)
                    if (edge.Valence == EdgeAdjacency.Naked)
                    {
                        Curve c = edge.DuplicateCurve();
                        if (c != null) naked.Add(c);
                    }
                if (naked.Count == 0) return null;
                Curve[] joined = Curve.JoinCurves(naked, 2.1 * mtol);
                return (joined != null && joined.Length > 0) ? joined : null;
            }

            Mesh[] meshArray = Mesh.CreateFromBrep(B);
            if (meshArray == null || meshArray.Length == 0) return null;
            Mesh mesh = new Mesh();
            foreach (var mm in meshArray) if (mm != null) mesh.Append(mm);
            Polyline[] outlines = mesh.GetOutlines(Plane.WorldXY);
            if (outlines == null || outlines.Length == 0) return null;
            double[] len = new double[outlines.Length];
            for (int j = 0; j < outlines.Length; j++) len[j] = outlines[j].Length;
            Array.Sort<double, Polyline>(len, outlines);
            Polyline big = outlines[outlines.Length - 1];
            return (big != null && big.IsValid && big.Count >= 3) ? new Curve[] { big.ToNurbsCurve() } : null;
        }

        private double FastDistance(Point3d p1, Point3d p2)
        {
            double x = (p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y) + (p2.Z - p1.Z) * (p2.Z - p1.Z);
            return x;
        }

        public (List<Curve[]>, List<GeometryBase[]>, List<int>) GooToOutlines(List<IGH_GeometricGoo> geo_)
        {
            var curves = new List<Curve[]>();
            var attributes_geometries = new List<GeometryBase[]>();
            var copies = new List<int>();

            foreach (IGH_GeometricGoo goo in geo_)
            {
                if (goo == null || !goo.IsValid) continue;
                Curve[] ring = null;
                string typeName = goo.TypeName;

                if (typeName == "Brep" || typeName == "Surface")
                {
                    if (goo.CastTo<Brep>(out Brep brep))
                        ring = BrepBoundaryCurves(brep);
                }
                else if (typeName == "Mesh")
                {
                    if (goo.CastTo<Mesh>(out Mesh mesh) && mesh != null && mesh.IsValid)
                    {
                        var loops = MeshLoops(mesh);
                        if (loops != null && loops.Length > 0)
                        {
                            ring = new Curve[loops.Length];
                            for (int j = 0; j < loops.Length; j++) ring[j] = loops[j]?.ToNurbsCurve();
                        }
                    }
                }
                else if (typeName == "Curve")
                {
                    if (goo.CastTo<Curve>(out Curve curve) && curve != null && curve.IsValid)
                    {
                        Polyline polyline;
                        if (!curve.TryGetPolyline(out polyline))
                        {
                            PolylineCurve pc = curve.ToPolyline(20, 1, 0, 0, 0, 0.01, 0, 0, true);
                            if (pc != null && pc.TryGetPolyline(out polyline) && polyline != null && polyline.IsValid && polyline.Count > 2)
                                ring = new Curve[] { polyline.ToNurbsCurve() };
                            else
                                RhinoApp.WriteLine("wrongCurve");
                        }
                        else if (polyline.IsValid && polyline.Count > 2 && FastDistance(polyline[0], polyline[polyline.Count - 1]) < 0.01)
                        {
                            ring = new Curve[] { polyline.ToNurbsCurve() };
                        }
                    }
                }

                if (ring == null || ring.Length == 0) continue;
                bool ok = true;
                foreach (var c in ring) if (c == null) { ok = false; break; }
                if (!ok) continue;

                curves.Add(ring);
                attributes_geometries.Add(new GeometryBase[] { Grasshopper.Kernel.GH_Convert.ToGeometryBase(goo.DuplicateGeometry()) });
                copies.Add(1);
            }

            return (curves, attributes_geometries, copies);
        }

        private nest_rhino_lib.nest_geo process_geometry(IGH_DataAccess DA)
        {
            var geometry = ReadAllGoo(DA, 1);   // TREE input (all branches combined)

            try
            {
                (List<Curve[]>, List<GeometryBase[]>, List<int>) outlines = GooToOutlines(geometry);

                if (outlines.Item1.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "No closed outlines from " + geometry.Count + " input(s). Curves must be CLOSED; surfaces must be PLANAR.");
                    return null;
                }

                // hard_coded_input = TRUE: keep each input goo as its OWN part using its pre-grouped Curve[] —
                // a plain Curve is one ring (NO holes), a planar surface is its outer ring + its own trim loops
                // (holes). This does NOT run the global containment pass (identify_groups), so separate curves are
                // never auto-paired as holes just because one happens to sit inside another. Holes come ONLY from
                // surface input. (hard_coded_input sorts each group's rings largest-first, so the outer boundary is
                // ring 0 and the rest are holes.)
                var ng = nest_rhino_lib.nest_geo_util.geo_to_nest_geo(outlines.Item1, outlines.Item3, new List<double> { 0, 0 }, outlines.Item2, true);
                // OpenNest1 takes RAW polylines (no upstream Geometry/Sheets Offset components), so the Spacing
                // input applies the nesting offset HERE: grow each part's nesting boundary by spacing/2 (paired
                // with the sheet inset in process_sheets) so placed parts keep a `spacing` gap. The native solver
                // spacing is then forced to 0 (see RunSolve) so the gap is applied ONCE. The Geo output stays the
                // ORIGINAL geometry; only the nesting boundary (Borders) carries the offset — same as OpenNest2.
                if (this.spacing > 0) ng.offset_nesting_boundary(this.spacing * 0.5);
                return ng;
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Geometry processing failed: " + ex.Message);
                return null;
            }
        }

        // Assemble + publish outputs from the finished solve (PASS 2, UI thread), and cache them for re-emit.
        private void AssembleOutputs(IGH_DataAccess DA)
        {
            List<Polyline> output_sheets = new List<Polyline>();
            for (int i = 0; i < nest.output_sheets.Count; i++)
                for (int j = 0; j < nest.output_sheets[i].Count; j++)
                    output_sheets.Add(new Polyline(nest.output_sheets[i][j]));
            DA.SetDataList(0, output_sheets);
            this.sheets_display.AddRange(nest.output_sheets);

            GH_Structure<GH_Curve> borders = new GH_Structure<GH_Curve>();
            GH_Structure<IGH_GeometricGoo> all_geo_groups = new GH_Structure<IGH_GeometricGoo>();
            GH_Structure<GH_Integer> element_id = new GH_Structure<GH_Integer>();
            GH_Structure<GH_Transform> xforms = new GH_Structure<GH_Transform>();
            GH_Structure<GH_Integer> sheet_id = new GH_Structure<GH_Integer>();

            for (int i = 0; i < nest.output_transforms.Count; i++)
            {
                element_id.Append(new GH_Integer(i), new GH_Path(i));
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

                        if (object_type == "Curve")
                        {
                            bbox.Union(geo_temp.GetBoundingBox(false));
                            this.geometry.Add(geo_temp as Curve);
                            this.geometry_colors.Add(nest_geo.attributes[nest_geo.geometry_sorted[i][j]].ObjectColor);
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
            DA.SetDataTree(2, element_id);
            DA.SetDataTree(3, xforms);
            DA.SetDataTree(4, sheet_id);

            _out_sheets = output_sheets; _out_borders = borders; _out_elemid = element_id;
            _out_xforms = xforms; _out_sheetid = sheet_id;
        }
    }
}
