// nest_spectral C ABI implementation. Voxelize every part at a shared pitch, greedily spectral-pack
// into the container, then map each placement (voxel position + cube orientation) back to a world pose.
#include "nest_spectral_capi.h"

#include <vector>
#include <algorithm>
#include <cmath>

#include "spectral/grid.hpp"
#include "spectral/voxelize.hpp"
#include "spectral/packer.hpp"

using namespace nsp;

// Placement -> world pose. The voxelization/crop/rotation/placement chain composes to a single rigid
// transform world = R*p + t, where (per axis):  oc = o + (crop+0.5)*h ;  pc = (pos - rotmin + 0.5)*h ;
// t = pc - R*oc.  o = part bbox min, crop = make_tight offset, h = pitch, R = the cube rotation matrix.
static void placement_pose(const Mat3& R, const double o[3], const int crop[3], double h,
                           const int pos[3], const int rotmin[3],
                           double& tx, double& ty, double& tz, double* rot9) {
    double oc[3], pc[3];
    for (int d = 0; d < 3; d++) {
        oc[d] = o[d] + (crop[d] + 0.5) * h;
        pc[d] = (pos[d] - rotmin[d] + 0.5) * h;
    }
    double Roc0 = R[0] * oc[0] + R[1] * oc[1] + R[2] * oc[2];
    double Roc1 = R[3] * oc[0] + R[4] * oc[1] + R[5] * oc[2];
    double Roc2 = R[6] * oc[0] + R[7] * oc[1] + R[8] * oc[2];
    tx = pc[0] - Roc0;
    ty = pc[1] - Roc1;
    tz = pc[2] - Roc2;
    for (int i = 0; i < 9; i++) rot9[i] = (double)R[i];
}

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
    int*          out_n_containers) {

    if (out_n_containers) *out_n_containers = 0;
    if (part_count <= 0 || !part_vertex_counts || !part_xyz || !part_tri_counts || !part_tris)
        return -1;
    if (container_x <= 0 || container_y <= 0 || container_z <= 0) return -2;

    // Resolve params + defaults.
    int    res     = (params && params->voxel_resolution > 0) ? params->voxel_resolution : 64;
    int    norient = params ? params->num_orientations : 1;
    if (norient != 1 && norient != 4 && norient != 6 && norient != 24) norient = 1;
    double P       = (params && params->height_penalty > 0) ? params->height_penalty : 1e8;
    bool   sortvol = params ? (params->sort_by_volume != 0) : true;
    size_t threads = (params && params->threads > 0) ? (size_t)params->threads : 1;

    const double maxdim = std::max({container_x, container_y, container_z});
    const double pitch  = maxdim / (double)res;
    if (!(pitch > 0)) return -3;
    const int TX = std::max(1, (int)std::lround(container_x / pitch));
    const int TY = std::max(1, (int)std::lround(container_y / pitch));
    const int TZ = std::max(1, (int)std::lround(container_z / pitch));

    // Voxelize every part once; remember its bbox-min origin and tight-crop offset for the pose math.
    struct PartInfo { double o[3]; int crop[3]; Grid tight; };
    std::vector<PartInfo> parts(part_count);
    size_t voff = 0, toff = 0;
    for (int p = 0; p < part_count; p++) {
        int nv = part_vertex_counts[p], nt = part_tri_counts[p];
        VoxelizeResult vr = voxelize_mesh(part_xyz + voff * 3, nv, part_tris + toff * 3, nt, pitch);
        int crop[3];
        parts[p].tight = make_tight(vr.grid, crop);
        for (int d = 0; d < 3; d++) { parts[p].o[d] = vr.origin[d]; parts[p].crop[d] = crop[d]; }
        voff += (size_t)nv;
        toff += (size_t)nt;
    }

    // Expand by quantity into a flat instance list (matches the host's output buffer ordering).
    std::vector<Grid> items;
    std::vector<int>  inst_part;
    for (int p = 0; p < part_count; p++) {
        int q = part_quantities ? std::max(0, part_quantities[p]) : 1;
        for (int k = 0; k < q; k++) { items.push_back(parts[p].tight); inst_part.push_back(p); }
    }
    const int inst_count = (int)items.size();
    if (inst_count == 0) return 0;

    SpectralParams sp;
    sp.num_orientations = norient;
    sp.height_penalty   = P;
    sp.sort_by_volume   = sortvol;
    sp.nthreads         = threads;

    Grid tray;
    std::vector<ItemPlacement> places = pack(items, TX, TY, TZ, sp, tray);

    int placed = 0;
    for (int i = 0; i < inst_count; i++) {
        int pidx = inst_part[i];
        if (out_part_index) out_part_index[i] = pidx;
        const ItemPlacement& pl = places[i];
        if (pl.placed) {
            placement_pose(pl.R, parts[pidx].o, parts[pidx].crop, pitch, pl.pos, pl.rotmin,
                           out_tx[i], out_ty[i], out_tz[i], out_rot + 9 * i);
            out_container_id[i] = 0;
            placed++;
        } else {
            out_tx[i] = out_ty[i] = out_tz[i] = 0.0;
            for (int d = 0; d < 9; d++) out_rot[9 * i + d] = (d % 4 == 0) ? 1.0 : 0.0;  // identity
            out_container_id[i] = -1;
        }
    }
    if (out_n_containers) *out_n_containers = (placed > 0) ? 1 : 0;
    return placed;
}
