using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using NestConstructive;
using nest_rhino_lib;

namespace opennest_2
{
    // Separate component: existing engines, defaults and component IDs are untouched.
    public sealed class OpenNestConstructiveComponent : GH_Component
    {
        public OpenNestConstructiveComponent() : base("OpenNest Constructive", "Constructive",
            "Independent polygon constructive search. Uses pre-offset OpenNest Geometry and Sheets; identical rectangular stock only. " +
            "Allowed angles are explicit degrees; mirroring is opt-in. Run on a rising edge; switch off or ESC to cancel. " +
            "A deadline can return a partial layout. No equivalence to a commercial solver is claimed.", "Params", "OpenNest2") { }
        public override Guid ComponentGuid => new Guid("7B25F695-9C08-4F48-8B9C-872A720628D1");
        protected override System.Drawing.Bitmap Icon => Properties.Resources.opennest;
        Task<Native.Result> _task;
        CancellationTokenSource _cancel;
        ConstructiveRun _run;
        Native.Result _result;
        bool _previousRun, _removed;
        string _error;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("Sheets", "Sheets", "Prepared OpenNest Sheets. Identical axis-aligned rectangles and keep-outs; upstream margin retained.", GH_ParamAccess.item);
            p.AddGenericParameter("Geometry", "Geometry", "Prepared OpenNest Geometry. Outer/holes, quantities and existing clearance offsets retained.", GH_ParamAccess.item);
            p.AddNumberParameter("Seconds", "Seconds", "Positive finite deadline; valid partial layouts may be returned.", GH_ParamAccess.item, 60);
            p.AddNumberParameter("Angles", "Angles", "Allowed orientations in degrees. Empty = 0,45,...315. Intersected with each part's rotation restriction.", GH_ParamAccess.list);
            p[3].Optional = true;
            p.AddBooleanParameter("Mirror", "Mirror", "Allow local X reflection before rotation for every part. False preserves handedness.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("Max Sheets", "Max", "Maximum identical prepared sheets to use.", GH_ParamAccess.item, 5);
            p.AddBooleanParameter("Run", "Run", "False -> true starts. False or ESC requests cancellation. Toggle again to solve changed inputs.", GH_ParamAccess.item, false);
        }
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter("Sheets", "Sheets", "Prepared stock outlines used by the solver.", GH_ParamAccess.list);
            p.AddCurveParameter("Borders", "Borders", "Original un-offset part borders, grouped by part and copy.", GH_ParamAccess.tree);
            p.AddGeometryParameter("Geometry", "Geometry", "Placed original geometry, grouped by part and copy.", GH_ParamAccess.tree);
            p.AddTransformParameter("Transforms", "Transforms", "World transforms for each placed part and copy.", GH_ParamAccess.tree);
            p.AddIntegerParameter("Sheet Id", "Sheet", "Zero-based sheet ID by part and copy. Missing copies are -1.", GH_ParamAccess.tree);
            p.AddTextParameter("Status", "Status", "Completion, placed count and native budget status. Native validity concerns prepared polygons, not analytic CAD clearance certification.", GH_ParamAccess.item);
        }
        static T Unwrap<T>(object value) where T : class => value is GH_ObjectWrapper w ? w.Value as T : value as T;
        void Escape(object sender, EventArgs args) { _cancel?.Cancel(); }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            bool run = false; da.GetData(6, ref run);
            if (_task != null && !_task.IsCompleted) {
                if (!run) _cancel.Cancel();
                Message = "Searching";
                Emit(da);
                return;
            }
            if (_task != null) {
                try { _result = _task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { _error = "Cancelled before native solve started."; }
                catch (Exception e) { _error = e.Message; }
                _task = null;
                Rhino.RhinoApp.EscapeKeyPressed -= Escape;
                _cancel.Dispose(); _cancel = null;
            }
            bool launch = run && !_previousRun;
            _previousRun = run;
            if (launch) {
                try {
                    object sheetValue = null, geoValue = null;
                    if (!da.GetData(0, ref sheetValue) || !da.GetData(1, ref geoValue)) return;
                    var sheets = Unwrap<nest_sheets>(sheetValue); var geo = Unwrap<nest_geo>(geoValue);
                    if (sheets == null || geo == null) throw new ArgumentException("Connect OpenNest Sheets and Geometry outputs.");
                    double seconds = 60; int max = 5; bool mirror = false; var angles = new List<double>();
                    da.GetData(2, ref seconds); da.GetDataList(3, angles); da.GetData(4, ref mirror); da.GetData(5, ref max);
                    _run = ConstructiveRun.Prepare(sheets, geo, seconds, max, angles, mirror);
                    _result = null; _error = null; _cancel = new CancellationTokenSource();
                    var snapshot = _run; var token = _cancel.Token;
                    Rhino.RhinoApp.EscapeKeyPressed += Escape;
                    _task = Task.Run(() => Native.Solve(snapshot.Input, snapshot.Parameters, token));
                    _task.ContinueWith(_ => Rhino.RhinoApp.InvokeOnUiThread((Action)(() => {
                        if (!_removed) OnPingDocument()?.ScheduleSolution(1, d => { if (!_removed) ExpireSolution(false); });
                    })));
                }
                catch (Exception e) { _error = e.Message; }
            }
            Emit(da);
        }

