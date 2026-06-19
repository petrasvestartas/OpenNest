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
                long done = NestPhysicsWrapper.np_progress();
                this.Message = _cancelled ? ("stopping…  " + done + " rounds")
                                          : (done + " / ~" + _totalBudget + " rounds  (ESC = stop)");
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
            pManager.AddGenericParameter("Sheets", "Sheets", "From OpenNest tab, use component Sheets.", GH_ParamAccess.item);
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry.", GH_ParamAccess.item);

            // Optional wired options ("key value" strings, e.g. from the Nest Options component); they override
            // the matching on-canvas option rows. Inserted BEFORE Iterations. MakeOptionsInput is shared with the
            // old-file fixup in NestOptionsHostComponent.Read.
            pManager.AddParameter(MakeOptionsInput());
            pManager.AddIntegerParameter("Iterations", "Iterations", "Relaxation rounds; higher packs tighter but is slower. ~4000 = the tight all-on-one-sheet pack; lower for a quick rough preview.", GH_ParamAccess.item, 4000);
            // Standard Run input (replaces the old on-canvas Run button). Wire a Boolean Toggle.
            pManager.AddBooleanParameter("Run", "Run", "Wire a Boolean Toggle. TRUE = solve now and re-solve when an input changes (background thread, live preview); FALSE = hold the last result. (Options are still edited on the component body; ESC also stops a running solve.)", GH_ParamAccess.item, false);
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
                try { EngineGate.Physics.Dequeue(_wake); } catch { }
            }
            _prevRunInput = runInput;

            // ===== phases before the result is ready: gate on Run, launch the solve, or ignore re-solves
            if (_phase != Phase.Ready)
            {
                if (_phase == Phase.Computing)
                {
                    if (!_runActive) { CancelSolve(); return; }     // Run toggled off -> stop the running solve
                    if (InputsChanged(DA)) ArmDebounce();           // live: input changed -> debounce -> restart
                    return;
                }

                // Idle. A "launch now" comes from the Run click that started the session, or a debounced restart.
                bool launch = _runButtonRequested;
                _runButtonRequested = false;

                if (!_runActive) { this.Message = null; EmitCachedOutputs(DA); return; }

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

                nest_rhino_lib.nest_sheets nest_sheets = null;
                DA.GetData(0, ref nest_sheets);
                nest_rhino_lib.nest_geo nest_geo_in = null;
                DA.GetData(1, ref nest_geo_in);
                if (nest_sheets == null || nest_geo_in == null) { ReleaseEngine(); this.Message = "missing Sheets/Geometry"; return; }

                nest_sheets = nest_sheets.duplicate();        // don't mutate the upstream sheets (shared with sibling nesters)
                var nest_geo_dup = nest_geo_in.duplicate();   // don't mutate upstream geometry
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

                // element_holes / poles / compact: parse BY NAME (robust to dropdown order). Sheet holes are
                // ALWAYS kept out when the sheet has them (no toggle). element_holes: 0=off, 1=fill, 2=fill-first.
                int partHolesMode = 1; int poles = 48; bool compact = true; int fitMode = 0;   // 0 = all parts / fewest sheets (default)
                foreach (var ln in parameters_text)
                {
                    string t = (ln ?? "").Trim();
                    var tk = t.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (tk.Length < 2) continue;
                    if (t.StartsWith("element_holes", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int em)) partHolesMode = em;
                    else if (t.StartsWith("poles", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int pv)) poles = pv;
                    else if (t.StartsWith("compact", StringComparison.OrdinalIgnoreCase) && double.TryParse(tk[tk.Length - 1], out double cv)) compact = (cv != 0.0);
                    else if (t.StartsWith("fit", StringComparison.OrdinalIgnoreCase) && int.TryParse(tk[tk.Length - 1], out int fm)) fitMode = fm;
                }

                int max_iterations = 1;
                DA.GetData(3, ref max_iterations);   // Iterations shifted to index 3 (Options inserted at 2)

                NpRun pending = new NpRun();
                try { pending.Flatten(nest_sheets, nest_geo_dup, parameters, max_iterations, partHolesMode, poles, compact, fitMode); }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); ReleaseEngine(); this.Message = "input error"; return; }
                if (!pending.Ready) { ReleaseEngine(); this.Message = "no geometry / sheet"; return; }

                _pending = pending;
                _pendingNestGeo = nest_geo_dup;
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
                             + "  →  " + nest.output_sheets.Count + " sheet(s)";

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

        // Cheap stable signature of the meaningful inputs (iterations + option tokens + every part-boundary and
        // sheet point) so a real edit is told apart from an unrelated re-expire / our own completion re-expire.
        private static string SigOf(nest_rhino_lib.nest_sheets sheets, nest_rhino_lib.nest_geo geo, int iters, List<string> optTokens)
        {
            long[] h = { unchecked((long)1469598103934665603) };
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
                            var pl = loop.Item2; if (pl == null) continue;
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

        private bool InputsChanged(IGH_DataAccess DA)
        {
            nest_rhino_lib.nest_sheets s = null; DA.GetData(0, ref s);
            nest_rhino_lib.nest_geo g = null; DA.GetData(1, ref g);
            int it = 1; DA.GetData(3, ref it);   // Iterations at index 3 (Options at 2)
            _pendingSig = SigOf(s, g, it, BuildOptionStrings());
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
        public long TotalBudget => _np.iter_budget * Math.Max(1, _np.n_starts);
        public bool Ready => _F > 0 && _nsheet > 0;

        // Build the native input arrays + per-part transforms on the UI thread (RhinoCommon geometry).
        private int _partHolesTotal = 0;   // total interior holes sent to the solver (for the diagnostic)
        private int _sheetHolesTotal = 0;  // total sheet keep-out holes sent to the solver (for the diagnostic)
        private int _unplacedTotal = 0;    // parts that didn't fit any sheet -> laid out outside (diagnostic)

        public void Flatten(nest_rhino_lib.nest_sheets nest_sheets, nest_rhino_lib.nest_geo nest_geo,
                            List<double> parameters, int max_iterations, int partHolesMode = 1,
                            int poles = 0, bool compact = false, int fitMode = 0)
        {
            int nGroups = nest_geo.boundary_sorted.Count;

            // per-group orientation onto WorldXY (matches rhino_example.to_xy: the part's first outer
            // vertex becomes the local frame origin).
            Transform[] to_xy = new Transform[nGroups];
            for (int i = 0; i < nGroups; i++)
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
            for (int i = 0; i < nGroups; i++)
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
            np.max_sheets = nsheet > 0 ? nsheet : 6;
            np.n_starts = Math.Max(1, (int)GetP(2, 1));   // multi-start: run N seeds, keep the densest (positional [2] after spacing+simplify removed)
            np.part_holes_mode = partHolesMode;   // 0=off, 1=post-pass fill, 2=fill-first (from Element Holes dropdown)
            np.pole_max = poles > 0 ? poles : 0;      // surrogate poles/part (16 ≈ 2.7x faster; 0 = default 48)
            np.final_compact = compact ? 1 : 0;       // post-relaxation compaction slide (tighter pack)
            np.fit_mode = fitMode;                    // 0 = all parts / fewest sheets; 1 = ONE sheet, max fill (overflow placed outside)

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
            NestPhysicsWrapper.np_cancel_reset();
            rc = NestPhysicsWrapper.np_nest(
                _F, _pvcA, _pxyA, _protA, _nsheet, _sovcA, _soxyA, _shcA, _hvcA, _hxyA,
                _phcA, _phvcA, _phxyA,
                ref _np, _out_tx, _out_ty, _out_angle, _out_sheet, out _n_sheets_used);
            rounds_done = NestPhysicsWrapper.np_progress();
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

        // UI THREAD: build the final per-group transforms, emit sheets, print the diagnostic (after Solve).
        public void Assemble()
        {
            int F = _F, nsheet = _nsheet;
            _unplacedTotal = 0; for (int f = 0; f < F; f++) if (_out_sheet[f] < 0) _unplacedTotal++;

            // ---- DIAGNOSTIC: what the solver received + produced (Rhino command line). ----
            {
                double totalArea = 0.0; int cur = 0;
                for (int f = 0; f < F; f++)
                {
                    int n = _pvc[f]; double a = 0.0;
                    for (int k = 0; k < n; k++)
                    {
                        int k2 = (k + 1) % n;
                        a += _pxyA[2 * (cur + k)] * _pxyA[2 * (cur + k2) + 1] - _pxyA[2 * (cur + k2)] * _pxyA[2 * (cur + k) + 1];
                    }
                    totalArea += Math.Abs(a) * 0.5; cur += n;
                }
                double sw = 0, sh = 0;
                if (nsheet > 0) { var bb = _sheets.sheets[0][0].BoundingBox; sw = bb.Max.X - bb.Min.X; sh = bb.Max.Y - bb.Min.Y; }
                double sheetArea = sw * sh;
                int totalVerts = 0; foreach (var c in _pvc) totalVerts += c;
                Rhino.RhinoApp.WriteLine(string.Format(
                    "[nest_physics] parts={0} (avg {1} verts) | sheet0 = {2:F1} x {3:F1} ({4} sheets) | part area = {5:F0} = {6:F1}% of sheet | rot={7} seed={8} iter={9} poles={13} compact={14} starts={15} | fill_holes={11} part_holes_sent={12} sheet_holes_sent={16} | RESULT sheets used = {10} unplaced(outside)={17}",
                    F, (F > 0 ? totalVerts / F : 0), sw, sh, nsheet, totalArea,
                    (sheetArea > 0 ? 100.0 * totalArea / sheetArea : 0), _np.num_rotations, _np.seed, _np.iter_budget, _n_sheets_used,
                    _np.part_holes_mode, _partHolesTotal, _np.pole_max, _np.final_compact, _np.n_starts, _sheetHolesTotal, _unplacedTotal));
            }

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
