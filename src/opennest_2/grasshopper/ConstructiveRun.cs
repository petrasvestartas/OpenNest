using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using nest_rhino_lib;
using NestConstructive;

namespace opennest_2
{
    // Uses already prepared Geometry/Sheets outlines; offsets are never applied twice.
    public sealed class ConstructiveRun
    {
        public Native.Input Input;
        public Native.Parameters Parameters;
        public nest_geo Geometry;
        public nest_sheets Sheets;
        public Transform[] ToLocal;
        public Point3d[] SheetOrigins;

        public static ConstructiveRun Prepare(nest_sheets sheets, nest_geo geometry,
            double seconds, int maxSheets, IList<double> anglesDegrees, bool mirror)
        {
            if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds) || maxSheets < 1)
                throw new ArgumentException("Positive finite seconds and sheet limit required.");
            var run = new ConstructiveRun { Geometry = geometry.duplicate(), Sheets = sheets.duplicate() };
            if (geometry.last_offset_failures != 0 || sheets.last_offset_failures != 0)
                throw new ArgumentException("An upstream spacing offset failed. Fix Geometry/Sheets before nesting.");
            int available = 0;
            while (available < run.Sheets.sheets.Length && run.Sheets.sheets[available] != null && run.Sheets.sheets[available].Length > 0)
                available++;
            if (available == 0 || geometry.boundary_sorted.Count == 0) throw new ArgumentException("No parts or sheets.");
            maxSheets = Math.Min(maxSheets, available);
            var xy = new List<double>(); var starts = new List<int>(); var sizes = new List<int>();
            var owners = new List<int>(); var holes = new List<int>();
            Action<Polyline, Transform, int, bool> addRing = (source, xf, owner, hole) => {
                if (source == null || !source.IsClosed || source.Count < 4) throw new ArgumentException("Closed polygon rings required.");
                var poly = new Polyline(source); poly.Transform(xf);
                int n = poly.Count - 1;
                starts.Add(xy.Count / 2); sizes.Add(n); owners.Add(owner); holes.Add(hole ? 1 : 0);
                for (int k = 0; k < n; k++) {
                    var p = poly[k];
                    if (!p.IsValid || Math.Abs(p.Z) > 1e-6) throw new ArgumentException("Prepared rings must be parallel to WorldXY.");
                    xy.Add(p.X); xy.Add(p.Y);
                }
            };
            var reference = run.Sheets.sheets[0];
            var box = reference[0].BoundingBox;
            double width = box.Max.X - box.Min.X, height = box.Max.Y - box.Min.Y;
            if (width <= 0 || height <= 0) throw new ArgumentException("Positive rectangular stock required.");
            run.SheetOrigins = new Point3d[maxSheets];
            for (int s = 0; s < maxSheets; s++) {
                var rings = run.Sheets.sheets[s]; var bounds = rings[0].BoundingBox;
                run.SheetOrigins[s] = bounds.Min;
                if (Math.Abs(bounds.Max.X - bounds.Min.X - width) > 1e-6 ||
                    Math.Abs(bounds.Max.Y - bounds.Min.Y - height) > 1e-6 || rings.Length != reference.Length)
                    throw new ArgumentException("Constructive requires identical rectangular sheets and keep-outs.");
                // Every edge must follow a stock side; reject nonrectangular concave outlines.
                for (int k = 1; k < rings[0].Count; k++) {
                    Point3d a = rings[0][k-1] - (Vector3d)bounds.Min, b = rings[0][k] - (Vector3d)bounds.Min;
                    bool vertical = Math.Abs(a.X-b.X) < 1e-6 && (Math.Abs(a.X) < 1e-6 || Math.Abs(a.X-width) < 1e-6);
                    bool horizontal = Math.Abs(a.Y-b.Y) < 1e-6 && (Math.Abs(a.Y) < 1e-6 || Math.Abs(a.Y-height) < 1e-6);
                    if (!(vertical || horizontal) || Math.Abs(a.Z) > 1e-6 || Math.Abs(b.Z) > 1e-6)
                        throw new ArgumentException("Only axis-aligned rectangular stock is supported.");
                }
                for (int h = 1; h < rings.Length; h++) {
                    if (rings[h].Count != reference[h].Count) throw new ArgumentException("Stock keep-outs must match.");
                    for (int k = 0; k < rings[h].Count; k++)
                        if (((rings[h][k] - bounds.Min) - (reference[h][k] - box.Min)).Length > 1e-6)
                            throw new ArgumentException("Stock keep-outs must match in local coordinates.");
                }
            }
            for (int h = 1; h < reference.Length; h++)
                addRing(reference[h], Transform.Translation(Point3d.Origin - box.Min), -1, false);
            int groups = run.Geometry.boundary_sorted.Count;
            var quantities = new int[groups]; var mirrors = new int[groups];
            var angleStarts = new int[groups]; var angleCounts = new int[groups]; var angles = new List<double>();
            run.ToLocal = new Transform[groups];
            var requested = anglesDegrees.Count == 0 ? new List<double> { 0, 45, 90, 135, 180, 225, 270, 315 } : anglesDegrees;
            if (requested.Any(a => double.IsNaN(a) || double.IsInfinity(a))) throw new ArgumentException("Angles must be finite degrees.");
            for (int g = 0; g < groups; g++) {
                var group = run.Geometry.boundary_sorted[g];
                if (group.Count == 0 || run.Geometry.geometry_sorted[g].Count == 0) throw new ArgumentException("Empty geometry group.");
                run.ToLocal[g] = Transform.Translation(Point3d.Origin - group[0].Item2[0]);
                for (int h = 0; h < group.Count; h++) addRing(group[h].Item2, run.ToLocal[g], g, h > 0);
                int gi = run.Geometry.geometry_sorted[g][0];
                quantities[g] = gi < run.Geometry.copies.Count ? Math.Max(1, run.Geometry.copies[gi]) : 1;
                mirrors[g] = mirror ? 1 : 0;
                int restriction = run.Geometry.rotations != null && gi < run.Geometry.rotations.Count ? run.Geometry.rotations[gi] : 0;
                angleStarts[g] = angles.Count;
                foreach (double degrees in requested) {
                    double a = degrees * Math.PI / 180;
                    // Intersect explicit orientations with the upstream per-part rotation restriction.
                    if (restriction > 0 && Math.Abs(a * restriction / (2*Math.PI) - Math.Round(a * restriction / (2*Math.PI))) > 1e-7) continue;
                    angles.Add(a);
                }
                angleCounts[g] = angles.Count - angleStarts[g];
                if (angleCounts[g] == 0) throw new ArgumentException("Allowed angles conflict with a part's rotation restriction.");
            }
            run.Input = new Native.Input { XY = xy.ToArray(), RingStarts = starts.ToArray(), RingSizes = sizes.ToArray(),
                RingParts = owners.ToArray(), RingHoles = holes.ToArray(), Quantities = quantities, MirrorFlags = mirrors,
                Angles = angles.ToArray(), AngleStarts = angleStarts, AngleCounts = angleCounts, Width = width, Height = height };
            run.Parameters = Native.Parameters.Default;
            run.Parameters.max_sheets = maxSheets;
            run.Parameters.time_limit = seconds;
            run.Parameters.scan_step = Math.Max(width, height) / 30;
            return run;
        }

        public Transform WorldTransform(Native.Placement p)
        {
            Transform reflection = p.mirrored == 0 ? Transform.Identity : Transform.Scale(Plane.WorldXY, -1, 1, 1);
            return Transform.Translation((Vector3d)SheetOrigins[p.sheet]) * Transform.Translation(p.tx, p.ty, 0) *
                Transform.Rotation(p.angle, Vector3d.ZAxis, Point3d.Origin) * reflection * ToLocal[p.part];
        }
    }
}
