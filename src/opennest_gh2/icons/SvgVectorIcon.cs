using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Eto.Drawing;
using Grasshopper2.UI.Icon;
using Grasshopper2.UI.Icon.Vector;

namespace opennest_gh2.icons
{
    // Converts an embedded Illustrator SVG into a GH2 true-vector icon (VectorIcon) at runtime, so component
    // icons stay crisp at any zoom. Maps SVG primitives onto Grasshopper2 Builder shapes; <path> is flattened
    // to polylines (still vector). Per-icon fallback to the embedded PNG if anything is unsupported.
    public static class SvgVectorIcon
    {
        private static readonly Dictionary<string, IIcon> _cache = new Dictionary<string, IIcon>();
        private const int SIZE = 24;          // OpenNest SVGs use viewBox 0 0 24 24
        private const bool FLIP_Y = false;    // SVG and GH2 icon space are both y-down

        public static IIcon Load(string svgFile)
        {
            lock (_cache)
            {
                if (_cache.TryGetValue(svgFile, out var cached)) return cached;
                IIcon icon = null;
                try { icon = Build(svgFile); } catch { icon = null; }
                if (icon == null) icon = FallbackPng(svgFile);
                _cache[svgFile] = icon;
                return icon;
            }
        }

        private static IIcon FallbackPng(string svgFile)
        {
            try
            {
                var asm = typeof(opennest_gh2.OpenNestGh2Plugin).Assembly;
                using (var s = asm.GetManifestResourceStream("opennest_gh2.icons." + Path.ChangeExtension(svgFile, ".png")))
                {
                    if (s == null) return null;
                    return AbstractIcon.FromBitmap(new[] { new Bitmap(s) });
                }
            }
            catch { return null; }
        }

        private static IIcon Build(string svgFile)
        {
            var asm = typeof(opennest_gh2.OpenNestGh2Plugin).Assembly;
            XDocument doc;
            using (var s = asm.GetManifestResourceStream("opennest_gh2.icons." + svgFile))
            {
                if (s == null) return null;
                doc = XDocument.Load(s);
            }
            var root = doc.Root;
            if (root == null) return null;

            // viewBox -> scale to SIZE
            double vbScale = 1.0, vbX = 0, vbY = 0;
            var vb = (string)root.Attribute("viewBox");
            if (!string.IsNullOrEmpty(vb))
            {
                var p = vb.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (p.Length == 4 && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double w) && w > 0)
                { vbX = D(p[0]); vbY = D(p[1]); vbScale = SIZE / w; }
            }
            var baseM = Mul(new double[] { vbScale, 0, 0, vbScale, -vbX * vbScale, -vbY * vbScale }, Identity);

            var css = Parsecss(root);
            var b = new Builder(SIZE);
            Walk(root, b, baseM, css, new Style());
            return new VectorIcon(b);
        }

        // ---- recursive element walk ----
        private static void Walk(XElement el, Builder b, double[] m, Dictionary<string, Style> css, Style inherited)
        {
            foreach (var c in el.Elements())
            {
                string name = c.Name.LocalName;
                double[] cm = m;
                var tr = (string)c.Attribute("transform");
                if (!string.IsNullOrEmpty(tr)) cm = Mul(m, ParseTransform(tr));
                Style st = ResolveStyle(c, css, inherited);

                switch (name)
                {
                    case "g": Walk(c, b, cm, css, st); break;
                    case "polygon": Poly(b, Points(c), true, st, cm); break;
                    case "polyline": Poly(b, Points(c), false, st, cm); break;
                    case "line": Emit(b, st, () => b.Line(Pt(cm, A(c, "x1"), A(c, "y1")), Pt(cm, A(c, "x2"), A(c, "y2")))); break;
                    case "rect": Rect(b, c, st, cm); break;
                    case "circle": Circle(b, c, st, cm); break;
                    case "ellipse": Ellipse(b, c, st, cm); break;
                    case "path": foreach (var run in PathRuns((string)c.Attribute("d"), cm)) Poly(b, run, false, st, Identity); break;
                    case "text": Text(b, c, st, cm); break;
                    default: Walk(c, b, cm, css, st); break;
                }
            }
        }

        // ---- shape emitters ----
        private static void Poly(Builder b, List<PointF> pts, bool close, Style st, double[] m)
        {
            if (pts == null || pts.Count < 2) return;
            var arr = m == Identity ? pts.ToArray() : pts.Select(p => Pt(m, p.X, p.Y)).ToArray();
            if (close && arr.Length >= 3 && Dist(arr[0], arr[arr.Length - 1]) > 1e-4f)
                arr = arr.Concat(new[] { arr[0] }).ToArray();
            Emit(b, st, () => b.Polyline(arr));
        }

