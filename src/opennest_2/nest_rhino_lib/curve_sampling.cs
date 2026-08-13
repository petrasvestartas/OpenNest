using System;

namespace nest_rhino_lib
{
    /// <summary>
    /// Pure (Rhino-free) math behind the NESTING-outline discretisation in
    /// <see cref="nest_geo.curve_to_polyline"/> and the ROLE allowance in nest_geo.role_allowance.
    ///
    /// WHY THE SAMPLING IS ROLE-FREE AND ON-CURVE. curve_to_polyline is called from five places (both
    /// nest_geo group builders, the Rhino command, and two opennest_gh2 components) and in NONE of them is it
    /// yet known whether the ring it is about to build is a part's outer boundary, a part's hole, a sheet
    /// outline or a sheet void — the outer/hole sort happens afterwards. A ring's ROLE decides which way it
    /// may err:
    ///   part OUTER  must CONTAIN the real part      -> circumscribe
    ///   part HOLE   is free space INSIDE the part   -> inscribe  (a superset lets a nested part eat material)
    ///   sheet OUTER is a container                  -> inscribe  (a superset places parts off the sheet)
    ///   sheet VOID  is forbidden material           -> circumscribe
    /// so a sampler that pushed every ring one way would be wrong for half of them. Sampling ON the curve is
    /// the one choice that is safe to make blind: the chord polygon of a convex stretch is INSCRIBED, which
    /// is already correct for the two inscribe roles, and its error is bounded by the chord SAGITTA either
    /// way. The two circumscribe roles get their allowance later, from nest_geo.role_allowance, which MEASURES
    /// the escape with <see cref="max_escape_adaptive"/> and grows the ring by exactly that with a real polygon
    /// offset — then RE-MEASURES the offset ring and tops it up if anything is still on the wrong side.
    ///
    /// Correctness therefore comes from the allowance, and TIGHTNESS from the density below: the finer the
    /// sampling, the smaller the measured escape and the less the ring has to be grown.
    ///
    /// Split out of nest_geo.cs (which needs RhinoCommon and therefore a running Rhino) so the load-bearing
    /// math can be unit-tested headless — see tools/nest_geo_harness/. The curve itself is passed in as a
    /// <see cref="curve_point"/> delegate for the same reason.
    /// </summary>
    internal static class curve_sampling
    {
        // FIRST GUESS at the chord count: max turn allowed per chord. This is the scale-free form of a sagitta
        // budget (sagitta/radius = 1 - cos(step/2)), so it behaves the same on a 2 mm fillet and a 2 m arc:
        // 12 deg => 30 chords for a full circle and a deviation of 0.55% of the radius. Deliberately WIDER
        // than the 10 deg MergeColinearSegments threshold used in curve_to_polyline, or the merge pass would
        // immediately swallow the samples we just added.
        public const double step_angle_fine = 12.0 * Math.PI / 180.0;

        // Simplify < 0 is the caller explicitly ASKING for a coarse outline ("max 3 points per sub-segment"),
        // so halve the density there: 24 deg => 15 chords per circle, 2.2% of the radius, still > 10 deg.
        public const double step_angle_coarse = 24.0 * Math.PI / 180.0;

        // ABSOLUTE sagitta budget, as a fraction of the bounding-box diagonal of the SUB-SEGMENT being sampled
        // (nest_geo.sagitta_budget). The turn rule above is a per-RADIUS budget, so it under-resolves a
        // sub-segment that does most of its turning in one spot (a spline lobe): the average chord is fine
        // while one chord cuts a corner off. This is the criterion divisions_for_sagitta actually enforces, by
        // measuring; the turn rule only seeds it.
        //
        // Per SUB-SEGMENT, not per ring: sized against the whole ring, a 1200x800 panel gets a budget of 5.8,
        // so an R=5 bump could give away its entire bulge and never trigger a refinement pass — the budget
        // tracked the part instead of the feature. It was never a correctness bug (divisions_for_sagitta only
        // ever raises the count above the turn seed, and role_allowance repairs whatever the chords give away)
        // but it made the allowance fattest on exactly the parts that can least afford one. Measured effect of
        // the change across tools/nest_geo_harness: only the spline blob moves at all (38 -> 46 verts at
        // Simplify 0, worst chord sagitta 0.7867 -> 0.5009; 16 -> 22 at Simplify -100). Circle, half-disc,
        // stadium, slat, bumped panel, filleted rect and the T11 scallop are unchanged.
        public const double sagitta_fraction_fine = 0.004;
        public const double sagitta_fraction_coarse = 0.012;

