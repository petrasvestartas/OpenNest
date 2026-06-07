using System.Collections.Generic;
using Grasshopper2.UI.Icon;
using Rhino.Geometry;

namespace opennest_gh2.components
{
    // Loads embedded SVG icons (icons\*.svg -> opennest_gh2.icons.<file>.svg). GH2 renders SVG (vector) icons.
    internal static class NestIcons
    {
        public static IIcon Load(string svgFile)
            => AbstractIcon.FromResource("opennest_gh2.icons." + svgFile, typeof(opennest_gh2.OpenNestGh2Plugin));
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
