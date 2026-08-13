// Rhino-free regression test for the NESTING-outline math.
//     dotnet run --project tools/nest_geo_harness -c Release      (exit 0 = all checks passed)
//
// WHAT IS REAL PRODUCTION CODE HERE (compiled straight out of the repo, see the .csproj):
//   * nest_rhino_lib/curve_sampling.cs  -> divisions_for_turn / divisions_for_sagitta / worst_sagitta
//     (the entire decision of how many chords a curved sub-segment gets), plus inside_ring / dist_to_ring /
//     max_escape_adaptive (the MEASUREMENT that decides how far a ring has to be nudged for its ROLE, and the
//     adaptive refinement that decides where to probe for it).
//   * nest_rhino_lib/polygon_offset.cs  -> offset_ring (the polygon offset that applies that nudge, and the
//     Clipper fallback for the Spacing offset).
//   * nest_rhino_lib/Text/Util/Clipper.cs -> the vendored Clipper offsetter itself.
//
// WHAT IS *NOT* COVERED HERE, AND MUST BE CHECKED BY HAND IN RHINO. This file re-implements the RhinoCommon
// glue, so everything in that glue is untested by this harness:
//   * curve_explode.GetSegments / MakeCurveSegments — where the sub-segment split actually happens. The
//     harness is HANDED a Seg[]; it never derives one. A join/explode difference (a PolyCurve that arrives as
//     one segment, a NURBS split at a C1 kink) changes every count below and is invisible here.
//   * Curve.IsLinear(mtol*10) — the harness's Seg.IsLinear is exact. A "nearly straight" NURBS that Rhino
//     calls linear contributes ONE point in production and is never sampled.
//   * The pts.Count < 3 fallback in curve_to_polyline (re-sampling the WHOLE curve) — never reached here.
//   * Polyline.MergeColinearSegments / CollapseShortSegments — modelled by MergeColinear() below, which does
//     NOT reproduce Rhino's iteration order, only its 10 deg threshold.
//   * Curve.ClosestPoint (the ring-vertex -> curve-parameter mapping role_allowance seeds its scan with) and
//     Curve.DivideByCount — modelled by RingCurve below against an exact analytic parameterisation.
//   * The Polyline <-> double[] adapters, Point3d Z handling, and the RhinoDoc tolerance lookup.
//   * Rhino's own Curve.Offset (offset_closed_polyline tries it FIRST and only falls through to Clipper).
//     The harness proves only that the CLIPPER path behaves.
//   * curve_to_polyline_unfillet in full (its Simplify/TryGetArc/Intersection.LineLine machinery). Only the
//     property that matters downstream — that role_allowance repairs whatever ring it produces — is modelled,
//     by feeding role_allowance a deliberately coarse inscribed ring (section H).
//   * Grasshopper plumbing, the NFP solver, sheet handling, transforms.
//
// THE PROPERTY UNDER TEST. A ring's ROLE decides which way it may err, and the four roles disagree:
//     part OUTER  must CONTAIN the real part      | sheet OUTER is a container -> must be INSCRIBED in it
//     part HOLE   is free space INSIDE the part   | sheet VOID  is forbidden   -> must CONTAIN it
//     -> so the sampler is role-free and samples ON the curve, and role_allowance offsets afterwards.
// Every case below is therefore checked in all four roles, and every ring it produces is checked for
// self-intersection.
//
// COVERAGE RULE learned the hard way (round 3 passed 95/95 while shipping a 63% under-read): every shape in
// section B turns over the MAJORITY of its perimeter, so a probe budget spread uniformly by arc length landed
// ~12 probes on every chord by accident. Section G exists to break that: its shapes put ALL of their curvature
// into a few percent of the perimeter, which is what an ordinary nesting part looks like.

using System;
using System.Collections.Generic;
using System.Globalization;
using nest_rhino_lib;

namespace nest_geo_harness
{
    // ---------------------------------------------------------------------------------------------------
    // analytic curve segments (stand-ins for the RhinoCommon sub-segments curve_explode.GetSegments returns)
    // ---------------------------------------------------------------------------------------------------
    struct V2
    {
        public double X, Y;
        public V2(double x, double y) { X = x; Y = y; }
        public static V2 operator +(V2 a, V2 b) { return new V2(a.X + b.X, a.Y + b.Y); }
        public static V2 operator -(V2 a, V2 b) { return new V2(a.X - b.X, a.Y - b.Y); }
        public static V2 operator *(V2 a, double s) { return new V2(a.X * s, a.Y * s); }
        public double Len { get { return Math.Sqrt(X * X + Y * Y); } }
        public V2 Unit { get { double l = Len; return l > 0 ? new V2(X / l, Y / l) : new V2(0, 0); } }
        public double Dot(V2 b) { return X * b.X + Y * b.Y; }
    }

    abstract class Seg
    {
        public abstract V2 PointAt(double t);          // t in [0,1]
        public abstract V2 TangentAt(double t);        // unit
        public abstract bool IsLinear { get; }
        public abstract double ExactTurn { get; }      // < 0 = unknown (stands in for TryGetArc failing)
        public abstract double Length { get; }
        // arc-length parameterisation, i.e. what Curve.DivideByCount gives
        public abstract double ParamAtNormalized(double s);
    }

    sealed class LineSeg : Seg
    {
        readonly V2 a, b;
        public LineSeg(V2 a, V2 b) { this.a = a; this.b = b; }
        public override V2 PointAt(double t) { return a + (b - a) * t; }
        public override V2 TangentAt(double t) { return (b - a).Unit; }
        public override bool IsLinear { get { return true; } }
        public override double ExactTurn { get { return 0.0; } }
        public override double Length { get { return (b - a).Len; } }
        public override double ParamAtNormalized(double s) { return s; }
    }

