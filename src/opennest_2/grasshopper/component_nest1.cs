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
using System.Drawing.Printing;

namespace opennest_2
{
    public class component_nest1 : GH_Component, IGH_BakeAwareObject
    {

        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest;

        public override Guid ComponentGuid => new Guid("30400898-3A5A-434F-A703-864B9309D79E");
        
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

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            var col = Attributes.Selected ? args.WireColour_Selected : args.WireColour;
            var lineWeight = args.DefaultCurveThickness;

            // While a background solve runs, draw the live tightening layout (orange) instead of the
            // not-yet-assembled final output. These are the solver's swapped snapshot refs (thread-safe).
            if (_phase == Phase.Computing)
            {
                var nest_ = nest;
                if (nest_ != null)
                {
                    var ls = nest_.LiveSheets;
                    if (ls != null) foreach (var pl in ls) args.Display.DrawPolyline(pl, col);
                    var lb = nest_.LiveBorders;
                    if (lb != null) foreach (var pl in lb) args.Display.DrawPolyline(pl, col, lineWeight);
                }
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

        public component_nest1()
              : base("OpenNest1", "OpenNest1",
                "Nests parts onto sheets with the no-fit-polygon solver (no attributes). Takes curves or surfaces directly; supports multi-start Tries and a live preview.",
                "Params", "OpenNest2")
        {
            _previewTimer = new System.Timers.Timer(120) { AutoReset = false };
            _previewTimer.Elapsed += OnPreviewTick;
        }


        private double x = 0;
        private double spacing = 0;
        private int placement = 0;
        private double tolerance = 0.1;
        private int rotations = 4;
        private int iterations = 1;
        private int seed = 1;
        private bool reset = true;
        private bool run = false;

        // ---- non-blocking solve + live preview + ESC-to-cancel (mirrors OpenNest2) ----
        private enum Phase { Idle, Computing, Ready }
        private volatile Phase _phase = Phase.Idle;
        private System.Threading.Tasks.Task _solveTask;
        private volatile bool _cancelled = false;
        private readonly System.Timers.Timer _previewTimer;

        // ---- multi-start (seed sweep): run N tries with different seeds, keep the TIGHTEST (lowest fitness) ----
        private int tries = 1;
        private int _sweepTries = 1;
        private int _baseSeed = 1;
        private int _baseIterations = 1;
        private List<double> _baseParams;
        private nest_rhino_lib.nest_geo _templateGeo;     // pristine geo; duplicated per try (never solved on directly)
        private nest_rhino_lib.nest_sheets _sweepSheets;  // read-only across tries (the context clones it)
        private volatile int _currentTry = 0;
        private double _bestFitness = 0;

     
        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter("Sheets", "Sheets", "Sheets — closed planar surfaces (outer + holes) or closed curves. A single sheet is auto-copied so parts can overflow onto more.", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "Geo", "Parts to nest — closed curves or planar surfaces.", GH_ParamAccess.list);
            pManager.AddNumberParameter("Spacing", "Spacing", "Gap to keep between placed parts.", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Placement", "Placement", "Placement strategy index.", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Tolerance", "Tolerance", "Curve simplification tolerance.", GH_ParamAccess.item, 0.1);
            pManager.AddIntegerParameter("Rotations", "Rotations", "Number of rotation angles to try per part.", GH_ParamAccess.item, 4);
            pManager.AddIntegerParameter("Iterations", "Iterations", "Number of solver iterations.", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Seed", "Seed", "Random seed for reproducible results.", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Tries", "Tries", "Multi-start: run this many attempts (seed, seed+1, …) and keep the TIGHTEST layout. 1 = single run; 4–8 reliably beats run-to-run variance. The live preview shows the current try; ESC stops the sweep and keeps the best so far.", GH_ParamAccess.item, 1);
            pManager.AddBooleanParameter("Reset", "Reset", "Reset the solver and clear results.", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Run", "Run", "Start the nesting solve.", GH_ParamAccess.item, false);

            for (int i = 2; i < pManager.ParamCount; i++)
            {
                pManager[i].Optional = true;
            }

        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheet outlines used for nesting.", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "Geo", "Nested parts placed on the sheets.", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("ID", "ID", "Polygon id number", GH_ParamAccess.list);
            pManager.AddTransformParameter("Transform", "Transform", "Placement transform per part.", GH_ParamAccess.list);
            pManager.AddIntegerParameter("IDS", "IDS", "Sheet id number", GH_ParamAccess.list);
        }


        private (List<double>, string) process_inputs(IGH_DataAccess DA)
        {
            this.spacing = 0;
            this.placement = 0;
            this.tolerance = 0.1;
            this.rotations = 4;
            this.iterations = 1;
            this.seed = 1;


            DA.GetData(2, ref spacing);
            DA.GetData(3, ref placement);
            DA.GetData(4, ref tolerance);
            DA.GetData(5, ref rotations);
            DA.GetData(6, ref iterations);
            DA.GetData(7, ref seed);


            spacing = run ? spacing : 0;
            placement = run ? placement : 0;
            tolerance = run ? tolerance : 0.1;
            rotations = run ? rotations : 4;
            iterations = run ? iterations : 1;
            seed = run ? seed : 1;


            var parameters = new List<double> {
                rotations, // "num_of_rotations 4",
                0, // "wiggle 0",
                placement, // "placement_type 0",
                spacing, // "spacing 0.0",
                seed, // "seed 30",
                tolerance, // "simplify_tolerance 1",
                10, // "mutation 10",
                10, // population — was 200 (20x slower); 10 is the canonical SVGnest/DeepNest default (same as OpenNest2)
                0, // "time 0",
                10 // "MecSoft_Font-1 10"
            };

            string font_and_size = "MecSoft_Font-1";

            return (parameters, font_and_size);
        }

        public Polyline ToPolyline(Curve curve, bool collapseShortSegments = true)
        {
            curve.TryGetPolyline(out Polyline polyline);
            if (collapseShortSegments)
            {
                polyline.CollapseShortSegments(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);
            }
            return polyline;
        }

        private List<Polyline> ToPolylines(IEnumerable<Curve> nurbsCurves, bool collapseShortSegments = true)
        {
            List<Polyline> polylines = new List<Polyline>(nurbsCurves.Count());
            foreach (var curve in nurbsCurves)
            {
                if (curve.TryGetPolyline(out Polyline polyline))
                {
                    polylines.Add(polyline);
                }
                else
                {
                    var poly = curve.ToPolyline(0.01, 0.01, 0.01, 100).ToPolyline();
                    if (poly != null)
                        polylines.Add(poly);
                }
            }
            return polylines;
        }

        private nest_rhino_lib.nest_sheets process_sheets(IGH_DataAccess DA)
        {
            // Sheets accept SURFACES (planar Breps, with holes) and closed curves. A surface yields its
            // outer loop + hole loops (naked-edge JoinCurves); a curve yields one closed loop. Each input
            // sheet becomes a loop set [outer, hole1, ...]; nest_sheets sorts loops by size (outer first)
            // and the cpp path forwards the holes as keep-out regions. This fixes "Data conversion failed
            // from Surface to Curve" and the downstream null-ref that occurred when a rejected surface left
            // ZERO sheets (the solver then ran with no sheet -> "Object reference not set").
            var sheetGoo = new List<IGH_GeometricGoo>();
            DA.GetDataList(0, sheetGoo);

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
                        Curve[] rings = BrepBoundaryCurves(brep);   // outer + hole loops (proven path)
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
            // A single sheet is auto-copied (11 total) so parts that don't fit overflow onto more sheets.
            // Each copy is shifted in +X by the sheet width; holes move WITH their outer loop.
            if (sheetSets.Count == 1)
            {
                var template = sheetSets[0];
                var bb = BoundingBox.Empty;
                foreach (var pl in template) bb.Union(pl.BoundingBox);
                double w = (bb.Max.X - bb.Min.X) + 0.01;
                for (int i = 1; i < 11; i++)
                {
                    var copy = new List<Polyline>();
                    foreach (var pl in template)
                    {
                        var p = new Polyline(pl);
                        p.Transform(Transform.Translation(new Vector3d(i * w, 0, 0)));
                        copy.Add(p);
                    }
                    sheetSets.Add(copy);
                }
            }

            var gaps = new List<double> { 0.0 };
            var rotations = new List<int> { 4 };
            int placement = 0;
            return new nest_rhino_lib.nest_sheets(sheetSets, gaps, rotations, placement);
        }

        // Curve -> closed XY polyline (sheet loops). Native polyline first, else fine tessellation.
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
            for (int i = 0; i < (int)outlines.Length; i++)
            {
                length[i] = outlines[i].Length;
            }
            Array.Sort<double, Polyline>(length, outlines);
            return new Polyline[] { outlines[(int)outlines.Length - 1] };
        }

