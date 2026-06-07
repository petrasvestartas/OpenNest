using System.Collections.Generic;
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
    }
}