        void Emit(IGH_DataAccess da)
        {
            if (_error != null) AddRuntimeMessage(GH_RuntimeMessageLevel.Error, _error);
            if (_result == null || _run == null) {
                da.SetData(5, _task == null ? (_error ?? "Idle; toggle Run to start.") : "Searching; ESC or Run=false cancels.");
                return;
            }
            var borders = new GH_Structure<GH_Curve>(); var geometry = new GH_Structure<IGH_GeometricGoo>();
            var transforms = new GH_Structure<GH_Transform>(); var sheets = new GH_Structure<GH_Integer>();
            var outlines = new List<Curve>(); var seen = new HashSet<string>();
            foreach (var p in _result.Placements) {
                if (p.part < 0 || p.part >= _run.Input.Quantities.Length || p.copy < 0 || p.copy >= _run.Input.Quantities[p.part] ||
                    p.sheet < -1 || p.sheet >= _run.SheetOrigins.Length || !seen.Add(p.part + ":" + p.copy)) {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid native placement membership."); return;
                }
                var path = new GH_Path(p.part, p.copy); sheets.Append(new GH_Integer(p.sheet), path);
                if (p.sheet < 0) continue;
                var xf = _run.WorldTransform(p); transforms.Append(new GH_Transform(xf), path);
                foreach (var ring in _run.Geometry.boundary_sorted[p.part]) {
                    var curve = ring.Item4 != null ? ring.Item4.DuplicateCurve() : ring.Item2.ToNurbsCurve();
                    curve.Transform(xf); borders.Append(new GH_Curve(curve), path);
                }
                foreach (int index in _run.Geometry.geometry_sorted[p.part]) {
                    var item = _run.Geometry.geometry[index].Duplicate(); item.Transform(xf);
                    IGH_GeometricGoo goo = GH_Convert.ToGeometricGoo(item);
                    if (goo != null) geometry.Append(goo, path);
                }
            }
            for (int s = 0; s < _result.Statistics.sheets && s < _run.Sheets.sheets.Length; s++)
                foreach (var ring in _run.Sheets.sheets[s]) outlines.Add(ring.ToNurbsCurve());
            da.SetDataList(0, outlines); da.SetDataTree(1, borders); da.SetDataTree(2, geometry);
            da.SetDataTree(3, transforms); da.SetDataTree(4, sheets);
            string status = (_result.Code == 0 ? "Complete" : "Partial") + ": " + _result.Statistics.placed + "/" +
                _result.Statistics.requested + ", " + _result.Statistics.sheets + " sheets; " + _result.Statistics.elapsed.ToString("F2") + "s";
            if (_result.Statistics.cancelled != 0) status += "; cancelled/deadline";
            Message = status; da.SetData(5, status);
            if (_result.Code != 0) AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Partial layout: some copies remain unplaced.");
        }
        public override void RemovedFromDocument(GH_Document document)
        {
            _removed = true; _cancel?.Cancel(); Rhino.RhinoApp.EscapeKeyPressed -= Escape;
            base.RemovedFromDocument(document);
        }
    }
}
