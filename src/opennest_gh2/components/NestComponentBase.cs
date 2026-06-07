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
            _launchRequested = true;
            try { Document?.Solution.DelayedExpire(this); } catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
        }
        public void RequestStop()
        {
            if (_phase == Phase.Computing) { try { RequestCancel(); } catch { } _status = "stopping…"; }
        }
        // Relayout + repaint without a re-solve (proxies the protected ExpireDisplay).
        public void RefreshDisplay() { try { ExpireDisplay(); } catch { } }

        protected override IAttributes CreateAttributes() => new NestAttributes(this);

        // ---- state ----
        private volatile Phase _phase = Phase.Idle;
        private volatile bool _launchRequested;
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

        protected override void Process(IDataAccess access)
        {
            switch (_phase)
            {
                case Phase.Ready:
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
                    _conduit.AliveCheck = () => Document != null;   // self-disable once the component is removed
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
                _conduit.AliveCheck = () => Document != null;
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
