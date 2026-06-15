// nest_spectral — 3D linear convolution / cross-correlation of voxel grids via FFT (pocketfft, CPU).
// This is the CPU replacement for psacking's cuFFT path (dft_conv3_flat / dft_corr3_flat): zero-pad to
// (2N+1) per axis so the cyclic FFT product equals the LINEAR convolution, multiply in frequency space,
// inverse-transform, round to integers, truncate back to the tray box.
#ifndef NSP_FFT_HPP
#define NSP_FFT_HPP

#include <complex>
#include <vector>
#include <cmath>
#include <cstddef>
#include "../third_party/pocketfft_hdronly.h"
#include "grid.hpp"

namespace nsp {

#ifdef NSP_CUDA
// Defined in fft_cuda.cu / cuda_probe.cpp (built only when NEST_SPECTRAL_CUDA=ON).
bool cuda_backend_available();
bool conv3_cuda(const Grid& a, const Grid& b, Grid& result);   // false on any GPU failure -> use CPU
#endif

using cd = std::complex<double>;

// In-place 3D complex FFT of a contiguous (X,Y,Z) row-major buffer.
inline void fft3(cd* buf, int X, int Y, int Z, bool forward, double fct, size_t nthreads = 1) {
    pocketfft::shape_t  shape = {(size_t)X, (size_t)Y, (size_t)Z};
    pocketfft::stride_t stride = {(ptrdiff_t)((size_t)Y * Z * sizeof(cd)),
                                  (ptrdiff_t)((size_t)Z * sizeof(cd)),
                                  (ptrdiff_t)sizeof(cd)};
    pocketfft::shape_t  axes = {0, 1, 2};
    pocketfft::c2c(shape, stride, stride, axes, forward, buf, buf, fct, nthreads);
}

// CPU (pocketfft) linear convolution. The default backend and the fallback when no GPU is present.
inline void conv3_cpu(const Grid& a, const Grid& b, Grid& result, size_t nthreads = 1) {
    const int N = a.nx, M = a.ny, L = a.nz;
    const int X = 2 * N + 1, Y = 2 * M + 1, Z = 2 * L + 1;
    const size_t PV = (size_t)X * Y * Z;

    std::vector<cd> A(PV, cd(0, 0)), B(PV, cd(0, 0));
    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++)
            for (int k = 0; k < L; k++) {
                size_t idx = ((size_t)i * Y + j) * Z + k;
                A[idx] = cd((double)a(i, j, k), 0.0);
                B[idx] = cd((double)b(i, j, k), 0.0);
            }

    fft3(A.data(), X, Y, Z, true, 1.0, nthreads);
    fft3(B.data(), X, Y, Z, true, 1.0, nthreads);
    for (size_t i = 0; i < PV; i++) A[i] *= B[i];
    fft3(A.data(), X, Y, Z, false, 1.0 / (double)PV, nthreads);

    result.resize(N, M, L);
    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++)
            for (int k = 0; k < L; k++)
                result(i, j, k) = (int)std::llround(A[((size_t)i * Y + j) * Z + k].real());
}

// conv3 dispatcher: use the cuFFT GPU backend once, if an NVIDIA device + cuFFT are present (detected
// lazily and cached); otherwise the CPU pocketfft path. corr3 (below) calls this, so the whole spectral
// search runs on the GPU when available with no other change.
inline void conv3(const Grid& a, const Grid& b, Grid& result, size_t nthreads = 1) {
#ifdef NSP_CUDA
    static int gpu = -1;
    if (gpu < 0) gpu = cuda_backend_available() ? 1 : 0;
    if (gpu == 1) { if (conv3_cuda(a, b, result)) return; gpu = 0; }   // GPU failed -> disable, use CPU
#endif
    conv3_cpu(a, b, result, nthreads);
}

// Cross-correlation: result(q) = sum_p a(p) * b(p - q)  ==  flip(conv(flip(a), b)).
// Used for collision counts (a=tray, b=item) and proximity (a=distance field, b=item).
inline void corr3(const Grid& a, const Grid& b, Grid& result, size_t nthreads = 1) {
    Grid fa;
    flip3(a, fa);
    Grid conv;
    conv3(fa, b, conv, nthreads);
    flip3(conv, result);
}

} // namespace nsp
#endif // NSP_FFT_HPP
