using System;
using System.Collections.Generic;

namespace OpenNestConsole
{
    // Rhino-free analogue of NpRun.Flatten (opennest_2/grasshopper/component_nest.cs:578): turns a list
    // of plain polygon rings into the flat int[]/double[] arrays both C ABIs expect. v1 = outer rings only
    // (no part/sheet holes); each part has quantity 1. The array layout matches the wrappers exactly.
    internal static class NestFlatten
    {
        // Concatenate a list of rings (each = interleaved [x0,y0,...]) into per-ring vertex counts + flat XY.
        public static void Concat(IReadOnlyList<double[]> rings, out int[] vertexCounts, out double[] xy)
        {
            vertexCounts = new int[rings.Count];
            int total = 0;
            for (int i = 0; i < rings.Count; i++)
            {
                vertexCounts[i] = rings[i].Length / 2;
                total += rings[i].Length;
            }
            xy = new double[total];
            int cur = 0;
            foreach (var r in rings)
            {
                Array.Copy(r, 0, xy, cur, r.Length);
                cur += r.Length;
            }
        }

        // A placement returned by either engine, already resolved to world XY of the original ring.
        public sealed class Placement
        {
            public int PartIndex;   // index into the original parts list
            public int SheetId;     // engine sheet/bin id, -1 = unplaced
            public double[] Xy;      // placed, closed ring (interleaved)
        }

        // Apply the shared transform contract to the original ring:
        //   final = Rotate(point, angle, about (0,0)) + (tx, ty)   [+ sheet world origin, added by caller]
        // np angle is RADIANS, nfp angle is DEGREES (handled by the caller before calling this).
        public static double[] PlaceRing(double[] ring, double angleRad, double tx, double ty)
        {
            double c = Math.Cos(angleRad), s = Math.Sin(angleRad);
            var outXy = new double[ring.Length];
            for (int k = 0; k < ring.Length / 2; k++)
            {
                double x = ring[2 * k], y = ring[2 * k + 1];
                outXy[2 * k] = x * c - y * s + tx;
                outXy[2 * k + 1] = x * s + y * c + ty;
            }
            return outXy;
        }
    }
}
