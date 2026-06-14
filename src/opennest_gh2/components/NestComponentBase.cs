using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Grasshopper2.Components;
using Grasshopper2.Doc;        // IAttributes
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using opennest_2;              // NestOption
using opennest_gh2.attributes;

namespace opennest_gh2.components
{
    // Async two-phase base for the solver components (OpenNestCollision, OpenNest2), mirroring GH1's proven
    // state machine but using GH2's solution API. WHY async: GH2 only rebuilds a component's viewport display as
    // part of a COMPLETED document solution pass, and the progress popup clears the instant Process returns — so a
    // long blocking Process leaves "no preview / output only after another solution / popup never clears". Instead:
    //   click Run -> Process returns FAST (phase=Computing, starts a background Task) -> the Task runs the native
    //   solve while a ~8 fps monitor animates a Rhino DisplayConduit + on-body status -> on completion the Task
    //   marshals Expire()+Document.Solution.Start() to the UI thread -> ONE publish pass (phase=Ready) sets the
    //   outputs, GH2 rebuilds the display (geometry appears), and the button reverts Stop->Run.
    public abstract class NestComponentBase : Component, attributes.INestOptionsHost
    {
        protected enum Phase { Idle, Computing, Ready }

        protected NestComponentBase(Nomen nomen) : base(nomen) { Threading = ThreadingState.UiSingleThreaded; }
        protected NestComponentBase(IReader reader) : base(reader) { Threading = ThreadingState.UiSingleThreaded; }

        // ---- INestOptionsHost ----
        public bool OptionsExpanded { get; set; } = false;
        public abstract IReadOnlyList<NestOption> Options { get; }
        public bool Solving => _phase == Phase.Computing;
        public string StatusText => _status;

        // Run button (one-shot): launch a solve; stop the running one. DelayedExpire goes through the document's
        // NORMAL solution machinery (which rebuilds the viewport display) — unlike the headless Solution.Start().
        public void RequestLaunch()
        {
            if (_phase != Phase.Idle) return;     // already computing/publishing
            ClearAll();                           // wipe the previous run's geometry/preview -> start from scratch
            _aborted = false;
            _launchRequested = true;
            try { Document?.Solution.DelayedExpire(this); } catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
        }
        public void RequestStop()
        {
            if (_phase == Phase.Computing)
            {
                _aborted = true;                  // discard best-so-far: the publish pass clears instead of assembling
                try { RequestCancel(); } catch { }
                StopMonitor();                    // stop animating the live preview immediately
                ClearAll();                       // drop the in-progress preview now
                _status = "stopped";
            }
        }