    sealed class ArcSeg : Seg
    {
        readonly V2 c; readonly double r, a0, a1;
        public ArcSeg(V2 centre, double radius, double startAngle, double endAngle)
        { c = centre; r = radius; a0 = startAngle; a1 = endAngle; }
        double Ang(double t) { return a0 + (a1 - a0) * t; }
        public override V2 PointAt(double t) { double a = Ang(t); return new V2(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a)); }
        public override V2 TangentAt(double t)
        { double a = Ang(t); double s = Math.Sign(a1 - a0); return new V2(-Math.Sin(a) * s, Math.Cos(a) * s); }
        public override bool IsLinear { get { return false; } }
        public override double ExactTurn { get { return Math.Abs(a1 - a0); } }   // stands in for TryGetArc
        public override double Length { get { return Math.Abs(a1 - a0) * r; } }
        public override double ParamAtNormalized(double s) { return s; }         // angle is arc length / r
    }

    // Shared arc-length table for the segments that have no closed-form one.
    abstract class TabulatedSeg : Seg
    {
        double[] ts, cum;
        protected void Tabulate(int n)
        {
            ts = new double[n + 1]; cum = new double[n + 1];
            V2 prev = PointAt(0); cum[0] = 0; ts[0] = 0;
            for (int i = 1; i <= n; i++)
            {
                double t = i / (double)n; V2 p = PointAt(t);
                ts[i] = t; cum[i] = cum[i - 1] + (p - prev).Len; prev = p;
            }
        }
        public override double Length { get { return cum[cum.Length - 1]; } }
        public override double ParamAtNormalized(double s)
        {
            double target = s * Length;
            int lo = 0, hi = cum.Length - 1;
            while (lo + 1 < hi) { int mid = (lo + hi) / 2; if (cum[mid] <= target) lo = mid; else hi = mid; }
            double seg = cum[hi] - cum[lo];
            double f = seg > 0 ? (target - cum[lo]) / seg : 0;
            return ts[lo] + (ts[hi] - ts[lo]) * f;
        }
    }

    sealed class BezierSeg : TabulatedSeg
    {
        readonly V2 p0, p1, p2, p3;
        public BezierSeg(V2 p0, V2 p1, V2 p2, V2 p3)
        { this.p0 = p0; this.p1 = p1; this.p2 = p2; this.p3 = p3; Tabulate(4000); }
        public override V2 PointAt(double t)
        {
            double u = 1 - t;
            return p0 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + p3 * (t * t * t);
        }
        public override V2 TangentAt(double t)
        {
            double u = 1 - t;
            return ((p1 - p0) * (3 * u * u) + (p2 - p1) * (6 * u * t) + (p3 - p2) * (3 * t * t)).Unit;
        }
        public override bool IsLinear { get { return false; } }
        public override double ExactTurn { get { return -1.0; } }    // not an arc: force the tangent probe
    }

    // r(theta) = a + b*cos(k*theta) over a FULL turn, as ONE sub-segment. This is the T11 case: a smooth
    // closed spline, which curve_explode.MakeCurveSegments leaves whole (it splits a NURBS only at C1
    // discontinuities), so one sub-segment's chord ceiling is the ceiling for the entire ring.
    sealed class PolarSeg : TabulatedSeg
    {
        readonly double a, b, k;
        public PolarSeg(double a, double b, double k) { this.a = a; this.b = b; this.k = k; Tabulate(20000); }
        public override V2 PointAt(double t)
        {
            double th = 2 * Math.PI * t, r = a + b * Math.Cos(k * th);
            return new V2(r * Math.Cos(th), r * Math.Sin(th));
        }
        public override V2 TangentAt(double t)
        {
            double th = 2 * Math.PI * t, r = a + b * Math.Cos(k * th), dr = -b * k * Math.Sin(k * th);
            return new V2(dr * Math.Cos(th) - r * Math.Sin(th), dr * Math.Sin(th) + r * Math.Cos(th)).Unit;
        }
        public override bool IsLinear { get { return false; } }
        public override double ExactTurn { get { return -1.0; } }
    }

    // One sub-segment traversed backwards. Reversing the whole array of these flips a ring's WINDING, which
    // identify_groups does to every ring whose orientation is wrong for its role — so every guarantee has to
    // hold both ways round.
    sealed class RevSeg : Seg
    {
        readonly Seg s;
        public RevSeg(Seg s) { this.s = s; }
        public override V2 PointAt(double t) { return s.PointAt(1 - t); }
        public override V2 TangentAt(double t) { return s.TangentAt(1 - t) * -1.0; }
        public override bool IsLinear { get { return s.IsLinear; } }
        public override double ExactTurn { get { return s.ExactTurn; } }
        public override double Length { get { return s.Length; } }
        public override double ParamAtNormalized(double u) { return 1 - s.ParamAtNormalized(1 - u); }
    }

    // MODEL of the single Rhino Curve that role_allowance is handed (boundary_sorted Item4): the whole closed
    // ring, with ONE global parameter. Sub-segment i owns [i, i+1] and is evaluated in its own natural
    // parameter — i.e. the parameterisation is deliberately UNEVEN with respect to arc length (a 4000-long
    // straight and a 62-long arc get one unit each), which is the awkward case a real PolyCurve presents.
    //
    // Stands in for Curve.PointAt / Curve.ClosestPoint / Curve.DivideByCount.
    sealed class RingCurve
    {
        readonly Seg[] segs; readonly double[] cum; readonly double len;
        public RingCurve(Seg[] segs)
        {
            this.segs = segs;
            cum = new double[segs.Length + 1];
            for (int i = 0; i < segs.Length; i++) cum[i + 1] = cum[i] + segs[i].Length;
            len = cum[segs.Length];
        }
        public double DomainMin { get { return 0.0; } }
        public double DomainMax { get { return segs.Length; } }
        public V2 PointAt(double t)
        {
            int i = (int)Math.Floor(t);
            if (i < 0) { i = 0; t = 0; }
            if (i >= segs.Length) { i = segs.Length - 1; t = segs.Length; }
            return segs[i].PointAt(t - i);
        }
        // Curve.DivideByCount(m, true): m+1 ARC-LENGTH-spaced parameters spanning the whole domain.
        public double[] DivideByCount(int m)
        {
            if (m < 1) m = 1;
            var ts = new double[m + 1];
            for (int k = 0; k <= m; k++)
            {
                double target = len * k / m;
                int i = 0; while (i + 1 < segs.Length && cum[i + 1] < target) i++;
                double local = segs[i].Length > 0 ? (target - cum[i]) / segs[i].Length : 0.0;
                ts[k] = i + segs[i].ParamAtNormalized(Math.Max(0.0, Math.Min(1.0, local)));
            }
            ts[m] = segs.Length;
            return ts;
        }
        // Curve.ClosestPoint: coarse scan then a local golden-section-free bisection refine. Only has to be
        // good enough to land in the right chord's span.
        public double ClosestParam(V2 p)
        {
            const int coarse = 2048;
            double bestT = 0, bestD = double.MaxValue;
            for (int k = 0; k <= coarse; k++)
            {
                double t = segs.Length * k / (double)coarse;
                V2 q = PointAt(t); double d = (q - p).Len;
                if (d < bestD) { bestD = d; bestT = t; }
            }
            double h = segs.Length / (double)coarse;
            for (int it = 0; it < 40; it++)
            {
                double a = Math.Max(0, bestT - h), b = Math.Min(segs.Length, bestT + h);
                V2 qa = PointAt(a), qb = PointAt(b);
                double da = (qa - p).Len, db = (qb - p).Len;
                if (da < bestD) { bestD = da; bestT = a; }
                if (db < bestD) { bestD = db; bestT = b; }
                h *= 0.5;
            }
            return bestT;
        }
    }

    static class Program
    {
        const double mtol = 0.001;              // stands in for RhinoDoc.ModelAbsoluteTolerance
        static int failures = 0;
        static void Check(bool ok, string what, string detail)
        {
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what + (detail == null ? "" : ("   " + detail)));
            if (!ok) failures++;
        }
        static string F(double v) { return v.ToString("F4", CultureInfo.InvariantCulture); }

        // -----------------------------------------------------------------------------------------------
        // MODEL of nest_geo.curve_to_polyline's assembly loop (the parts that need RhinoCommon).
        // Every sampling DECISION comes from the REAL curve_sampling.
        // -----------------------------------------------------------------------------------------------
        static double SegmentTurn(Seg s)
        {
            if (s.ExactTurn >= 0) return s.ExactTurn;                 // stands in for TryGetArc
            const int probes = 128;                                   // same probe count as nest_geo
            double turn = 0; V2 prev = new V2(0, 0); bool have = false;
            for (int i = 0; i <= probes; i++)
            {
                V2 t = s.TangentAt(i / (double)probes);
                if (t.Len <= 0) continue;
                if (have) turn += Math.Acos(Math.Max(-1, Math.Min(1, prev.Dot(t))));
                prev = t; have = true;
            }
            return turn;
        }

        // nest_geo.sample_parameters: the START plus n-1 interior arc-length divisions; the END belongs to the
        // next sub-segment.
        static double[] SampleParams(Seg s, int n)
        {
            var ts = new double[Math.Max(1, n)];
            ts[0] = 0.0;
            for (int i = 1; i < n; i++) ts[i] = s.ParamAtNormalized(i / (double)n);
            return ts;
        }

        // Curve.GetBoundingBox(true).Diagonal.Length for ONE sub-segment — what nest_geo.sagitta_budget sizes
        // the refinement budget against.
        static double SegDiagonal(Seg s)
        {
            double x0 = double.MaxValue, y0 = double.MaxValue, x1 = -double.MaxValue, y1 = -double.MaxValue;
            for (int i = 0; i <= 400; i++)
            {
                V2 p = s.PointAt(i / 400.0);
                x0 = Math.Min(x0, p.X); y0 = Math.Min(y0, p.Y);
                x1 = Math.Max(x1, p.X); y1 = Math.Max(y1, p.Y);
            }
            return Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
        }

        // NEW behaviour: linear sub-segments keep only their start point; curved ones are sampled ON the curve
        // until the real curve_sampling.divisions_for_sagitta is satisfied. NO outward bias — the ring is
        // role-free, and role_allowance (RoleRing below) does the role-specific nudge afterwards.
        static List<V2> BuildRing(Seg[] segs, double sdl, bool keep_all)
        {
            bool coarse = sdl < 0;
            double step = coarse ? curve_sampling.step_angle_coarse : curve_sampling.step_angle_fine;
            int cap = coarse ? curve_sampling.max_divisions_coarse : curve_sampling.max_divisions_fine;
            double frac = coarse ? curve_sampling.sagitta_fraction_coarse : curve_sampling.sagitta_fraction_fine;

            var pts = new List<V2>();
            foreach (Seg s in segs)
            {
                if (s.IsLinear) { pts.Add(s.PointAt(0)); continue; }
                // nest_geo.sagitta_budget: a fraction of THIS SUB-SEGMENT's own bbox diagonal, not the ring's.
                double dev_tol = Math.Max(mtol, frac * SegDiagonal(s));

                int n = curve_sampling.divisions_for_turn(SegmentTurn(s), step, cap);       // REAL
                if (sdl > 0 && s.Length > 0) n = Math.Max(n, (int)Math.Ceiling(s.Length / sdl));
                if (n < 2) n = 2;
                if (n > cap) n = cap;

                Seg local = s;
                curve_sampling.curve_point at = delegate (double t, out double x, out double y)
                { V2 p = local.PointAt(t); x = p.X; y = p.Y; };
                curve_sampling.curve_divisions div = delegate (int m) { return SampleParams(local, m); };
                int seed = n;
                n = curve_sampling.divisions_for_sagitta(at, div, n, cap, dev_tol, 1.0,      // REAL
                                                         curve_sampling.sagitta_probes);
                // NGH_DEBUG=1 prints why each sub-segment got the count it did — the turn seed, the budget,
                // the ceiling and what the chords ended up giving away. That is how the ceiling was found to
                // be what binds T11 (seed 388 -> 256) while every ordinary shape converges on the seed alone.
                if (Environment.GetEnvironmentVariable("NGH_DEBUG") == "1")
                    Console.WriteLine("        [dbg] turn=" + SegmentTurn(local).ToString("F3") + " seed=" + seed
                        + " cap=" + cap + " dev_tol=" + dev_tol.ToString("F4") + " -> n=" + n
                        + " measured=" + curve_sampling.worst_sagitta(at, SampleParams(local, n), 1.0, curve_sampling.sagitta_probes).ToString("F4"));

                double[] ts = SampleParams(local, n);
                for (int d = 0; d < ts.Length; d++) pts.Add(local.PointAt(ts[d]));
            }
            if (!keep_all) MergeColinear(pts, 10.0);
            return pts;
        }

        // Stand-in for Polyline.MergeColinearSegments(10 deg): drop any vertex whose corner is within the
        // tolerance of straight. Rhino's exact iteration order is not reproducible here and does not need to
        // be — the point of the test is that role_allowance measures the ring AFTER whatever the merge did.
        static void MergeColinear(List<V2> pts, double degrees)
        {
            double lim = degrees * Math.PI / 180.0;
            bool changed = true;
            while (changed && pts.Count > 3)
            {
                changed = false;
                for (int i = 0; i < pts.Count && pts.Count > 3; i++)
                {
                    V2 a = pts[(i + pts.Count - 1) % pts.Count], b = pts[i], c = pts[(i + 1) % pts.Count];
                    V2 u = (b - a).Unit, v = (c - b).Unit;
                    if (u.Len == 0 || v.Len == 0) continue;
                    if (Math.Acos(Math.Max(-1, Math.Min(1, u.Dot(v)))) < lim) { pts.RemoveAt(i); i--; changed = true; }
                }
            }
        }

        // MODEL of nest_geo.role_allowance around the REAL curve_sampling.max_escape_adaptive and the REAL
        // polygon_offset.offset_ring / polygon_offset.grid_step. Everything Rhino-shaped — the ClosestPoint
        // mapping and DivideByCount that build the seed partition, and the Polyline adapters — is RingCurve.
        static double lastAllowance;
        static double lastMeasured;
        static int lastProbes;

        // nest_geo.escape_seeds: the ring's own vertices mapped back onto the curve (so every CHORD is its own
        // scan interval) UNION a uniform arc-length backstop, sorted and deduped.
        static double[] EscapeSeeds(List<V2> ring, RingCurve rc)
        {
            var ts = new List<double>(ring.Count + curve_sampling.escape_seed_intervals + 4);
            ts.AddRange(rc.DivideByCount(Math.Max(curve_sampling.escape_seed_intervals, ring.Count)));
            foreach (V2 v in ring) ts.Add(rc.ClosestParam(v));
            ts.Add(rc.DomainMin); ts.Add(rc.DomainMax);
            ts.Sort();
            double keep = (rc.DomainMax - rc.DomainMin) * 1e-12;
            var res = new List<double>(ts.Count);
            foreach (double t in ts)
            {
                if (t < rc.DomainMin || t > rc.DomainMax || double.IsNaN(t)) continue;
                if (res.Count > 0 && t - res[res.Count - 1] <= keep) continue;
                res.Add(t);
            }
            return res.ToArray();
        }

        static double MeasureEscape(List<V2> ring, RingCurve rc, bool circumscribe, out int probes)
        {
            probes = 0;
            int n = ring.Count;
            if (n < 3) return 0.0;
            double[] rx = new double[n], ry = new double[n];
            for (int i = 0; i < n; i++) { rx[i] = ring[i].X; ry[i] = ring[i].Y; }
            curve_sampling.curve_point at = delegate (double t, out double x, out double y)
            { V2 p = rc.PointAt(t); x = p.X; y = p.Y; };
            return curve_sampling.max_escape_adaptive(rx, ry, n, at, EscapeSeeds(ring, rc), circumscribe,   // REAL
                                                      curve_sampling.escape_probes_per_chord,
                                                      Math.Max(4096, 64 * n), out probes);
        }

        static double AllowanceFloor(List<V2> ring)
        {
            int n = ring.Count;
            double[] rx = new double[n], ry = new double[n];
            double ext = 0;
            for (int i = 0; i < n; i++)
            {
                rx[i] = ring[i].X; ry[i] = ring[i].Y;
                ext = Math.Max(ext, Math.Max(Math.Abs(rx[i]), Math.Abs(ry[i])));
            }
            double step = polygon_offset.grid_step(rx, ry, n, 0.0);   // REAL
            return Math.Max(step, Math.Max(1e-12, ext * 1e-12));
        }

        static List<V2> RoleRing(List<V2> ring, Seg[] original, bool circumscribe)
        {
            lastAllowance = 0.0; lastMeasured = 0.0; lastProbes = 0;
            if (ring.Count < 3) return ring;
            var rc = new RingCurve(original);

            int probes;
            double dev = MeasureEscape(ring, rc, circumscribe, out probes);
            lastMeasured = dev; lastProbes = probes;
            double eps = AllowanceFloor(ring);
            if (!(dev > eps)) return ring;

            // MEASURE -> OFFSET -> RE-MEASURE, exactly as production does.
            double d = dev * curve_sampling.allowance_safety;
            List<V2> best = ring;
            for (int pass = 0; pass < 3; pass++)
            {
                List<V2> off = OffsetOut(ring, d, !circumscribe);
                if (off == null || off.Count < 3) break;
                best = off; lastAllowance = d;
                int p2;
                double residual = MeasureEscape(off, rc, circumscribe, out p2);
                lastProbes += p2;
                if (!(residual > eps)) return off;
                d += residual * curve_sampling.allowance_safety;
            }
            return best;
        }

        // OLD (shipped before round 2) behaviour, reproduced from nest_geo.cs: one point per sub-segment, and a
        // fixed DivideByCount(4) whenever the boundary exploded to fewer than 3 sub-segments.
        static List<V2> BuildOld(Seg[] segs, double sdl)
        {
            var pts = new List<V2>();
            if (segs.Length >= 3)
            {
                foreach (Seg s in segs)
                {
                    if (sdl == 0 || s.IsLinear || s.Length < Math.Abs(sdl)) { pts.Add(s.PointAt(0)); }
                    else if (sdl >= 0)
                    {
                        int n = Math.Max(1, (int)Math.Ceiling(s.Length / sdl));
                        for (int i = 0; i < n; i++) pts.Add(s.PointAt(s.ParamAtNormalized(i / (double)n)));
                    }
                    else { pts.Add(s.PointAt(0)); pts.Add(s.PointAt(0.5)); }
                }
            }
            else
            {
                for (int i = 0; i < 4; i++) pts.Add(segs[0].PointAt(segs[0].ParamAtNormalized(i / 4.0)));
            }
            return pts;
        }

        // -----------------------------------------------------------------------------------------------
        // geometry checks (independent of the production code on purpose)
        // -----------------------------------------------------------------------------------------------
        static bool Inside(List<V2> poly, double px, double py)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                double xi = poly[i].X, yi = poly[i].Y, xj = poly[j].X, yj = poly[j].Y;
                if (((yi > py) != (yj > py)) && (px < (xj - xi) * (py - yi) / (yj - yi) + xi)) inside = !inside;
            }
            return inside;
        }
        static double DistToSeg(double px, double py, double ax, double ay, double bx, double by)
        {
            double dx = bx - ax, dy = by - ay, l2 = dx * dx + dy * dy;
            double t = l2 > 0 ? ((px - ax) * dx + (py - ay) * dy) / l2 : 0;
            t = Math.Max(0, Math.Min(1, t));
            double qx = ax + dx * t, qy = ay + dy * t;
            return Math.Sqrt((px - qx) * (px - qx) + (py - qy) * (py - qy));
        }
        static double DistToBoundary(List<V2> poly, double px, double py)
        {
            double best = double.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                best = Math.Min(best, DistToSeg(px, py, poly[j].X, poly[j].Y, poly[i].X, poly[i].Y));
            return best;
        }
        // Deepest point of A that lies OUTSIDE B, measured to B's boundary. 0 => A is contained in B.
        static double Escape(List<V2> a, List<V2> b)
        {
            double worst = 0;
            foreach (V2 p in a)
                if (!Inside(b, p.X, p.Y)) worst = Math.Max(worst, DistToBoundary(b, p.X, p.Y));
            return worst;
        }
        // MODEL of Curve.DivideByCount(total, true): total+1 points spread uniformly BY ARC LENGTH over the
        // WHOLE ring — not per sub-segment. This is exactly what production hands the escape measurement, and
        // reproducing it faithfully is the point: do NOT "improve" it here or the harness stops testing what
        // ships.
        static List<V2> DensifyArc(Seg[] segs, int total)
        {
            var rc = new RingCurve(segs);
            double[] ts = rc.DivideByCount(Math.Max(1, total));
            var pts = new List<V2>(ts.Length);
            for (int i = 0; i < ts.Length; i++) pts.Add(rc.PointAt(ts[i]));
            return pts;
        }

        // Worst sagitta of a ground-truth polygon, measured at its own chord MIDPOINTS (`mids` is the SAME
        // sampling at phase 0.5, so mids[i] sits between poly[i] and poly[i+1]). This is the resolution floor
        // of every containment assertion made against that polygon: a point sitting exactly ON the curve reads
        // as up to this far outside it. Indexed rather than nearest-searched — these polygons run to 35000
        // vertices and the O(N^2) form takes minutes.
        static double SagittaOf(List<V2> poly, List<V2> mids)
        {
            int n = Math.Min(poly.Count, mids.Count);
            double worst = 0;
            for (int i = 0; i < n; i++)
            {
                V2 a = poly[i], b = poly[(i + 1) % poly.Count];
                worst = Math.Max(worst, DistToSeg(mids[i].X, mids[i].Y, a.X, a.Y, b.X, b.Y));
            }
            return worst;
        }

        // GROUND TRUTH — independent of production, and deliberately NOT spread by arc length alone. The
        // share-by-length rule is what made this harness blind to the round-3 blocker: on a 4000x40 slat the
        // two R=20 ends are 1.5% of the perimeter, so at total=16000 they would get 247 points each and the
        // "truth" would itself under-read the arc by ~0.0016 — the same order as the residual under test.
        // Every sub-segment therefore ALSO gets enough points to hold its own sagitta below `stepDeg` of turn
        // per chord, independent of how small a share of the perimeter it is.
        //
        // `phase` picks which of the two jobs this set is doing:
        //   0.0 -> sub-segment junctions are sampled EXACTLY. Use as the region POLYGON: at 0.5 the first and
        //          last sample of each sub-segment sit inside it and the polygon cuts every junction corner
        //          (a 0.18 artefact on a 1200x800 panel — measured, and it is what this comment is here for).
        //   0.5 -> samples sit off the ring's vertices. Use as the measured POINT SET, so a probe cannot land
        //          on a vertex of the polygon it is being measured against and read a spurious 0.
        static List<V2> Truth(Seg[] segs, int total, double phase, double stepDeg)
        {
            double step = stepDeg * Math.PI / 180.0;
            double L = 0; foreach (Seg s in segs) L += s.Length;
            var pts = new List<V2>(total + segs.Length);
            foreach (Seg s in segs)
            {
                int byLen = (int)Math.Round(total * (L > 0 ? s.Length / L : 1.0 / segs.Length));
                int byTurn = s.IsLinear ? 0 : (int)Math.Ceiling(SegmentTurn(s) / step);
                int n = Math.Max(2, Math.Max(byLen, byTurn));
                for (int i = 0; i < n; i++) pts.Add(s.PointAt(s.ParamAtNormalized(Math.Min(1.0, (i + phase) / n))));
            }
            return pts;
        }
        // Points along a ring's EDGES, not just its vertices: the deepest point of a chord that bridges
        // outside the real region sits in the middle of that chord.
        static List<V2> EdgeSamples(List<V2> poly, int per)
        {
            var pts = new List<V2>(poly.Count * per);
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                for (int q = 0; q < per; q++) pts.Add(poly[j] + (poly[i] - poly[j]) * (q / (double)per));
            return pts;
        }
        // Self-intersection: any pair of NON-adjacent edges that properly cross.
        static int SelfCrossings(List<V2> p)
        {
            int n = p.Count, hits = 0;
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                {
                    if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                    if (SegCross(p[i], p[(i + 1) % n], p[j], p[(j + 1) % n])) hits++;
                }
            return hits;
        }
        static double Cross(V2 o, V2 a, V2 b) { return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X); }
        static bool SegCross(V2 a, V2 b, V2 c, V2 d)
        {
            double d1 = Cross(a, b, c), d2 = Cross(a, b, d), d3 = Cross(c, d, a), d4 = Cross(c, d, b);
            return ((d1 > 0) != (d2 > 0)) && ((d3 > 0) != (d4 > 0));
        }
        static double Area(List<V2> p) { return Math.Abs(SignedArea(p)); }
        // Positive = counter-clockwise. The SIGN is what identify_groups normalises (outer CCW, hole CW) and
        // what polygon_offset must hand back unchanged.
        static double SignedArea(List<V2> p)
        {
            double a = 0;
            for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += p[j].X * p[i].Y - p[i].X * p[j].Y;
            return a * 0.5;
        }
        // Worst chord sagitta actually left on the ring, from the REAL curve_sampling.dist_to_ring.
        static double WorstDeviation(List<V2> ring, Seg[] segs)
        {
            double[] rx = new double[ring.Count], ry = new double[ring.Count];
            for (int i = 0; i < ring.Count; i++) { rx[i] = ring[i].X; ry[i] = ring[i].Y; }
            double worst = 0;
            foreach (V2 p in Truth(segs, 4001, 0.5, 0.2))
                worst = Math.Max(worst, curve_sampling.dist_to_ring(rx, ry, ring.Count, p.X, p.Y));
            return worst;
        }

        static List<V2> OffsetOut(List<V2> poly, double d, bool shrink)
        {
            int n = poly.Count;
            double[] x = new double[n], y = new double[n];
            for (int i = 0; i < n; i++) { x[i] = poly[i].X; y[i] = poly[i].Y; }
            double[][] r = polygon_offset.offset_ring(x, y, n, d, shrink);   // REAL production code
            if (r == null) return null;
            var res = new List<V2>(r[0].Length);
            for (int i = 0; i < r[0].Length; i++) res.Add(new V2(r[0][i], r[1][i]));
            return res;
        }

        static List<V2> Hull(List<V2> pts)
        {
            var p = new List<V2>(pts);
            p.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            Func<V2, V2, V2, double> cross = (o, a, b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<V2>();
            foreach (V2 v in p)
            { while (h.Count >= 2 && cross(h[h.Count - 2], h[h.Count - 1], v) <= 0) h.RemoveAt(h.Count - 1); h.Add(v); }
            int lower = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--)
            { while (h.Count >= lower && cross(h[h.Count - 2], h[h.Count - 1], p[i]) <= 0) h.RemoveAt(h.Count - 1); h.Add(p[i]); }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        // Minimum centre-to-centre distance at which two copies of this nesting polygon stop overlapping,
        // over ALL translation directions: the no-fit polygon of A against itself is A (+) (-A), and the
        // closest legal placement is the nearest point of its boundary to the origin. This is exactly the
        // tightest placement an NFP nester is allowed to produce.
        static double MinCentreDistance(List<V2> a)
        {
            var sums = new List<V2>(a.Count * a.Count);
            foreach (V2 p in a) foreach (V2 q in a) sums.Add(p - q);
            var nfp = Hull(sums);
            double best = double.MaxValue;
            for (int i = 0, j = nfp.Count - 1; i < nfp.Count; j = i++)
                best = Math.Min(best, DistToSeg(0, 0, nfp[j].X, nfp[j].Y, nfp[i].X, nfp[i].Y));
            return best;
        }

        // -----------------------------------------------------------------------------------------------
        // datasets
        // -----------------------------------------------------------------------------------------------
        static Seg[] Circle(double r)
        {
            return new Seg[] { new ArcSeg(new V2(0, 0), r, 0, 2 * Math.PI) };   // ONE closed ArcCurve, as JoinCurves gives
        }
        static Seg[] Stadium(double L, double R)
        {
            return new Seg[] {
                new LineSeg(new V2(-L, -R), new V2(L, -R)),
                new ArcSeg(new V2(L, 0), R, -Math.PI / 2, Math.PI / 2),
                new LineSeg(new V2(L, R), new V2(-L, R)),
                new ArcSeg(new V2(-L, 0), R, Math.PI / 2, 3 * Math.PI / 2),
            };
        }
        // Arc + line meeting at 90 deg: at the arc's first and last sample the neighbouring ring vertex is the
        // FAR end of the straight, so a normal estimated from the neighbours points ~90 deg off radial. That
        // is the junction case a per-vertex outward push cannot get right; on-curve sampling has no normal to
        // get wrong.
        static Seg[] HalfDisc(double R)
        {
            return new Seg[] {
                new ArcSeg(new V2(0, 0), R, 0, Math.PI),
                new LineSeg(new V2(-R, 0), new V2(R, 0)),
            };
        }
        // Closed cubic-Bezier blob with a deep CONCAVE notch on the left.
        static Seg[] Blob()
        {
            V2 a = new V2(100, 0), b = new V2(0, 90), c = new V2(-100, 0), d = new V2(0, -90);
            return new Seg[] {
                new BezierSeg(a, new V2(100, 60), new V2(50, 90), b),
                new BezierSeg(b, new V2(-50, 90), new V2(-20, 10), c),     // pinched inward -> concave stretch
                new BezierSeg(c, new V2(-20, -10), new V2(-50, -90), d),
                new BezierSeg(d, new V2(50, -90), new V2(100, -60), a),
            };
        }
        // T11: r = 100 + 20*cos(16*theta) as ONE sub-segment. 16 lobes, min radius of curvature ~1.3, so the
        // old 64-chord ceiling gave 4 chords per lobe.
        static Seg[] Scallop() { return new Seg[] { new PolarSeg(100, 20, 16) }; }

        static Seg[] Polygon(params V2[] pts)
        {
            var segs = new Seg[pts.Length];
            for (int i = 0; i < pts.Length; i++) segs[i] = new LineSeg(pts[i], pts[(i + 1) % pts.Length]);
            return segs;
        }

        // ---- SECTION G shapes: curvature over a SMALL fraction of the perimeter ------------------------
        // Everything in section B turns over most of its perimeter, so a probe budget spread uniformly by arc
        // length happened to land ~12 probes on every chord. These do not: they are what an ordinary nesting
        // part looks like, and they are what caught the round-3 measurement bug.

        // 4000 x 40 slat with R=20 semicircular ends: a joined line/arc/line/arc PolyCurve, the most common
        // nesting part there is. Perimeter 8125.66, of which the two arcs are 125.66 = 1.5%.
        static Seg[] Slat() { return Stadium(2000, 20); }

        // 1200 x 800 panel with ONE semicircular bump of radius R on the bottom edge. At R=5 the bump is
        // 0.39% of the perimeter.
        static Seg[] BumpedPanel(double W, double H, double R)
        {
            return new Seg[] {
                new LineSeg(new V2(0, 0), new V2(W / 2 - R, 0)),
                new ArcSeg(new V2(W / 2, 0), R, Math.PI, 2 * Math.PI),        // dips to y = -R, outside the rect
                new LineSeg(new V2(W / 2 + R, 0), new V2(W, 0)),
                new LineSeg(new V2(W, 0), new V2(W, H)),
                new LineSeg(new V2(W, H), new V2(0, H)),
                new LineSeg(new V2(0, H), new V2(0, 0)),
            };
        }

        // Filleted rectangle W x H, corner radius R: four straights and four quarter arcs.
        static Seg[] FilletRect(double W, double H, double R)
        {
            double x0 = 0, y0 = 0, x1 = W, y1 = H;
            return new Seg[] {
                new LineSeg(new V2(x0 + R, y0), new V2(x1 - R, y0)),
                new ArcSeg(new V2(x1 - R, y0 + R), R, -Math.PI / 2, 0),
                new LineSeg(new V2(x1, y0 + R), new V2(x1, y1 - R)),
                new ArcSeg(new V2(x1 - R, y1 - R), R, 0, Math.PI / 2),
                new LineSeg(new V2(x1 - R, y1), new V2(x0 + R, y1)),
                new ArcSeg(new V2(x0 + R, y1 - R), R, Math.PI / 2, Math.PI),
                new LineSeg(new V2(x0, y1 - R), new V2(x0, y0 + R)),
                new ArcSeg(new V2(x0 + R, y0 + R), R, Math.PI, 3 * Math.PI / 2),
            };
        }

        // 1000 x 600 plate with a 40 x 25 tab on top: R=5 CONVEX fillets on the tab's two outer corners and
        // R=5 CONCAVE fillets where it meets the plate. The concave pair is the case the INSCRIBE roles care
        // about — a chord there bridges OUTSIDE the real region.
        static Seg[] TabPlate()
        {
            const double W = 1000, H = 600, xa = 480, xb = 520, ht = 25, r = 5;
            return new Seg[] {
                new LineSeg(new V2(0, 0), new V2(W, 0)),
                new LineSeg(new V2(W, 0), new V2(W, H)),
                new LineSeg(new V2(W, H), new V2(xb + r, H)),
                new ArcSeg(new V2(xb + r, H + r), r, -Math.PI / 2, -Math.PI),          // concave, going CW
                new LineSeg(new V2(xb, H + r), new V2(xb, H + ht - r)),
                new ArcSeg(new V2(xb - r, H + ht - r), r, 0, Math.PI / 2),             // convex
                new LineSeg(new V2(xb - r, H + ht), new V2(xa + r, H + ht)),
                new ArcSeg(new V2(xa + r, H + ht - r), r, Math.PI / 2, Math.PI),       // convex
                new LineSeg(new V2(xa, H + ht - r), new V2(xa, H + r)),
                new ArcSeg(new V2(xa - r, H + r), r, 0, -Math.PI / 2),                 // concave, going CW
                new LineSeg(new V2(xa - r, H), new V2(0, H)),
                new LineSeg(new V2(0, H), new V2(0, 0)),
            };
        }

        // Two R=100 discs joined by a neck of half-width 3, with R=3 CONCAVE fillets where the neck meets each
        // disc (MINOR 3 of the round-3 review). Two things make it worth its own case: the four fillets are
        // 1.4% of the perimeter, and they are the only CONCAVE stretches — so this is the shape that exercises
        // the INSCRIBE roles at a small curvature fraction, the way the slat exercises the circumscribe ones.
        static Seg[] Dumbbell()
        {
            const double R = 100, c = 150, hw = 3, f = 3;
            double px = c - Math.Sqrt((R + f) * (R + f) - (hw + f) * (hw + f));   // 47.1749: fillet centre x
            double phi = Math.Atan2(-(hw + f), px - c);                           // -3.083308: disc tangency
            return new Seg[] {
                new LineSeg(new V2(-px, -hw), new V2(px, -hw)),
                new ArcSeg(new V2( px, -(hw + f)), f, Math.PI / 2, Math.PI + phi),      // concave, CW
                new ArcSeg(new V2( c, 0), R, phi, -phi),                               // right disc, CCW
                new ArcSeg(new V2( px,  (hw + f)), f, -(Math.PI + phi), -Math.PI / 2),  // concave, CW
                new LineSeg(new V2(px, hw), new V2(-px, hw)),
                new ArcSeg(new V2(-px,  (hw + f)), f, -Math.PI / 2, phi),               // concave, CW
                new ArcSeg(new V2(-c, 0), R, Math.PI + phi, Math.PI - phi),            // left disc, CCW
                new ArcSeg(new V2(-px, -(hw + f)), f, -phi, Math.PI / 2),               // concave, CW
            };
        }

        // MODEL of the ONE branch of curve_to_polyline_unfillet that the MAJOR is about: nest_geo.cs:615, the
        // "boundary explodes to fewer than 4 sub-segments" fallback that a planar circle takes (it joins to a
        // single closed ArcCurve). DivideByCount(min(30, max(3, len/|seg|)), true) — an INSCRIBED n-gon, with
        // no allowance of any kind before this fix.
        static List<V2> UnfilletCircle(double R, double seg)
        {
            int n = Math.Min(30, Math.Max(3, (int)(2 * Math.PI * R / Math.Abs(seg))));
            var pts = new List<V2>(n);
            for (int i = 0; i < n; i++)
                pts.Add(new V2(R * Math.Cos(2 * Math.PI * i / n), R * Math.Sin(2 * Math.PI * i / n)));
            return pts;
        }

        static Seg[] Reversed(Seg[] segs)
        {
            var r = new Seg[segs.Length];
            for (int i = 0; i < segs.Length; i++) r[i] = new RevSeg(segs[segs.Length - 1 - i]);
            return r;
        }

        // -----------------------------------------------------------------------------------------------
        // one case, all four roles
        // -----------------------------------------------------------------------------------------------
        static void RoleCase(string name, Seg[] segs, double sdl)
        {
            bool keep_all = sdl == 0;          // identify_groups: Simplify 0 keeps every vertex, < 0 merges
            // Ground truth is itself a polygon INSCRIBED in the curve, so a point exactly ON the curve reads as
            // up to `truthSag` outside it. That is the resolution of these tests, and the tolerance they use —
            // asserting 0 would be asserting something about the harness, not about the production code.
            var truth = Truth(segs, 16000, 0.0, 0.2);
            var truthPts = Truth(segs, 16000, 0.5, 0.2);
            // A point sitting exactly ON the curve reads as up to truthSag OUTSIDE this polygon, and the rings
            // under test have every vertex on the curve, so that is the floor of what these tests can resolve.
            // Doubled to keep the comparison off the knife edge; still ~1e-5, four orders below the allowances.
            double truthSag = 2.0 * WorstDeviation(truth, segs) + 1e-9;
            var ring = BuildRing(segs, sdl, keep_all);

            Console.WriteLine();
            Console.WriteLine(name + "   (Simplify " + F(sdl) + ")   " + ring.Count + " verts, worst chord sagitta "
                              + F(WorstDeviation(ring, segs)) + "   [test resolution " + truthSag.ToString("E1", CultureInfo.InvariantCulture) + "]");

            // part OUTER / sheet VOID: must CONTAIN the real region.
            var grown = RoleRing(ring, segs, true);
            double growBy = lastAllowance;
            int probes = lastProbes;
            double escOut = Escape(truthPts, grown);
            // part HOLE / sheet OUTER: must be CONTAINED IN the real region.
            var shrunk = RoleRing(ring, segs, false);
            double shrinkBy = lastAllowance;
            double escIn = Escape(EdgeSamples(shrunk, 6), truth);

            double areaRaw = Area(ring), areaG = Area(grown), areaS = Area(shrunk);
            Console.WriteLine("        circumscribe: +" + F(growBy) + " -> " + grown.Count + " verts, area x"
                              + F(areaG / areaRaw) + "   |   inscribe: -" + F(shrinkBy) + " -> " + shrunk.Count
                              + " verts, area x" + F(areaS / areaRaw)
                              + "   [" + probes + " curve probes over measure+verify, per-call budget "
                              + Math.Max(4096, 64 * ring.Count) + "]");

            Check(escOut <= truthSag, name + " / part OUTER contains the true part", "escape " + F(escOut));
            Check(escOut <= truthSag, name + " / sheet VOID contains the true void", "escape " + F(escOut));
            Check(escIn <= truthSag, name + " / part HOLE stays inside the true hole", "escape " + F(escIn));
            Check(escIn <= truthSag, name + " / sheet OUTER stays inside the true sheet", "escape " + F(escIn));
            Check(SelfCrossings(ring) == 0, name + " / sampled ring is SIMPLE", SelfCrossings(ring) + " crossings");
            Check(SelfCrossings(grown) == 0, name + " / grown ring is SIMPLE", SelfCrossings(grown) + " crossings");
            Check(SelfCrossings(shrunk) == 0, name + " / shrunk ring is SIMPLE", SelfCrossings(shrunk) + " crossings");
        }

        // -----------------------------------------------------------------------------------------------
        // SECTION G driver: the SAME four role guarantees, but asserted against a ground truth dense enough
        // to resolve a feature that is a fraction of a percent of the perimeter, and with the production
        // MEASUREMENT itself under test (it may under-read the true escape by at most the safety factor,
        // because that is the whole justification for the safety factor).
        // -----------------------------------------------------------------------------------------------
        // Worst RELATIVE under-read of the production measurement against the true escape, over every section
        // G case. This is the number curve_sampling.allowance_safety has to cover, so it is worth printing
        // rather than assuming: at 12 probes per chord the peak sits at most a 24th of a chord from the
        // nearest probe, which for a parabolic profile is (1/12)^2 = 0.7% of it.
        static double worstUnderRead = double.NegativeInfinity;

        static void StrictCase(string name, Seg[] segsCCW, double sdl, bool cw)
        {
            Seg[] segs = cw ? Reversed(segsCCW) : segsCCW;
            bool keep_all = sdl == 0;                       // identify_groups: Simplify 0 keeps every vertex
            var ring = BuildRing(segs, sdl, keep_all);
            // 0.02 deg of turn per chord, so a R=5 bump is resolved to ~4e-8 no matter how small a share of
            // the perimeter it is. Two phases: the POLYGON (junction-exact) and the measured POINT SET.
            var finePoly = Truth(segs, 8000, 0.0, 0.02);
            var finePts = Truth(segs, 8000, 0.5, 0.02);
            double tol = 4.0 * SagittaOf(finePoly, finePts) + 1e-9;
            string tag = name + "  [Simplify " + F(sdl) + (cw ? ", CW]" : ", CCW]");

            double trueRaw = Escape(finePts, ring);         // real material outside the RAW ring
            var grown = RoleRing(ring, segs, true);
            double measured = lastMeasured, growBy = lastAllowance;
            int probes = lastProbes;
            double residual = Escape(finePts, grown);       // real material STILL outside the grown ring

            var shrunk = RoleRing(ring, segs, false);
            double shrinkBy = lastAllowance;
            double poke = Escape(EdgeSamples(shrunk, 8), finePoly);   // shrunk ring outside the real region

            Console.WriteLine();
            Console.WriteLine(tag + "   " + ring.Count + " verts");
            Console.WriteLine("        raw ring: true escape " + F(trueRaw) + ", production MEASURED "
                              + F(measured) + (trueRaw > 1e-9 ? ("  (" + (100.0 * (1.0 - measured / trueRaw)).ToString("F1", CultureInfo.InvariantCulture) + "% under-read)") : ""));
            Console.WriteLine("        circumscribe: +" + F(growBy) + " -> residual " + F(residual)
                              + "   |   inscribe: -" + F(shrinkBy) + " -> pokes out " + F(poke)
                              + "   [resolution " + tol.ToString("E1", CultureInfo.InvariantCulture)
                              + ", " + probes + " curve probes over measure+verify, per-call budget "
                              + Math.Max(4096, 64 * ring.Count) + "]");

            if (trueRaw > 1e-9) worstUnderRead = Math.Max(worstUnderRead, 1.0 - measured / trueRaw);
            Check(measured * curve_sampling.allowance_safety >= trueRaw - tol,
                  tag + " MEASUREMENT is within the safety factor of the true escape",
                  "measured " + F(measured) + " vs true " + F(trueRaw));
            Check(residual <= tol, tag + " part OUTER / sheet VOID contains the true region", "escape " + F(residual));
            Check(poke <= tol, tag + " part HOLE / sheet OUTER is a strict subset of the true region", "poke " + F(poke));
            Check(SelfCrossings(grown) == 0, tag + " grown ring is SIMPLE", SelfCrossings(grown) + " crossings");
            Check(SelfCrossings(shrunk) == 0, tag + " shrunk ring is SIMPLE", SelfCrossings(shrunk) + " crossings");
        }

        static int Main()
        {
            Console.WriteLine("=== A. the pre-round-2 discretisation, for reference ===");
            foreach (double sdl in new double[] { 0, -100 })
                foreach (var pair in new[] {
                    new { n = "circle R=100", s = Circle(100) },
                    new { n = "half-disc R=100", s = HalfDisc(100) },
                    new { n = "stadium R=50", s = Stadium(120, 50) },
                })
                {
                    var oldp = BuildOld(pair.s, sdl);
                    var truth = Truth(pair.s, 4000, 0.0, 0.2);
                    double esc = Escape(truth, oldp);
                    Check(esc > 1e-6, pair.n + ", Simplify " + F(sdl) + ": OLD outline under-approximates (repro)",
                          oldp.Count + " verts, true boundary escapes by " + F(esc));
                }

            Console.WriteLine();
            Console.WriteLine("=== B. role guarantees: every ring, every role, both Simplify modes ===");
            foreach (double sdl in new double[] { 0, -100 })
            {
                RoleCase("circle R=100", Circle(100), sdl);
                RoleCase("half-disc R=100 (arc + line, 90 deg junction)", HalfDisc(100), sdl);
                RoleCase("stadium R=50 (line/arc tangency)", Stadium(120, 50), sdl);
                RoleCase("spline blob with a concave notch", Blob(), sdl);
                RoleCase("T11 scallop r=100+20cos(16t)", Scallop(), sdl);
            }

            Console.WriteLine();
            Console.WriteLine("=== C. the round-2 defect, in its own units: a CIRCULAR R=100 HOLE ===");
            // A hole ring describes FREE SPACE INSIDE the part, so no vertex of it may sit outside the real
            // hole. The round-2 outward bias put every vertex of this ring at radius 100.5434 (Simplify 0) /
            // 102.0845 (Simplify -100) — material the solver would then let a nested part occupy.
            foreach (double sdl in new double[] { 0, -100 })
                foreach (bool cw in new bool[] { false, true })
                {
                    Seg[] hole = cw
                        ? new Seg[] { new ArcSeg(new V2(0, 0), 100, 0, -2 * Math.PI) }
                        : Circle(100);
                    var ring = RoleRing(BuildRing(hole, sdl, sdl == 0), hole, false /*inscribe*/);
                    double rmax = 0; foreach (V2 v in ring) rmax = Math.Max(rmax, v.Len);
                    Check(rmax <= 100.0 + 1e-9,
                          "R=100 hole, Simplify " + F(sdl) + (cw ? ", CW" : ", CCW") + ": no vertex outside the real hole",
                          "max vertex radius " + F(rmax));
                }

            Console.WriteLine();
            Console.WriteLine("=== D. polyline input is untouched, bit for bit, in every role ===");
            {
                var hexPts = new V2[6];
                for (int i = 0; i < 6; i++) hexPts[i] = new V2(80 * Math.Cos(i * Math.PI / 3), 80 * Math.Sin(i * Math.PI / 3));
                Seg[] hex = Polygon(hexPts);
                foreach (double sdl in new double[] { 0, -100, 25 })
                {
                    var got = BuildRing(hex, sdl, sdl == 0);
                    bool same = got.Count == 6;
                    if (same) for (int i = 0; i < 6; i++)
                            if (got[i].X != hexPts[i].X || got[i].Y != hexPts[i].Y) same = false;
                    var g = RoleRing(got, hex, true); double ga = lastAllowance;
                    var s = RoleRing(got, hex, false); double sa = lastAllowance;
                    Check(same, "hexagon, Simplify " + F(sdl) + ": identical vertices out", got.Count + " verts");
                    Check(ga == 0 && sa == 0 && ReferenceEquals(g, got) && ReferenceEquals(s, got),
                          "hexagon, Simplify " + F(sdl) + ": role allowance is a NO-OP on an exact ring", null);
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== E. reported failing input: circular surfaces R=100, Spacing=20 ===");
            {
                const double R = 100, spacing = 20;
                Seg[] disc = Circle(R);
                foreach (string which in new string[] { "old", "new" })
                {
                    List<V2> outline = which == "old" ? BuildOld(disc, 0) : RoleRing(BuildRing(disc, 0, true), disc, true);
                    List<V2> grown = OffsetOut(outline, spacing * 0.5, false);
                    double contact = MinCentreDistance(grown);
                    double gap = contact - 2 * R;             // true disc-to-disc gap at that spacing
                    Console.WriteLine("        " + which + ": " + outline.Count + "-gon, centres land "
                                      + F(contact) + " apart -> real gap between the discs " + F(gap)
                                      + " (Spacing asked for " + F(spacing) + ")");
                    if (which == "old")
                        Check(gap < 0, "OLD: placed discs INTERPENETRATE (repro)", "gap " + F(gap));
                    else
                        Check(gap >= spacing - 1e-6, "NEW: real gap >= Spacing", "gap " + F(gap));
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== F. the Clipper offset behind both the Spacing offset and the role allowance ===");
            {
                // (c) concave outline: a sharp offset across the notch self-intersects. Clipper cleans it up.
                var vee = new List<V2> {
                    new V2(0,0), new V2(100,0), new V2(100,100), new V2(55,100),
                    new V2(50,20), new V2(45,100), new V2(0,100)
                };
                var grown = OffsetOut(vee, 12, false);       // 12 > the 5-unit notch half-width -> self-intersects
                bool ok = grown != null && grown.Count >= 3 && Area(grown) > Area(vee);
                bool allIn = ok;
                if (ok) foreach (V2 v in vee) if (!Inside(grown, v.X, v.Y)) allIn = false;
                Check(ok, "concave V-notch grows instead of failing", grown == null ? "null" : (grown.Count + " verts, area " + F(Area(grown))));
                Check(allIn, "every original vertex is inside the grown ring", null);

                // (a) a hole narrower than 2*distance is genuinely consumed -> null, so the caller can WARN.
                var slot = new List<V2> { new V2(0, 0), new V2(200, 0), new V2(200, 6), new V2(0, 6) };
                var shrunk = OffsetOut(slot, 5, true);       // half-width 3 < 5
                Check(shrunk == null, "hole consumed by the inset reports FAILURE (null), not silence", "-> warning");

                // a normal shrink still works, and both windings behave identically
                var sq = new List<V2> { new V2(0, 0), new V2(100, 0), new V2(100, 100), new V2(0, 100) };
                var sqCW = new List<V2> { new V2(0, 0), new V2(0, 100), new V2(100, 100), new V2(100, 0) };
                var s1 = OffsetOut(sq, 10, true); var s2 = OffsetOut(sqCW, 10, true);
                var g1 = OffsetOut(sq, 10, false); var g2 = OffsetOut(sqCW, 10, false);
                Check(s1 != null && Math.Abs(Area(s1) - 6400) < 1e-3, "square shrink 100->80", s1 == null ? "null" : F(Area(s1)));
                Check(g1 != null && Math.Abs(Area(g1) - 14400) < 1e-3, "square grow 100->120", g1 == null ? "null" : F(Area(g1)));
                Check(s2 != null && Math.Abs(Area(s2) - Area(s1)) < 1e-6, "CW ring shrinks the same as CCW", null);
                Check(g2 != null && Math.Abs(Area(g2) - Area(g1)) < 1e-6, "CW ring grows the same as CCW", null);

                // (d) WINDING is preserved. identify_groups normalises outer=CCW / hole=CW immediately before
                // role_allowance runs, and the offsetter used to hand back the opposite winding (it forces the
                // input CCW and Clipper emits by its own convention), silently undoing that normalisation.
                foreach (bool ccwIn in new bool[] { true, false })
                {
                    var src = ccwIn ? sq : sqCW;
                    foreach (bool shrink in new bool[] { false, true })
                    {
                        var got = OffsetOut(src, 10, shrink);
                        bool same = got != null && (SignedArea(got) > 0) == (SignedArea(src) > 0);
                        Check(same, "offset preserves winding (" + (ccwIn ? "CCW" : "CW") + ", "
                                    + (shrink ? "shrink" : "grow") + ")",
                              got == null ? "null" : ("signed area " + F(SignedArea(got))));
                    }
                }

                // (e) the integer grid is anchored on the RING's own bbox, not on the world origin. The scale
                // is min(1e6, 1e8 / (extent + distance)); with `extent` read as the largest ABSOLUTE
                // coordinate, a part sitting at (1e7, 1e7) got 1e8/1e7 = 10 units of scale, i.e. a 0.1 grid,
                // and the 5% allowance pad was eaten by quantisation. Centred, `extent` is the ring's own
                // half-size and the grid is the same wherever the part sits.
                foreach (double at in new double[] { 0, 1e4, 1e6, 1e7 })
                {
                    Seg[] disc = new Seg[] { new ArcSeg(new V2(at, at), 100, 0, 2 * Math.PI) };
                    var r = BuildRing(disc, 0, true);
                    var grown2 = RoleRing(r, disc, true);
                    double esc = Escape(Truth(disc, 8000, 0.5, 0.02), grown2);
                    double[] gx = new double[r.Count], gy = new double[r.Count];
                    for (int i = 0; i < r.Count; i++) { gx[i] = r[i].X; gy[i] = r[i].Y; }
                    double step = polygon_offset.grid_step(gx, gy, r.Count, lastAllowance);   // REAL
                    Check(esc <= 1e-4, "R=100 circle at (" + at.ToString("E0", CultureInfo.InvariantCulture)
                                       + "): grown ring still CONTAINS it",
                          "escape " + esc.ToString("E1", CultureInfo.InvariantCulture)
                          + ", offset grid " + step.ToString("E1", CultureInfo.InvariantCulture));
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== G. curvature over a SMALL fraction of the perimeter (the round-3 blind spot) ===");
            foreach (double sdl in new double[] { 0, -100 })
                foreach (bool cw in new bool[] { false, true })
                {
                    StrictCase("4000x40 slat, R=20 ends", Slat(), sdl, cw);
                    StrictCase("1200x800 panel, R=5 bump", BumpedPanel(1200, 800, 5), sdl, cw);
                    StrictCase("1200x800 panel, R=10 bump", BumpedPanel(1200, 800, 10), sdl, cw);
                    StrictCase("300x200 rect, R=10 fillets", FilletRect(300, 200, 10), sdl, cw);
                    StrictCase("1000x600 plate with a 40x25 tab", TabPlate(), sdl, cw);
                    StrictCase("dumbbell R=100 discs, neck half-width 3", Dumbbell(), sdl, cw);
                }

            Console.WriteLine();
            Console.WriteLine("        worst under-read of the production measurement across section G: "
                              + (100.0 * worstUnderRead).ToString("F3", CultureInfo.InvariantCulture)
                              + "%   (allowance_safety pads by "
                              + (100.0 * (curve_sampling.allowance_safety - 1)).ToString("F1", CultureInfo.InvariantCulture) + "%)");
            Check(worstUnderRead < curve_sampling.allowance_safety - 1.0,
                  "every section G measurement is inside the safety pad",
                  "worst " + (100.0 * worstUnderRead).ToString("F3", CultureInfo.InvariantCulture) + "%");

            Console.WriteLine();
            Console.WriteLine("=== H. the curve_to_polyline_unfillet branch, which used to SKIP role_allowance ===");
            // opennest_gh2's OpenNest1Component hard-codes seg = diagonal*0.01 for its PARTS, and both group
            // builders used to skip apply_ring_roles whenever seg > 0 — so every part that component nested
            // came out of curve_to_polyline_unfillet with no allowance at all. Same defect class as round 2,
            // on the branch round 3 did not touch.
            {
                Seg[] disc = Circle(100);
                var finePts = Truth(disc, 8000, 0.5, 0.02);
                var finePoly = Truth(disc, 8000, 0.0, 0.02);
                double tol = 4.0 * SagittaOf(finePoly, finePts) + 1e-9;
                foreach (double seg in new double[] { 14.14, 50 })
                {
                    var raw = UnfilletCircle(100, seg);
                    double escRaw = Escape(finePts, raw);
                    double overlapRaw = 2 * 100 - MinCentreDistance(raw);   // at Spacing 0
                    var grown = RoleRing(raw, disc, true);
                    double residual = Escape(finePts, grown);
                    double gap = MinCentreDistance(grown) - 2 * 100;
                    Console.WriteLine();
                    Console.WriteLine("        seg=" + F(seg) + ": " + raw.Count + "-gon, true circle escapes it by "
                                      + F(escRaw) + " -> two copies interpenetrate " + F(overlapRaw));
                    Check(escRaw > tol, "unfillet circle seg=" + F(seg) + ": RAW ring under-approximates (repro)",
                          "escape " + F(escRaw));
                    Check(overlapRaw > 0, "unfillet circle seg=" + F(seg) + ": RAW ring lets copies interpenetrate (repro)",
                          "overlap " + F(overlapRaw));
                    Check(residual <= tol, "unfillet circle seg=" + F(seg) + ": role allowance CONTAINS the true circle",
                          "+" + F(lastAllowance) + " -> residual " + F(residual));
                    Check(gap >= -tol, "unfillet circle seg=" + F(seg) + ": copies no longer interpenetrate at Spacing 0",
                          "gap " + F(gap));
                }
                // The OTHER unfillet behaviour: a fillet is REPLACED by the sharp corner it was cut from, so
                // the ring sits OUTSIDE the curve there. That must stay free for a part OUTER (measuring only
                // one side is the whole reason role_allowance can be applied to this branch at all) and must
                // SHRINK a hole, whose sharp corners otherwise eat material a nested part would be allowed in.
                Seg[] filleted = FilletRect(300, 200, 10);
                var sharp = new List<V2> { new V2(0, 0), new V2(300, 0), new V2(300, 200), new V2(0, 200) };
                var fPts = Truth(filleted, 8000, 0.5, 0.02);
                var fPoly = Truth(filleted, 8000, 0.0, 0.02);
                double ftol = 4.0 * SagittaOf(fPoly, fPts) + 1e-9;
                var outer = RoleRing(sharp, filleted, true);
                double outerAllow = lastAllowance;
                var hole = RoleRing(sharp, filleted, false);
                double holeAllow = lastAllowance;
                double holePoke = Escape(EdgeSamples(hole, 8), fPoly);
                Console.WriteLine();
                Console.WriteLine("        unfilleted R=10 corners: OUTER allowance " + F(outerAllow)
                                  + " (must be 0), HOLE allowance " + F(holeAllow) + " -> pokes out " + F(holePoke));
                Check(outerAllow == 0 && ReferenceEquals(outer, sharp),
                      "unfilleted OUTER: sharp corner is on the HARMLESS side, no allowance", null);
                Check(holeAllow > 0, "unfilleted HOLE: sharp corner is measured and shrunk away", "-" + F(holeAllow));
                Check(holePoke <= ftol, "unfilleted HOLE stays inside the real filleted hole", "poke " + F(holePoke));
            }

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : (failures + " CHECK(S) FAILED"));
            return failures == 0 ? 0 : 1;
        }
    }
}
