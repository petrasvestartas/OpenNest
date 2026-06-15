// nest_spectral — GPU FFT backend (cuFFT). Compiled only when NEST_SPECTRAL_CUDA=ON (the CUDA toolkit
// is needed to BUILD this, never to RUN: cufft is delay-loaded, so the DLL still loads on machines with
// no CUDA and the dispatcher in fft.hpp falls back to the CPU pocketfft path). conv3_cuda returns false
// on ANY CUDA/cuFFT failure (incl. a GPU whose architecture this build wasn't compiled for) so the
// dispatcher can fall back to the CPU path — the GPU path never silently produces a wrong result.
#include <cuda_runtime.h>
#include <cufft.h>
#include <vector>
#include <algorithm>
#include <cmath>
#include "grid.hpp"

namespace nsp {

// Complex multiply A *= B, with the 1/PV inverse-FFT normalization folded in (FFT is linear).
__global__ void nsp_cmul_scale(cufftComplex* a, const cufftComplex* b, int n, float scale) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) {
        float re = a[i].x * b[i].x - a[i].y * b[i].y;
        float im = a[i].x * b[i].y + a[i].y * b[i].x;
        a[i].x = re * scale;
        a[i].y = im * scale;
    }
}

// result(i,j,k) = sum a(u,v,w)*b(i-u,j-v,k-w), truncated to a's box. a,b share dims (item padded to tray).
// Returns false (and frees everything) on any CUDA/cuFFT error so the caller can use the CPU backend.
bool conv3_cuda(const Grid& a, const Grid& b, Grid& result) {
    const int N = a.nx, M = a.ny, L = a.nz;
    const int X = 2 * N + 1, Y = 2 * M + 1, Z = 2 * L + 1;
    const size_t PV = (size_t)X * Y * Z;

    cufftComplex *dA = nullptr, *dB = nullptr;
    cufftHandle plan = 0;
    auto cleanup = [&]() { if (plan) cufftDestroy(plan); if (dA) cudaFree(dA); if (dB) cudaFree(dB); };

    std::vector<cufftComplex> h(PV);   // value-initialized to {0,0}

    if (cudaMalloc(&dA, PV * sizeof(cufftComplex)) != cudaSuccess) { cleanup(); return false; }
    if (cudaMalloc(&dB, PV * sizeof(cufftComplex)) != cudaSuccess) { cleanup(); return false; }

    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++)
            for (int k = 0; k < L; k++) h[((size_t)i * Y + j) * Z + k].x = (float)a(i, j, k);
    if (cudaMemcpy(dA, h.data(), PV * sizeof(cufftComplex), cudaMemcpyHostToDevice) != cudaSuccess) { cleanup(); return false; }

    std::fill(h.begin(), h.end(), cufftComplex{0.f, 0.f});
    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++)
            for (int k = 0; k < L; k++) h[((size_t)i * Y + j) * Z + k].x = (float)b(i, j, k);
    if (cudaMemcpy(dB, h.data(), PV * sizeof(cufftComplex), cudaMemcpyHostToDevice) != cudaSuccess) { cleanup(); return false; }

    if (cufftPlan3d(&plan, X, Y, Z, CUFFT_C2C) != CUFFT_SUCCESS) { cleanup(); return false; }
    if (cufftExecC2C(plan, dA, dA, CUFFT_FORWARD) != CUFFT_SUCCESS) { cleanup(); return false; }
    if (cufftExecC2C(plan, dB, dB, CUFFT_FORWARD) != CUFFT_SUCCESS) { cleanup(); return false; }

    int threads = 256, blocks = (int)((PV + threads - 1) / threads);
    nsp_cmul_scale<<<blocks, threads>>>(dA, dB, (int)PV, 1.0f / (float)PV);
    if (cudaGetLastError() != cudaSuccess) { cleanup(); return false; }   // launch error (e.g. arch mismatch)

    if (cufftExecC2C(plan, dA, dA, CUFFT_INVERSE) != CUFFT_SUCCESS) { cleanup(); return false; }
    if (cudaMemcpy(h.data(), dA, PV * sizeof(cufftComplex), cudaMemcpyDeviceToHost) != cudaSuccess) { cleanup(); return false; }

    result.resize(N, M, L);
    for (int i = 0; i < N; i++)
        for (int j = 0; j < M; j++)
            for (int k = 0; k < L; k++)
                result(i, j, k) = (int)llroundf(h[((size_t)i * Y + j) * Z + k].x);

    cleanup();
    return true;
}

} // namespace nsp
