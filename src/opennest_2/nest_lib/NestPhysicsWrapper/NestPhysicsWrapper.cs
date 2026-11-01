using System.Runtime.InteropServices;

namespace NestPhysics
{
    // Mirrors `struct NpParams` in nest_physics_capi.h. Sequential layout + matching field types so
    // the blittable struct crosses the boundary verbatim. NOTE: C# `long` is 64-bit, matching the C
    // `long long` (the C `long` would be 32-bit on MinGW/Windows, which is why the C side uses long long).
    [StructLayout(LayoutKind.Sequential)]
    public struct NpParams
    {
        public int num_rotations;
        public double spacing;
        public double simplify_tolerance;
        public int seed;
        public double time_budget_secs;
        public long iter_budget;
        public int iter_mode;
        public int max_sheets;
        public int n_starts;
        public int part_holes_mode;   // 0 = keep-empty (default); 1 = fill-holes (nest parts into other parts' holes)
        public int pole_max;          // surrogate poles per part (0 = default 48; ~16 is ~2.7x faster, tighter at low iters)
        public int final_compact;     // 1 = post-relaxation left/down compaction slide (reclaims slack -> fewer spills)
        public int fit_mode;          // 0 = all parts on fewest sheets (default); 1 = ONE sheet, max fill (overflow reported unplaced -> host lays it outside); 2 = all parts, GLOBAL bin pack (sheets optimised jointly; uniform sheet sizes, else falls back to 0); 3 = all parts, GROUP & FILL (plate groups per sheet + joint fill; mixed sizes / sheet holes ok)
        public int n_threads;         // fit_mode 3 only: solver threads of the portfolio (0 = engine default 4). Appended field; it occupies the old tail padding, so Marshal.SizeOf stays 80
    }

    // P/Invoke into nest_physics.dll (the C++ sparrow port + penetration-depth overlap proxy).
    // The DLL is copied next to the .gha by the project's post-build step (same mechanism as
    // minkowski.dll), so it is found in the plugin directory at load time.
    public static class NestPhysicsWrapper
    {
        // Bare name -> resolves to nest_physics.dll on Windows and nest_physics.dylib on macOS.
        private const string LIBRARY_NAME = "nest_physics";

        // Returns 0 on success. Output buffers are allocated by the caller (length = part_count, except
        // out_n_sheets which is length 1 / a single int).
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int np_nest(
            int part_count,
            int[] part_vertex_counts,
            double[] part_xy,
            int[] part_rotations,   // [part_count] per-part rotation override (0 = free continuous; N>0 = N discrete; 1 = fixed)
            int sheet_count,
            int[] sheet_outer_vertex_counts,
            double[] sheet_outer_xy,
            int[] sheet_hole_counts,
            int[] hole_vertex_counts,
            double[] hole_xy,
            int[] part_hole_counts,
            int[] part_hole_vertex_counts,
            double[] part_hole_xy,
            ref NpParams parameters,
            double[] out_tx,
            double[] out_ty,
            double[] out_angle,
            int[] out_sheet_id,
            out int out_n_sheets);

        // E1: np_nest + a per-part priority array right after part_rotations ([part_count] 0 = normal,
        // >0 = MANDATORY; null = exactly np_nest). Only fit_mode 1 (ONE sheet, max fill) reads it: every
        // mandatory part on the sheet first, then the most placed area. See nest_physics_capi.h.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int np_nest_prio(
            int part_count,
            int[] part_vertex_counts,
            double[] part_xy,
            int[] part_rotations,
            int[] part_priority,
            int sheet_count,
            int[] sheet_outer_vertex_counts,
            double[] sheet_outer_xy,
            int[] sheet_hole_counts,
            int[] hole_vertex_counts,
            double[] hole_xy,
            int[] part_hole_counts,
            int[] part_hole_vertex_counts,
            double[] part_hole_xy,
            ref NpParams parameters,
            double[] out_tx,
            double[] out_ty,
            double[] out_angle,
            int[] out_sheet_id,
            out int out_n_sheets);

        // E1 priority verdict of the last solve: mandatory parts, how many got no sheet, placed / offered
        // area, drop-policy retries run. Returns out_mandatory_missing.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int np_last_prio(out int out_mandatory, out int out_mandatory_missing,
                                              out double out_placed_area, out double out_offered_area,
                                              out int out_retries);

        // Cooperative cancel + progress for running np_nest on a background thread.
        // np_cancel(): ask the running solve to stop at the next round and return its best-so-far.
        // np_cancel_reset(): clear the flag (np_nest clears it on entry too).
        // np_progress(): relaxation rounds done so far (monotonic during a solve) — for a live readout.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void np_cancel();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void np_cancel_reset();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern long np_progress();

        // Snapshot the current best layout mid-solve (for an animated preview). Same (angle,tx,ty,sheet)
        // convention as np_nest. Caller preallocates buffers length part_count; unplaced => sheet -1.
        // Returns the number of placed parts (0 if no solve active). Safe to call from the UI thread.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int np_poll_layout(
            int part_count,
            double[] out_tx,
            double[] out_ty,
            double[] out_angle,
            int[] out_sheet_id,
            out int out_n_sheets);
    }
}
