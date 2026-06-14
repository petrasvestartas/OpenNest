// nest_spectral — greedy spectral packer. Sort items by volume (largest first); for each item try a set
// of 90-degree cube orientations and, via FFT cross-correlation against the tray, pick the lowest-scoring
// collision-free cell (score = proximity-to-existing-geometry + a cubic height penalty so items settle low).
// Tray axis k (z) is the "up"/gravity axis. This is the CPU port of psacking's pack loop.
#ifndef NSP_PACKER_HPP
#define NSP_PACKER_HPP

#include <vector>
#include <limits>
#include <algorithm>
#include <numeric>
#include "grid.hpp"
#include "fft.hpp"
#include "distance.hpp"

namespace nsp {

struct SpectralParams {
    int    num_orientations = 1;     // 1, 4, 6, or 24 (cube-symmetry rotations sampled per item)
    double height_penalty   = 1e8;   // P: weight on (z/L)^3, biases items toward the floor
    bool   sort_by_volume   = true;  // greedy largest-first
    size_t nthreads         = 1;     // pocketfft threads per transform
};

struct ItemPlacement {
    int    item_index = -1;          // index into the original input list
    bool   placed     = false;
    int    pos[3]     = {0, 0, 0};   // tray voxel coord of the rotated item's min corner
    int    orient     = 0;           // index into cube_rotations()
    Mat3   R          = MAT_IDENTITY;
    int    rotmin[3]  = {0, 0, 0};   // shift produced when the item grid was rotated (for the world pose)
    double score      = 0.0;
};

// Best collision-free placement of `item` (tight) into `tray`, scored against precomputed field `phi`.
inline bool search_placement(const Grid& item, const Grid& tray, const Grid& phi,
                             double P, int pos[3], double& score, size_t nthreads) {
    const int N = tray.nx, M = tray.ny, L = tray.nz;
    if (item.nx > N || item.ny > M || item.nz > L) return false;

    Grid item_pad = pad_to(item, N, M, L);
    Grid collision, prox;
    corr3(tray, item_pad, collision, nthreads);
    corr3(phi,  item_pad, prox,      nthreads);

    double best = std::numeric_limits<double>::infinity();
    bool found = false;
    const int maxI = N - item.nx, maxJ = M - item.ny, maxK = L - item.nz;
    for (int i = 0; i <= maxI; i++)
        for (int j = 0; j <= maxJ; j++)
            for (int k = 0; k <= maxK; k++) {
                if (collision(i, j, k) != 0) continue;            // would overlap existing voxels
                double qz = (double)k / (double)L;
                double s = (double)prox(i, j, k) + P * qz * qz * qz;
                if (s < best) {
                    best = s; found = true; score = s;
                    pos[0] = i; pos[1] = j; pos[2] = k;
                }
            }
    return found;
}

// OR an item's voxels into the tray at `pos`, tagged with `val`. Returns false on (unexpected) overlap.
inline bool place(const Grid& item, Grid& tray, const int pos[3], int val) {
    for (int i = 0; i < item.nx; i++)
        for (int j = 0; j < item.ny; j++)
            for (int k = 0; k < item.nz; k++)
                if (item(i, j, k) > 0) {
                    int ti = pos[0] + i, tj = pos[1] + j, tk = pos[2] + k;
                    if (ti < 0 || tj < 0 || tk < 0 || ti >= tray.nx || tj >= tray.ny || tk >= tray.nz) return false;
                    if (tray(ti, tj, tk) > 0) return false;
                    tray(ti, tj, tk) = val;
                }
    return true;
}

// Greedy pack of binary voxel `items` into a TX*TY*TZ tray. Fills `tray_out` (voxels tagged 1..n_placed)
// and returns a placement per item, indexed by original input order.
inline std::vector<ItemPlacement> pack(const std::vector<Grid>& items, int TX, int TY, int TZ,
                                       const SpectralParams& p, Grid& tray_out) {
    const int n = (int)items.size();
    std::vector<int> order(n);
    std::iota(order.begin(), order.end(), 0);
    if (p.sort_by_volume)
        std::sort(order.begin(), order.end(),
                  [&](int a, int b) { return items[a].occupied() > items[b].occupied(); });

    tray_out.resize(TX, TY, TZ);
    std::vector<ItemPlacement> result(n);
    for (int i = 0; i < n; i++) result[i].item_index = i;

    const std::vector<int> orients = orientation_indices(p.num_orientations);
    int n_placed = 0;

    for (int oidx = 0; oidx < n; oidx++) {
        int idx = order[oidx];
        Grid phi;
        distance_field(tray_out, phi);    // depends only on the tray; shared across this item's orientations

        double best_score = std::numeric_limits<double>::infinity();
        bool   any = false;
        Grid   best_rot;
        ItemPlacement best;
        best.item_index = idx;

        for (int oi : orients) {
            const Mat3& R = cube_rotations()[oi];
            int rotmin[3];
            Grid rot = rotate_by(items[idx], R, rotmin);
            if (rot.nx > TX || rot.ny > TY || rot.nz > TZ) continue;

            int pos[3]; double score;
            if (search_placement(rot, tray_out, phi, p.height_penalty, pos, score, p.nthreads)) {
                if (score < best_score) {
                    best_score = score; any = true;
                    best.placed = true; best.orient = oi; best.R = R; best.score = score;
                    best.pos[0] = pos[0]; best.pos[1] = pos[1]; best.pos[2] = pos[2];
                    best.rotmin[0] = rotmin[0]; best.rotmin[1] = rotmin[1]; best.rotmin[2] = rotmin[2];
                    best_rot = rot;
                }
            }
        }

        if (any) {
            place(best_rot, tray_out, best.pos, ++n_placed);
            result[idx] = best;
        }
    }
    return result;
}

// Density of the packed tray = occupied voxels / volume of the occupied bounding box.
inline double packing_density(const Grid& tray) {
    int lo[3], hi[3];
    if (!occupied_bounds(tray, lo, hi)) return 0.0;
    double bbox = (double)(hi[0] - lo[0] + 1) * (hi[1] - lo[1] + 1) * (hi[2] - lo[2] + 1);
    return bbox > 0 ? (double)tray.occupied() / bbox : 0.0;
}

} // namespace nsp
#endif // NSP_PACKER_HPP