        // Surface/Brep -> closed boundary loops as RAW curves (outer + holes), the SAME way the proven
        // "Geometry Srf" component does it: collect naked edges and JoinCurves them, then let
        // geo_to_nest_geo tessellate. (Pre-converting to Polyline here was fragile and made the component
        // go red on real surfaces.) Non-planar/multi-face falls back to the largest XY mesh silhouette.
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

            // Non-planar / multi-face: largest XY mesh silhouette (approximate; holes dropped).
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

        // Convert each input Goo (CURVE, SURFACE/BREP, or MESH) into its boundary polyline ring(s) and keep
        // curves[], attributes[], copies[] in LOCKSTEP (one entry per item that produced a valid ring set).
        // Surfaces/Breps go through BrepLoops -> outer + hole polylines; curves are taken/tessellated and
        // required to be closed. Items that produce nothing are skipped without desyncing the three lists.
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
                        ring = BrepBoundaryCurves(brep);   // naked-edge loops, raw curves (proven path)
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
            var geometry = new List<IGH_GeometricGoo>();
            DA.GetDataList<IGH_GeometricGoo>(1, geometry);

            try
            {
                (List<Curve[]>, List<GeometryBase[]>, List<int>) outlines = GooToOutlines(geometry);

                if (outlines.Item1.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "No closed outlines from " + geometry.Count + " input(s). Curves must be CLOSED; surfaces must be PLANAR.");
                    return null;
                }

