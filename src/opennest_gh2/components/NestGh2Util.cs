using System.Collections.Generic;
using Grasshopper2.Data;
using Grasshopper2.UI.Icon;
using Rhino.Geometry;

namespace opennest_gh2.components
{
    // Loads an embedded PNG icon (icons\<file>.png -> opennest_gh2.icons.<file>.png) as an Eto bitmap and
    // wraps it in a GH2 icon. Robust across the WIP SDK (FromBitmap is the universal path).
    internal static class NestIcons
    {
        public static IIcon Load(string pngFile)
        {
            try
            {
                var asm = typeof(opennest_gh2.OpenNestGh2Plugin).Assembly;
                using (var s = asm.GetManifestResourceStream("opennest_gh2.icons." + pngFile))
                {
                    if (s == null) return null;
                    var bmp = new Eto.Drawing.Bitmap(s);
                    return AbstractIcon.FromBitmap(new[] { bmp });
                }
            }
            catch { return null; }
        }
    }

    internal static class NestGh2Util
    {
        // Bounding-box diagonal of a set of curves (used to derive a tessellation length for curved input).
        public static double Diagonal(IEnumerable<Curve> curves)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (var c in curves)
                if (c != null) bb.Union(c.GetBoundingBox(false));
            return bb.IsValid ? bb.Diagonal.Length : 0.0;
        }

        // First value in a Tree (or default). Used to read a scalar from an Access.Tree input so that NO input
        // can drive GH2 per-branch iteration — the component then runs ONCE and emits ONE object.
        public static T First<T>(Tree<T> t, T def)
        {
            if (t != null) foreach (var v in t.AllItems) return v;
            return def;
        }

        // All values in a Tree as a list (e.g. per-part Copies, GH1-style), empty -> the given fallback.
        public static List<T> AllOr<T>(Tree<T> t, T fallback)
        {
            var list = new List<T>();
            if (t != null) foreach (var v in t.AllItems) list.Add(v);
            if (list.Count == 0) list.Add(fallback);
            return list;
        }
    }
}
