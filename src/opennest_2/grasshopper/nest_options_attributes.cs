using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace opennest_2
{
    // Shared base for nest components that expose their settings as on-canvas controls instead of an "Options"
    // string input. Holds the option list, wires the custom attributes, persists values in the .gh file, and
    // re-solves on edit. Cross-platform: the attributes use System.Windows.Forms menus, which Rhino 8 backs with
    // its own Cocoa implementation on Mac (the canonical GH component-menu path).
    public abstract class NestOptionsHostComponent : GH_Component, INestOptionsHost
    {
        protected readonly List<NestOption> _options = new List<NestOption>();
        public IReadOnlyList<NestOption> Options => _options;

        // Collapsed by default so the component stays compact; click the "Options" header to expand.
        private bool _optionsExpanded = false;
        public bool OptionsExpanded { get => _optionsExpanded; set => _optionsExpanded = value; }

        // Set by the on-component Run button; consumed by SolveInstance to launch one solve.
        protected volatile bool _runButtonRequested = false;

        protected NestOptionsHostComponent(string name, string nick, string desc, string cat, string sub)
            : base(name, nick, desc, cat, sub) { }

        public override void CreateAttributes() => m_attributes = new NestOptionsAttributes(this);

        // True while a solve is running (the Run button then shows "Stop"). Overridden by each component.
        public virtual bool IsBusy => false;

        // The on-component Run button. Default just requests a solve; components override to also cancel
        // a running solve when clicked while busy.
        public virtual void OnRunClicked()
        {
            _runButtonRequested = true;
            ExpireSolution(true);
        }

        public virtual void OnOptionChanged(NestOption opt)
        {
            RecordUndoEvent("OpenNest option: " + opt.Label);
            ExpireSolution(true);   // re-layout + re-solve (cheap unless Run is on)
        }

        // Expand/collapse just re-lays-out the component; no need to re-solve.
        public virtual void OnOptionsToggled()
        {
            if (Attributes != null) Attributes.ExpireLayout();
            Instances.RedrawCanvas();
        }

        // ---- convenience accessors used by components that read option values directly ----
        protected NestOption Opt(string key) => _options.FirstOrDefault(o => o.Key == key);
        protected double OptNum(string key, double dflt = 0) { var o = Opt(key); return o != null ? o.Value : dflt; }
        protected int OptChoiceIndex(string key, int dflt = 0) { var o = Opt(key); return o != null ? o.SelectedIndex : dflt; }

        // Persist each option value (keyed by "opt_<Key>") plus the panel state. Additive — neither component
        // had Write/Read before.
        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("options_expanded", _optionsExpanded);
            foreach (var o in _options)
            {
                string k = "opt_" + o.Key;
                if (o.Kind == NestOptionKind.Choice) writer.SetInt32(k, o.SelectedIndex);
                else if (o.Kind == NestOptionKind.Number) writer.SetDouble(k, o.Value);
                else writer.SetString(k, o.TextValue ?? "");
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            try { if (reader.ItemExists("options_expanded")) _optionsExpanded = reader.GetBoolean("options_expanded"); } catch { }
            foreach (var o in _options)
            {
                string k = "opt_" + o.Key;
                try
                {
                    if (!reader.ItemExists(k)) continue;
                    if (o.Kind == NestOptionKind.Choice) o.SelectedIndex = reader.GetInt32(k);
                    else if (o.Kind == NestOptionKind.Number) o.Value = reader.GetDouble(k);
                    else o.TextValue = reader.GetString(k);
                }
                catch { /* tolerate missing/legacy keys */ }
            }
            return base.Read(reader);
        }
    }

    // Draws a collapsible "Options" header under the component body. When expanded, draws one row per option:
    // a label on the left and a control box on the right (a dropdown for choices, a type-in box for numbers/
    // text). Clicking the header toggles collapse; clicking a row opens its editor at the row.
    public class NestOptionsAttributes : GH_ComponentAttributes
    {
        const int RowH = 20;      // row height (model px; GH applies zoom)
        const int RunH = 22;      // Run button height
        const int HeaderH = 18;
        const int Pad = 3;
        const int LabelW = 92;    // label column width
        const int CtrlMinW = 64;

        private readonly INestOptionsHost _host;
        private RectangleF _runRect = RectangleF.Empty;
        private RectangleF _headerRect = RectangleF.Empty;
        private RectangleF[] _ctrlRects = new RectangleF[0];

        public NestOptionsAttributes(GH_Component owner) : base(owner)
        {
            _host = owner as INestOptionsHost;
        }

        private int RowCount => (_host != null && _host.OptionsExpanded) ? _host.Options.Count : 0;
        private bool HasOptions => _host?.Options?.Count > 0;

        protected override void Layout()
        {
            base.Layout();
            if (!HasOptions) { _ctrlRects = new RectangleF[0]; _headerRect = RectangleF.Empty; return; }

            int n = RowCount;
            // Layout (top -> bottom): Run button, Options header, then option rows (when expanded).
            float extra = RunH + HeaderH + n * RowH + Pad;

            RectangleF b = Bounds;
            b.Height += extra;
            Bounds = b;

            float top = Bounds.Bottom - extra + Pad * 0.5f;
            _runRect = new RectangleF(Bounds.Left + Pad, top, Bounds.Width - 2 * Pad, RunH - 2);
            _headerRect = new RectangleF(Bounds.Left + Pad, top + RunH, Bounds.Width - 2 * Pad, HeaderH);

            _ctrlRects = new RectangleF[n];
            float ctrlX = Bounds.Left + Pad + LabelW;
            float ctrlW = Math.Max(CtrlMinW, Bounds.Width - 2 * Pad - LabelW);
            float rowsTop = top + RunH + HeaderH;
            for (int i = 0; i < n; i++)
                _ctrlRects[i] = new RectangleF(ctrlX, rowsTop + i * RowH + 1, ctrlW, RowH - 2);
        }

        protected override void Render(GH_Canvas canvas, Graphics g, GH_CanvasChannel channel)
        {
            base.Render(canvas, g, channel);
            if (channel != GH_CanvasChannel.Objects || !HasOptions) return;

            // Fade out text with zoom exactly like GH does for its own param names / slider text.
            int alpha = GH_Canvas.ZoomFadeMedium;
            if (alpha < 5) return;

            Font font = GH_FontServer.StandardAdjusted;

            using (var whiteBrush = new SolidBrush(Color.FromArgb(alpha, Color.White)))
            using (var labelBrush = new SolidBrush(Color.FromArgb(alpha, Color.Black)))
            using (var sfCenter = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            using (var sfLabel = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Alignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                Color A(Color c) => Color.FromArgb(alpha, c);
                Color Lighten(Color c, double f) => Color.FromArgb(
                    (int)(c.R + (255 - c.R) * f), (int)(c.G + (255 - c.G) * f), (int)(c.B + (255 - c.B) * f));
                Color Darken(Color c, double f) => Color.FromArgb(
                    (int)(c.R * (1 - f)), (int)(c.G * (1 - f)), (int)(c.B * (1 - f)));

                GraphicsPath Rounded(Rectangle r, int rad)
                {
                    var p = new GraphicsPath();
                    p.AddArc(r.X, r.Y, 2 * rad, 2 * rad, 180, 90);
                    p.AddArc(r.Right - 2 * rad, r.Y, 2 * rad, 2 * rad, 270, 90);
                    p.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
                    p.AddArc(r.X, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
                    p.CloseFigure();
                    return p;
                }

                // A raised, glossy button: drop shadow + vertical gradient + top highlight + dark border.
                void Button(Rectangle r, string s, Color baseCol)
                {
                    int rad = Math.Min(3, r.Height / 2);   // less-rounded corners
                    var sm = g.SmoothingMode;
                    g.SmoothingMode = SmoothingMode.AntiAlias;

                    var sr = r; sr.Offset(0, 1);
                    using (var shadowPath = Rounded(sr, rad))
                    using (var shadow = new SolidBrush(Color.FromArgb(alpha / 4, 0, 0, 0)))
                        g.FillPath(shadow, shadowPath);

                    using (var path = Rounded(r, rad))
                    {
                        // subtle vertical gradient (barely lighter top, barely darker bottom) — no separate
                        // highlight overlay (that left a visible seam line across the middle of the button)
                        using (var grad = new LinearGradientBrush(
                            new Point(r.X, r.Y - 1), new Point(r.X, r.Bottom + 1),
                            A(Lighten(baseCol, 0.14)), A(Darken(baseCol, 0.08))))
                            g.FillPath(grad, path);

                        using (var border = new Pen(A(Darken(baseCol, 0.4)), 1f))
                            g.DrawPath(border, path);
                    }
                    g.SmoothingMode = sm;
                    g.DrawString(s, font, whiteBrush, r, sfCenter);
                }

                Color dark = Color.FromArgb(64, 64, 64);
                Color black = Color.FromArgb(28, 28, 28);

                // ---- Run button (always visible, above the Options header): BLACK = Run, RED = Stop ----
                bool busy = _host.IsBusy;
                Button(GH_Convert.ToRectangle(_runRect), busy ? "■ Stop" : "▶ Run",
                       busy ? Color.FromArgb(180, 50, 50) : black);

                // ---- collapsible header ----
                Rectangle hRect = GH_Convert.ToRectangle(_headerRect);
                string arrow = _host.OptionsExpanded ? "▾" : "▸";
                Button(hRect, arrow + " Options", dark);

                if (!_host.OptionsExpanded) return;

                // ---- option rows: label on the body, value as a button ----
                var opts = _host.Options;
                for (int i = 0; i < opts.Count && i < _ctrlRects.Length; i++)
                {
                    var opt = opts[i];
                    Rectangle ctrl = GH_Convert.ToRectangle(_ctrlRects[i]);
                    var labelRect = new Rectangle((int)(Bounds.Left + Pad), ctrl.Y, LabelW - Pad, ctrl.Height);

                    g.DrawString(opt.Label, font, labelBrush, labelRect, sfLabel);

                    string txt = opt.Display();
                    if (opt.Kind == NestOptionKind.Choice) txt += "  ▼";
                    Button(ctrl, txt, dark);
                }
            }
        }

        public override GH_ObjectResponse RespondToMouseDown(GH_Canvas canvas, GH_CanvasMouseEvent e)
        {
            if (_host != null && e.Button == MouseButtons.Left && HasOptions)
            {
                // Run button: start a solve (or stop a running one)
                if (_runRect.Contains(e.CanvasLocation))
                {
                    _host.OnRunClicked();
                    return GH_ObjectResponse.Handled;
                }

                // header toggles the panel
                if (_headerRect.Contains(e.CanvasLocation))
                {
                    _host.OptionsExpanded = !_host.OptionsExpanded;
                    _host.OnOptionsToggled();
                    return GH_ObjectResponse.Handled;
                }

                if (_host.OptionsExpanded)
                {
                    var opts = _host.Options;
                    for (int i = 0; i < _ctrlRects.Length && i < opts.Count; i++)
                    {
                        if (_ctrlRects[i].Contains(e.CanvasLocation))
                        {
                            var opt = opts[i];
                            PointF canvasPt = canvas.Viewport.ProjectPoint(
                                new PointF(_ctrlRects[i].Left, _ctrlRects[i].Bottom));
                            Point screenPt = canvas.PointToScreen(Point.Round(canvasPt));

                            if (opt.Kind == NestOptionKind.Choice) ShowChoiceMenu(opt, screenPt);
                            else ShowTextEditor(opt, screenPt);
                            return GH_ObjectResponse.Handled;
                        }
                    }
                }
            }
            return base.RespondToMouseDown(canvas, e);
        }

        private void ShowChoiceMenu(NestOption opt, Point screenPt)
        {
            var menu = new ContextMenuStrip();
            for (int k = 0; k < opt.ChoiceLabels.Count; k++)
            {
                int idx = k;
                var item = new ToolStripMenuItem(opt.ChoiceLabels[k]) { Checked = (k == opt.SelectedIndex) };
                item.Click += (s, a) =>
                {
                    if (opt.SelectedIndex != idx)
                    {
                        opt.SelectedIndex = idx;
                        _host.OnOptionChanged(opt);
                    }
                };
                menu.Items.Add(item);
            }
            menu.Show(screenPt);
        }

        // A small editable box (numbers + the font text line). ToolStripTextBox in a ContextMenuStrip is plain
        // WinForms and works on both platforms via Rhino's shim.
        private void ShowTextEditor(NestOption opt, Point screenPt)
        {
            var menu = new ContextMenuStrip();
            var tb = new ToolStripTextBox { Text = opt.EditText() };
            tb.KeyDown += (s, a) =>
            {
                if (a.KeyCode == Keys.Enter)
                {
                    a.SuppressKeyPress = true;
                    opt.SetFromText(tb.Text);
                    menu.Close();
                    _host.OnOptionChanged(opt);
                }
                else if (a.KeyCode == Keys.Escape)
                {
                    menu.Close();
                }
            };
            menu.Items.Add(tb);
            menu.Show(screenPt);
            tb.Focus();
            tb.SelectAll();
        }
    }
}
