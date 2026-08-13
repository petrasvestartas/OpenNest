using System;
using System.Collections.Generic;

namespace nest_rhino_lib
{
    /// <summary>
    /// Pure (Rhino-free) polygon offset over the Clipper offsetter vendored in Text/Util/Clipper.cs.
    ///
    /// WHY IT EXISTS. Spacing in OpenNest1 comes from ONE place — nest_geo.offset_nesting_boundary and
    /// nest_sheets.offset_sheet_boundary — because the native solver is handed spacing 0. Both go through
    /// Rhino's Curve.Offset(..., CurveOffsetCornerStyle.Sharp), which returns NOTHING closed in two ordinary
    /// situations: a concave outline whose sharp offset self-intersects, and an inward offset that consumes
    /// the ring. Both used to fall through to "keep the original ring", i.e. a SILENT zero gap (the McNeel
    /// 221208 report). Clipper resolves self-intersections itself and returns an empty solution ONLY when the
    /// ring genuinely collapses, which turns a silent failure into a reportable one.
    ///
    /// Rhino-free on purpose: the caller in nest_geo.cs is a thin Polyline adapter, so this can be unit-tested
    /// headless — see tools/nest_geo_harness/.
    /// </summary>
    internal static class polygon_offset
    {
        /// <summary>
        /// The integer grid offset_ring will quantise this ring onto, in model units — i.e. the smallest
        /// distance it can meaningfully offset by. nest_geo.role_allowance uses it as the floor on an
        /// allowance: below this the offsetter cannot express the correction anyway, and asking it to try only
        /// round-trips the ring through the grid for nothing.
        /// </summary>
        public static double grid_step(double[] x, double[] y, int n, double distance)
        {
            double cx, cy, ext;
            if (!ring_frame(x, y, n, out cx, out cy, out ext)) return 0.0;
            return 1.0 / grid_scale(ext, Math.Abs(distance));
        }

        // Centre and half-extent of the ring. The Clipper grid is anchored HERE, not on the world origin.
        // grid_scale used to be fed the largest ABSOLUTE coordinate, which ties the resolution to where the
        // part happens to sit in the model rather than to how big it is: at (1e7, 1e7) that formula gives
        // 1e8/1e7 = 10 units of scale, i.e. a 0.1 grid, which is two orders of magnitude coarser than the 5%
        // pad on a typical allowance — and round 3 moved this offsetter onto the MAIN path for every curved
        // ring, so that grid started applying to ordinary parts. Centring makes it a function of the ring's
        // SIZE only. Checked in tools/nest_geo_harness section F: an R=100 circle's grown ring contains the
        // true circle at (0,0), (1e4,1e4), (1e6,1e6) and (1e7,1e7), with a 1.0e-6 grid at every one of them.
        private static bool ring_frame(double[] x, double[] y, int n, out double cx, out double cy, out double ext)
        {
            cx = cy = ext = 0.0;
            if (x == null || y == null || n < 3 || x.Length < n || y.Length < n) return false;
            double x0 = x[0], x1 = x[0], y0 = y[0], y1 = y[0];
            for (int i = 1; i < n; i++)
            {
                if (x[i] < x0) x0 = x[i]; else if (x[i] > x1) x1 = x[i];
                if (y[i] < y0) y0 = y[i]; else if (y[i] > y1) y1 = y[i];
            }
            cx = 0.5 * (x0 + x1); cy = 0.5 * (y0 + y1);
            ext = Math.Max(0.5 * (x1 - x0), 0.5 * (y1 - y0));
            return true;
        }

        // Clipper is integer-based: scale so the whole ring fits inside ~1e8 (well under Clipper's 0x3FFFFFFF
        // single-range limit, so its fast 64-bit slope arithmetic is used) while keeping sub-micron resolution
        // at ordinary part sizes.
        private static double grid_scale(double ext, double d)
        {
            double scale = Math.Min(1e6, 1e8 / Math.Max(1.0, ext + d));
            return scale < 1.0 ? 1.0 : scale;
        }

        /// <summary>
        /// Offset one closed ring (x/y, n vertices, NO closing duplicate) by |distance|, outward when
        /// `shrink` is false and inward when it is true. Returns { xs, ys } of the LARGEST resulting loop,
        /// or null when the ring is consumed / the input is unusable. The result keeps the INPUT's winding;
        /// vertex 0 does not survive (Clipper starts its loops wherever it likes).
        ///
        /// Keeping only the largest loop matters when a shrink SPLITS a ring: dropping the other lobes always
        /// makes the nesting outline bigger (a lost hole becomes solid material), which is the safe side.
        /// </summary>
        public static double[][] offset_ring(double[] x, double[] y, int n, double distance, bool shrink)
        {
            double d = Math.Abs(distance);
            double cx, cy, ext;
            if (!ring_frame(x, y, n, out cx, out cy, out ext) || !(d > 0)) return null;
            double scale = grid_scale(ext, d);

            var path = new List<IntPoint>(n);
            for (int i = 0; i < n; i++)
                path.Add(new IntPoint((long)Math.Round((x[i] - cx) * scale), (long)Math.Round((y[i] - cy) * scale)));
            bool in_ccw = Clipper.Orientation(path);
            if (!in_ccw) path.Reverse();                      // CCW, so a POSITIVE delta expands

            var co = new ClipperOffset(2.0, Math.Max(1.0, 0.1 * d * scale));
            co.AddPath(path, JoinType.jtMiter, EndType.etClosedPolygon);
            var sol = new List<List<IntPoint>>();
            try { co.Execute(ref sol, (shrink ? -d : d) * scale); } catch { return null; }
            if (sol == null || sol.Count == 0) return null;   // ring consumed by the offset: a REAL failure

            var big = sol[0]; double ba = Math.Abs(Clipper.Area(big));
            for (int i = 1; i < sol.Count; i++)
            {
                double a = Math.Abs(Clipper.Area(sol[i]));
                if (a > ba) { ba = a; big = sol[i]; }
            }
            if (big.Count < 3) return null;
            // Restore the INPUT's winding. The forced CCW above plus Clipper's own output convention would
            // otherwise hand back a ring wound the opposite way from the one that went in, silently undoing
            // the outer-CCW / hole-CW normalisation nest_geo.identify_groups performs immediately before.
            // (Vertex 0 still moves: Clipper starts the loop wherever it likes, and nothing downstream cares.)
            if (Clipper.Orientation(big) != in_ccw) big.Reverse();

            var ox = new double[big.Count];
            var oy = new double[big.Count];
            for (int i = 0; i < big.Count; i++) { ox[i] = big[i].X / scale + cx; oy[i] = big[i].Y / scale + cy; }
            return new double[][] { ox, oy };
        }
    }
}
