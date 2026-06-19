// nest_spectral — L1 (city-block) distance field of the tray occupancy. phi(cell) = #voxels to the
// nearest occupied voxel (0 on occupied cells). CPU port of psacking's calculate_distance: a separable
// forward+backward min-sweep along x, then y, then z gives the exact Manhattan distance transform.
// Correlating phi with an item rewards placements that hug existing geometry (low summed distance).
#ifndef NSP_DISTANCE_HPP
#define NSP_DISTANCE_HPP

#include "grid.hpp"

namespace nsp {

inline void distance_field(const Grid& occ, Grid& dist) {
    const int N = occ.nx, M = occ.ny, L = occ.nz;
    const int BIG = N + M + L + 10;
    dist.resize(N, M, L);
    for (size_t i = 0; i < occ.size(); i++) dist.data[i] = (occ.data[i] > 0) ? 0 : BIG;

    // sweep along x
    for (int j = 0; j < M; j++)
        for (int k = 0; k < L; k++) {
            for (int i = 1; i < N; i++)     dist(i, j, k) = std::min(dist(i, j, k), dist(i - 1, j, k) + 1);
            for (int i = N - 2; i >= 0; i--) dist(i, j, k) = std::min(dist(i, j, k), dist(i + 1, j, k) + 1);
        }
    // sweep along y
    for (int i = 0; i < N; i++)
        for (int k = 0; k < L; k++) {
            for (int j = 1; j < M; j++)     dist(i, j, k) = std::min(dist(i, j, k), dist(i, j - 1, k) + 1);
            for (int j = M - 2; j >= 0; j--) dist(i, j, k) = std::min(dist(i, j, k), dist(i, j + 1, k) + 1);
        }
    // sweep along z
    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++) {
            for (int k = 1; k < L; k++)     dist(i, j, k) = std::min(dist(i, j, k), dist(i, j, k - 1) + 1);
            for (int k = L - 2; k >= 0; k--) dist(i, j, k) = std::min(dist(i, j, k), dist(i, j, k + 1) + 1);
        }
}

} // namespace nsp
#endif // NSP_DISTANCE_HPP
