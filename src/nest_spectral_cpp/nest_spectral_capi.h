// C ABI for nest_spectral: a single stateless entry point a host (e.g. the OpenNest Grasshopper plugin)
// can P/Invoke for 3D mesh-mesh nesting. Geometry crosses as flat double[]/int[] arrays; the host
// preallocates the outputs (it knows instance_count = sum of quantities). cdecl, no name mangling.
//
// This is the CPU spectral packer (FFT-correlation collision + distance proximity + height penalty,
// voxel representation) - the same method as the MIT/Inkbit "Spectral Packing" paper, ported off CUDA
// to pocketfft so it runs anywhere the other OpenNest engines do (no GPU required).
#ifndef NEST_SPECTRAL_CAPI_H
#define NEST_SPECTRAL_CAPI_H

#if defined(_WIN32) || defined(_WIN64)
  #define NS_EXPORT __declspec(dllexport)
#else
  #define NS_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct NsParams {
    int    voxel_resolution;   // voxels along the container's LONGEST axis; sets the global voxel pitch
                               //   (pitch = max(cx,cy,cz)/voxel_resolution). Higher = finer + slower. 0 -> 64.
    int    num_orientations;   // discrete cube orientations tried per part: 1, 4 (z-spins), 6 (per face),
                               //   or 24 (full cube group). Other values fall back to 1.
    double height_penalty;     // weight on (z/Lz)^3 in the placement score: biases parts toward the floor
                               //   (z is the "up" axis). <=0 -> default 1e8.
    int    sort_by_volume;     // 1 = greedy largest-first (default); 0 = input order.
    int    threads;            // FFT worker threads per transform. <=0 -> 1.
} NsParams;

// Nest `part_count` triangle meshes into ONE axis-aligned container box (cx x cy x cz, model units;
// the box's min corner is the local origin). Parts are voxelized at a shared pitch and packed by the
// spectral greedy. Each part may be requested `quantity` times.
//
// Inputs:
//   part_vertex_counts[part_count]          vertices per part
//   part_xyz[3*sum(part_vertex_counts)]     interleaved x,y,z for all parts, in order
//   part_tri_counts[part_count]             triangles per part
//   part_tris[3*sum(part_tri_counts)]       triangle vertex indices, LOCAL to each part (0-based)
//   part_quantities[part_count]             instances per part (NULL => 1 each)
//
// Outputs (host-allocated, length instance_count = sum(part_quantities); emitted in expansion order
// part0 x q0, part1 x q1, ...). The placed pose of an instance maps an original mesh point p to:
//       world = R * p + (tx, ty, tz)
// expressed in the container-LOCAL frame (origin at the container's min corner; the host adds the
// container's own world placement).
//   out_tx, out_ty, out_tz   translation per instance
//   out_rot[9*instance_count] row-major 3x3 rotation per instance (a 90-degree cube rotation, det +1)
//   out_container_id          0 if placed in the container, -1 if it did not fit
//   out_part_index            source part index for each instance
//   out_n_containers          (length 1) number of containers used (0 or 1 in v1)
//
// Returns the number of placed instances (>=0), or a negative error code.
NS_EXPORT int nest_spectral(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xyz,
    const int*    part_tri_counts,
    const int*    part_tris,
    const int*    part_quantities,
    double        container_x,
    double        container_y,
    double        container_z,
    const NsParams* params,
    double*       out_tx,
    double*       out_ty,
    double*       out_tz,
    double*       out_rot,
    int*          out_container_id,
    int*          out_part_index,
    int*          out_n_containers);

#ifdef __cplusplus
}
#endif

#endif // NEST_SPECTRAL_CAPI_H
