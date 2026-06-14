// nest_spectral — flat 3D voxel grid + lattice ops (rotation, flip, pad, crop, bounds).
// Row-major: data[(i*ny + j)*nz + k], axes (i=x, j=y, k=z). Header-only, std-only.
#ifndef NSP_GRID_HPP
#define NSP_GRID_HPP

#include <vector>
#include <array>
#include <climits>
#include <algorithm>
#include <cstddef>

namespace nsp {

struct Grid {
    std::vector<int> data;
    int nx = 0, ny = 0, nz = 0;

    Grid() = default;
    Grid(int x, int y, int z) : data((size_t)x * y * z, 0), nx(x), ny(y), nz(z) {}

    inline int&       operator()(int i, int j, int k)       { return data[((size_t)i * ny + j) * nz + k]; }
    inline int        operator()(int i, int j, int k) const { return data[((size_t)i * ny + j) * nz + k]; }
    inline size_t     size()  const { return data.size(); }
    inline void       resize(int x, int y, int z) { nx = x; ny = y; nz = z; data.assign((size_t)x * y * z, 0); }
    inline long long  occupied() const { long long c = 0; for (int v : data) c += (v > 0); return c; }
};

// 3x3 integer rotation matrix (row-major), a signed permutation with det = +1.
using Mat3 = std::array<int, 9>;
constexpr Mat3 MAT_IDENTITY = {1, 0, 0, 0, 1, 0, 0, 0, 1};

inline Mat3 mat_mul(const Mat3& a, const Mat3& b) {
    Mat3 r{};
    for (int i = 0; i < 3; i++)
        for (int j = 0; j < 3; j++)
            r[i * 3 + j] = a[i * 3 + 0] * b[0 * 3 + j] + a[i * 3 + 1] * b[1 * 3 + j] + a[i * 3 + 2] * b[2 * 3 + j];
    return r;
}

// The 24 proper rotations of the cube, in the exact order rotations.py generates them
// (so an orientation index maps to a consistent 3x3 matrix for the output pose).
inline const std::vector<Mat3>& cube_rotations() {
    static const std::vector<Mat3> mats = [] {
        const Mat3 RX = {1, 0, 0, 0, 0, -1, 0, 1, 0};
        const Mat3 RY = {0, 0, 1, 0, 1, 0, -1, 0, 0};
        const Mat3 RZ = {0, -1, 0, 1, 0, 0, 0, 0, 1};
        const Mat3 RX2 = mat_mul(RX, RX), RX3 = mat_mul(RX2, RX);
        const Mat3 RY3 = mat_mul(mat_mul(RY, RY), RY);
        const Mat3 RZ2 = mat_mul(RZ, RZ), RZ3 = mat_mul(RZ2, RZ);
        const Mat3 zrot[4] = {MAT_IDENTITY, RZ, RZ2, RZ3};
        const Mat3 face[6] = {MAT_IDENTITY, RX, RX2, RX3, RY, RY3};
        std::vector<Mat3> out;
        for (const Mat3& f : face)
            for (const Mat3& z : zrot)
                out.push_back(mat_mul(z, f));   // Rz @ Rface
        return out;
    }();
    return mats;
}

// Orientation indices into cube_rotations() for the supported counts {1,4,6,24}.
inline std::vector<int> orientation_indices(int count) {
    switch (count) {
        case 1:  return {0};
        case 4:  return {0, 1, 2, 3};               // identity + 3 Z-rotations (face 0)
        case 6:  return {0, 4, 8, 12, 16, 20};      // one per cube face (z-rot 0 of each)
        default: { std::vector<int> all(24); for (int i = 0; i < 24; i++) all[i] = i; return all; }
    }
}

// Reverse every axis: out(i,j,k) = in(nx-1-i, ny-1-j, nz-1-k).
inline void flip3(const Grid& in, Grid& out) {
    out.resize(in.nx, in.ny, in.nz);
    for (int i = 0; i < in.nx; i++)
        for (int j = 0; j < in.ny; j++)
            for (int k = 0; k < in.nz; k++)
                out(i, j, k) = in(in.nx - 1 - i, in.ny - 1 - j, in.nz - 1 - k);
}

// Zero-pad `in` into a grid of (X,Y,Z) >= its dims, placed at the origin corner.
inline Grid pad_to(const Grid& in, int X, int Y, int Z) {
    Grid out(X, Y, Z);
    for (int i = 0; i < in.nx; i++)
        for (int j = 0; j < in.ny; j++)
            for (int k = 0; k < in.nz; k++)
                out(i, j, k) = in(i, j, k);
    return out;
}

// Inclusive bounds of occupied (>0) voxels. Returns false if empty.
inline bool occupied_bounds(const Grid& g, int lo[3], int hi[3]) {
    lo[0] = lo[1] = lo[2] = INT_MAX;
    hi[0] = hi[1] = hi[2] = INT_MIN;
    for (int i = 0; i < g.nx; i++)
        for (int j = 0; j < g.ny; j++)
            for (int k = 0; k < g.nz; k++)
                if (g(i, j, k) > 0) {
                    lo[0] = std::min(lo[0], i); hi[0] = std::max(hi[0], i);
                    lo[1] = std::min(lo[1], j); hi[1] = std::max(hi[1], j);
                    lo[2] = std::min(lo[2], k); hi[2] = std::max(hi[2], k);
                }
    return hi[0] >= lo[0];
}

// Crop to the tight occupied box. `off` receives the min corner removed (for the world transform).
inline Grid make_tight(const Grid& g, int off[3]) {
    int lo[3], hi[3];
    if (!occupied_bounds(g, lo, hi)) { off[0] = off[1] = off[2] = 0; return Grid(1, 1, 1); }
    off[0] = lo[0]; off[1] = lo[1]; off[2] = lo[2];
    Grid out(hi[0] - lo[0] + 1, hi[1] - lo[1] + 1, hi[2] - lo[2] + 1);
    for (int i = lo[0]; i <= hi[0]; i++)
        for (int j = lo[1]; j <= hi[1]; j++)
            for (int k = lo[2]; k <= hi[2]; k++)
                out(i - lo[0], j - lo[1], k - lo[2]) = g(i, j, k);
    return out;
}

// Rotate a voxel grid by the signed-permutation matrix R, re-anchored to the origin corner.
// `rotmin` receives R applied to the box's min corner (the shift folded out), for the pose math.
inline Grid rotate_by(const Grid& in, const Mat3& R, int rotmin[3]) {
    auto apply = [&](int i, int j, int k, int& oi, int& oj, int& ok) {
        oi = R[0] * i + R[1] * j + R[2] * k;
        oj = R[3] * i + R[4] * j + R[5] * k;
        ok = R[6] * i + R[7] * j + R[8] * k;
    };
    int mn[3] = {INT_MAX, INT_MAX, INT_MAX}, mx[3] = {INT_MIN, INT_MIN, INT_MIN};
    const int cx[2] = {0, in.nx - 1}, cy[2] = {0, in.ny - 1}, cz[2] = {0, in.nz - 1};
    for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++) for (int c = 0; c < 2; c++) {
        int o[3]; apply(cx[a], cy[b], cz[c], o[0], o[1], o[2]);
        for (int d = 0; d < 3; d++) { mn[d] = std::min(mn[d], o[d]); mx[d] = std::max(mx[d], o[d]); }
    }
    rotmin[0] = mn[0]; rotmin[1] = mn[1]; rotmin[2] = mn[2];
    Grid out(mx[0] - mn[0] + 1, mx[1] - mn[1] + 1, mx[2] - mn[2] + 1);
    for (int i = 0; i < in.nx; i++)
        for (int j = 0; j < in.ny; j++)
            for (int k = 0; k < in.nz; k++)
                if (in(i, j, k) > 0) {
                    int o[3]; apply(i, j, k, o[0], o[1], o[2]);
                    out(o[0] - mn[0], o[1] - mn[1], o[2] - mn[2]) = in(i, j, k);
                }
    return out;
}

} // namespace nsp
#endif // NSP_GRID_HPP
