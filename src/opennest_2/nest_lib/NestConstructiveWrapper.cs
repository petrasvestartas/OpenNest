using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace NestConstructive
{
    // Independent versioned C API; never forwards to another nesting engine.
    public static class Native
    {
        const string Dll = "nest_constructive";
        static readonly object Gate = new object();
        [StructLayout(LayoutKind.Sequential)]
        public struct Parameters
        {
            public int struct_size, max_sheets, beam_width, branch_parts, position_limit;
            public int restarts, workers, seed, rotations, reserved;
            public double time_limit, scan_step, scale;
            public static Parameters Default => new Parameters {
                struct_size = 64, max_sheets = 5, beam_width = 8, branch_parts = 4,
                position_limit = 32, restarts = 4, workers = 1, seed = 100, rotations = 8,
                time_limit = 60, scan_step = 100, scale = 100000 };
        }
        [StructLayout(LayoutKind.Sequential)]
        public struct Placement { public int part, copy, sheet, mirrored; public double tx, ty, angle; }
        [StructLayout(LayoutKind.Sequential)]
        public struct Stats {
            public int struct_size, placed, requested, sheets, iterations, cancelled;
            public long nfp_hits, nfp_misses;
            public double elapsed;
        }
        public sealed class Input {
            public double[] XY, Angles;
            public int[] RingStarts, RingSizes, RingParts, RingHoles, Quantities, MirrorFlags, AngleStarts, AngleCounts;
            public double Width, Height;
        }
        public sealed class Result { public int Code; public Placement[] Placements; public Stats Statistics; }
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr nc_last_error();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern void nc_cancel();
        [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] static extern int nc_solve(
            double[] xy, int point_count, int[] ring_starts, int[] ring_sizes, int[] ring_parts,
            int[] ring_holes, int ring_count, int[] quantities, int[] mirrors, int part_count,
            double[] angles, int[] angle_starts, int[] angle_counts, int angle_count,
            double width, double height, ref Parameters parameters,
            [Out] Placement[] output, int capacity, ref Stats stats);

        public static Result Solve(Input input, Parameters parameters, CancellationToken cancellation)
        {
            if (Marshal.SizeOf(typeof(Parameters)) != 64 || Marshal.SizeOf(typeof(Placement)) != 40 ||
                Marshal.SizeOf(typeof(Stats)) != 48) throw new InvalidOperationException("Constructive ABI layout mismatch.");
            int count = 0;
            foreach (int q in input.Quantities) count = checked(count + q);
            var result = new Result { Placements = new Placement[count], Statistics = new Stats { struct_size = 48 } };
            lock (Gate)
            {
                cancellation.ThrowIfCancellationRequested();
                // Registration exists only while this instance owns the native solve.
                using (cancellation.Register(() => nc_cancel()))
                {
                    parameters.struct_size = 64;
                    result.Code = nc_solve(input.XY, input.XY.Length / 2, input.RingStarts, input.RingSizes,
                        input.RingParts, input.RingHoles, input.RingSizes.Length, input.Quantities,
                        input.MirrorFlags, input.Quantities.Length, input.Angles, input.AngleStarts,
                        input.AngleCounts, input.Angles.Length, input.Width, input.Height, ref parameters,
                        result.Placements, count, ref result.Statistics);
                }
                if (result.Code < 0)
                    throw new InvalidOperationException("Constructive solver: " + Marshal.PtrToStringAnsi(nc_last_error()));
            }
            return result;
        }
    }
}
