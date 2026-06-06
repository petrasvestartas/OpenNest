using System.Runtime.InteropServices;

namespace NfpNest
{
    // Mirrors `struct NfpParams` in nfp_nest_capi.h. Sequential layout + matching field types/order so
    // the blittable struct crosses the boundary verbatim (same approach as NestPhysics.NpParams).
    [StructLayout(LayoutKind.Sequential)]
    public struct NfpParams
    {
        public int    placementType;   // 0=box, 1=gravity, 2=squeeze
        public int    rotations;
        public int    mutationRate;
        public int    populationSize;
        public int    seed;
        public double curveTolerance;
        public double clipperScale;
        public double spacing;
        public double sheetSpacing;
        public double rotationLimit;
        public int    useHoles;
        public int    exploreConcave;
        public int    clipByHull;
        public int    clipByRects;
        public int    simplify;
        public int    mode;            // 0=faithful (parity), 1=default, 2=turbo (multi-seed)
        public int    generations;
        public int    numSeeds;
        public int    useParallel;
        public double timeBudgetSecs;
        public int    maxSheets;
        public int    edgeSamples;      // -1 = engine default; >=0 overrides (0 = off, faster)
        public int    compactionPasses; // -1 = engine default; 0 = off (fast first run)
        public int    tryAllRotations;  // bool
        public int    exactNfp;         // bool: full-res exact NFP (no gap, tightest, slower)
    }

    // P/Invoke into nfp_nest.dll (the C++ Boost.Polygon NFP/SvgNest GA nester). The DLL is copied next to
    // the .gha by the project's post-build step (same mechanism as minkowski.dll / nest_physics.dll), so it
    // is found in the plugin directory at load time.
    public static class NfpNestWrapper
    {
        // Bare name -> resolves to nfp_nest.dll on Windows and nfp_nest.dylib on macOS.
        private const string LIBRARY_NAME = "nfp_nest";

        // Returns the number of placed instances (>=0), or negative on error. Output buffers are
        // caller-allocated, length = instance_count = sum(part_quantities), emitted in expansion order
        // (part0 x q0, part1 x q1, ...). out_n_sheets / out_fitness are single values.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nfp_nest(
            int          part_count,
            int[]        part_vertex_counts,
            double[]     part_xy,
            int[]        part_quantities,
            int[]        part_hole_counts,
            int[]        part_hole_vertex_counts,
            double[]     part_hole_xy,
            int          sheet_count,
            int[]        sheet_vertex_counts,
            double[]     sheet_xy,
            int[]        sheet_hole_counts,
            int[]        sheet_hole_vertex_counts,
            double[]     sheet_hole_xy,
            ref NfpParams parameters,
            double[]     out_tx,
            double[]     out_ty,
            double[]     out_angle,
            int[]        out_sheet_id,
            int[]        out_part_index,
            out int      out_n_sheets,
            out double   out_fitness);

        // Cooperative cancel + progress for running nfp_nest on a background thread, and a live snapshot
        // of the current best layout for an animated preview (same role as the np_* equivalents).
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nfp_cancel();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void nfp_cancel_reset();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern long nfp_progress();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern double nfp_fitness();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nfp_poll_layout(
            int      instance_count,
            double[] out_tx,
            double[] out_ty,
            double[] out_angle,
            int[]    out_sheet_id,
            int[]    out_part_index,
            out int  out_n_sheets);
    }
}