        private static void Rect(Builder b, XElement c, Style st, double[] m)
        {
            double x = A(c, "x"), y = A(c, "y"), w = A(c, "width"), h = A(c, "height");
            var p0 = Pt(m, x, y); var p1 = Pt(m, x + w, y + h);
            Emit(b, st, () => b.Box(p0.X, p0.Y, p1.X, p1.Y));
        }

        private static void Circle(Builder b, XElement c, Style st, double[] m)
        {
            var ctr = Pt(m, A(c, "cx"), A(c, "cy"));
            double r = A(c, "r") * Scale(m);
            Emit(b, st, () => b.Circle(ctr.X, ctr.Y, (float)r));
        }

        private static void Ellipse(Builder b, XElement c, Style st, double[] m)
        {
            var ctr = Pt(m, A(c, "cx"), A(c, "cy"));
            double rx = A(c, "rx") * Scale(m), ry = A(c, "ry") * Scale(m);
            Emit(b, st, () => b.Ellipse(ctr.X, ctr.Y, (float)rx, (float)ry));
        }

        private static void Text(Builder b, XElement c, Style st, double[] m)
        {
            string txt = c.Value?.Trim();
            if (string.IsNullOrEmpty(txt)) return;
            var p = Pt(m, A(c, "x"), A(c, "y"));
            double size = A(c, "font-size"); if (size <= 0) size = 8;
            Emit(b, st, () => b.Text(txt, p.X, p.Y, size * Scale(m)));
        }

        // apply current fill/edge (scoped IDisposables) then draw
        private static void Emit(Builder b, Style st, Action draw)
        {
            var disp = new List<IDisposable>(2);
            if (st.Stroke.HasValue) disp.Add(b.WithEdge(st.Stroke.Value, (float)Math.Max(0.2, st.StrokeWidth)));
            if (st.Fill.HasValue) disp.Add(b.WithFill(st.Fill.Value));
            try { draw(); }
            finally { for (int i = disp.Count - 1; i >= 0; i--) disp[i].Dispose(); }
        }

        // ---- style ---- (SVG defaults: fill=black, stroke=none, stroke-width=1). A class/inline rule only
        // overrides a property it actually declares (tracked by the *Set flags), so a class with no `fill`
        // does NOT wipe the inherited/default black fill.
        private sealed class Style
        {
            public Color? Fill = Colors.Black; public bool FillSet = true;
            public Color? Stroke = null; public bool StrokeSet = false;
            public double StrokeWidth = 1.0;
            public Style Clone() => new Style { Fill = Fill, FillSet = FillSet, Stroke = Stroke, StrokeSet = StrokeSet, StrokeWidth = StrokeWidth };
        }

        private static Style ResolveStyle(XElement c, Dictionary<string, Style> css, Style inherited)
        {
            var st = inherited.Clone();
            var cls = (string)c.Attribute("class");
            if (cls != null) foreach (var k in cls.Split(' ')) if (css.TryGetValue(k.Trim(), out var s)) Merge(st, s);
            Merge(st, ParseDecls((string)c.Attribute("style")));
            var f = (string)c.Attribute("fill"); if (f != null) { st.Fill = ParseColor(f); st.FillSet = true; }
            var sk = (string)c.Attribute("stroke"); if (sk != null) { st.Stroke = ParseColor(sk); st.StrokeSet = true; }
            var sw = (string)c.Attribute("stroke-width"); if (sw != null) st.StrokeWidth = D(sw);
            return st;
        }

        // copy only the properties the source actually declared
        private static void Merge(Style dst, Style src)
        {
            if (src == null) return;
            if (src.FillSet) { dst.Fill = src.Fill; dst.FillSet = true; }
            if (src.StrokeSet) { dst.Stroke = src.Stroke; dst.StrokeSet = true; }
            if (src.StrokeWidth > 0 && src.StrokeWidth != 1.0) dst.StrokeWidth = src.StrokeWidth;
        }

        private static Dictionary<string, Style> Parsecss(XElement root)
        {
            var map = new Dictionary<string, Style>();
            foreach (var styleEl in root.Descendants().Where(e => e.Name.LocalName == "style"))
                foreach (Match mm in Regex.Matches(styleEl.Value, @"\.([A-Za-z0-9_-]+)\s*\{([^}]*)\}"))
                    map[mm.Groups[1].Value] = ParseDecls(mm.Groups[2].Value);   // class attr uses the bare name
            return map;
        }