        // Interior probes per chord when measuring its sagitta. 3 (the quarter points) catches an S-shaped
        // chord, which a single mid-point probe can read as zero. It can still under-read a chord whose peak
        // is narrow and off-centre (a lobe tip near one end), which is why this is only the DENSITY rule:
        // being wrong here makes a ring coarser and its allowance fatter, never unsafe. The guarantee is
        // measured separately, by max_escape_adaptive, at escape_probes_per_chord.
        public const int sagitta_probes = 3;

        // Vertex CEILING per exploded sub-segment. This is a cost backstop, not the accuracy rule — the
        // sagitta budget above is. It has to be far above the old 64 because curve_explode.MakeCurveSegments
        // splits a NURBS only at C1 discontinuities, so a smooth closed spline arrives as ONE sub-segment and
        // its ceiling is the ceiling for the whole ring: a 16-lobe scallop needs ~16 chords per lobe, which
        // 64 (4 per lobe) cannot express at all. NFP cost scales with vertex count, so ordinary shapes must
        // not get near this — they don't: the turn rule alone puts a circle at 30, a half-disc at 16 and a
        // stadium at 32, and only the scallop asks for more than 256 (388).
        public const int max_divisions_fine = 256;
        public const int max_divisions_coarse = 128;

        // How densely max_escape_adaptive probes each ring CHORD — and it really is PER CHORD: the scan
        // refines each chord's own stretch of curve until the probe spacing is under (that chord's length) /
        // this. It is NOT a budget spread over the ring.
        //
        // That distinction is the whole of the round-3 blocker. The measurement used to be
        // DivideByCount(12 * vertex_count) — uniform by ARC LENGTH over the whole ring — which lands 12
        // probes per chord only when every chord has the same arc length. On a 4000x40 slat with R=20
        // semicircular ends (a joined line/arc/line/arc PolyCurve, the most ordinary nesting part there is)
        // the ring is 32 verts, so 384 probes went onto a perimeter of 8125.66: one probe every 21.16 units
        // against an arc chord of 4.19, i.e. ONE probe per FIVE chords. Measured escape 0.040618 against a
        // true arc sagitta of 0.109562 — a 62.9% under-read, leaving 0.066916 of real material outside the
        // nesting polygon and up to 0.134 of interpenetration between two touching slats. Reproduced in
        // tools/nest_geo_harness section G, which exists because every shape in section B turns over the
        // MAJORITY of its perimeter and so got its 12 probes per chord by accident.
        public const int escape_probes_per_chord = 12;

        // Cost ceilings for that scan. The seed partition is the ring's own chords (mapped back onto the
        // curve) plus a uniform arc-length backstop; depth bounds the bisection under each seed interval.
        public const int escape_seed_intervals = 64;
        public const int escape_max_depth = 28;

        // Two small errors sit between the measured escape and the ring the offsetter returns, so the measured
        // distance is padded before it is used:
        //   * a chord's escape profile peaks in the middle and the probes straddle it, so a probe spacing of
        //     chord/k under-reads the peak by about (1/k)^2 of it;
        //   * the MITERED join of the offsetter cuts a hair inside the true (round) offset at a corner.
        // MEASURED, over the 24 cases of tools/nest_geo_harness section G (each against a ground truth sampled
        // at 0.02 deg of turn per chord): the WORST under-read of the whole measurement is 0.011%, well inside
        // this 5%. The pad costs nothing — the allowance is well under 1% of the part — and it is no longer the
        // only thing standing between the measurement and the guarantee: nest_geo.role_allowance re-measures
        // the ring it is about to ship and tops the offset up if anything is still outside.
        public const double allowance_safety = 1.05;

        /// <summary>A point on the curve at parameter t. Supplied by the caller so this file stays Rhino-free.</summary>
        public delegate void curve_point(double t, out double x, out double y);

