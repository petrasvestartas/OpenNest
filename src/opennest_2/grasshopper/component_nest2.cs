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
        private static readonly object _nfpGate = new object(); // the SvgNest GA uses process-global caches
        private static bool _nfpBusy = false;                   // => only one NFP solve at a time across the process

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

        private List<nest_rhino_lib.nest_geo> nest_geos;

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
            BuildOptions();
        }

        // The settings shown as on-canvas controls (dropdowns + type-in boxes), replacing the old Options
        // string. ORDER IS LOAD-BEARING: the first 9 numeric rows map to rhino_example's parameters[0..8]
        // by index; all_rotations/exact_nfp are parsed by name; the font row must stay LAST.
        private void BuildOptions()
        {
            // Only options that actually affect the C++ engine are exposed. 'wiggle' (rotation jitter) and
            // 'time' (time budget) are intentionally NOT here: the GA uses discrete rotations so rotation_limit
            // is unused, and run_cpp drives the solve by Iterations (timeBudgetSecs is hard-coded 0). They are
            // still emitted as fixed 0 in BuildOptionStrings to keep the downstream positional indices intact.
            _options.Clear();
            _options.Add(NestOption.Number("num_of_rotations", "Rotations", 8, 1, 3600, 0, "Orientations each part may try (360/n)."));
            _options.Add(NestOption.Choice("placement_type", "Packing", new[] { "Box", "Gravity", "Squeeze", "Bottom Left" }, new[] { "0", "1", "2", "3" }, 1, "Placement strategy. Bottom Left = pack each part into the lowest-then-leftmost feasible spot."));
            _options.Add(NestOption.Number("spacing", "Spacing", 0.0, 0, 1000, 2, "Gap kept between parts (model units)."));
            _options.Add(NestOption.Number("seed", "Seed", 30, 0, 100000, 0, "Random seed (same seed = same result)."));
            _options.Add(NestOption.Number("mutation", "Mutation", 10, 0, 100, 0, "GA mutation rate."));
            _options.Add(NestOption.Number("population", "Population", 10, 1, 100000, 0, "GA population size."));
            _options.Add(NestOption.Choice("all_rotations", "All Rotations", new[] { "Off", "On" }, new[] { "0", "1" }, 1, "Try every orientation per part for tightest packing — C++ engine only (the C# engine ignores it; it uses 'Rotations'). Default ON; capped at 8 orientations so a large 'Rotations' value can't hang the solver."));
            _options.Add(NestOption.Choice("element_holes", "Element Holes", new[] { "Off", "Fill" }, new[] { "0", "1" }, 1, "Nest smaller parts INSIDE larger parts' holes."));
            _options.Add(NestOption.Text("font", "Sheet Font", "MecSoft_Font-1 1", "Sheet-number label: font name + text size."));
        }

        // Rebuild the "name value" token list the existing pipeline consumes. Order is LOAD-BEARING: the C# ctor
        // (rhino_example) reads parameters[0..8] BY INDEX, so wiggle (idx 1) and time (idx 8) are emitted as fixed
        // 0 even though they aren't UI controls; all_rotations/exact_nfp are parsed by name; font stays last.
        private List<string> BuildOptionStrings()
        {
            string Tok(string key, string fallback) { var o = Opt(key); return o != null ? o.EmitToken() : key + " " + fallback; }
            return new List<string>
            {
                Tok("num_of_rotations", "8"),
                "wiggle 0",                       // not exposed (unused by the discrete GA)
                Tok("placement_type", "1"),
                Tok("spacing", "0"),
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
            pManager.AddGenericParameter("Sheets", "Sheets", "From OpenNest tab, use component Sheets.", GH_ParamAccess.item);
            pManager.AddGenericParameter("Geometry", "Geometry", "From OpenNest tab, use component Geometry.", GH_ParamAccess.item);

            // Nesting options are edited directly on the component body (dropdowns + type-in boxes), so there is
            // no longer an "Options" string input. See BuildOptions() / NestOptionsAttributes.
            pManager.AddIntegerParameter("Iterations", "Iterations", "GA generations to evolve. Each generation evaluates the whole population and keeps the best, so the result improves over generations. ~10-40 typical (a live orange preview tightens as it runs; press ESC to stop and keep the best). Pair with the 'population' option (default 10).", GH_ParamAccess.item, 10);
            // No "Run" input: use the green Run button on the component body (it turns red "Stop" while solving).
            pManager[2].Optional = true;
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
            if (nest_geos == null) nest_geos = new List<nest_rhino_lib.nest_geo>();

            // ===== phases before the result is ready: gate on Run, launch the solve, or ignore re-solves
            if (_phase != Phase.Ready)
            {
                if (_phase == Phase.Computing)
                {
                    return;   // already solving; cancel only via the on-component Stop button or ESC
                }

                // Idle: launch only when the on-component Run button was clicked.
                bool run = _runButtonRequested;
                _runButtonRequested = false;

                if (!run)
                {
                    // A re-expire WITHOUT a Run (e.g. an upstream OpenNest settled, a param/canvas change): HOLD
                    // the last completed result — re-emit its outputs and keep the preview drawn instead of wiping
                    // everything. (BeforeSolveInstance already skipped its reset for the same reason.)
                    EmitCachedOutputs(DA);
                    return;
                }

                // Run clicked: fresh solve — clear the old result + preview, prep the solver, launch.
                ResetDisplayLists();
                _previewBorders = new List<Polyline>();
                _previewSheets = new List<Polyline>();
                _hasResult = false;

                nest_rhino_lib.nest_sheets nest_sheets = null;
                DA.GetData(0, ref nest_sheets);
                nest_rhino_lib.nest_geo nest_geo_in = null;
                DA.GetData(1, ref nest_geo_in);
                if (nest_sheets == null || nest_geo_in == null) { this.Message = "missing Sheets/Geometry"; return; }

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

                // Solver backend is fixed to the C++ nfp_nest.dll engine (the managed C# path / "Engine"
                // option was removed). "all_rotations 1" => tightest packing (capped at 8 orientations).
                string engine = "cpp";
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
                DA.GetData(2, ref max_iterations);
                if (max_iterations < 1) max_iterations = 1;

                // The SvgNest GA uses process-global static caches -> only one solve at a time.
                lock (_nfpGate)
                {
                    if (_nfpBusy) { this.Message = "another OpenNest is solving — wait"; return; }
                    _nfpBusy = true;
                }

                try
                {
                    _pendingNest = new nest_lib.rhino_example(ref nest_sheets, ref nest_geo_dup, parameters, max_iterations);
                    _pendingNest.Engine = engine;
                    _pendingNest.TryAllRotations = allRotations;
                    _pendingNest.ExactNfp = exactNfp;
                    _pendingNest.UseHoles = useHoles;
                }
                catch (Exception ex)
                {
                    Rhino.RhinoApp.WriteLine(ex.ToString());
                    lock (_nfpGate) { _nfpBusy = false; }
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

                            if (nest_geo.geometry_attributes.Count > nest_geo.geometry_sorted[i][j])
                                foreach (var geometry_attibute in nest_geo.geometry_attributes[nest_geo.geometry_sorted[i][j]])
                                {
                                    GeometryBase geometry_attibute_temp = geometry_attibute.Duplicate();
                                    geometry_attibute_temp.Transform(nest_geo.xforms[i][k]);
                                    geometry_attributes.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(geometry_attibute_temp), new GH_Path(i));
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
            finally { lock (_nfpGate) { _nfpBusy = false; } }
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
                _pendingNest.StopRequested = true; _cancelled = true; this.Message = "stopping…";
            }
        }

        private void CancelSolve()
        {
            if (_phase == Phase.Computing && _pendingNest != null) { _pendingNest.StopRequested = true; _cancelled = true; }
        }

        // On-component Run button: shows "Stop" while solving.
        public override bool IsBusy => _phase == Phase.Computing;
        public override void OnRunClicked()
        {
            if (_phase == Phase.Computing) { CancelSolve(); this.Message = "stopping…"; }
            else { _runButtonRequested = true; ExpireSolution(true); }
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
            try { lock (_nfpGate) { _nfpBusy = false; } } catch { }
            base.RemovedFromDocument(document);
        }

        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest_2;

        public override Guid ComponentGuid => new Guid("55F51CCA-4E86-4498-8FCE-38ABBE131C8C");

    }
}
