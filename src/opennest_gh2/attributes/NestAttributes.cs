using System;
using System.Collections.Generic;
using Eto.Drawing;
using Eto.Forms;
using Grasshopper2.Doc.Attributes;
using Grasshopper2.UI.Flex;
using Grasshopper2.UI.Primitives;
using Grasshopper2.UI.Skinning;
using opennest_2;                 // NestOption (GH1 option model, reused)
using GComponent = Grasshopper2.Components.Component;

namespace opennest_gh2.attributes
{
    // Implemented by the nest components so the custom attributes can read/toggle the on-canvas Run latch
    // and the collapsible options panel.
    internal interface IRunHost
    {
        bool Run { get; set; }
    }

    internal interface INestOptionsHost : IRunHost
    {
        IReadOnlyList<NestOption> Options { get; }
        bool OptionsExpanded { get; set; }

        // Live-solve feedback (driven by the component's monitor) drawn on the body.
        bool Solving { get; }
        string StatusText { get; }
        // Request the running native solve to stop (keeps the best-so-far).
        void RequestStop();
        // Relayout + repaint the component WITHOUT a re-solve (component proxies its protected ExpireDisplay).
        void RefreshDisplay();
    }

    // Custom GH2 component attributes mirroring the GH1 OpenNest nest UI: a centered Run/Stop button, a
    // collapsible "Options" header, and one row per option (dropdown for Choice, type-in for Number/Text).
    // Drawn with Eto.Drawing via context.Graphics; clicks handled in HandleMouseDown (canvas coordinates).
    public class NestAttributes : ComponentAttributes
    {
        private const float RunH = 22f, HeaderH = 18f, RowH = 20f, StatusH = 14f, Pad = 3f, LabelW = 92f, CtrlMinW = 64f;

        private RectangleF _runRect, _headerRect, _statusRect;
        private RectangleF[] _rowRects = new RectangleF[0];

        public NestAttributes(GComponent owner) : base(owner) { }

        private INestOptionsHost Host => Owner as INestOptionsHost;
        private int RowCount => (Host != null && Host.OptionsExpanded) ? Host.Options.Count : 0;
        private bool ShowStatus => Host != null && Host.Solving;

        protected override void LayoutBounds(Shape shape)
        {
            base.LayoutBounds(shape);
            if (Host == null) return;

            RectangleF b = Bounds;
            float left = b.X + Pad, innerW = b.Width - 2f * Pad, y = b.Bottom + Pad;

            _runRect = new RectangleF(left, y, innerW, RunH); y += RunH + Pad;

            float statusH = ShowStatus ? StatusH : 0f;
            _statusRect = new RectangleF(left, y, innerW, statusH);
            if (statusH > 0f) y += statusH + Pad;

            _headerRect = new RectangleF(left, y, innerW, HeaderH); y += HeaderH;

            int n = RowCount;
            _rowRects = new RectangleF[n];
            if (n > 0)
            {
                y += Pad;
                for (int i = 0; i < n; i++) { _rowRects[i] = new RectangleF(left, y, innerW, RowH); y += RowH; }
            }

            float extra = Pad + RunH + Pad + (statusH > 0f ? statusH + Pad : 0f) + HeaderH + (n > 0 ? Pad + n * RowH : 0f) + Pad;
            b.Height += extra;
            Bounds = b;
        }

        protected override void DrawForeground(Eto.Drawing.Context context, Skin skin, Capsule capsule, Shade shade)
        {
            base.DrawForeground(context, skin, capsule, shade);
            if (Host == null) return;
            Graphics g = context.Graphics;

            // Run / Stop button. Run==true means a live session is active -> show red "Stop"; otherwise green "Run".
            bool run = Host.Run;
            Button(g, _runRect, run ? "Stop" : "Run", run ? Color.FromArgb(176, 48, 48) : Color.FromArgb(40, 132, 56), Colors.White, true);

            // Live status strip (round/generation count) while solving.
            if (ShowStatus)
            {
                string s = Host.StatusText ?? "working…";
                Font sf = SystemFonts.Default(7f);
                SizeF ss = g.MeasureString(sf, s);
                g.DrawText(sf, Color.FromArgb(176, 48, 48), _statusRect.X + (_statusRect.Width - ss.Width) / 2f,
                           _statusRect.Y + (_statusRect.Height - ss.Height) / 2f, s);
            }

            // Options header (collapsible) with a chevron
            Button(g, _headerRect, "Options", Color.FromArgb(72, 72, 72), Colors.White, false);
            Chevron(g, new RectangleF(_headerRect.Right - 14f, _headerRect.Y, 12f, _headerRect.Height), Host.OptionsExpanded);

            // Option rows: label + value box
            var opts = Host.Options;
            Font lf = SystemFonts.Default(7f);
            for (int i = 0; i < _rowRects.Length && i < opts.Count; i++)
            {
                RectangleF row = _rowRects[i];
                float labelW = Math.Min(LabelW, Math.Max(0f, row.Width - CtrlMinW));
                var labelRect = new RectangleF(row.X, row.Y, labelW, row.Height);
                var ctrlRect = new RectangleF(row.X + labelW, row.Y, row.Width - labelW, row.Height);

                g.DrawText(lf, Color.FromArgb(20, 20, 20), labelRect.X + 1f, labelRect.Y + (labelRect.Height - lf.LineHeight) / 2f, opts[i].Label);

                bool choice = opts[i].Kind == NestOptionKind.Choice;
                var box = RectangleF.Inflate(ctrlRect, -1f, -2f);
                g.FillRectangle(Color.FromArgb(232, 232, 232), box);
                using (var pen = new Pen(Color.FromArgb(150, 150, 150), 1f)) g.DrawRectangle(pen, box);
                float textRight = choice ? box.Right - 12f : box.Right - 2f;
                SizeF ds = g.MeasureString(lf, opts[i].Display());
                g.DrawText(lf, Color.FromArgb(20, 20, 20), box.X + 3f, box.Y + (box.Height - ds.Height) / 2f, Trim(g, lf, opts[i].Display(), textRight - box.X - 3f));
                if (choice) Caret(g, new RectangleF(box.Right - 11f, box.Y, 9f, box.Height));
            }
        }