        /// <summary>The n sampling parameters for a sub-segment (arc-length spaced, END excluded).</summary>
        public delegate double[] curve_divisions(int n);

        /// <summary>
        /// Chord count for a sub-segment that turns through `turn_angle` radians in total, capped at `cap`.
        /// A straight segment (turn 0) needs exactly one chord.
        /// </summary>
        public static int divisions_for_turn(double turn_angle, double step_angle, int cap)
        {
            if (cap < 1) cap = 1;
            if (!(turn_angle > 0.0) || !(step_angle > 0.0)) return 1;
            double raw = Math.Ceiling(turn_angle / step_angle);
            if (!(raw >= 1.0)) return 1;
            if (raw >= cap) return cap;
            return (int)raw;
        }

        /// <summary>
        /// Largest sagitta over the chords of a sub-segment sampled at `ts`, measured — not estimated from
        /// curvature. Chord d runs from ts[d] to ts[d+1], and the LAST one to `t_end` (the sub-segment's end
        /// parameter, which the next sub-segment contributes as its own start, so it is never sampled here).
        /// </summary>
        public static double worst_sagitta(curve_point at, double[] ts, double t_end, int probes)
        {
            if (at == null || ts == null || ts.Length == 0) return 0.0;
            if (probes < 1) probes = 1;
            double worst = 0.0;
            for (int d = 0; d < ts.Length; d++)
            {
                double a = ts[d];
                double b = (d + 1 < ts.Length) ? ts[d + 1] : t_end;
                if (!(Math.Abs(b - a) > 0.0)) continue;
                double ax, ay, bx, by;
                at(a, out ax, out ay);
                at(b, out bx, out by);
                for (int q = 1; q <= probes; q++)
                {
                    double px, py;
                    at(a + (b - a) * (q / (double)(probes + 1)), out px, out py);
                    double h = dist_to_segment(px, py, ax, ay, bx, by);
                    if (h > worst) worst = h;
                }
            }
            return worst;
        }

        /// <summary>
        /// Refine `n0` chords upward until no chord's sagitta exceeds `dev_tol`, or the ceiling `cap` is hit.
        /// The sagitta falls with the SQUARE of the chord, so the count that meets the budget scales with the
        /// square root of the overshoot — which converges in a pass or two for an ordinary arc. It does NOT
        /// for a curve whose turning is concentrated (a lobed spline: halving the chord there barely halves
        /// the sagitta until the chords stop straddling a lobe), which is why the pass budget is 8 and not 3:
        /// a loop that runs out of passes returns a count it never verified.
        /// Stopping at `cap` is not a correctness problem: the residual is what nest_geo.role_allowance then
        /// measures and offsets by, so a capped ring is merely a looser one.
        /// </summary>
        public static int divisions_for_sagitta(curve_point at, curve_divisions div, int n0, int cap,
                                                double dev_tol, double t_end, int probes)
        {
            if (cap < 1) cap = 1;
            int n = n0 < 1 ? 1 : (n0 > cap ? cap : n0);
            if (at == null || div == null || !(dev_tol > 0.0)) return n;
            for (int pass = 0; pass < 8 && n < cap; pass++)
            {
                double[] ts = div(n);
                if (ts == null || ts.Length == 0) break;
                double worst = worst_sagitta(at, ts, t_end, probes);
                if (!(worst > dev_tol)) break;
                int bumped = (int)Math.Ceiling(n * Math.Sqrt(worst / dev_tol));
                if (bumped <= n) break;
                n = bumped > cap ? cap : bumped;
            }
            return n;
        }

        /// <summary>Distance from (px,py) to the segment (ax,ay)-(bx,by).</summary>
        public static double dist_to_segment(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = (l2 > 0.0) ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0.0;
            if (t < 0.0) t = 0.0; else if (t > 1.0) t = 1.0;
            double qx = ax + dx * t - px, qy = ay + dy * t - py;
            return Math.Sqrt(qx * qx + qy * qy);
        }

        /// <summary>Distance from (px,py) to the CLOSED ring x/y (n vertices, no closing duplicate).</summary>
        public static double dist_to_ring(double[] x, double[] y, int n, double px, double py)
        {
            double ignored;
            return dist_to_ring(x, y, n, px, py, out ignored);
        }

