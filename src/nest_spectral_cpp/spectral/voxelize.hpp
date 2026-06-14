// nest_spectral — solid voxelizer: triangle mesh (interleaved verts + triangle indices) -> binary Grid
// at a fixed voxel pitch (model units per voxel), shared by every part so collisions are commensurate.
// Method: for each voxel column (i,j) shoot a ray along +z and collect triangle crossings; a voxel is
// INSIDE when its centre lies between an odd number of crossings (scanline parity fill). Self-contained
// (no LibSL); robust for watertight meshes, the common Rhino/Grasshopper case.
#ifndef NSP_VOXELIZE_HPP
#define NSP_VOXELIZE_HPP

#include <vector>
#include <algorithm>
#include <cmath>
#include "grid.hpp"

namespace nsp {

struct VoxelizeResult {
    Grid   grid;            // binary occupancy, dims derived from the mesh bbox and pitch
    double origin[3] = {0, 0, 0};  // world coord of voxel (0,0,0)'s min corner (= mesh bbox min)
    double pitch = 1.0;     // model units per voxel edge
};

// Voxelize one mesh. `verts` = n_verts*3 doubles (x,y,z); `tris` = n_tris*3 int vertex indices.
inline VoxelizeResult voxelize_mesh(const double* verts, int n_verts,
                                    const int* tris, int n_tris, double pitch) {
    VoxelizeResult R;
    R.pitch = pitch;
    if (n_verts <= 0 || n_tris <= 0 || pitch <= 0) { R.grid = Grid(1, 1, 1); return R; }

    double mn[3] = {1e300, 1e300, 1e300}, mx[3] = {-1e300, -1e300, -1e300};
    for (int v = 0; v < n_verts; v++)
        for (int d = 0; d < 3; d++) {
            double c = verts[v * 3 + d];
            mn[d] = std::min(mn[d], c); mx[d] = std::max(mx[d], c);
        }
    for (int d = 0; d < 3; d++) R.origin[d] = mn[d];

    const int nx = std::max(1, (int)std::ceil((mx[0] - mn[0]) / pitch));
    const int ny = std::max(1, (int)std::ceil((mx[1] - mn[1]) / pitch));
    const int nz = std::max(1, (int)std::ceil((mx[2] - mn[2]) / pitch));
    R.grid = Grid(nx, ny, nz);

    // z-crossings per (i,j) column
    std::vector<std::vector<double>> col((size_t)nx * ny);

    for (int t = 0; t < n_tris; t++) {
        const int ia = tris[t * 3 + 0], ib = tris[t * 3 + 1], ic = tris[t * 3 + 2];
        if (ia < 0 || ib < 0 || ic < 0 || ia >= n_verts || ib >= n_verts || ic >= n_verts) continue;
        const double Ax = verts[ia*3], Ay = verts[ia*3+1], Az = verts[ia*3+2];
        const double Bx = verts[ib*3], By = verts[ib*3+1], Bz = verts[ib*3+2];
        const double Cx = verts[ic*3], Cy = verts[ic*3+1], Cz = verts[ic*3+2];

        const double det = (By - Cy) * (Ax - Cx) + (Cx - Bx) * (Ay - Cy);   // 2*signed area (xy)
        if (std::fabs(det) < 1e-18) continue;                               // edge-on / degenerate in xy
        const double inv = 1.0 / det;

        double tminx = std::min({Ax, Bx, Cx}), tmaxx = std::max({Ax, Bx, Cx});
        double tminy = std::min({Ay, By, Cy}), tmaxy = std::max({Ay, By, Cy});
        int i0 = std::max(0, (int)std::floor((tminx - mn[0]) / pitch));
        int i1 = std::min(nx - 1, (int)std::ceil((tmaxx - mn[0]) / pitch));
        int j0 = std::max(0, (int)std::floor((tminy - mn[1]) / pitch));
        int j1 = std::min(ny - 1, (int)std::ceil((tmaxy - mn[1]) / pitch));

        // Sub-voxel jitter breaks exact ties when a column centre lands on a shared triangle edge
        // (e.g. a box face's diagonal): without it the edge is counted by BOTH triangles, doubling
        // the crossing and corrupting the parity fill. The offset is negligible vs a voxel.
        const double JX = 1e-4 * pitch, JY = 7e-5 * pitch;
        for (int i = i0; i <= i1; i++) {
            const double px = mn[0] + (i + 0.5) * pitch + JX;
            for (int j = j0; j <= j1; j++) {
                const double py = mn[1] + (j + 0.5) * pitch + JY;
                const double l1 = ((By - Cy) * (px - Cx) + (Cx - Bx) * (py - Cy)) * inv;
                const double l2 = ((Cy - Ay) * (px - Cx) + (Ax - Cx) * (py - Cy)) * inv;
                const double l3 = 1.0 - l1 - l2;
                if (l1 < -1e-9 || l2 < -1e-9 || l3 < -1e-9) continue;       // outside triangle
                col[(size_t)i * ny + j].push_back(l1 * Az + l2 * Bz + l3 * Cz);
            }
        }
    }

    for (int i = 0; i < nx; i++)
        for (int j = 0; j < ny; j++) {
            auto& zs = col[(size_t)i * ny + j];
            if (zs.size() < 2) continue;
            std::sort(zs.begin(), zs.end());
            for (size_t p = 0; p + 1 < zs.size(); p += 2) {                 // parity: fill between pairs
                const double z0 = zs[p], z1 = zs[p + 1];
                int k0 = std::max(0,      (int)std::ceil((z0 - mn[2]) / pitch - 0.5));
                int k1 = std::min(nz - 1, (int)std::floor((z1 - mn[2]) / pitch - 0.5));
                for (int k = k0; k <= k1; k++) R.grid(i, j, k) = 1;
            }
        }
    return R;
}

} // namespace nsp
#endif // NSP_VOXELIZE_HPP