        protected override Response HandleMouseDown(MouseEventArgs e)
        {
            if (Host != null && e.Buttons == MouseButtons.Primary)
            {
                PointF p = e.Location;
                if (_runRect.Contains(p))
                {
                    if (Host.Run)
                    {
                        // Stop the live session: request the native solve to stop; UI-only refresh (no re-solve).
                        Host.Run = false;
                        Host.RequestStop();
                        Relayout();
                    }
                    else
                    {
                        // Start: latch Run on and launch a solve (Expire schedules the compute).
                        Host.Run = true;
                        Relayout();          // immediate Run->Stop label repaint
                        Owner.Expire();      // kick the solve
                    }
                    return Response.Handled;
                }
                if (_headerRect.Contains(p))
                {
                    // Pure UI state change: relayout + redraw only (NEVER a re-solve — that was the toggle bug).
                    Host.OptionsExpanded = !Host.OptionsExpanded;
                    Relayout();
                    return Response.Handled;
                }
                for (int i = 0; i < _rowRects.Length && i < Host.Options.Count; i++)
                    if (_rowRects[i].Contains(p))
                    {
                        EditOption(Host.Options[i]);
                        return Response.Handled;
                    }
            }
            return base.HandleMouseDown(e);
        }

        // Force the attributes to recompute layout (so the hit-rects match what is drawn) and repaint,
        // WITHOUT triggering a component re-solve. ExpireDisplay raises the DisplayChanged event.
        private void Relayout()
        {
            try { Invalidate(); } catch { }
            try { Host?.RefreshDisplay(); } catch { }
        }

        // ---- editors ----
        private void EditOption(NestOption opt)
        {
            if (opt.Kind == NestOptionKind.Choice) ShowChoiceMenu(opt);
            else ShowTextEditor(opt);
        }

        private void ShowChoiceMenu(NestOption opt)
        {
            var menu = new ContextMenu();
            RadioMenuItem controller = null;
            for (int k = 0; k < opt.ChoiceLabels.Count; k++)
            {
                int idx = k;
                var item = new RadioMenuItem(controller) { Text = opt.ChoiceLabels[k], Checked = (k == opt.SelectedIndex) };
                if (controller == null) controller = item;
                item.Click += (s, a) => { if (opt.SelectedIndex != idx) { opt.SelectedIndex = idx; AfterOptionChanged(); } };
                menu.Items.Add(item);
            }
            menu.Show();
        }

        private void ShowTextEditor(NestOption opt)
        {
            var dlg = new Dialog { Title = opt.Label, Resizable = false, Minimizable = false, Maximizable = false };
            var tb = new TextBox { Text = opt.EditText(), Width = 160 };
            var ok = new Button { Text = "OK" };
            ok.Click += (s, a) => { opt.SetFromText(tb.Text); dlg.Close(); };
            dlg.DefaultButton = ok;
            dlg.Content = new StackLayout { Padding = 8, Spacing = 6, Items = { new Label { Text = opt.Label }, tb, ok } };
            try { dlg.ShowModal(); } catch { }
            AfterOptionChanged();
        }

        // An option VALUE changed: re-nest if a live session is running, otherwise just repaint the new value.
        private void AfterOptionChanged()
        {
            if (Host != null && Host.Run) Owner.Expire();
            else Relayout();
        }

        // ---- drawing helpers ----
        private static void Button(Graphics g, RectangleF r, string text, Color fill, Color textColor, bool bold)
        {
            g.FillRectangle(fill, r);
            using (var pen = new Pen(Color.FromArgb(16, 16, 16), 1f)) g.DrawRectangle(pen, r);
            Font f = bold ? SystemFonts.Bold(8f) : SystemFonts.Default(8f);
            SizeF sz = g.MeasureString(f, text);
            g.DrawText(f, textColor, r.X + (r.Width - sz.Width) / 2f, r.Y + (r.Height - sz.Height) / 2f, text);
        }

        private static void Chevron(Graphics g, RectangleF r, bool expanded)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 3f;
            PointF[] tri = expanded
                ? new[] { new PointF(cx - s, cy - s), new PointF(cx + s, cy - s), new PointF(cx, cy + s) }   // down
                : new[] { new PointF(cx - s, cy - s), new PointF(cx + s, cy), new PointF(cx - s, cy + s) };  // right
            g.FillPolygon(Colors.White, tri);
        }

        private static void Caret(Graphics g, RectangleF r)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = 2.5f;
            g.FillPolygon(Color.FromArgb(90, 90, 90),
                new PointF(cx - s, cy - 1f), new PointF(cx + s, cy - 1f), new PointF(cx, cy + s));
        }

        private static string Trim(Graphics g, Font f, string s, float maxW)
        {
            if (string.IsNullOrEmpty(s) || g.MeasureString(f, s).Width <= maxW) return s;
            while (s.Length > 1 && g.MeasureString(f, s + "…").Width > maxW) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }
    }
}