        /// <summary>
        /// Distance from (px,py) to the CLOSED ring x/y, plus the LENGTH of the edge that turned out to be
        /// nearest. That length is what sets the probe spacing the adaptive escape scan has to reach here:
        /// the escape of a chord peaks in the middle of that chord, so the probes must be finer than the
        /// chord, and "the chord" is a LOCAL property — a 4000-long straight and a 4.19 arc chord on the same
        /// ring need probe spacings three orders of magnitude apart.
        /// </summary>
        public static double dist_to_ring(double[] x, double[] y, int n, double px, double py, out double edge_len)
        {
            edge_len = 0.0;
            if (x == null || y == null || n < 2) return 0.0;
            double best = double.MaxValue;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                double d = dist_to_segment(px, py, x[j], y[j], x[i], y[i]);
                if (d < best)
                {
                    best = d;
                    double ex = x[i] - x[j], ey = y[i] - y[j];
                    edge_len = Math.Sqrt(ex * ex + ey * ey);
                }
            }
            return best == double.MaxValue ? 0.0 : best;
        }

        /// <summary>Crossing-number point-in-ring test (x/y closed, n vertices, no closing duplicate).</summary>
        public static bool inside_ring(double[] x, double[] y, int n, double px, double py)
        {
            if (x == null || y == null || n < 3) return false;
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                if (((y[i] > py) != (y[j] > py)) &&
                    (px < (x[j] - x[i]) * (py - y[i]) / (y[j] - y[i]) + x[i]))
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>
        /// How far the REAL boundary escapes the nesting ring rx/ry on ONE side — the exact distance a polygon
        /// offset then has to make up. The ring array carries no closing duplicate; the curve is supplied as
        /// the <paramref name="at"/> delegate and is probed ADAPTIVELY, not from a fixed point list.
        ///
        ///   outside = true  -> the deepest point of the real region that the ring does NOT cover. Growing the
        ///                      ring by this makes it a SUPERSET: every such point is within this distance of
        ///                      the ring's boundary, so the outward offset (a Minkowski sum with a disc)
        ///                      swallows it. Used for a part's OUTER ring and a sheet's VOIDS.
        ///   outside = false -> the deepest point of the ring that lies OUTSIDE the real region (a chord
        ///                      bridging a CONCAVE stretch). Shrinking by this makes the ring a SUBSET, by the
        ///                      same argument. Used for a part's HOLES and a sheet's OUTER ring.
        ///
        /// Measuring only ONE side matters: curve_to_polyline_unfillet deliberately replaces a fillet with the
        /// sharp corner, so its ring sits well OUTSIDE the curve there. Counting that as an escape would grow
        /// every unfilleted part by the fillet radius for nothing.
        ///
        /// WHY ADAPTIVE. The escape is a LOCAL quantity: it peaks in the middle of each ring chord's stretch
        /// of curve, so it can only be found by probes finer than THAT chord. A fixed probe list spread by arc
        /// length gets that right only on a shape whose chords all have the same length; on anything with a
        /// small curved feature on a large part it walks straight past the peak (see escape_probes_per_chord).
        /// So the scan starts from `seeds` — the caller supplies the ring's own chord boundaries, mapped back
        /// onto the curve, plus a uniform backstop — and bisects each seed interval until BOTH
        ///   * the interval's chord is shorter than (nearest ring edge) / probes_per_chord, and
        ///   * the curve's own deviation from that chord is under a quarter of the same number
        /// which is what stops it descending forever along a 4000-long straight (where the nearest ring edge
        /// IS that straight, so one probe every 333 units is already finer than asked) while still resolving a
        /// 4.19 arc chord to 0.26. The second condition is what catches a feature that falls BETWEEN two
        /// seeds: a chord whose middle bulges is refined no matter how short the chord itself is.
        ///
        /// `probes` reports how many curve evaluations it took, so callers and tests can see the cost.
        /// </summary>
        public static double max_escape_adaptive(double[] rx, double[] ry, int rn,
                                                 curve_point at, double[] seeds, bool outside,
                                                 int probes_per_chord, int budget, out int probes)
        {
            probes = 0;
            if (rx == null || ry == null || at == null || seeds == null) return 0.0;
            if (rn < 3 || rx.Length < rn || ry.Length < rn || seeds.Length < 2) return 0.0;
            if (probes_per_chord < 2) probes_per_chord = 2;
            if (budget < 256) budget = 256;

            // The budget is spent PER SEED INTERVAL, not globally: depth-first recursion over one greedy
            // interval could otherwise exhaust a global pool and leave every later chord probed only at its
            // two endpoints — the same "we did not look" failure this method exists to remove, just moved.
            // The floor of 64 is what it takes to bisect one interval down to a twelfth of a chord (4 levels)
            // with room to spare, so a ring with many seeds spends a little over `budget` rather than
            // shortchanging its chords.
            var st = new escape_scan(rx, ry, rn, at, outside, probes_per_chord);
            int per_interval = Math.Max(64, budget / Math.Max(1, seeds.Length - 1));
            double worst = 0.0;
            double ax, ay, ea, la;
            st.eval(seeds[0], out ax, out ay, out ea, out la);
            for (int i = 1; i < seeds.Length; i++)
            {
                double bx, by, eb, lb;
                st.eval(seeds[i], out bx, out by, out eb, out lb);
                st.limit = st.probes + per_interval;
                double e = st.scan(seeds[i - 1], seeds[i], ax, ay, ea, la, bx, by, eb, lb, 0);
                if (e > worst) worst = e;
                ax = bx; ay = by; ea = eb; la = lb;
            }
            probes = st.probes;
            return worst;
        }

        /// <summary>Recursion state for <see cref="max_escape_adaptive"/>, kept off the argument list.</summary>
        private sealed class escape_scan
        {
            readonly double[] rx, ry; readonly int rn;
            readonly curve_point at; readonly bool outside;
            readonly int per_chord; readonly double floor_len;
            public int probes;
            public int limit;          // probe count at which the CURRENT seed interval must stop refining

            public escape_scan(double[] rx, double[] ry, int rn, curve_point at, bool outside, int per_chord)
            {
                this.rx = rx; this.ry = ry; this.rn = rn; this.at = at; this.outside = outside;
                this.per_chord = per_chord;
                double x0 = double.MaxValue, y0 = double.MaxValue, x1 = -double.MaxValue, y1 = -double.MaxValue;
                for (int i = 0; i < rn; i++)
                {
                    if (rx[i] < x0) x0 = rx[i];
                    if (rx[i] > x1) x1 = rx[i];
                    if (ry[i] < y0) y0 = ry[i];
                    if (ry[i] > y1) y1 = ry[i];
                }
                // A degenerate (zero-length) ring edge must not drive the required spacing to zero.
                double ext = Math.Max(x1 - x0, y1 - y0);
                floor_len = (ext > 0.0) ? ext * 1e-7 : 1e-12;
            }

            public void eval(double t, out double px, out double py, out double esc, out double edge)
            {
                at(t, out px, out py);
                probes++;
                double d = dist_to_ring(rx, ry, rn, px, py, out edge);
                if (edge < floor_len) edge = floor_len;
                esc = (inside_ring(rx, ry, rn, px, py) == outside) ? 0.0 : d;   // harmless side reads zero
            }

            public double scan(double a, double b, double ax, double ay, double ea, double la,
                               double bx, double by, double eb, double lb, int depth)
            {
                double worst = ea > eb ? ea : eb;
                if (depth >= escape_max_depth || probes >= limit) return worst;
                double m = 0.5 * (a + b);
                if (!(m > a) || !(m < b)) return worst;          // parameter resolution exhausted

                double mx, my, em, lm;
                eval(m, out mx, out my, out em, out lm);
                if (em > worst) worst = em;

                double dx = bx - ax, dy = by - ay;
                double span = Math.Sqrt(dx * dx + dy * dy);
                double sag = dist_to_segment(mx, my, ax, ay, bx, by);
                double need = la; if (lb < need) need = lb; if (lm < need) need = lm;
                need /= per_chord;
                if (span <= need && sag <= 0.25 * need) return worst;

                double l = scan(a, m, ax, ay, ea, la, mx, my, em, lm, depth + 1);
                if (l > worst) worst = l;
                double r = scan(m, b, mx, my, em, lm, bx, by, eb, lb, depth + 1);
                if (r > worst) worst = r;
                return worst;
            }
        }
    }
}
