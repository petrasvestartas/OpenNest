// nest_spectral — GPU correlator backend (cuFFT), device-resident. One persistent cuFFT plan + device
// buffers for a fixed tray size; FFT(flip(tray)) and FFT(flip(phi)) are uploaded+transformed once per
// item and kept on the device, so each orientation is just: upload item -> FFT -> two multiplies with
// the cached spectra -> two inverse FFTs. Built only when NEST_SPECTRAL_CUDA=ON; cufft is delay-loaded
// so the DLL still loads with no CUDA (see cuda_probe.cpp). Every entry is error-checked; on failure the
// CPU correlator (correlator.hpp) takes over.
#include <cuda_runtime.h>
#include <cufft.h>
#include <vector>
#include <algorithm>
#include <cmath>
#include "grid.hpp"

namespace nsp {

// out = (A .* B) * scale   (element-wise complex multiply with the 1/PV inverse-FFT normalization).
__global__ void nsp_cmul(cufftComplex* out, const cufftComplex* A, const cufftComplex* B, int n, float scale) {
    int i = blockIdx.x * blockDim.x + threadIdx.x;
    if (i < n) {
        float re = A[i].x * B[i].x - A[i].y * B[i].y;
        float im = A[i].x * B[i].y + A[i].y * B[i].x;
        out[i].x = re * scale;
        out[i].y = im * scale;
    }
}

struct GpuCorr {
    int nx, ny, nz, X, Y, Z;
    size_t PV;
    cufftHandle plan = 0;
    cufftComplex *dTrayF = nullptr, *dPhiF = nullptr, *dItem = nullptr, *dProd = nullptr;
    std::vector<cufftComplex> h;   // host scratch (PV)
};
static GpuCorr* g = nullptr;

void gpu_corr_free() {
    if (!g) return;
    if (g->plan) cufftDestroy(g->plan);
    if (g->dTrayF) cudaFree(g->dTrayF);
    if (g->dPhiF) cudaFree(g->dPhiF);
    if (g->dItem) cudaFree(g->dItem);
    if (g->dProd) cudaFree(g->dProd);
    delete g;
    g = nullptr;
}

bool gpu_corr_init(int nx, int ny, int nz) {
    gpu_corr_free();
    GpuCorr* c = new GpuCorr();
    c->nx = nx; c->ny = ny; c->nz = nz;
    c->X = 2 * nx + 1; c->Y = 2 * ny + 1; c->Z = 2 * nz + 1;
    c->PV = (size_t)c->X * c->Y * c->Z;
    c->h.resize(c->PV);
    bool ok = cudaMalloc(&c->dTrayF, c->PV * sizeof(cufftComplex)) == cudaSuccess
           && cudaMalloc(&c->dPhiF,  c->PV * sizeof(cufftComplex)) == cudaSuccess
           && cudaMalloc(&c->dItem,  c->PV * sizeof(cufftComplex)) == cudaSuccess
           && cudaMalloc(&c->dProd,  c->PV * sizeof(cufftComplex)) == cudaSuccess
           && cufftPlan3d(&c->plan, c->X, c->Y, c->Z, CUFFT_C2C) == CUFFT_SUCCESS;
    if (!ok) {
        if (c->plan) cufftDestroy(c->plan);
        if (c->dTrayF) cudaFree(c->dTrayF);
        if (c->dPhiF) cudaFree(c->dPhiF);
        if (c->dItem) cudaFree(c->dItem);
        if (c->dProd) cudaFree(c->dProd);
        delete c;
        return false;
    }
    g = c;
    return true;
}

// Pack `src` (optionally flipped) into the host buffer, upload to dItem, FFT forward into `dst`.
static bool upload_fft(const Grid& src, cufftComplex* dst, bool flip) {
    std::fill(g->h.begin(), g->h.end(), cufftComplex{0.f, 0.f});
    if (flip) {
        Grid f; flip3(src, f);
        for (int i = 0; i < g->nx; i++)
            for (int j = 0; j < g->ny; j++)
                for (int k = 0; k < g->nz; k++) g->h[((size_t)i * g->Y + j) * g->Z + k].x = (float)f(i, j, k);
    } else {
        for (int i = 0; i < src.nx; i++)
            for (int j = 0; j < src.ny; j++)
                for (int k = 0; k < src.nz; k++) g->h[((size_t)i * g->Y + j) * g->Z + k].x = (float)src(i, j, k);
    }
    if (cudaMemcpy(g->dItem, g->h.data(), g->PV * sizeof(cufftComplex), cudaMemcpyHostToDevice) != cudaSuccess) return false;
    return cufftExecC2C(g->plan, g->dItem, dst, CUFFT_FORWARD) == CUFFT_SUCCESS;
}

void gpu_corr_set_tray(const Grid& tray) { upload_fft(tray, g->dTrayF, true); }
void gpu_corr_set_phi(const Grid& phi)   { upload_fft(phi,  g->dPhiF,  true); }

// out = flip( truncate( IFFT( cached_F .* itemF ) ) ).  dItem must hold itemF (kept between the two calls).
static bool corr_one(const cufftComplex* F, Grid& out) {
    int threads = 256, blocks = (int)((g->PV + threads - 1) / threads);
    nsp_cmul<<<blocks, threads>>>(g->dProd, F, g->dItem, (int)g->PV, 1.0f / (float)g->PV);
    if (cudaGetLastError() != cudaSuccess) return false;
    if (cufftExecC2C(g->plan, g->dProd, g->dProd, CUFFT_INVERSE) != CUFFT_SUCCESS) return false;
    if (cudaMemcpy(g->h.data(), g->dProd, g->PV * sizeof(cufftComplex), cudaMemcpyDeviceToHost) != cudaSuccess) return false;
    Grid conv(g->nx, g->ny, g->nz);
    for (int i = 0; i < g->nx; i++)
        for (int j = 0; j < g->ny; j++)
            for (int k = 0; k < g->nz; k++)
                conv(i, j, k) = (int)llroundf(g->h[((size_t)i * g->Y + j) * g->Z + k].x);
    flip3(conv, out);
    return true;
}

bool gpu_corr_correlate(const Grid& item, Grid& collision, Grid& proximity) {
    if (!upload_fft(item, g->dItem, false)) return false;   // itemF in dItem (in-place forward FFT)
    if (!corr_one(g->dTrayF, collision)) return false;
    if (!corr_one(g->dPhiF,  proximity)) return false;
    return true;
}

} // namespace nsp
