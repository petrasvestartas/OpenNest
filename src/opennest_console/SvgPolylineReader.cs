using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace OpenNestConsole
{
    // Minimal reader for the `<polyline points="x,y x,y ...">` elements in the 48-element example
    // (shadoks-CGSHOP2024/sample_polygons.svg). Mirrors the convention of the C++ CLI parser in
    // nest_physics_cpp/nest_physics.cpp: every polyline is one closed ring, the FIRST one is the
    // decorative container (the caller skips it and uses a fixed sheet). Returns each ring as an
    // interleaved [x0,y0,x1,y1,...] array with any closing-duplicate vertex dropped.
    internal static class SvgPolylineReader
    {
        public static List<double[]> ReadRings(string path)
        {
            var rings = new List<double[]>();
            string text = File.ReadAllText(path);
            const string key = "points=\"";
            int pos = 0;
            while ((pos = text.IndexOf(key, pos)) >= 0)
            {
                pos += key.Length;
                int end = text.IndexOf('"', pos);
                if (end < 0) break;
                string body = text.Substring(pos, end - pos);
                pos = end + 1;

                var coords = new List<double>();
                foreach (var tok in body.Split(new[] { ' ', '\t', '\r', '\n' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    int comma = tok.IndexOf(',');
                    if (comma < 0) continue;
                    if (double.TryParse(tok.Substring(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
                        double.TryParse(tok.Substring(comma + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    {
                        coords.Add(x);
                        coords.Add(y);
                    }
                }
                if (coords.Count >= 6) // at least 3 vertices
                    rings.Add(DropClosingDuplicate(coords.ToArray()));
            }
            return rings;
        }

        // The C ABI (clean_ring) rejects a repeated closing vertex; drop it here so vertex counts are distinct.
        private static double[] DropClosingDuplicate(double[] xy)
        {
            int n = xy.Length / 2;
            if (n >= 2)
            {
                double dx = xy[0] - xy[2 * (n - 1)];
                double dy = xy[1] - xy[2 * (n - 1) + 1];
                if (dx * dx + dy * dy < 1e-12)
                {
                    var trimmed = new double[xy.Length - 2];
                    System.Array.Copy(xy, trimmed, trimmed.Length);
                    return trimmed;
                }
            }
            return xy;
        }
    }
}
