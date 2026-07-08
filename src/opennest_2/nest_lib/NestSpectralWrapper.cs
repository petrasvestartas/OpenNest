using System.Runtime.InteropServices;

namespace opennest_2
{
    // P/Invoke surface for the native nest_spectral engine (src/nest_spectral_cpp -> nest_spectral.dll).
    // Mirrors NestPhysicsWrapper exactly: bare-name DllImport (the OS resolves nest_spectral.dll from the
    // plugin folder next to the .gha), cdecl, a blittable LayoutKind.Sequential params struct passed by ref,
    // plain managed int[]/double[] for in/out arrays (the marshaller pins them for the call).
    public static class NestSpectralWrapper
    {
        private const string LIBRARY_NAME = "nest_spectral";

        // Must match NsParams in nest_spectral_capi.h field-for-field (int,int,double,int,int,double).
        [StructLayout(LayoutKind.Sequential)]
        public struct NsParams
        {
            public int    voxel_resolution;   // voxels along the container's longest axis (sets the pitch); 0 -> 64
            public int    num_orientations;   // 1, 4, 6, or 24 cube orientations tried per part
            public double height_penalty;     // weight on (z/Lz)^3; biases parts toward the floor; <=0 -> 1e8
            public int    sort_by_volume;     // 1 = greedy largest-first
            public int    threads;            // FFT worker threads per transform; <=0 -> 1
            public double clearance;          // min empty gap between parts and from walls, model units; <=0 -> off
        }

        // world = R * p + (tx,ty,tz) per instance, container-local frame. Returns placed-instance count
        // (>=0) or a negative error code. Output buffers are caller-allocated, length = sum(quantities),
        // in expansion order (part0 x q0, part1 x q1, ...). out_rot holds 9 row-major doubles per instance.
        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern int nest_spectral(
            int part_count,
            int[] part_vertex_counts,
            double[] part_xyz,
            int[] part_tri_counts,
            int[] part_tris,
            int[] part_quantities,
            double container_x,
            double container_y,
            double container_z,
            ref NsParams parameters,
            double[] out_tx,
            double[] out_ty,
            double[] out_tz,
            double[] out_rot,
            int[] out_container_id,
            int[] out_part_index,
            out int out_n_containers,
            out int out_used_gpu);
    }
}
