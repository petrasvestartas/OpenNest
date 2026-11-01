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
    ///
    /// JOIN STYLE. A part's CLEARANCE region is the Minkowski sum of the part with a disk of radius
    /// clearance/2, i.e. a ROUND-join offset. A mitre offset is a superset of it: at a convex corner the
    /// mitre tip sits up to (mitre limit - 1) * d further out (measured 2.1-2.5 extra at d = 5 on a customer
    /// job), so mitred parts are nested with MORE than the requested gap at every convex corner and dense
    /// layouts become infeasible. offset_ring therefore takes `round_sagitta`: &gt; 0 selects round joins
    /// whose fillet chords stay within that distance of the true arc (see round_sagitta_target);
    /// 0 keeps the old mitre join, which the sheet inset and role_allowance still use.
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

        // Worst chord / nominal step sagitta ratio of the vendored Clipper's DoRound (steps = Round(a / step),
        // so the closing chord of a fillet spans up to 1.5 steps and sagitta grows with the angle squared).
        private const double round_last_chord_factor = 2.25;

        /// <summary>
        /// Chord budget for the round fillets of a clearance offset by |d| (model units): 0.4% of the offset,
        /// clamped to [0.01 mm, 0.05 mm]. `units_per_mm` converts millimetres into model units (1 for a mm
        /// model). At d = 5 mm this is 0.02 mm, the same order as the curve sampler's chord cap
        /// (curve_sampling.chord_fraction_of_ring_fine x a plate-sized batch), so the offset adds no coarser
        /// approximation than the intake already has; the clamp keeps a large clearance from dropping the
        /// fillets to a handful of chords and a tiny one from exploding the vertex count.
        /// </summary>
        public static double round_sagitta_target(double d, double units_per_mm)
        {
            if (!(units_per_mm > 0) || double.IsInfinity(units_per_mm)) units_per_mm = 1.0;
            double lo = 0.01 * units_per_mm, hi = 0.05 * units_per_mm;
            double s = 0.004 * Math.Abs(d);
            return s < lo ? lo : (s > hi ? hi : s);
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
        ///
        /// round_sagitta &gt; 0: ROUND joins (the exact clearance region, fillet vertices ON the arc, chords at
        /// most round_sagitta inside it — model units). 0 (default): MITRE joins, limit 2.
        /// </summary>
        public static double[][] offset_ring(double[] x, double[] y, int n, double distance, bool shrink,
                                             double round_sagitta = 0.0)
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

            // Round: ArcTolerance is the max chord-to-arc deviation in SCALED units. The vendored Clipper rounds
            // the step count per fillet, so the last chord can span up to 1.5 steps (sagitta x 2.25): divide by
            // that so the WORST chord, not the average one, honours round_sagitta. Never below one grid unit
            // (Clipper itself also caps it at 0.25 * d). Mitre: the original arguments (the arc tolerance is
            // unused there, the mitre limit is 2).
            bool round = round_sagitta > 0.0;
            double arc_tol = round ? Math.Max(1.0, round_sagitta / round_last_chord_factor * scale)
                                   : Math.Max(1.0, 0.1 * d * scale);
            var co = new ClipperOffset(2.0, arc_tol);
            co.AddPath(path, round ? JoinType.jtRound : JoinType.jtMiter, EndType.etClosedPolygon);
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
