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
        const int CaretW = 12;    // reserved width for the dropdown caret on Choice rows

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
            if (!HasOptions) { _runRect = RectangleF.Empty; _headerRect = RectangleF.Empty; _ctrlRects = new RectangleF[0]; return; }

            // Anchor the whole control band to the natural body bottom captured BEFORE we grow Bounds,
            // using integer-only math. The previous code positioned rows relative to the *post-grow*
            // Bounds.Bottom, which assumes base.Layout() produces the same natural body height on every
            // platform — it does not (macOS Rhino's libgdiplus shim measures the name/param column with
            // different font metrics), so the band drifted off the body. Capturing the bottom first and
            // building downward with whole-pixel heights makes the result identical on Windows and macOS.
            int n = RowCount;
            int bodyBottom = (int)Math.Round(Bounds.Bottom);
            int left = (int)Math.Round(Bounds.Left);
            int width = (int)Math.Round(Bounds.Width);
            int innerW = width - 2 * Pad;

            // extra == the exact sum of every element + gap drawn below the body (so nothing can exceed it).
            int extra = Pad + RunH + Pad + HeaderH + (n > 0 ? Pad + n * RowH : 0) + Pad;

            RectangleF b = Bounds;
            b.Height = bodyBottom - b.Y + extra;   // set (not accumulate) from the captured integer bottom
            Bounds = b;

            int y = bodyBottom + Pad;
            _runRect = new RectangleF(left + Pad, y, innerW, RunH); y += RunH + Pad;
            _headerRect = new RectangleF(left + Pad, y, innerW, HeaderH); y += HeaderH;

            _ctrlRects = new RectangleF[n];
            if (n > 0)
            {
                y += Pad;
                // The value button fills from after the label column to the RIGHT INNER EDGE, so its right
                // side is always `right - Pad` and it can never spill past the body. On a narrow body (the
                // macOS auto-sized body is narrower than on Windows) the label column shrinks instead of the
                // old behaviour, which floored the button width at CtrlMinW and let it protrude to the right.
                int labelW = Math.Min(LabelW, Math.Max(0, innerW - CtrlMinW));
                int ctrlX = left + Pad + labelW;
                int ctrlW = innerW - labelW;
                for (int i = 0; i < n; i++) { _ctrlRects[i] = new RectangleF(ctrlX, y, ctrlW, RowH); y += RowH; }
            }
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
            // Horizontal-only trimming/wrap control. Vertical centering is done manually in CenteredText
            // (StringFormat.LineAlignment.Center is unreliable under macOS Rhino's libgdiplus shim).
            using (var sfText = new StringFormat
            {
                LineAlignment = StringAlignment.Near,
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

                // A raised, glossy button: drop shadow + vertical gradient + dark border, with an optional
                // leading vector icon followed by centered text. Icons are drawn as geometry (not Unicode
                // glyphs) so layout never depends on per-platform font fallback. `hit` is the full hit-test
                // rect from Layout(); the capsule is inset 1px vertically for visual breathing room only.
                void Button(Rectangle hit, string s, Color baseCol, IconKind icon = IconKind.None, int rightInset = 0)
                {
                    Rectangle r = Rectangle.Inflate(hit, 0, -1);
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

                    // Center the icon+text group as a unit so they never overlap (mirrors GH_Capsule).
                    // `rightInset` reserves space on the right (e.g. for the dropdown caret) so text never
                    // runs under it and is centered within the remaining area.
                    int avail = Math.Max(1, r.Width - rightInset);
                    int iconSz = icon == IconKind.None ? 0 : (int)Math.Round(r.Height * 0.45f);
                    int gap = icon == IconKind.None ? 0 : 4;
                    float textW = string.IsNullOrEmpty(s) ? 0 : g.MeasureString(s, font, avail, sfText).Width;
                    float groupW = iconSz + gap + textW;
                    float gx = r.X + Math.Max(0, (avail - groupW) / 2f);
                    if (icon != IconKind.None)
                    {
                        var ib = new RectangleF(gx, r.Y + (r.Height - iconSz) / 2f, iconSz, iconSz);
                        DrawIcon(g, ib, icon, A(Color.White));
                        gx += iconSz + gap;
                    }
                    CenteredText(g, s, font, whiteBrush, new RectangleF(gx, r.Y, r.X + avail - gx, r.Height), sfText);
                }

                Color dark = Color.FromArgb(64, 64, 64);
                Color black = Color.FromArgb(28, 28, 28);

                // ---- Run button (always visible, above the Options header): BLACK = Run, RED = Stop ----
                bool busy = _host.IsBusy;
                Button(GH_Convert.ToRectangle(_runRect), busy ? "Stop" : "Run",
                       busy ? Color.FromArgb(180, 50, 50) : black, busy ? IconKind.Stop : IconKind.Play);

                // ---- collapsible header ----
                Rectangle hRect = GH_Convert.ToRectangle(_headerRect);
                Button(hRect, "Options", dark, _host.OptionsExpanded ? IconKind.ChevronDown : IconKind.ChevronRight);

                if (!_host.OptionsExpanded) return;

                // ---- option rows: label on the body, value as a button ----
                var opts = _host.Options;
                for (int i = 0; i < opts.Count && i < _ctrlRects.Length; i++)
                {
                    var opt = opts[i];
                    Rectangle ctrl = GH_Convert.ToRectangle(_ctrlRects[i]);
                    // Label fills from the left inset up to just before the value button (it follows the
                    // control's left edge, which shrinks with the body on narrow/macOS layouts).
                    float labelLeft = Bounds.Left + Pad;
                    var labelRect = new RectangleF(labelLeft, ctrl.Y, Math.Max(0, ctrl.Left - labelLeft - Pad), ctrl.Height);
                    CenteredText(g, opt.Label, font, labelBrush, labelRect, sfText);

                    bool isChoice = opt.Kind == NestOptionKind.Choice;
                    Button(ctrl, opt.Display(), dark, IconKind.None, isChoice ? CaretW : 0);
                    if (isChoice)
                    {
                        // dropdown caret as geometry in the reserved strip at the right edge of the value button
                        var inner = Rectangle.Inflate(ctrl, 0, -1);
                        int cs = Math.Min(CaretW - 4, inner.Height - 6);
                        var cb = new RectangleF(inner.Right - CaretW + (CaretW - cs) / 2f,
                                                inner.Y + (inner.Height - cs) / 2f, cs, cs);
                        DrawIcon(g, cb, IconKind.CaretDown, A(Color.White));
                    }
                }
            }
        }

        private enum IconKind { None, Play, Stop, ChevronDown, ChevronRight, CaretDown }

        // Draws a small UI glyph as pure vector geometry inside `box`, so its position/size never depend on
        // per-platform font fallback (the macOS GDI+ shim substitutes a different font for Unicode arrows,
        // which shifted the old glyph-based icons). Filled/stroked with `color` (already alpha-faded).
        private static void DrawIcon(Graphics g, RectangleF box, IconKind kind, Color color)
        {
            var sm = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = box.X, y = box.Y, w = box.Width, h = box.Height;
            float cx = x + w / 2f, cy = y + h / 2f;
            using (var brush = new SolidBrush(color))
            using (var pen = new Pen(color, Math.Max(1f, h * 0.16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (kind)
                {
                    case IconKind.Play:
                        g.FillPolygon(brush, new[] { new PointF(x, y), new PointF(x, y + h), new PointF(x + w, cy) });
                        break;
                    case IconKind.Stop:
                        g.FillRectangle(brush, x, y, w, h);
                        break;
                    case IconKind.CaretDown:
                        g.FillPolygon(brush, new[] { new PointF(x, y + h * 0.25f), new PointF(x + w, y + h * 0.25f), new PointF(cx, y + h * 0.75f) });
                        break;
                    case IconKind.ChevronDown:
                        g.DrawLines(pen, new[] { new PointF(x + w * 0.2f, y + h * 0.35f), new PointF(cx, y + h * 0.65f), new PointF(x + w * 0.8f, y + h * 0.35f) });
                        break;
                    case IconKind.ChevronRight:
                        g.DrawLines(pen, new[] { new PointF(x + w * 0.35f, y + h * 0.2f), new PointF(x + w * 0.65f, cy), new PointF(x + w * 0.35f, y + h * 0.8f) });
                        break;
                }
            }
            g.SmoothingMode = sm;
        }

        // Draws `s` horizontally per `sf` but vertically centered in `r` by measured height — robust on
        // macOS where StringFormat.LineAlignment.Center mis-centers under the libgdiplus shim.
        private static void CenteredText(Graphics g, string s, Font f, Brush br, RectangleF r, StringFormat sf)
        {
            if (string.IsNullOrEmpty(s)) return;
            float th = g.MeasureString(s, f, (int)Math.Ceiling(Math.Max(1, r.Width)), sf).Height;
            float ty = r.Y + Math.Max(0, (r.Height - th) / 2f);
            // The layout rect runs from the centered top (ty) down to the bottom of r, so its height is
            // always >= the measured line height. A rect exactly `th` tall makes macOS libgdiplus decide
            // the line doesn't fit and render just the ellipsis ("..."), even though the text is short —
            // that was the cause of values showing "..." instead of names. Near alignment then draws the
            // line at ty, i.e. vertically centered.
            var layout = new RectangleF(r.X, ty, r.Width, r.Bottom - ty);
            g.DrawString(s, f, br, layout, sf);
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
            // Use an Eto.Forms context menu, NOT a WinForms ContextMenuStrip. On Rhino-for-Mac there is no
            // real WinForms — the compatibility shim under-measures menu-item text in its own render pass and
            // trims long labels with an ellipsis ("Bottom-Left" -> "Bott.-left"), and that trim ignores any
            // item width / MinimumSize we set. Eto is Rhino's native cross-platform toolkit (real Cocoa menus
            // on Mac, the same toolkit Grasshopper's own Mac UI uses), so items size to their text correctly.
            // Fully qualified to avoid clashing with the System.Windows.Forms types this file also uses.
            var menu = new Eto.Forms.ContextMenu();
            Eto.Forms.RadioMenuItem controller = null;
            for (int k = 0; k < opt.ChoiceLabels.Count; k++)
            {
                int idx = k;
                var item = new Eto.Forms.RadioMenuItem(controller)
                {
                    Text = opt.ChoiceLabels[k],
                    Checked = (k == opt.SelectedIndex)
                };
                if (controller == null) controller = item;   // first item is the radio-group controller
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

            // Show at the mouse cursor (where the user just clicked the value button).
            menu.Show();
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