        // a Style carrying ONLY the declared properties (flags off unless declared)
        private static Style ParseDecls(string decls)
        {
            var st = new Style { FillSet = false, StrokeSet = false };
            if (string.IsNullOrEmpty(decls)) return st;
            foreach (var d in decls.Split(';'))
            {
                var kv = d.Split(':'); if (kv.Length != 2) continue;
                string k = kv[0].Trim(), v = kv[1].Trim();
                if (k == "fill") { st.Fill = ParseColor(v); st.FillSet = true; }
                else if (k == "stroke") { st.Stroke = ParseColor(v); st.StrokeSet = true; }
                else if (k == "stroke-width") st.StrokeWidth = D(v);
            }
            return st;
        }

        private static Color? ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "none" || s == "transparent") return null;
            s = s.Trim();
            if (s.StartsWith("#"))
            {
                s = s.Substring(1);
                if (s.Length == 3) s = "" + s[0] + s[0] + s[1] + s[1] + s[2] + s[2];
                if (s.Length >= 6 && int.TryParse(s.Substring(0, 2), NumberStyles.HexNumber, null, out int r)
                    && int.TryParse(s.Substring(2, 2), NumberStyles.HexNumber, null, out int g)
                    && int.TryParse(s.Substring(4, 2), NumberStyles.HexNumber, null, out int bl))
                    return Color.FromArgb(r, g, bl);
            }
            switch (s.ToLowerInvariant())
            {
                case "black": return Colors.Black;
                case "white": return Colors.White;
                case "red": return Colors.Red;
                case "green": return Colors.Green;
                case "blue": return Colors.Blue;
                case "gray": case "grey": return Colors.Gray;
            }
            return Color.FromArgb(1, 1, 1);
        }

        // ---- geometry helpers ----
        private static readonly double[] Identity = { 1, 0, 0, 1, 0, 0 };
        private static double[] Mul(double[] m, double[] n) => new[]
        {
            m[0]*n[0]+m[2]*n[1], m[1]*n[0]+m[3]*n[1],
            m[0]*n[2]+m[2]*n[3], m[1]*n[2]+m[3]*n[3],
            m[0]*n[4]+m[2]*n[5]+m[4], m[1]*n[4]+m[3]*n[5]+m[5]
        };
        private static PointF Pt(double[] m, double x, double y)
        {
            double px = m[0] * x + m[2] * y + m[4], py = m[1] * x + m[3] * y + m[5];
            return new PointF((float)px, (float)(FLIP_Y ? SIZE - py : py));
        }
        private static double Scale(double[] m) => Math.Sqrt(Math.Abs(m[0] * m[3] - m[1] * m[2]));
        private static float Dist(PointF a, PointF c) => (float)Math.Sqrt((a.X - c.X) * (a.X - c.X) + (a.Y - c.Y) * (a.Y - c.Y));

        private static double[] ParseTransform(string t)
        {
            double[] m = Identity;
            foreach (Match mm in Regex.Matches(t, @"(matrix|translate|scale|rotate)\s*\(([^)]*)\)"))
            {
                var n = mm.Groups[2].Value.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(D).ToArray();
                double[] x = Identity;
                switch (mm.Groups[1].Value)
                {
                    case "matrix": if (n.Length == 6) x = n; break;
                    case "translate": x = new double[] { 1, 0, 0, 1, n.Length > 0 ? n[0] : 0, n.Length > 1 ? n[1] : 0 }; break;
                    case "scale": x = new double[] { n.Length > 0 ? n[0] : 1, 0, 0, n.Length > 1 ? n[1] : (n.Length > 0 ? n[0] : 1), 0, 0 }; break;
                    case "rotate":
                        double a = (n.Length > 0 ? n[0] : 0) * Math.PI / 180.0; double cs = Math.Cos(a), sn = Math.Sin(a);
                        x = new double[] { cs, sn, -sn, cs, 0, 0 }; break;
                }
                m = Mul(m, x);
            }
            return m;
        }

        private static List<PointF> Points(XElement c)
        {
            var pts = new List<PointF>();
            var raw = (string)c.Attribute("points");
            if (string.IsNullOrEmpty(raw)) return pts;
            var n = raw.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i + 1 < n.Length; i += 2) pts.Add(new PointF((float)D(n[i]), (float)D(n[i + 1])));
            return pts;
        }

        // ---- SVG path -> flattened polyline runs (already transformed) ----
        private static IEnumerable<List<PointF>> PathRuns(string d, double[] m)
        {
            var runs = new List<List<PointF>>();
            if (string.IsNullOrEmpty(d)) return runs;
            var toks = Regex.Matches(d, @"[MmLlHhVvCcSsQqTtAaZz]|-?\d*\.?\d+(?:e-?\d+)?")
                            .Select(x => x.Value).ToList();
            int i = 0; double cx = 0, cy = 0, sx = 0, sy = 0; char cmd = ' ';
            List<PointF> cur = null;
            double px = 0, py = 0; // last control reflection
            Func<double> N = () => double.Parse(toks[i++], CultureInfo.InvariantCulture);
            void MoveTo(double X, double Y) { cur = new List<PointF>(); runs.Add(cur); cx = X; cy = Y; sx = X; sy = Y; cur.Add(Pt(m, cx, cy)); }
            void LineTo(double X, double Y) { if (cur == null) MoveTo(X, Y); else { cx = X; cy = Y; cur.Add(Pt(m, cx, cy)); } }
            void Cubic(double x1, double y1, double x2, double y2, double X, double Y)
            {
                const int N2 = 16; double ax = cx, ay = cy;
                for (int k = 1; k <= N2; k++) { double t = (double)k / N2, u = 1 - t;
                    double bx = u*u*u*ax + 3*u*u*t*x1 + 3*u*t*t*x2 + t*t*t*X;
                    double by = u*u*u*ay + 3*u*u*t*y1 + 3*u*t*t*y2 + t*t*t*Y;
                    cur.Add(Pt(m, bx, by)); }
                px = x2; py = y2; cx = X; cy = Y;
            }
            while (i < toks.Count)
            {
                string tk = toks[i];
                if (tk.Length == 1 && char.IsLetter(tk[0])) { cmd = tk[0]; i++; }
                bool rel = char.IsLower(cmd);
                switch (char.ToUpper(cmd))
                {
                    case 'M': { double x = N(), y = N(); if (rel) { x += cx; y += cy; } MoveTo(x, y); cmd = rel ? 'l' : 'L'; break; }
                    case 'L': { double x = N(), y = N(); if (rel) { x += cx; y += cy; } LineTo(x, y); break; }
                    case 'H': { double x = N(); if (rel) x += cx; LineTo(x, cy); break; }
                    case 'V': { double y = N(); if (rel) y += cy; LineTo(cx, y); break; }
                    case 'C': { double x1 = N(), y1 = N(), x2 = N(), y2 = N(), x = N(), y = N(); if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; x += cx; y += cy; } if (cur == null) MoveTo(cx, cy); Cubic(x1, y1, x2, y2, x, y); break; }
                    case 'S': { double x2 = N(), y2 = N(), x = N(), y = N(); if (rel) { x2 += cx; y2 += cy; x += cx; y += cy; } double x1 = 2 * cx - px, y1 = 2 * cy - py; if (cur == null) MoveTo(cx, cy); Cubic(x1, y1, x2, y2, x, y); break; }
                    case 'Q': { double qx = N(), qy = N(), x = N(), y = N(); if (rel) { qx += cx; qy += cy; x += cx; y += cy; } if (cur == null) MoveTo(cx, cy); Cubic(cx + 2.0/3*(qx-cx), cy + 2.0/3*(qy-cy), x + 2.0/3*(qx-x), y + 2.0/3*(qy-y), x, y); break; }
                    case 'T': { double x = N(), y = N(); if (rel) { x += cx; y += cy; } if (cur == null) MoveTo(cx, cy); Cubic(cx, cy, x, y, x, y); break; }
                    case 'A': { N(); N(); N(); N(); N(); double x = N(), y = N(); if (rel) { x += cx; y += cy; } LineTo(x, y); break; } // arc -> line (rare)
                    case 'Z': if (cur != null) cur.Add(Pt(m, sx, sy)); cx = sx; cy = sy; break;
                    default: i++; break;
                }
            }
            return runs;
        }

        private static double A(XElement c, string n) { var v = (string)c.Attribute(n); return string.IsNullOrEmpty(v) ? 0 : D(v); }
        private static double D(string s) => double.TryParse(Regex.Match(s ?? "", @"-?\d*\.?\d+(?:e-?\d+)?").Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
}