                // hard_coded_input:false => identify_groups auto-pairs each outer ring with the smaller
                // rings it contains (big rect + smaller rect inside = one part with a hole), so a FLAT LIST
                // gets element holes detected the same way the Geometry component does.
                return nest_rhino_lib.nest_geo_util.geo_to_nest_geo(outlines.Item1, outlines.Item3, new List<double> { 0, 0 }, outlines.Item2, false);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Geometry processing failed: " + ex.Message);
                return null;
            }
        }

        nest_lib.rhino_example nest = null;
        nest_rhino_lib.nest_sheets nest_sheets = null;
        nest_rhino_lib.nest_geo nest_geo = null;
        int counter = 0;

        protected override void AfterSolveInstance()
        {
            // Start the background solve only after this SolveInstance unwinds on the UI thread, so the UI
            // never blocks. The solve runs on a worker; ESC + the ~8 fps preview clock run alongside it.
            if (_phase == Phase.Computing && _solveTask != null &&
                _solveTask.Status == System.Threading.Tasks.TaskStatus.Created)
            {
                StartClocks();
                _solveTask.Start(System.Threading.Tasks.TaskScheduler.Default);
            }
        }

        private void StartClocks()
        {
            _cancelled = false;
            try { Rhino.RhinoApp.EscapeKeyPressed += OnEscape; } catch { }
            _previewTimer.Start();
        }

        private void StopClocks()
        {
            try { Rhino.RhinoApp.EscapeKeyPressed -= OnEscape; } catch { }
            try { _previewTimer.Stop(); } catch { }
        }

        // ESC fires on the UI thread (free now that the solve is on a worker) -> ask the solver to stop;
        // run_cpp's poll loop sees StopRequested and calls nfp_cancel, ending the native solve early.
        private void OnEscape(object sender, EventArgs e)
        {
            if (_phase == Phase.Computing && nest != null)
            {
                nest.StopRequested = true;
                _cancelled = true;
                this.Message = "stopping…";
            }
        }

        // ~8 fps: refresh the status label + repaint the live layout the solver publishes in nest.LiveBorders.
        private void OnPreviewTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            try
            {
                var nest_ = nest;
                int gen = nest_ != null ? nest_.CurrentGeneration : 0;
                double fit = nest_ != null ? nest_.CurrentFitness : 0.0;
                string tryTag = _sweepTries > 1 ? ("try " + _currentTry + "/" + _sweepTries + "   ") : "";
                this.Message = _cancelled ? ("stopping…  " + tryTag + "gen " + gen)
                                          : (tryTag + "gen " + gen + "   fit " + fit.ToString("F3") + "   (ESC = stop)");
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try
                    {
                        if (_phase == Phase.Computing && nest_ != null)
                        {
                            var bb = BoundingBox.Empty;
                            var ls = nest_.LiveSheets; if (ls != null) foreach (var pl in ls) bb.Union(pl.BoundingBox);
                            var lb = nest_.LiveBorders; if (lb != null) foreach (var pl in lb) bb.Union(pl.BoundingBox);
                            if (bb.IsValid) this.bbox = bb;
                            ExpirePreview(true);
                            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
                        }
                    }
                    catch { }
                }));
            }
            catch { }
            if (_phase == Phase.Computing) _previewTimer.Start();   // re-arm
        }

        // Runs on a worker thread. When done, flip to Ready and marshal ExpireSolution onto the UI thread
        // (NEVER call ExpireSolution from a worker) -> SolveInstance re-runs and publishes the outputs.
        // MULTI-START: run _sweepTries attempts with seeds (base, base+1, ...), keep the (nest, nest_geo)
        // with the lowest fitness (= tightest). Each try gets a fresh duplicated geo (the solve writes
        // transforms into it); the sheets are read-only so they're shared. Runs on a worker thread; when
        // done, flip to Ready and marshal ExpireSolution onto the UI thread (never call it from a worker).
        private void RunSolveBackground()
        {
            try
            {
                double bestFit = double.MaxValue;
                nest_lib.rhino_example bestNest = null;
                nest_rhino_lib.nest_geo bestGeo = null;

                for (int t = 0; t < _sweepTries; t++)
                {
                    _currentTry = t + 1;

                    var geoTry = _templateGeo.duplicate();                  // pristine copy per try
                    var sheetsRef = _sweepSheets;                           // read-only; guard the field from ref-reassign
                    var paramsTry = new List<double>(_baseParams);
                    if (paramsTry.Count > 4) paramsTry[4] = _baseSeed + t;  // vary the seed (parameters[4]) per try

                    var nestTry = new nest_lib.rhino_example(ref sheetsRef, ref geoTry, paramsTry, _baseIterations);
                    nestTry.Engine = "cpp";        // OpenNest1 always uses the fast C++ nfp_nest engine
                    nestTry.TryAllRotations = 1;   // All Rotations ON (engine caps it at 8 orientations)

                    // expose the in-flight try so the live preview + ESC act on it
                    nest = nestTry; nest_geo = geoTry;

                    nestTry.static_solver(ref geoTry);

                    double fit = nestTry.CurrentFitness;   // engine's final fitness; lower = tighter
                    if (bestNest == null || fit < bestFit) { bestFit = fit; bestNest = nestTry; bestGeo = geoTry; }

                    if (_cancelled) break;   // ESC -> stop the sweep, keep the best completed so far
                }

                if (bestNest != null) { nest = bestNest; nest_geo = bestGeo; _bestFitness = bestFit; }
            }
            catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            _phase = Phase.Ready;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => { try { ExpireSolution(true); } catch { } }));
        }



        protected override void BeforeSolveInstance()
        {
            // Only wipe the display on a genuinely fresh solve (Idle). During Computing the live preview
            // must persist; on the Ready (publish) pass the output block fills these lists itself.
            if (_phase != Phase.Idle) return;
            ResetDisplayLists();
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

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ===== PASS 2: the background solve finished -> assemble + publish outputs on the UI thread =====
            if (_phase == Phase.Ready)
            {
                StopClocks();
                try { AssembleOutputs(DA); }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
                string tag = (_sweepTries > 1 ? (" — best of " + _sweepTries) : "") + "   fit " + _bestFitness.ToString("F3");
                this.Message = (_cancelled ? "stopped" : "done") + tag;
                _phase = Phase.Idle;
                return;
            }

            // Already solving: ignore re-solves so the live preview keeps running (cancel via ESC).
            if (_phase == Phase.Computing) return;

            // ===== Idle: read inputs; launch a NON-BLOCKING (multi-start) background solve only when Run is true =====
            this.reset = true;
            this.run = false;
            this.tries = 1;
            DA.GetData(8, ref tries);
            DA.GetData(9, ref reset);
            DA.GetData(10, ref run);

            var (parameters, font_and_size) = process_inputs(DA);
            if (!run) { this.Message = null; return; }

            _sweepSheets = process_sheets(DA);
            _templateGeo = process_geometry(DA);
            if (_sweepSheets == null || _templateGeo == null) return;

            _baseParams = parameters;
            _baseSeed = (parameters != null && parameters.Count > 4) ? (int)parameters[4] : 1;
            _baseIterations = iterations;
            _sweepTries = Math.Max(1, tries);
            _bestFitness = 0;
            nest = null; nest_geo = null;   // set per-try by the sweep (read by the live preview)

            _solveTask = new System.Threading.Tasks.Task(RunSolveBackground);
            _phase = Phase.Computing;   // the task is started in AfterSolveInstance, after the UI thread frees
            this.Message = _sweepTries > 1 ? ("starting… " + _sweepTries + " tries") : "starting…";
        }

        // Assemble + publish outputs from the finished solve (runs on the UI thread in PASS 2). This is the
        // original inline output block, unchanged, just moved out of the (now background) solve path.
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
            GH_Structure<GH_Curve> sheet_txt = new GH_Structure<GH_Curve>();
            GH_Structure<IGH_GeometricGoo> geometry_attributes = new GH_Structure<IGH_GeometricGoo>();
            var doc = Rhino.RhinoDoc.ActiveDoc;

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

                        if (nest_geo.geometry_attributes.Count > nest_geo.geometry_sorted[i][j])
                            foreach (var geometry_attibute in nest_geo.geometry_attributes[nest_geo.geometry_sorted[i][j]])
                            {
                                GeometryBase geometry_attibute_temp = geometry_attibute.Duplicate();
                                geometry_attibute_temp.Transform(nest_geo.xforms[i][k]);
                                geometry_attributes.Append(Grasshopper.Kernel.GH_Convert.ToGeometricGoo(geometry_attibute_temp), new GH_Path(i));
                            }

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
        }
    }
}