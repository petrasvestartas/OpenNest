using Ed.Eto;
using nest_rhino_lib.sort_2d;
using Rhino;
using Rhino.Collections;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using Rhino.FileIO;
using Rhino.Geometry;
using Rhino.Geometry.Collections;
using Rhino.Geometry.Intersect;
using Rhino.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace nest_rhino_lib
{
    /// <summary>
    /// Holds the parts to nest. Each group carries TWO outlines: the original geometry (geometry[] /
    /// boundary_sorted Item4) and the NESTING polyline (boundary_sorted Item2) that the solver actually
    /// collides on.
    ///
    /// SPACING / OFFSET is applied HERE, upstream of the solver — NOT by the solver. To give parts a gap,
    /// call <see cref="offset_nesting_boundary"/> before solving:
    ///
    /// This is a CONVENTION, not a limitation, and the difference matters: the nfp_nest engine CAN offset
    /// (NestingContext::init runs NestingEngine::offsetTree(part, 0.5 * config.spacing) and inflates the sheet
    /// to match), so every caller deliberately hands it spacing 0 — OpenNest2 never exposes the port at all,
    /// and OpenNest1 zeroes it after doing the offset itself. Do NOT "simplify" that away: leaving the
    /// engine's spacing set while the outlines are already grown dilates them a SECOND time. (The separate
    /// nest_physics engine behind OpenNestCollision is the one that genuinely ignores spacing — its
    /// NpParams.spacing has no effect because offset_shape was never ported.)
    ///
    ///     geo.offset_nesting_boundary(spacing / 2);   // grow outers out, shrink holes in
    ///
    /// This grows ONLY the nesting polyline (Item2) — each outer loop outward, each hole inward — so placed
    /// parts keep `spacing` of clearance, while geometry[] / Item4 stay the ORIGINAL curve. The output
    /// (borders + attributes) is therefore the un-offset geometry: the offset is nesting-only and invisible
    /// in the result. On any failure the original polyline is kept, so a part is never dropped.
    ///
    /// Sheets follow the same pattern via nest_sheets.offset_sheet_boundary(margin).
    /// </summary>
    public class nest_geo
    {
        public List<int> indices;

        public List<int> copies;

        // Per-part rotation-count override, index-aligned with indices/copies/geometry.
        // 0 = inherit the solver's global Rotations setting (the default); N>0 = this part
        // may only use N discrete orientations (360/N step; 1 = fixed, no rotation).
        public List<int> rotations;

        public List<GeometryBase> geometry;

        public List<GeometryBase[]> geometry_attributes;

        // Parallel to geometry_attributes: source attribute-input-port index of each attribute item (base
        // "Attributes" port = 0, "Attributes 2" = 1, ...). Lets the nest component output attributes in
        // {part; port} sub-branches so the user can trace which input each one came from.
        // attribute_port_count = number of attribute input ports on the source Geometry component
        // (1 = only the base port => flat {part} output; >= 2 => {part; port} sub-branches).
        public List<int[]> geometry_attribute_ports;
        public int attribute_port_count = 1;

        public List<ObjectAttributes> attributes;

        public List<List<int>> geometry_sorted;

        public List<int> boudary_indices_non_sorted;

        public List<Curve> boundary_curves_non_sorted;

        public List<List<Tuple<int, Polyline, BoundingBox, Curve>>> boundary_sorted;

        public List<TextEntity> disply_texts;

        public List<List<Transform>> xforms;

        private HashSet<int> visited = new HashSet<int>();

        private List<int> ids = new List<int>();

        private int current_id = -1;

        public nest_geo()
        {
            this.indices = new List<int>();
            this.copies = new List<int>();
            this.rotations = new List<int>();
            this.geometry = new List<GeometryBase>();
            this.geometry_attributes = new List<GeometryBase[]>();
            this.geometry_attribute_ports = new List<int[]>();
            this.attributes = new List<ObjectAttributes>();
            this.geometry_sorted = new List<List<int>>();
            this.boudary_indices_non_sorted = new List<int>();
            this.boundary_curves_non_sorted = new List<Curve>();
            this.boundary_sorted = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();
            this.disply_texts = new List<TextEntity>();
            this.xforms = new List<List<Transform>>();
        }

        public List<Guid> bake()
        {
          
            List<Guid> guids = new List<Guid>();
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<Guid> guids1 = new List<Guid>();
                foreach (int item in this.geometry_sorted[i])
                {
                    this.attributes[item].RemoveFromAllGroups();
                    this.attributes[item].RemoveDisplayModeOverride();
                    GeometryBase geometryBase = this.geometry[item].Duplicate();
                    geometryBase.Translate(new Vector3d(0, 0, (double)1000));
                    guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(geometryBase, this.attributes[item]));
                }
                var group = RhinoDoc.ActiveDoc.Groups.FindName(string.Concat("nest_geo_", i.ToString()));
                if (group != null)
                {
                    RhinoDoc.ActiveDoc.Groups.Delete(group);
                }
                int num = RhinoDoc.ActiveDoc.Groups.Add(string.Concat("nest_geo_", i.ToString()), guids1);
                RhinoDoc.ActiveDoc.Groups.Show(num);
                RhinoDoc.ActiveDoc.Groups.Unlock(num);
                guids.AddRange(guids1);
            }
            return guids;
        }

        public List<Guid> bake_with_transforms()
        {
            RhinoApp.WriteLine("bake with transforms");
            List<Guid> guids = new List<Guid>();
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                for (int j = 0; j < this.xforms[i].Count; j++)
                {
                    List<Guid> guids1 = new List<Guid>();
                    foreach (int item in this.geometry_sorted[i])
                    {
                        this.attributes[item].RemoveFromAllGroups();
                        this.attributes[item].RemoveDisplayModeOverride();
                        GeometryBase geometryBase = this.geometry[item].Duplicate();
                        geometryBase.Transform(this.xforms[i][j]);
                        guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(geometryBase, this.attributes[item]));

                        var attribute_copy_for_geometry_attributes = this.attributes[item].Duplicate();
                        attribute_copy_for_geometry_attributes.ObjectColor = System.Drawing.Color.FromArgb(255, 0, 0);
                        attribute_copy_for_geometry_attributes.ColorSource = ObjectColorSource.ColorFromObject;
                        attribute_copy_for_geometry_attributes.PlotColor = System.Drawing.Color.FromArgb(255, 0, 0);
                        attribute_copy_for_geometry_attributes.PlotColorSource = ObjectPlotColorSource.PlotColorFromObject;

                        if (this.geometry_attributes.Count > item)
                        {
                            foreach (var geometry_attribute in this.geometry_attributes[item])
                            {
                                var copy = geometry_attribute.Duplicate();
                                copy.Transform(this.xforms[i][j]);
                                guids1.Add(RhinoDoc.ActiveDoc.Objects.Add(copy, attribute_copy_for_geometry_attributes));
                            }
                        }
                    }
                    var group = RhinoDoc.ActiveDoc.Groups.FindName(string.Concat("nest_geo_", j.ToString(), "_", j.ToString()));
                    if (group != null)
                    {
                        RhinoDoc.ActiveDoc.Groups.Delete(group);
                    }
                    int num = RhinoDoc.ActiveDoc.Groups.Add(string.Concat("nest_geo_", i.ToString()), guids1);
                    RhinoDoc.ActiveDoc.Groups.Show(num);
                    RhinoDoc.ActiveDoc.Groups.Unlock(num);
                    guids.AddRange(guids1);
                }
            }
            return guids;
        }

        private void BoundingBoxCallback(object sender, RTreeEventArgs e)
        {
            if (e.Id > this.current_id)
            {
                this.ids.Add(e.Id);
            }
        }

        public bool ccw(Point3d a, Point3d b, Point3d c)
        {
            bool x = (b.X - a.X) * (c.Y - a.Y) > (b.Y - a.Y) * (c.X - a.X);
            return x;
        }

        public List<Point3d> convex_hull(List<Point3d> p)
        {
            List<Point3d> point3ds;
            if (p.Count != 0)
            {
                p.Sort();
                List<Point3d> point3ds1 = new List<Point3d>();
                foreach (Point3d point3d in p)
                {
                    while (true)
                    {
                        if ((point3ds1.Count < 2 ? true : this.ccw(point3ds1[point3ds1.Count - 2], point3ds1[point3ds1.Count - 1], point3d)))
                        {
                            break;
                        }
                        point3ds1.RemoveAt(point3ds1.Count - 1);
                    }
                    point3ds1.Add(point3d);
                }
                int count = point3ds1.Count + 1;
                for (int i = p.Count - 1; i >= 0; i--)
                {
                    Point3d item = p[i];
                    while (true)
                    {
                        if ((point3ds1.Count < count ? true : this.ccw(point3ds1[point3ds1.Count - 2], point3ds1[point3ds1.Count - 1], item)))
                        {
                            break;
                        }
                        point3ds1.RemoveAt(point3ds1.Count - 1);
                    }
                    point3ds1.Add(item);
                }
                point3ds = point3ds1;
            }
            else
            {
                point3ds = new List<Point3d>();
            }
            return point3ds;
        }

        // Build the NESTING polyline (boundary_sorted Item2 — the polygon the NFP solver collides on) from a
        // boundary curve. LINEAR sub-segments contribute exactly their start point, so a polyline/polygon in
        // gives the identical polygon out (bit-for-bit, every mode). CURVED sub-segments are sampled ON the
        // curve, refined until no chord's MEASURED sagitta exceeds the ring's budget.
        //
        // Deliberately ROLE-FREE — see the header of curve_sampling.cs. All five callers (both group builders
        // below, opennest_commands, and two opennest_gh2 components) run BEFORE anything is sorted into outer
        // and hole, so nothing here knows whether this ring must circumscribe (a part's outer boundary, a
        // sheet's void) or inscribe (a part's hole, a sheet's outline). On-curve chords sit INSIDE the real
        // boundary wherever it is convex, which is already the safe side for the inscribe roles; the
        // circumscribe roles get their allowance from role_allowance, once the sort has run.
        //
        // This replaces two under-approximations that shipped for years:
        //   * segment_division_length == 0 took only PointAtStart per sub-segment, so every arc and spline
        //     collapsed to its CHORD;
        //   * a boundary that exploded to FEWER THAN 3 sub-segments (a planar circle joins to ONE closed
        //     ArcCurve) went to DivideByCount(4) — a circle of radius R nested as a square INSCRIBED in it,
        //     with the real circle bulging 0.293*R outside the polygon the solver was avoiding.
        // Both fed OpenNest1's "Spacing shows up as the gap BETWEEN parts" promise a polygon smaller than the
        // part; OpenNest2 / OpenNestCollision show the same outline on their Borders port.
        public Polyline curve_to_polyline(Curve curve, double segment_division_length = 0, bool hull = false, bool keep_all = false)
        {
            Curve[] segments = curve_explode.GetSegments(curve, true, 1);
            if (segments == null || segments.Length == 0) segments = new Curve[] { curve };

            double mtol = (RhinoDoc.ActiveDoc != null) ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
            if (mtol <= 0) mtol = 0.001;
            // Simplify < 0 is an explicit request for a COARSE outline, so widen the sagitta budget there.
            // Coarse now costs only TIGHTNESS: role_allowance measures whatever the coarse chords give up and
            // offsets exactly that back, so a coarse outline can never cost clearance.
            bool coarse = segment_division_length < 0;
            double step = coarse ? curve_sampling.step_angle_coarse : curve_sampling.step_angle_fine;
            int cap = coarse ? curve_sampling.max_divisions_coarse : curve_sampling.max_divisions_fine;
            double frac = coarse ? curve_sampling.sagitta_fraction_coarse : curve_sampling.sagitta_fraction_fine;

            var pts = new List<Point3d>();
            for (int i = 0; i < segments.Length; i++)
            {
                Curve seg = segments[i];
                if (seg == null) continue;
                if (seg.IsLinear(mtol * 10)) pts.Add(seg.PointAtStart);
                else add_curved(pts, seg, segment_division_length, step, cap, frac, mtol);
            }

            // Degenerate explode (a single line, an unsplittable stub): fall back to sampling the WHOLE curve,
            // which is what the old < 3 sub-segment branch did — only sagitta-driven instead of a fixed 4.
            if (pts.Count < 3)
            {
                pts.Clear();
                add_curved(pts, curve, segment_division_length, step, cap, frac, mtol);
            }
            if (pts.Count == 0) pts.Add(curve.PointAtStart);

            Polyline polyline = new Polyline(pts);
            if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
            {
                polyline.Add(polyline[0]);
            }
            // keep_all (Simplify==0): preserve EVERY input vertex - skip the colinear merge that
            // otherwise collapses fine curve detail (e.g. 67-pt ribbons -> ~15) and loosens nesting.
            // Where it does run it is harmless to the guarantees: it can only move the ring by up to the
            // 10 deg corner it drops, and role_allowance measures the ring AFTER this, not before.
            if (!keep_all)
                polyline.MergeColinearSegments(RhinoMath.ToRadians(10), true);
            if (hull)
            {
                polyline = new Polyline(this.convex_hull(polyline.ToList<Point3d>()));
            }
            return polyline;
        }

        // Absolute sagitta budget for ONE SUB-SEGMENT: a fraction of that sub-segment's own size, floored at
        // the model tolerance. It used to be a fraction of the WHOLE RING's diagonal, which makes the budget a
        // function of the part rather than of the feature: on a 1200x800 panel the budget is 5.8, so an R=5
        // bump could give away its entire 5 units of bulge and never trigger a refinement pass. Sized against
        // the sub-segment it is 0.4% of 10 = 0.04 there, which is the same order as the 12 deg turn rule that
        // seeds the count — so ordinary arcs still converge on the seed and pay nothing.
        //
        // Loosening this was never a CORRECTNESS problem (divisions_for_sagitta only ever raises the count
        // above the turn seed, and role_allowance measures and repairs whatever the chords give away); it cost
        // TIGHTNESS, i.e. a fatter allowance on exactly the parts that can least afford one.
        private static double sagitta_budget(Curve seg, double frac, double mtol)
        {
            double diag = 0.0;
            // accurate: the QUICK box of a NURBS is its control-point hull, which for a rational circle is
            // ~2x the real extent — the budget would scale with the representation instead of the shape.
            try { BoundingBox bb = seg.GetBoundingBox(true); if (bb.IsValid) diag = bb.Diagonal.Length; } catch { }
            return Math.Max(mtol, frac * diag);
        }

        // Sample ONE curved sub-segment onto the ring: its START plus n-1 interior divisions. The END is
        // deliberately left out — the NEXT sub-segment contributes it as its own start, which is the ring
        // assembly rule this method has always followed (no vertex emitted twice).
        //
        // The count starts from the sub-segment's TOTAL turn (one Rhino call, no sampling) and is then refined
        // against the sagitta the chords ACTUALLY give up. Both steps matter: the turn rule alone under-counts
        // a spline that does all of its turning in one spot, and the measurement alone would need a wild first
        // guess to converge from.
        private static void add_curved(List<Point3d> pts, Curve seg, double segment_division_length,
                                       double step, int cap, double frac, double mtol)
        {
            double dev_tol = sagitta_budget(seg, frac, mtol);
            int n = curve_sampling.divisions_for_turn(segment_turn(seg, mtol), step, cap);
            if (segment_division_length > 0)
            {
                double len = seg.GetLength();
                if (len > 0) n = Math.Max(n, (int)Math.Ceiling(len / segment_division_length));
            }
            if (n < 2) n = 2;                 // a curved sub-segment is never worth just its chord
            if (n > cap) n = cap;

            Curve local = seg;
            curve_sampling.curve_point at = delegate (double t, out double x, out double y)
            {
                Point3d p = local.PointAt(t);
                x = p.X; y = p.Y;
            };
            curve_sampling.curve_divisions div = delegate (int m) { return sample_parameters(local, m); };
            n = curve_sampling.divisions_for_sagitta(at, div, n, cap, dev_tol, seg.Domain.Max,
                                                     curve_sampling.sagitta_probes);

            double[] ts = sample_parameters(seg, n);
            for (int d = 0; d < ts.Length; d++) pts.Add(seg.PointAt(ts[d]));
        }

        // Total ABSOLUTE tangent turning of a sub-segment, in radians. Exact for arcs (the common case: Brep
        // fillets and circular edges); sampled for splines and conics, where it also counts the turning of an
        // S-curve on both sides so the sampling never under-resolves an inflection.
        //
        // 128 probes, not the 24 this started with: a smooth closed spline arrives here as ONE sub-segment
        // (curve_explode splits a NURBS only at C1 discontinuities), so a 16-lobe scallop turns 16 times
        // inside a single call and 24 probes ALIAS it down to a fraction of its real turning. That seeds
        // divisions_for_sagitta so far below the answer that its own chord probes then straddle a lobe and
        // read the sagitta low too, and the refinement settles ~35% over budget. Probing the tangent is
        // cheap and only splines pay for it (arcs take the TryGetArc shortcut above).
        private static double segment_turn(Curve seg, double mtol)
        {
            Arc arc;
            if (seg.TryGetArc(out arc, mtol)) return Math.Abs(arc.Angle);

            const int probes = 128;
            Interval dom = seg.Domain;
            double turn = 0.0;
            Vector3d prev = Vector3d.Unset;
            for (int i = 0; i <= probes; i++)
            {
                Vector3d t;
                try { t = seg.TangentAt(dom.ParameterAt(i / (double)probes)); }
                catch { continue; }
                if (!t.IsValid || t.IsZero) continue;
                if (prev.IsValid) turn += Vector3d.VectorAngle(prev, t);
                prev = t;
            }
            return turn;
        }

        // n arc-length samples: the sub-segment START plus its n-1 interior divisions. The END is deliberately
        // left out — the NEXT sub-segment contributes it as its own start, which is the ring assembly rule
        // this method has always followed (no vertex emitted twice).
        private static double[] sample_parameters(Curve seg, int n)
        {
            var ts = new List<double>(Math.Max(1, n));
            ts.Add(seg.Domain.Min);
            if (n > 1)
            {
                double[] inner = null;
                try { inner = seg.DivideByCount(n, false); } catch { }
                if (inner != null && inner.Length >= n - 1)
                    for (int i = 0; i < n - 1; i++) ts.Add(inner[i]);
                else
                    for (int i = 1; i < n; i++) ts.Add(seg.Domain.ParameterAt(i / (double)n));
            }
            return ts.ToArray();
        }

        // ------------------------------------------------------------------------------------------------
        // ROLE allowance
        // ------------------------------------------------------------------------------------------------
        // curve_to_polyline samples ON the curve, so a ring comes back sitting INSIDE the real boundary
        // wherever it is convex and OUTSIDE it wherever it is concave — bounded by the chord sagitta either
        // way, but signed by the geometry, not by what the ring is FOR. Once the outer/hole sort has run the
        // role IS known, and each ring is nudged the one way that is safe for it:
        //
        //   circumscribe (part OUTER, sheet VOID) : GROW  by the deepest point of the real region the ring misses
        //   inscribe     (part HOLE,  sheet OUTER): SHRINK by the deepest point of the ring outside the real region
        //
        // The distance is MEASURED against the original curve (boundary_sorted Item4) rather than estimated
        // from the sampling parameters, which is what makes it a guarantee and not a heuristic: it survives
        // MergeColinearSegments, a capped chord count, the convex hull, and the sub-segment junctions where a
        // per-vertex push has no reliable normal to push along. It is exactly ZERO for a polyline input, so a
        // polygon part comes through untouched, bit for bit.
        //
        // A real polygon offset does the nudging (via the vendored Clipper offsetter) rather than a per-vertex
        // push: an offset handles corners, cannot be talked into crossing itself the way an unbounded vertex
        // push can, and adds no vertices on a mitered join.
        //
        // WHERE the measurement probes is as load-bearing as the fact that it measures at all, and is what
        // round 3 got wrong. The escape peaks in the MIDDLE of each chord's stretch of curve, so it can only be
        // found by probes finer than that chord — and "that chord" is local: a 4000x40 slat's ring carries one
        // 4000-long straight next to fifteen 4.19 arc chords. A probe list spread uniformly by arc length (what
        // this used to do) lands on the arcs once every five chords and reads 0.040618 where the truth is
        // 0.109562. escape_seeds therefore hands the scan the ring's OWN chord boundaries and
        // max_escape_adaptive refines inside each one; then the whole thing is repeated on the OFFSET ring, so
        // what is returned has been checked rather than computed.
        internal static Polyline role_allowance(Polyline ring, Curve original, bool circumscribe)
        {
            if (ring == null || original == null) return ring;
            int n = ring_vertex_count(ring);
            if (n < 3) return ring;

            double[] rx = new double[n], ry = new double[n];
            for (int i = 0; i < n; i++) { rx[i] = ring[i].X; ry[i] = ring[i].Y; }

            Curve local = original;
            curve_sampling.curve_point at = delegate (double t, out double x, out double y)
            {
                Point3d p = local.PointAt(t);
                x = p.X; y = p.Y;
            };

            double dev = measure_escape(rx, ry, n, at, ring, original, circumscribe);

            // The gate is the OFFSETTER's own grid step, not half the model tolerance. A measured zero now
            // means "every chord was probed on its own span until the spacing was under a twelfth of it, and
            // nothing was on the wrong side" — the old 0.5*mtol gate was reading a measurement that had not
            // looked (an R=5 bump on a 1200x800 panel measured 0.00000 and was skipped while 0.096 of real
            // material sat outside). Below the grid step there is nothing the offsetter can express anyway, so
            // skipping is the honest answer rather than a round-trip through the integer grid for nothing.
            double eps = allowance_floor(rx, ry, n);
            if (!(dev > eps)) return ring;

            // MEASURE -> OFFSET -> RE-MEASURE. Round 3 shipped the first step only, so the guarantee was
            // "the number we computed times 1.05", which is worth exactly as much as the number. Re-measuring
            // the ring that is actually about to be returned makes it "we checked", and the top-up covers the
            // offsetter's mitered joins as well as any probe the scan still straddled.
            double d = dev * curve_sampling.allowance_safety;
            Polyline best = ring;
            for (int pass = 0; pass < 3; pass++)
            {
                Polyline off = clipper_offset_closed_polyline(ring, d, !circumscribe);
                // A shrink that CONSUMES the ring means the ring is smaller than its own discretisation error
                // — a hole that could not have held anything anyway. Keep the last good ring, never drop one.
                if (off == null || off.Count < 4) break;
                best = off;
                int m = ring_vertex_count(off);
                if (m < 3) break;
                double[] ox = new double[m], oy = new double[m];
                for (int i = 0; i < m; i++) { ox[i] = off[i].X; oy[i] = off[i].Y; }
                double residual = measure_escape(ox, oy, m, at, off, original, circumscribe);
                if (!(residual > eps)) return off;
                d += residual * curve_sampling.allowance_safety;
            }
            return best;
        }

        private static int ring_vertex_count(Polyline ring)
        {
            int n = ring.Count;
            if (n > 1 && ring[0].DistanceTo(ring[n - 1]) < 1e-9) n--;   // drop the closing duplicate
            return n;
        }

        // Smallest allowance worth applying to this ring: what polygon_offset can actually resolve on it,
        // floored well above double noise so a POLYLINE input (whose escape is exactly 0) is still returned
        // bit for bit.
        private static double allowance_floor(double[] rx, double[] ry, int n)
        {
            double step = polygon_offset.grid_step(rx, ry, n, 0.0);
            double ext = 0.0;
            for (int i = 0; i < n; i++)
            {
                if (Math.Abs(rx[i]) > ext) ext = Math.Abs(rx[i]);
                if (Math.Abs(ry[i]) > ext) ext = Math.Abs(ry[i]);
            }
            double noise = Math.Max(1e-12, ext * 1e-12);
            return Math.Max(step, noise);
        }

        private static double measure_escape(double[] rx, double[] ry, int n, curve_sampling.curve_point at,
                                             Polyline ring, Curve original, bool circumscribe)
        {
            double[] seeds = escape_seeds(ring, n, original);
            if (seeds == null || seeds.Length < 2) return 0.0;
            int probes;
            // Cost is O(probes * n) — each probe is one Curve.PointAt plus a scan of the ring's edges — and
            // escape_seeds adds n Curve.ClosestPoint calls on top. Measured across tools/nest_geo_harness,
            // counting the measure pass AND the verification pass together: 718 to 3150 probes for rings of 9
            // to 46 vertices, 6690 for the 94-vertex dumbbell and 17654 for the 256-vertex T11 scallop,
            // against per-call budgets of 4096 / 6016 / 16384 respectively. Nothing in the harness reaches the
            // ceiling; a ring that did would only get a looser measurement, and so a fatter allowance, never
            // an unsafe one.
            return curve_sampling.max_escape_adaptive(rx, ry, n, at, seeds, circumscribe,
                                                      curve_sampling.escape_probes_per_chord,
                                                      Math.Max(4096, 64 * n), out probes);
        }

        // The parameter breakpoints the escape scan starts from. TWO sources, and both are load-bearing:
        //
        //   * the ring's OWN vertices, mapped back onto the curve with ClosestPoint, so every CHORD becomes
        //     its own interval and the scan resolves each one to a twelfth of ITS length. This is the fix for
        //     the round-3 defect: a probe list spread uniformly by arc length put one probe on every FIVE
        //     chords of a 4000x40 slat's R=20 ends.
        //   * a uniform arc-length partition, as a coverage backstop for the rings where that mapping is not
        //     a clean monotone cover of the curve — a coarse ring on a shape with a narrow neck, a convex
        //     hull, or curve_to_polyline_unfillet's output, whose sharp corner vertices are not on the curve
        //     at all. Extra seeds only cost probes; a missing one would cost a guarantee.
        private static double[] escape_seeds(Polyline ring, int n, Curve original)
        {
            Interval dom;
            try { dom = original.Domain; } catch { return null; }
            if (!(dom.Length > 0)) return null;

            var ts = new List<double>(n + curve_sampling.escape_seed_intervals + 4);
            double[] uni = null;
            try { uni = original.DivideByCount(Math.Max(curve_sampling.escape_seed_intervals, n), true); } catch { }
            if (uni != null) ts.AddRange(uni);
            for (int i = 0; i < n; i++)
            {
                double t;
                try { if (original.ClosestPoint(ring[i], out t)) ts.Add(t); } catch { }
            }
            ts.Add(dom.Min);
            ts.Add(dom.Max);

            ts.Sort();
            double keep = dom.Length * 1e-12;
            var res = new List<double>(ts.Count);
            for (int i = 0; i < ts.Count; i++)
            {
                double t = ts[i];
                if (t < dom.Min || t > dom.Max || double.IsNaN(t)) continue;
                if (res.Count > 0 && t - res[res.Count - 1] <= keep) continue;
                res.Add(t);
            }
            return res.Count >= 2 ? res.ToArray() : null;
        }

        // Apply role_allowance across ONE part group. Groups are sorted largest-first, so index 0 is the
        // part's OUTER boundary (must CONTAIN the part) and every later ring is a HOLE (free space INSIDE the
        // part, so it must be CONTAINED in the real hole) — the same j > 0 convention offset_nesting_boundary
        // uses. Item3 is refreshed with the same tolerance inflation the group builders apply.
        //
        // RUNS FOR EVERY BRANCH, curve_to_polyline_unfillet included. It used to be skipped when
        // segment_division_length > 0, on the grounds that unfillet is a deliberately different outline model
        // — but "different model" was not a reason to drop the guarantee, and the branch inscribes just as
        // badly: opennest_gh2's OpenNest1Component hard-codes seg = diagonal*0.01 for its PARTS, so every part
        // it nests took that branch. Measured on the real path: an R=100 circle at seg = 14.14 comes out of
        // curve_to_polyline_unfillet as a 30-gon whose true circle escapes by 0.547810, i.e. two copies can be
        // centred 198.904 apart for 1.0956 of interpenetration at Spacing 0; at seg = 50, 3.4074 of escape and
        // 6.8148 of overlap.
        //
        // Applying it there is safe precisely BECAUSE max_escape_adaptive measures only ONE side: unfillet
        // replaces a fillet with the sharp corner it was cut from, so its ring sits well outside the curve at
        // that corner — on the harmless side for an outer, which therefore grows only by the arc sagitta and
        // not by the fillet radius. A HOLE is the case that visibly changes: an unfilleted hole ring CONTAINS
        // the real hole (its sharp corners eat into material a nested part would then be allowed to occupy),
        // so it now shrinks by that corner. That costs hole area and is the correct direction.
        private static void apply_ring_roles(IList<Tuple<int, Polyline, BoundingBox, Curve>> group, double mtol)
        {
            if (group == null) return;
            for (int j = 0; j < group.Count; j++)
            {
                var t = group[j];
                Polyline r = role_allowance(t.Item2, t.Item4, j == 0);
                if (ReferenceEquals(r, t.Item2)) continue;
                BoundingBox bb = r.BoundingBox;
                bb.Inflate(mtol * 2);
                group[j] = Tuple.Create<int, Polyline, BoundingBox, Curve>(t.Item1, r, bb, t.Item4);
            }
        }

        // Build the NESTING rings for ONE sheet from its boundary loops — the sheet counterpart of a part
        // group, and the only place a sheet's ring ROLES are known (nest_sheets is handed finished Polylines
        // and never sees the curve). Returned LARGEST FIRST, the order nest_sheets sorts into itself, so
        // index 0 is the ring nest_sheets and offset_sheet_boundary already treat as the sheet OUTER.
        //
        // A sheet is a CONTAINER, so its outer ring is the exact opposite of a part's: it must be INSCRIBED in
        // the real sheet or parts get placed off the material. Its voids are forbidden regions and must
        // CONTAIN the real void. Callers that just call curve_to_polyline per loop already get the safe side
        // for the outer (on-curve chords); this adds the last sagitta of guarantee, and fixes the voids.
        public List<Polyline> sheet_to_polylines(IEnumerable<Curve> loops, double segment_division_length = 0, bool keep_all = true)
        {
            var rings = new List<Polyline>();
            var sources = new List<Curve>();
            if (loops == null) return rings;
            foreach (Curve c in loops)
            {
                if (c == null) continue;
                Polyline pl = this.curve_to_polyline(c, segment_division_length, false, keep_all);
                if (pl == null || pl.Count < 4) continue;
                rings.Add(pl); sources.Add(c);
            }
            if (rings.Count == 0) return rings;

            double mtol = (RhinoDoc.ActiveDoc != null) ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
            if (mtol <= 0) mtol = 0.001;

            var keys = new double[rings.Count];
            var order = new int[rings.Count];
            for (int i = 0; i < rings.Count; i++) { keys[i] = -rings[i].BoundingBox.Diagonal.Length; order[i] = i; }
            Array.Sort(keys, order);

            var res = new List<Polyline>(order.Length);
            for (int i = 0; i < order.Length; i++)
                res.Add(role_allowance(rings[order[i]], sources[order[i]], i > 0 /*outer inscribes, voids grow*/));
            return res;
        }

        public Polyline curve_to_polyline_unfillet(Curve curve, double segment_division_length = 0, bool hull = false)
        {
            Point3d[] point3dArray = null;
            Arc arc = new Arc();
            double num = 0;
            double num1 = 0;
            Point3d[] point3dArray1 = null;
            Point3d[] point3dArray2 = null;
            Point3d[] point3dArray3 = null;
            double modelAbsoluteTolerance = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            double modelAngleToleranceRadians = RhinoDoc.ActiveDoc.ModelAngleToleranceRadians;
            Curve curve1 = curve.Simplify(CurveSimplifyOptions.All, modelAbsoluteTolerance * 10, modelAngleToleranceRadians * 2) ?? curve;
            Curve[] segments = curve_explode.GetSegments(curve1, true, 1);
            Polyline polyline = new Polyline((int)segments.Length + 1);

            if ((int)segments.Length >= 4)
            {
                bool flag = false;
                bool flag1 = false;
                for (int i = 0; i < (int)segments.Length; i++)
                {
                    int length = ((i - 1) % (int)segments.Length + (int)segments.Length) % (int)segments.Length;
                    int length1 = ((i + 1) % (int)segments.Length + (int)segments.Length) % (int)segments.Length;
                    if (!segments[i].TryGetArc(out arc, modelAbsoluteTolerance))
                    {
                        if (i == 0)
                        {
                            polyline.Add(segments[i].PointAtStart);
                        }
                        if (!segments[i].IsLinear(modelAbsoluteTolerance))
                        {
                            segments[i].DivideByCount(Math.Min(15, Math.Max(2, (int)(segments[i].GetLength() / Math.Abs(segment_division_length)))), false, out point3dArray1);
                            polyline.AddRange(point3dArray1);
                        }
                        polyline.Add(segments[i].PointAtEnd);
                    }
                    else if (arc.EndAngleDegrees <= 270)
                    {
                        Line line = new Line(segments[length].PointAtEnd, segments[length].PointAtStart);
                        Line line1 = new Line(segments[length1].PointAtStart, segments[length1].PointAtEnd);
                        if (arc.Length > Math.Abs(segment_division_length))
                        {
                            if (i == 0)
                                polyline.Add(segments[i].PointAtStart);

                            Curve curve2 = segments[i];
                            //Interval domain = segments[i].Domain;
                            //polyline.Add(curve2.PointAt(domain.Mid));

                            if (segment_division_length != 0)
                            {
                                int divisions = Math.Min(20, Math.Max(2, (int)Math.Floor(curve2.GetLength() / segment_division_length)));
                                curve2.DivideByCount(divisions, true, out Point3d[] division_points);
                                polyline.AddRange(division_points);
                            }



                            polyline.Add(segments[i].PointAtEnd);
                        }
                        else if ((!segments[length].IsLinear(modelAbsoluteTolerance) ? false : segments[length1].IsLinear(modelAbsoluteTolerance)))
                        {
                            if (i == 0)
                            {
                                flag = true;
                            }
                            if (i == (int)segments.Length - 1)
                            {
                                flag1 = true;
                            }
                            Intersection.LineLine(line, line1, out num, out num1, modelAbsoluteTolerance, false);
                            if (Math.Abs(3.14159265358979 - Vector3d.VectorAngle(line.Direction, line1.Direction, Plane.WorldXY)) < modelAngleToleranceRadians)
                            {
                                polyline.Add(segments[i].PointAtEnd);
                            }
                            else if (polyline.Count() <= 0)
                            {
                                polyline.Add(line.PointAt(num));
                            }
                            else
                            {
                                polyline[polyline.Count-1]=(line.PointAt(num));
                            }
                        }
                        else
                        {
                            polyline.Add(segments[i].PointAtEnd);
                        }
                    }
                    else
                    {
                        if (i == 0)
                        {
                            polyline.Add(segments[i].PointAtStart);
                        }
                        if (!segments[i].IsLinear(modelAbsoluteTolerance))
                        {
                            segments[i].DivideByCount(Math.Min(8, Math.Max(3, (int)(segments[i].GetLength() / Math.Abs(segment_division_length)))), false, out point3dArray2);
                            polyline.AddRange(point3dArray2);
                        }
                        polyline.Add(segments[i].PointAtEnd);
                    }
                }
                Line line2 = new Line(polyline[0], polyline[1]);
                Line line3 = new Line(polyline.Last(), polyline[polyline.Count() - 2]);
                if (flag1 & flag)
                {
                    polyline[polyline.Count-1]=(polyline[0]);
                }
                polyline.Add(polyline[0]);
            }
            else
            {
                curve.DivideByCount(Math.Min(30, Math.Max(3, (int)(curve.GetLength() / Math.Abs(segment_division_length)))), true, out point3dArray);
                polyline.AddRange(point3dArray);
                if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
                {
                    polyline.Add(polyline[0]);
                }
            }
            polyline.MergeColinearSegments(modelAngleToleranceRadians * 1.5, true);
            polyline.CollapseShortSegments(modelAbsoluteTolerance * 10);
            if ((!polyline.IsValid ? true : polyline.Count() == 2))
            {
                polyline.Clear();
                curve.DivideByCount(Math.Min(8, Math.Max(4, (int)(curve.GetLength() / Math.Abs(segment_division_length * 0.5)))), true, out point3dArray3);
                polyline.AddRange(point3dArray3);
                if (polyline.Last().DistanceToSquared(polyline[0]) > 0.0001)
                {
                    polyline.Add(polyline[0]);
                }
            }
            if (hull)
            {
                polyline = new Polyline(this.convex_hull(polyline.ToList<Point3d>()));
            }
            return polyline;
        }

        public nest_geo duplicate()
        {
            nest_geo nestGeo = new nest_geo()
            {
                indices = this.indices,
                copies = this.copies,
                rotations = this.rotations,
                attributes = this.attributes,
                geometry_sorted = this.geometry_sorted,
                boudary_indices_non_sorted = this.boudary_indices_non_sorted
            };
            nestGeo.attributes = new List<ObjectAttributes>();
            for (int i = 0; i < this.attributes.Count; i++)
            {
                nestGeo.attributes.Add(this.attributes[i].Duplicate());
            }
            nestGeo.geometry = new List<GeometryBase>();
            for (int j = 0; j < this.geometry.Count; j++)
            {
                nestGeo.geometry.Add(this.geometry[j].Duplicate());
            }

            nestGeo.geometry_attributes = new List<GeometryBase[]>();

            for (int j = 0; j < this.geometry_attributes.Count; j++)
            {
                var copy = new GeometryBase[this.geometry_attributes[j].Length];
                for (int k = 0; k < copy.Length; k++)
                {
                    copy[k] = this.geometry_attributes[j][k].Duplicate();
                }
                nestGeo.geometry_attributes.Add(copy);



            }

            // Carry the per-attribute source-port indices (and the port count) so {part; port} output survives a duplicate.
            nestGeo.geometry_attribute_ports = new List<int[]>();
            for (int j = 0; j < this.geometry_attribute_ports.Count; j++)
                nestGeo.geometry_attribute_ports.Add((int[])this.geometry_attribute_ports[j].Clone());
            nestGeo.attribute_port_count = this.attribute_port_count;


            nestGeo.boundary_curves_non_sorted = new List<Curve>();
            for (int k = 0; k < this.boundary_curves_non_sorted.Count; k++)
            {
                nestGeo.boundary_curves_non_sorted.Add(this.boundary_curves_non_sorted[k].DuplicateCurve());
            }
            nestGeo.boundary_sorted = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();
            for (int l = 0; l < this.boundary_sorted.Count; l++)
            {
                List<Tuple<int, Polyline, BoundingBox, Curve>> tuples = new List<Tuple<int, Polyline, BoundingBox, Curve>>();
                nestGeo.boundary_sorted.Add(tuples);
                for (int m = 0; m < this.boundary_sorted[l].Count; m++)
                {
                    nestGeo.boundary_sorted.Last<List<Tuple<int, Polyline, BoundingBox, Curve>>>().Add(Tuple.Create<int, Polyline, BoundingBox, Curve>(this.boundary_sorted[l][m].Item1, this.boundary_sorted[l][m].Item2.Duplicate(), this.boundary_sorted[l][m].Item3, this.boundary_sorted[l][m].Item4.DuplicateCurve()));
                }
            }
            nestGeo.disply_texts = new List<TextEntity>();
            for (int n = 0; n < this.disply_texts.Count; n++)
            {
                nestGeo.disply_texts.Add(this.disply_texts[n]);
            }
            return nestGeo;
        }

        public void extend_openlines(double distance)
        {
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<int> nums = new List<int>();
                foreach (int item in this.geometry_sorted[i])
                {
                    nums.Add(item);
                    if (this.geometry[item].ObjectType.ToString() == "Curve")
                    {
                        Curve curve = this.geometry[item] as Curve;
                        if (curve.PointAtStart.DistanceToSquared(curve.PointAtEnd) > RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * RhinoDoc.ActiveDoc.ModelAbsoluteTolerance)
                        {
                            Curve curve1 = distance < 0 ? curve.Trim(3, -distance) : curve.Extend(CurveEnd.Both, distance, CurveExtensionStyle.Smooth);

                            if (curve1 != null && curve1.IsValid)
                            {
                                this.geometry[item] = curve1;
                            }
                        }
                    }
                }
                this.geometry_sorted[i] = nums;
            }
        }

        public void hard_coded_input(List<int> ids, double segment_division_length = 0, bool hull = false)
        {

            var double_lengths = new double[ids.Count];
            var plines_simplified = new Tuple<int, Polyline, BoundingBox, Curve>[ids.Count];
            for (int i = 0; i < ids.Count; i++)
            {
                Polyline pline = segment_division_length == 0 ?
                    this.curve_to_polyline(boundary_curves_non_sorted[ids[i]], 0, hull, true) :   // 0 = keep all vertices
                    segment_division_length < 0 ?
                    this.curve_to_polyline(boundary_curves_non_sorted[ids[i]], segment_division_length, hull) :
                    this.curve_to_polyline_unfillet(boundary_curves_non_sorted[ids[i]], segment_division_length, hull);
                BoundingBox bbox = pline.Count == 2 ? new BoundingBox(pline[0], pline[1]) : pline.BoundingBox;
                bbox.Inflate(RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 2);
                // Use the GLOBAL index ids[i] (not the local loop index i) for the source index and the original
                // curve — matching the curve read above (boundary_curves_non_sorted[ids[i]]). With local i, every
                // group's rings took the first global curves' index/curve, corrupting per-part source/Item4.
                plines_simplified[i] = Tuple.Create(boudary_indices_non_sorted[ids[i]], pline, bbox, boundary_curves_non_sorted[ids[i]]);
                double_lengths[i] = (bbox.Diagonal.Length);


            }

            Array.Sort(double_lengths, plines_simplified);
            Array.Reverse(double_lengths);
            Array.Reverse(plines_simplified);

            // The sort is what makes the ROLES knowable: [0] is now the outer boundary and the rest are holes.
            // Applied to EVERY branch, curve_to_polyline_unfillet included — see apply_ring_roles.
            var group = plines_simplified.ToList();
            {
                double mtol_roles = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
                apply_ring_roles(group, mtol_roles <= 0 ? 0.001 : mtol_roles);
            }

            this.geometry_sorted.Add(ids);
            this.boundary_sorted.Add(group);


     

        }

        public void identify_groups(double segment_division_length = 0, bool hull = false)
        {
            ////////////////////////////////////////////////////////////////////////////////////////
            ///Identify groups in boundaries
            ////////////////////////////////////////////////////////////////////////////////////////
            int n = boundary_curves_non_sorted.Count;
            double mtol_roles = Rhino.RhinoDoc.ActiveDoc != null ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 0.001;
            if (mtol_roles <= 0) mtol_roles = 0.001;

            var groups = new List<List<Tuple<int, Polyline, BoundingBox, Curve>>>();

            //Sort by bbox diagonal, from largest to smallest
            var double_lengths = new double[n];
            var plines_simplified = new Tuple<int, Polyline, BoundingBox, Curve>[n];

            for (int i = 0; i < n; i++)
            {
                Polyline pline = segment_division_length == 0 ?
                    curve_to_polyline(boundary_curves_non_sorted[i], 0, hull, true) :   // 0 = keep all vertices
                    segment_division_length < 0 ?
                    curve_to_polyline(boundary_curves_non_sorted[i], segment_division_length, hull) :
                    curve_to_polyline_unfillet(boundary_curves_non_sorted[i], segment_division_length, hull);
                BoundingBox bbox = pline.Count == 2 ? new BoundingBox(pline[0], pline[1]) : pline.BoundingBox;
                bbox.Inflate(Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance * 2);
                plines_simplified[i] = (Tuple.Create(boudary_indices_non_sorted[i], pline, bbox, boundary_curves_non_sorted[i]));
                double_lengths[i] = (bbox.Diagonal.Length);
            }


            Array.Sort(double_lengths, plines_simplified);
            Array.Reverse(double_lengths);
            Array.Reverse(plines_simplified);
       

            //RTree
            RTree tree = new RTree();

            for (int i = 0; i < n; i++)
                tree.Insert(plines_simplified[i].Item3, i);

            for (int i = 0; i < n; i++)
            {
                //skip if visited
                if (!visited.Add(i)) continue;

                // Geometry may sit on a plane offset from WorldXY (e.g. imported at z != 0). Use an XY plane
                // AT THIS CURVE'S ELEVATION for the winding + containment tests so outer+hole pairing still
                // works off the world origin (Curve.Contains against WorldXY can miss curves far from z=0).
                Plane testPlane = plines_simplified[i].Item2.Count > 0
                    ? new Plane(new Point3d(0, 0, plines_simplified[i].Item2[0].Z), Vector3d.ZAxis)
                    : Plane.WorldXY;

                //check winding
                if (plines_simplified[i].Item4.ClosedCurveOrientation(testPlane) == CurveOrientation.Clockwise)
                {
                    Polyline polyline_temp = new Polyline(plines_simplified[i].Item2);
                    polyline_temp.Reverse();
                    Curve curve_temp = plines_simplified[i].Item4.DuplicateCurve();
                    curve_temp.Reverse();
                    plines_simplified[i] = (Tuple.Create(plines_simplified[i].Item1, polyline_temp, plines_simplified[i].Item3, curve_temp));
                }

                //create local group
                var group_local = new List<Tuple<int, Polyline, BoundingBox, Curve>>() { plines_simplified[i] };

                //overwrite last search
                ids.Clear();
                current_id = i;

                //convert to curve for point inclusion test
                Curve temp_curve = plines_simplified[i].Item2.ToNurbsCurve();

                //search
                tree.Search(plines_simplified[i].Item3, BoundingBoxCallback);

                foreach (var id in ids)
                {
                    //iterate found polylines points while checking point inclusion in XY plane
                    for (int j = 0; j < plines_simplified[i].Item2.Count; j++)
                    {
                        if (temp_curve.Contains(plines_simplified[id].Item2[j], testPlane, 0.01) == PointContainment.Inside)
                        {
                            visited.Add(id);

                            if (plines_simplified[id].Item4.ClosedCurveOrientation(testPlane) == CurveOrientation.CounterClockwise)
                            {
                                Polyline polyline_temp = new Polyline(plines_simplified[id].Item2);
                                polyline_temp.Reverse();
                                Curve curve_temp = plines_simplified[id].Item4.DuplicateCurve();
                                curve_temp.Reverse();
                                plines_simplified[id] = (Tuple.Create(plines_simplified[id].Item1, polyline_temp, plines_simplified[id].Item3, curve_temp));
                            }
                            group_local.Add(plines_simplified[id]);
                            break;
                        }

                        //check just two points
                        if (j == 2)
                            break;
                    }//iterate collision points
                }//collision

                // The group is complete and ordered outer-first, so the ROLES are finally knowable: grow [0]
                // out to contain its part, shrink every hole in to stay inside the real hole. Applied to EVERY
                // branch, curve_to_polyline_unfillet included — see apply_ring_roles.
                apply_ring_roles(group_local, mtol_roles);

                this.boundary_sorted.Add(group_local);

                //add text to display
                var text = new TextEntity();
                text.PlainText = copies[group_local[0].Item1].ToString();
                text.TextHeight = 100;
                text.Plane = new Plane(group_local[0].Item2.CenterPoint(), Vector3d.ZAxis);

                this.disply_texts.Add(text);
            }

            ///////////////////////////////////////////////////////////////////////////////////////
            //Identify groups in all geometries
            ///////////////////////////////////////////////////////////////////////////////////////
            current_id = -1;
            RTree tree_all_geo = new RTree();

            for (int i = 0; i < geometry.Count; i++)
                tree_all_geo.Insert(geometry[i].GetBoundingBox(false), i);

            for (int i = 0; i < boundary_sorted.Count; i++)
            {
                //overwrite last search
                ids.Clear();

                //search by check the boundary groups
                tree_all_geo.Search(boundary_sorted[i][0].Item3, BoundingBoxCallback);

                List<int> group = new List<int>(boundary_sorted.Count)
                {
                    boundary_sorted[i][0].Item1
                };

                foreach (int id in ids)
                {
                    string object_type = geometry[id].ObjectType.ToString();

                    Point3d check_point = Point3d.Unset;

                    switch (object_type)
                    {
                        case ("Annotation"):
                            TextEntity textObj = geometry[id] as TextEntity;
                            check_point = textObj.Plane.Origin;
                            break;

                        case ("TextDot"):
                            TextDot textDotObj = geometry[id] as TextDot;
                            check_point = textDotObj.Point;
                            break;

                        case ("Curve"):
                            Curve curve = geometry[id] as Curve;
                            check_point = curve.PointAt(curve.Domain.Mid);
                            break;

                        case ("Point"):
                            Point p = geometry[id] as Point;
                            check_point = new Point3d(p.Location);
                            break;

                        case ("Mesh"):
                            Mesh m = geometry[id] as Mesh;
                            check_point = m.Vertices[i];
                            break;

                        case ("Brep"):
                            Brep b = geometry[id] as Brep;
                            check_point = b.Vertices[0].Location;
                            break;

                        case ("Extrusion"):
                            Extrusion e = geometry[id] as Extrusion;
                            check_point = e.PointAt(0, 0);
                            break;

                        default:
                            RhinoApp.WriteLine("Unknown type: " + object_type);
                            break;
                    }

                    if (check_point == Point3d.Unset)
                        continue;

                    if (boundary_sorted[i][0].Item4.Contains(check_point, Plane.WorldXY, 0.01) == PointContainment.Inside)
                        group.Add(indices[id]);
                }

                this.geometry_sorted.Add(group);
            }

        }

        // Offset ONLY the NESTING polyline (boundary_sorted Item2, what the solver collides on): each OUTER loop
        // OUTWARD, each HOLE INWARD, so placed parts keep `distance` of clearance — while Item4 and geometry[]
        // stay the ORIGINAL curve, so the OUTPUT is the original geometry (the offset is nesting-only). Direction
        // is chosen by AREA (orientation-agnostic): an outer keeps the offset that GREW, a hole the one that
        // SHRANK. Collinear-merged so no points are added. On any failure the original polyline is kept (a part
        // is never dropped).
        // Rings the LAST offset_nesting_boundary call could NOT offset (left at their original size, so those
        // parts contribute no clearance at all) and the number it attempted. The offset is the ONLY source of
        // spacing in OpenNest1 (it forces the native solver's spacing to 0), so a failure here is a silent
        // zero-gap — exactly the McNeel 221208 "spacing wont transform" symptom. Callers read these to warn.
        public int last_offset_failures;
        public int last_offset_rings;

        public void offset_nesting_boundary(double distance)
        {
            this.last_offset_failures = 0;
            this.last_offset_rings = 0;
            if (System.Math.Abs(distance) < 1e-9) return;
            double tol = RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            for (int i = 0; i < this.boundary_sorted.Count; i++)
                for (int j = 0; j < this.boundary_sorted[i].Count; j++)
                {
                    var tup = this.boundary_sorted[i][j];
                    this.last_offset_rings++;
                    if (tup.Item2 == null || tup.Item2.Count < 4) { this.last_offset_failures++; continue; }
                    Polyline off = offset_closed_polyline(tup.Item2, distance, j > 0 /*isHole*/, tol);
                    if (off == null || off.Count < 4) { this.last_offset_failures++; continue; }   // keep original on failure
                    this.boundary_sorted[i][j] = Tuple.Create<int, Polyline, BoundingBox, Curve>(
                        tup.Item1, off, off.BoundingBox, tup.Item4);
                }
        }

        // Offset a closed polyline by `distance`: outer => grow (keep the larger-area result), hole => shrink
        // (smaller). Tries both signs (orientation-agnostic), keeps closed results, merges collinear (no points
        // added). Returns null if no usable offset in the right direction was produced.
        //
        // Curve.Offset is tried FIRST (it preserves the vertex structure best) but it fails silently in three
        // known ways: an inward offset that consumes the ring returns nothing closed, a concave outline whose
        // Sharp offset self-intersects returns nothing closed, and it needs a plane the ring actually lies in.
        // The plane is now the ring's OWN elevation (mirroring the testPlane in identify_groups, which already
        // compensates for geometry imported at z != 0) instead of a hardcoded Plane.WorldXY, and the two
        // topological failures fall through to a Clipper polygon offset, which has neither failure mode.
        internal static Polyline offset_closed_polyline(Polyline src, double distance, bool isHole, double tol)
        {
            if (src == null || src.Count < 4) return null;
            double srcA = poly_area(src);
            Curve crv = src.ToPolylineCurve();
            // Offset in an XY plane AT THIS RING'S ELEVATION — Curve.Offset against Plane.WorldXY is unreliable
            // for curves far from z = 0, and identify_groups (nest_geo.cs, testPlane) already works this way.
            Plane plane = new Plane(new Point3d(0, 0, src[0].Z), Vector3d.ZAxis);
            Polyline pick = null; double pickA = isHole ? double.MaxValue : -1.0;
            foreach (double d in new double[] { distance, -distance })
            {
                Curve[] offs = null;
                try { offs = crv.Offset(plane, d, tol, CurveOffsetCornerStyle.Sharp); } catch { }
                if (offs == null) continue;
                foreach (Curve oc in offs)
                {
                    Polyline pl;
                    if (oc == null || !oc.IsClosed || !oc.TryGetPolyline(out pl)) continue;
                    double a = poly_area(pl);
                    if (isHole) { if (a > tol && a < pickA) { pickA = a; pick = pl; } }
                    else        { if (a > pickA)            { pickA = a; pick = pl; } }
                }
            }
            if (pick == null || (isHole ? (pickA >= srcA) : (pickA <= srcA)))
                return clipper_offset_closed_polyline(src, distance, isHole);   // Curve.Offset gave nothing usable

            pick.MergeColinearSegments(RhinoMath.ToRadians(1.0), true);
            pick.CollapseShortSegments(tol);
            return pick;
        }

        // Polyline adapter over polygon_offset (the pure, unit-tested Clipper offset). Used ONLY once
        // Curve.Offset has already produced nothing usable: Clipper resolves the self-intersections a concave
        // sharp offset creates, and comes back empty only when the ring genuinely collapses. Before 25ccd5b
        // the gap came from the Clipper-based solver, which has neither failure mode, so this is a return to a
        // known-good path rather than a new one. Z is carried over from the source ring.
        private static Polyline clipper_offset_closed_polyline(Polyline src, double distance, bool isHole)
        {
            if (src == null) return null;
            int n = src.Count;
            if (n > 1 && src[0].DistanceTo(src[n - 1]) < 1e-9) n--;   // drop the closing duplicate
            if (n < 3) return null;

            double z = src[0].Z;
            double[] x = new double[n], y = new double[n];
            for (int i = 0; i < n; i++) { x[i] = src[i].X; y[i] = src[i].Y; }

            // isHole means "this ring has to SHRINK" (a part's hole, or — from nest_sheets — a sheet's outer).
            double[][] off = polygon_offset.offset_ring(x, y, n, distance, isHole);
            if (off == null) return null;

            var res = new Polyline(off[0].Length + 1);
            for (int i = 0; i < off[0].Length; i++) res.Add(new Point3d(off[0][i], off[1][i], z));
            res.Add(res[0]);
            return res;
        }

        private static double poly_area(Polyline p)
        {
            int n = p.Count; if (n < 3) return 0.0;
            int m = (n > 1 && p[0].DistanceTo(p[n - 1]) < 1e-9) ? n - 1 : n;   // drop closing duplicate
            double a = 0.0;
            for (int k = 0; k < m; k++) { Point3d c = p[k], d = p[(k + 1) % m]; a += c.X * d.Y - d.X * c.Y; }
            return System.Math.Abs(a) * 0.5;
        }

        public void sort_groups(bool split_into_open_and_closed = true)
        {
            for (int i = 0; i < this.geometry_sorted.Count; i++)
            {
                List<Curve> curves = new List<Curve>(this.geometry_sorted.Count);
                List<int> nums = new List<int>(this.geometry_sorted.Count);
                List<int> nums1 = new List<int>(this.geometry_sorted.Count);
                for (int j = 0; j < this.geometry_sorted[i].Count; j++)
                {
                    if (this.geometry[this.geometry_sorted[i][j]].ObjectType.ToString() != "Curve")
                    {
                        nums1.Add(this.geometry_sorted[i][j]);
                    }
                    else
                    {
                        Curve item = this.geometry[this.geometry_sorted[i][j]] as Curve;
                        curves.Add(item);
                        nums.Add(this.geometry_sorted[i][j]);
                    }
                }
                List<int> curvesInCurvesFlatList = (new sort_by_closed_curves(curves, split_into_open_and_closed)).curves_in_curves_flat_list;
                this.geometry_sorted[i].Clear();
                Dictionary<int, int> nums2 = new Dictionary<int, int>(this.geometry_sorted[i].Count);
                for (int k = 0; k < curvesInCurvesFlatList.Count; k++)
                {
                    int num = curvesInCurvesFlatList[k];
                    this.geometry_sorted[i].Add(nums[num]);
                    nums2.Add(nums[k], nums[num]);
                    this.geometry[nums[num]] = curves[num];
                }
                this.geometry_sorted[i].AddRange(nums1);
                for (int l = 0; l < this.boundary_sorted[i].Count; l++)
                {
                    this.boundary_sorted[i][l] = Tuple.Create<int, Polyline, BoundingBox, Curve>(nums2[this.boundary_sorted[i][l].Item1], this.boundary_sorted[i][l].Item2, this.boundary_sorted[i][l].Item3, this.boundary_sorted[i][l].Item4);
                }
            }
        }

        /// <summary>
        /// Merges multiple nest_geo instances into a single combined nest_geo instance
        /// </summary>
        /// <param name="nestGeos">List of nest_geo instances to merge</param>
        /// <returns>A new nest_geo instance that contains all elements from the input instances</returns>
        public static nest_geo Merge(List<nest_geo> nestGeos)
        {
            if (nestGeos == null || nestGeos.Count == 0)
                return new nest_geo();
                
            if (nestGeos.Count == 1)
                return nestGeos[0];
                
            // Create a new combined nest_geo
            nest_geo combined = new nest_geo();
            
            // Track the offset needed for indices when merging
            int geometryIndexOffset = 0;
            
            // Process each nest_geo instance
            foreach (nest_geo source in nestGeos)
            {
                if (source == null) 
                    continue;
                
                // Add basic elements that can be directly merged
                combined.geometry.AddRange(source.geometry);
                combined.geometry_attributes.AddRange(source.geometry_attributes);
                // Keep the parallel attribute-port indices aligned (default port 0 for a source that predates them).
                if (source.geometry_attribute_ports != null && source.geometry_attribute_ports.Count == source.geometry_attributes.Count)
                    combined.geometry_attribute_ports.AddRange(source.geometry_attribute_ports);
                else
                    foreach (var ga in source.geometry_attributes) combined.geometry_attribute_ports.Add(new int[ga != null ? ga.Length : 0]);
                combined.attribute_port_count = System.Math.Max(combined.attribute_port_count, source.attribute_port_count < 1 ? 1 : source.attribute_port_count);
                combined.attributes.AddRange(source.attributes);
                combined.disply_texts.AddRange(source.disply_texts);
                combined.boundary_curves_non_sorted.AddRange(source.boundary_curves_non_sorted);
                combined.copies.AddRange(source.copies);
                // rotations is index-aligned with copies; pad with 0 (inherit) if a source predates it
                if (source.rotations != null && source.rotations.Count == source.copies.Count)
                    combined.rotations.AddRange(source.rotations);
                else
                    for (int ri = 0; ri < source.copies.Count; ri++) combined.rotations.Add(0);
                
                // Handle indices with offset
                foreach (int index in source.indices)
                {
                    combined.indices.Add(index + geometryIndexOffset);
                }
                
                // Handle boundary indices with offset
                foreach (int index in source.boudary_indices_non_sorted)
                {
                    combined.boudary_indices_non_sorted.Add(index + geometryIndexOffset);
                }
                
                // Handle geometry_sorted with offset
                foreach (List<int> indexGroup in source.geometry_sorted)
                {
                    List<int> adjustedGroup = new List<int>();
                    foreach (int index in indexGroup)
                    {
                        adjustedGroup.Add(index + geometryIndexOffset);
                    }
                    combined.geometry_sorted.Add(adjustedGroup);
                }
                
                // Handle boundary_sorted with offset
                foreach (var boundaryGroup in source.boundary_sorted)
                {
                    var adjustedBoundaryGroup = new List<Tuple<int, Polyline, BoundingBox, Curve>>();
                    foreach (var tuple in boundaryGroup)
                    {
                        adjustedBoundaryGroup.Add(new Tuple<int, Polyline, BoundingBox, Curve>(
                            tuple.Item1 + geometryIndexOffset, 
                            tuple.Item2, 
                            tuple.Item3, 
                            tuple.Item4));
                    }
                    combined.boundary_sorted.Add(adjustedBoundaryGroup);
                }
                
                // Handle transforms
                if (source.xforms != null)
                {
                    combined.xforms.AddRange(source.xforms);
                }
                
                // Update offset for the next nest_geo
                geometryIndexOffset += source.geometry.Count;
            }
            
            return combined;
        }
    }
}