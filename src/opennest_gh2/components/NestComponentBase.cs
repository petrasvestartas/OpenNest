using System;
using System.Collections.Generic;
using System.Threading;
using Grasshopper2.Components;
using Grasshopper2.Doc;        // IAttributes
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;
using opennest_2;              // NestOption
using opennest_gh2.attributes;

namespace opennest_gh2.components
{
    // Shared machinery for the two solver components (OpenNestCollision, OpenNest2): the Run latch (default
    // OFF), the result cache, the live-progress monitor (drives the GH2 progress popup + on-body status text),
    // ESC / cancellation, and the animated viewport preview (Rhino DisplayConduit). GH2 runs Process on a
    // background worker (Threading = SingleThreaded), so the blocking native solve runs there while a ~8 fps
    // timer polls the solver for progress + preview geometry and refreshes the UI — mirroring GH1's OnTick,
    // minus GH1's own Task/EngineGate (GH2 owns the threading; a per-engine static lock guards the natives).
    public abstract class NestComponentBase : Component, attributes.INestOptionsHost
    {
        protected NestComponentBase(Nomen nomen) : base(nomen) { Threading = ThreadingState.SingleThreaded; }
        protected NestComponentBase(IReader reader) : base(reader) { Threading = ThreadingState.SingleThreaded; }

        // ---- INestOptionsHost (read/driven by NestAttributes) ----
        public bool Run { get; set; } = false;            // latch: OFF on load -> never auto-burns a long solve
        public bool OptionsExpanded { get; set; } = false;
        public abstract IReadOnlyList<NestOption> Options { get; }
        public bool Solving => _solving;
        public string StatusText => _status;
        public void RequestStop() { try { RequestCancel(); } catch { } _status = "stopping…"; }
        // Proxy the protected ExpireDisplay so the attributes can relayout+repaint without a re-solve.
        public void RefreshDisplay() { try { ExpireDisplay(); } catch { } }

        protected override IAttributes CreateAttributes() => new NestAttributes(this);

        // ---- live-solve state ----
        private volatile bool _solving;
        private volatile string _status;
        private System.Timers.Timer _monitor;
        private readonly NestPreviewConduit _conduit = new NestPreviewConduit();
        private volatile Func<NestTick> _poll;            // set by the subclass once its solver is flattened

        // One progress snapshot. Borders/Sheets are immutable lists swapped in whole (no tearing).
        protected sealed class NestTick
        {
            public long Done, Total;
            public string Status;
            public List<Polyline> Borders, Sheets;
        }
        protected void SetPoll(Func<NestTick> poll) => _poll = poll;

        // ---- result cache (re-emit last result on a no-solve pass so downstream never blanks) ----
        private bool _hasResult;
        private Action<IDataAccess> _emit;
        protected void CacheResult(Action<IDataAccess> emit) { _emit = emit; _hasResult = true; }

        // ---- subclass hooks ----
        protected abstract void RequestCancel();                              // np_cancel / StopRequested
        protected abstract void DoSolve(IDataAccess access, CancellationToken token);  // flatten + solve + assemble + cache + output

        // GH2's required per-item entry (no token -> user cancel via ESC / the Stop button still works).
        protected override void Process(IDataAccess access) => Handle(access, CancellationToken.None);

        // GH2's batch entry hands us a token that fires when the solution is aborted (e.g. an input changed
        // mid-solve); poll it to stop the native solve promptly. For a nest there is normally a single item.
        protected override void Process(IDataAccess[] accesses, CancellationToken token)
        {
            if (accesses == null) return;
            foreach (var access in accesses) Handle(access, token);
        }

        private void Handle(IDataAccess access, CancellationToken token)
        {
            if (access == null) return;
            if (!Run)
            {
                if (_hasResult && _emit != null) { try { _emit(access); } catch { } }
                else access.AddRemark("Stopped", "Press Run on the component to nest.");
                return;
            }
            RunWithFeedback(access, token);
        }

        private void RunWithFeedback(IDataAccess access, CancellationToken token)
        {
            _solving = true; _status = "starting…";
            EventHandler escHandler = (s, e) => { if (_solving) { try { RequestCancel(); } catch { } _status = "stopping…"; } };
            CancellationTokenRegistration reg = default;
            try
            {
                try { Rhino.RhinoApp.EscapeKeyPressed += escHandler; } catch { }
                try { reg = token.Register(() => { if (_solving) { try { RequestCancel(); } catch { } } }); } catch { }
                EnableConduit(true);
                StartMonitor();
                DoSolve(access, token);          // BLOCKS here on the worker thread
            }
            catch (Exception ex) { Rhino.RhinoApp.WriteLine(ex.ToString()); }
            finally
            {
                StopMonitor();
                EnableConduit(false);
                try { reg.Dispose(); } catch { }
                try { Rhino.RhinoApp.EscapeKeyPressed -= escHandler; } catch { }
                _solving = false; _status = null; _poll = null;
                RefreshUi();                     // drop the status strip, repaint
            }
        }

        // ---- ~8 fps monitor: poll the solver, feed the popup % + status text + preview, refresh the UI ----
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
            if (!_solving) return;
            try
            {
                var p = _poll?.Invoke();
                if (p != null)
                {
                    if (p.Sheets != null) _conduit.Sheets = p.Sheets;
                    if (p.Borders != null) _conduit.Borders = p.Borders;
                    if (p.Status != null) _status = p.Status;
                    if (p.Total > 0) { try { SetProgressPercentage(p.Done, p.Total); } catch { } }
                    RefreshUi();
                }
            }
            catch { }
            var m = _monitor;
            if (_solving && m != null) { try { m.Start(); } catch { } }
        }

        private void RefreshUi()
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try { ExpireDisplay(); } catch { }                       // relayout + repaint the capsule (no re-solve)
                try { Rhino.RhinoDoc.ActiveDoc?.Views.Redraw(); } catch { }
            }));
        }

        private void EnableConduit(bool on)
        {
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
            {
                try
                {
                    if (on) { _conduit.Borders = new List<Polyline>(); _conduit.Sheets = new List<Polyline>(); }
                    _conduit.Enabled = on;
                    Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
                }
                catch { }
            }));
        }
    }

    // Draws the live "tightening" preview straight into the Rhino viewport while a nest solves (GH2's own
    // display bag only shows the COMPLETED result). Enabled by the base component for the solve's duration.
    internal sealed class NestPreviewConduit : Rhino.Display.DisplayConduit
    {
        public volatile List<Polyline> Borders = new List<Polyline>();
        public volatile List<Polyline> Sheets = new List<Polyline>();

        protected override void PostDrawObjects(Rhino.Display.DrawEventArgs e)
        {
            var sh = Sheets; var bo = Borders;
            if (sh != null) foreach (var pl in sh) if (pl != null && pl.Count > 1) e.Display.DrawPolyline(pl, System.Drawing.Color.Gray);
            if (bo != null) foreach (var pl in bo) if (pl != null && pl.Count > 1) e.Display.DrawPolyline(pl, System.Drawing.Color.OrangeRed, 2);
        }
    }
}