        // Hard reset of everything this component is showing/holding: cached outputs, final wires, and the conduit.
        // The next solution pass then emits NOTHING (outputs blank) until a fresh solve publishes, so no stale
        // geometry from a previous Run/Stop persists.
        private void ClearAll()
        {
            _emit = null;
            _finalWires = null;
            try
            {
                _conduit.Final = null;
                _conduit.Borders = new List<Polyline>();
                _conduit.Sheets = new List<Polyline>();
                _conduit.Enabled = false;
                Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                {
                    try { ExpireDisplay(); } catch { }
                    try { Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); } catch { }
                }));
            }
            catch { }
        }
        // Relayout + repaint without a re-solve (proxies the protected ExpireDisplay).
        public void RefreshDisplay() { try { ExpireDisplay(); } catch { } }

        // Is this component still a LIVE, on-canvas object in an OPEN document? The conduit calls this every draw
        // frame and self-disables when it returns false. WHY all three checks: after the component is deleted OR
        // the file is closed, GH2 does NOT null our Document reference, so "Document != null" alone leaks the final
        // preview forever (the reported bug). We additionally reject closed/closing/inert documents (file closed)
        // and documents that no longer contain this object by id (component deleted while the file stays open).
        internal bool IsComponentLive()
        {
            try
            {
                var d = Document;
                if (d == null) return false;
                switch (d.State)
                {
                    case Grasshopper2.Doc.DocumentState.Closing:
                    case Grasshopper2.Doc.DocumentState.Closed:
                    case Grasshopper2.Doc.DocumentState.Inert:
                        return false;
                }
                return d.Objects?.Find(InstanceId) != null;   // deleted from the canvas -> Find returns null
            }
            catch { return false; }
        }

        protected override IAttributes CreateAttributes() => new NestAttributes(this);

        // ---- state ----
        private volatile Phase _phase = Phase.Idle;
        private volatile bool _launchRequested;
        private bool _prevRunInput;               // previous value of the Run input port (rising/falling edge detect)
        private volatile bool _aborted;           // Stop pressed -> discard the result, publish a clear instead
        private volatile string _status;
        private Task _task;
        private System.Timers.Timer _monitor;
        private readonly NestPreviewConduit _conduit = new NestPreviewConduit();
        private volatile Func<NestTick> _poll;
        private EventHandler _escHandler;

        protected sealed class NestTick
        {
            public long Done, Total;
            public string Status;
            public List<Polyline> Borders, Sheets;
        }
        protected void SetPoll(Func<NestTick> poll) => _poll = poll;

        // result cache (re-emitted while computing / when idle so downstream never blanks)
        private Action<IDataAccess> _emit;
        protected void CacheResult(Action<IDataAccess> emit) => _emit = emit;

        // Final placed wires, shown by the CONDUIT after the solve (kept visible without baking). We deliberately
        // do NOT override DisplayBounds/DisplayWires/DisplayCapable: returning an empty/invalid DisplayBounds gets
        // unioned into GH2's global scene clip box and culls ALL viewport geometry (the "whole preview broke" bug).
        private volatile List<Curve> _finalWires;
        protected void SetFinalWires(IEnumerable<Curve> wires)
        {
            var list = new List<Curve>();
            if (wires != null) foreach (var c in wires) if (c != null) list.Add(c);
            _finalWires = list;
        }

        // ---- subclass hooks ----
        protected abstract bool Prepare(IDataAccess access);   // read+snapshot inputs, build solver, SetPoll; false = invalid
        protected abstract void SolveCore();                   // blocking native solve (lock inside) — background thread
        protected abstract void Assemble(IDataAccess access);  // set outputs + CacheResult — Ready (publish) pass
        protected abstract void RequestCancel();               // np_cancel / StopRequested
        protected abstract bool ReadRunInput(IDataAccess access);   // read the standard "Run" boolean input port

        // Wired "Options" tokens (input 3, before Run; same "key value" format the on-canvas rows emit —
        // see NestOptionCatalog) override the matching option rows, so the panel shows the values actually
        // used. No data wired -> the panel values rule, exactly as before.
        private void ApplyWiredOptions(IDataAccess access)
        {
            try
            {
                if (!access.GetTree(3, out Grasshopper2.Data.Tree<string> tree) || tree == null || tree.LeafCount == 0) return;
                var tokens = new List<string>();
                foreach (var s in tree.NonNullItems) tokens.Add(s);
                if (tokens.Count == 0) return;
                foreach (var bad in NestOptionCatalog.ApplyTokens(Options, tokens))
                    access.AddWarning("Unknown option", "Unknown option line ignored: \"" + bad + "\"");
            }
            catch { }
        }

        protected override void Process(IDataAccess access)
        {
            // Wired options (if any) override the on-canvas rows BEFORE the launch below snapshots them.
            ApplyWiredOptions(access);

            // Run is a standard input port now (was the on-canvas Run/Stop button). Edge-drive the async machine:
            // a rising edge (while Idle) launches a solve; a falling edge (while Computing) cancels it but keeps
            // the best-so-far. Using the INPUT's previous value (not the phase) means an ESC-stop isn't relaunched
            // just because Run is still held TRUE. To re-run after an input change, toggle Run off then on.
            bool runInput = false; try { runInput = ReadRunInput(access); } catch { }
            if (runInput && !_prevRunInput && _phase == Phase.Idle) { ClearAll(); _aborted = false; _launchRequested = true; }
            else if (!runInput && _prevRunInput && _phase == Phase.Computing) { try { RequestCancel(); } catch { } _status = "stopping…"; }
            _prevRunInput = runInput;

            switch (_phase)
            {
                case Phase.Ready:
                    if (_aborted)
                    {
                        // Stop was pressed: publish a CLEAR (emit nothing) instead of the best-so-far result.
                        _aborted = false;
                        ClearAll();
                        access.AddRemark("Stopped", "Run was stopped; press Run to nest from scratch.");
                        _phase = Phase.Idle;
                        return;
                    }
                    try { Assemble(access); }
                    catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); access.AddError("Nest failed", ex.Message); }
                    ShowFinalOnConduit();        // keep the result visible (conduit), no display-pipeline override
                    _phase = Phase.Idle;
                    return;

                case Phase.Computing:
                    EmitCached(access);          // background solve running — hold the last result
                    return;

                default: // Idle
                    if (_launchRequested)
                    {
                        _launchRequested = false;
                        if (!Prepare(access)) return;   // invalid input — Prepare added the error, stay Idle
                        _status = "starting…";
                        _phase = Phase.Computing;
                        EnableConduit(true);
                        StartMonitor();
                        try { Rhino.RhinoApp.EscapeKeyPressed -= OnEscape; } catch { }
                        _escHandler = OnEscape;
                        try { Rhino.RhinoApp.EscapeKeyPressed += _escHandler; } catch { }
                        _task = new Task(RunSolve);
                        _task.Start(TaskScheduler.Default);
                        EmitCached(access);             // hold previous result while computing (avoid a blank frame)
                        return;
                    }
                    EmitCached(access);
                    return;
            }
        }

        private void EmitCached(IDataAccess access)
        {
            if (_emit != null) { try { _emit(access); return; } catch { } }
            if (_phase == Phase.Idle) access.AddRemark("Idle", "Press Run on the component to nest.");
        }

        // BACKGROUND THREAD: the blocking native solve, then marshal a publish pass to the UI thread.
        private void RunSolve()
        {
            try { SolveCore(); }
            catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            finally
            {
                StopMonitor();
                try { Rhino.RhinoApp.EscapeKeyPressed -= _escHandler; } catch { }
            }
            _phase = Phase.Ready;
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    // Keep the conduit enabled (it switches to the final result in ShowFinalOnConduit). Publish via
                    // the NORMAL solution machinery so outputs + any downstream previews rebuild.
                    Document?.Solution.DelayedExpire(this);
                }
                catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            }));
        }

        private void OnEscape(object sender, EventArgs e)
        {
            if (_phase == Phase.Computing) { try { RequestCancel(); } catch { } _status = "stopping…"; }
        }

        // ---- ~8 fps monitor: poll the solver, update the conduit + on-body status, repaint (NO re-solve) ----
        private void StartMonitor()
        {
            _monitor = new System.Timers.Timer(120) { AutoReset = false };
            _monitor.Elapsed += OnTick;
            _monitor.Start();
        }
        private void StopMonitor()
        {
            var m = _monitor; _monitor = null;
            try { if (m != null) { m.Stop(); m.Dispose(); } } catch { }
        }
        private void OnTick(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (_phase != Phase.Computing) return;
            try
            {
                var p = _poll?.Invoke();
                if (p != null)
                {
                    if (p.Sheets != null) _conduit.Sheets = p.Sheets;
                    if (p.Borders != null) _conduit.Borders = p.Borders;
                    if (p.Status != null) _status = p.Status;
                    Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
                    {
                        try { ExpireDisplay(); } catch { }                       // canvas status repaint (display-only)
                        try { Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); } catch { }   // drives the conduit
                    }));
                }
            }
            catch { }
            var mm = _monitor;
            if (_phase == Phase.Computing && mm != null) { try { mm.Start(); } catch { } }
        }

        // Turn on the LIVE (orange) preview at the start of a solve.
        private void EnableConduit(bool on)
        {
            try
            {
                if (on)
                {
                    _conduit.Borders = new List<Polyline>();
                    _conduit.Sheets = new List<Polyline>();
                    _conduit.Live = true;
                    _conduit.AliveCheck = IsComponentLive;   // self-disable once removed / file closed
                }
                _conduit.Enabled = on;
                Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            catch { }
        }

        // After the solve: switch the conduit to draw the FINAL result and keep it enabled (so the nested
        // geometry stays visible without baking, and without any DisplayBounds override that would break GH2).
        private void ShowFinalOnConduit()
        {
            try
            {
                _conduit.Final = _finalWires;
                _conduit.Live = false;
                _conduit.AliveCheck = IsComponentLive;
                _conduit.Enabled = true;
                Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            catch { }
        }
    }

    // Draws the nest into the Rhino viewport: the live tightening preview (orange) while solving, then the final
    // placed result (dark) when idle. Independent of GH2's display pipeline, so it never affects other previews.
    internal sealed class NestPreviewConduit : Rhino.Display.DisplayConduit
    {
        public volatile List<Polyline> Borders = new List<Polyline>();   // live
        public volatile List<Polyline> Sheets = new List<Polyline>();    // live
        public volatile List<Curve> Final;                                // final result
        public volatile bool Live = true;
        public Func<bool> AliveCheck;                                     // false -> component removed, self-disable

        protected override void PostDrawObjects(Rhino.Display.DrawEventArgs e)
        {
            try { if (AliveCheck != null && !AliveCheck()) { Enabled = false; return; } } catch { }
            if (Live)
            {
                var sh = Sheets; var bo = Borders;
                if (sh != null) foreach (var pl in sh) if (pl != null && pl.Count > 1) e.Display.DrawPolyline(pl, System.Drawing.Color.Gray);
                if (bo != null) foreach (var pl in bo) if (pl != null && pl.Count > 1) e.Display.DrawPolyline(pl, System.Drawing.Color.OrangeRed, 2);
            }
            else
            {
                var f = Final;
                if (f != null) foreach (var c in f) if (c != null) e.Display.DrawCurve(c, System.Drawing.Color.FromArgb(40, 40, 40));
            }
        }
    }
}
