// nest_spectral — reusable spectral correlator for a FIXED tray size. The hot-loop optimization
// (psacking's GPUTrayContext idea): build the padded FFT plan ONCE, and cache FFT(flip(tray)) and
// FFT(flip(phi)) per item, so every orientation is just one item-FFT + two multiplies + two inverse
// FFTs instead of re-transforming the tray/phi each time and re-creating plans/buffers per call.
//
// Backends: GPU (cuFFT, device-resident caches + one persistent plan) when NSP_CUDA is built AND a
// device is present; otherwise CPU (pocketfft). A GPU failure mid-solve falls back to CPU transparently.
#ifndef NSP_CORRELATOR_HPP
#define NSP_CORRELATOR_HPP

#include <vector>
#include <complex>
#include <cmath>
#include "grid.hpp"
#include "fft.hpp"   // fft3, cd

namespace nsp {

#ifdef NSP_CUDA
// Implemented in fft_cuda.cu / cuda_probe.cpp (built only when NEST_SPECTRAL_CUDA=ON). A single
// device-resident context (the packer uses one Correlator at a time, single-threaded).
bool cuda_backend_available();
bool gpu_corr_init(int nx, int ny, int nz);
void gpu_corr_set_tray(const Grid& tray);
void gpu_corr_set_phi(const Grid& phi);
bool gpu_corr_correlate(const Grid& item, Grid& collision, Grid& proximity);
void gpu_corr_free();
#endif

class Correlator {
public:
    Correlator(int nx, int ny, int nz, size_t nthreads = 1)
        : nx_(nx), ny_(ny), nz_(nz), X_(2 * nx + 1), Y_(2 * ny + 1), Z_(2 * nz + 1),
          PV_((size_t)X_ * Y_ * Z_), nthreads_(nthreads ? nthreads : 1) {
#ifdef NSP_CUDA
        if (cuda_backend_available() && gpu_corr_init(nx, ny, nz)) gpu_ = true;
#endif
    }
    ~Correlator() {
#ifdef NSP_CUDA
        if (gpu_) gpu_corr_free();
#endif
    }
    Correlator(const Correlator&) = delete;
    Correlator& operator=(const Correlator&) = delete;

    bool on_gpu() const { return gpu_; }

    // Cache the (constant-per-item) operands. set_tray/set_phi are called once after each placement.
    void set_tray(const Grid& tray) {
        last_tray_ = tray;
#ifdef NSP_CUDA
        if (gpu_) { gpu_corr_set_tray(tray); return; }
#endif
        build_F(tray, trayF_);
    }
    void set_phi(const Grid& phi) {
        last_phi_ = phi;
#ifdef NSP_CUDA
        if (gpu_) { gpu_corr_set_phi(phi); return; }
#endif
        build_F(phi, phiF_);
    }

    // collision = corr(tray, item), proximity = corr(phi, item), each truncated to the tray dims.
    void correlate(const Grid& item, Grid& collision, Grid& proximity) {
#ifdef NSP_CUDA
        if (gpu_) {
            if (gpu_corr_correlate(item, collision, proximity)) return;
            // GPU failed mid-solve: drop to CPU and rebuild the caches from the stored tray/phi.
            gpu_ = false; gpu_corr_free();
            build_F(last_tray_, trayF_);
            build_F(last_phi_, phiF_);
        }
#endif
        cpu_correlate(item, collision, proximity);
    }

private:
    int nx_, ny_, nz_, X_, Y_, Z_;
    size_t PV_, nthreads_;
    bool gpu_ = false;
    std::vector<cd> trayF_, phiF_;
    Grid last_tray_, last_phi_;

    // F = FFT( pad( flip(g) ) ), the frequency-domain form of a constant correlation operand.
    void build_F(const Grid& g, std::vector<cd>& F) {
        Grid fg; flip3(g, fg);
        F.assign(PV_, cd(0, 0));
        for (int i = 0; i < nx_; i++)
            for (int j = 0; j < ny_; j++)
                for (int k = 0; k < nz_; k++)
                    F[((size_t)i * Y_ + j) * Z_ + k] = cd((double)fg(i, j, k), 0.0);
        fft3(F.data(), X_, Y_, Z_, true, 1.0, nthreads_);
    }

    void cpu_correlate(const Grid& item, Grid& collision, Grid& proximity) {
        std::vector<cd> I(PV_, cd(0, 0));
        for (int i = 0; i < item.nx; i++)
            for (int j = 0; j < item.ny; j++)
                for (int k = 0; k < item.nz; k++)
                    I[((size_t)i * Y_ + j) * Z_ + k] = cd((double)item(i, j, k), 0.0);
        fft3(I.data(), X_, Y_, Z_, true, 1.0, nthreads_);
        cpu_one(trayF_, I, collision);
        cpu_one(phiF_, I, proximity);
    }

    // out = flip( truncate( IFFT( F .* itemF ) ) )  ==  corr(operand, item)
    void cpu_one(const std::vector<cd>& F, const std::vector<cd>& I, Grid& out) {
        std::vector<cd> P(PV_);
        for (size_t n = 0; n < PV_; n++) P[n] = F[n] * I[n];
        fft3(P.data(), X_, Y_, Z_, false, 1.0 / (double)PV_, nthreads_);
        Grid conv(nx_, ny_, nz_);
        for (int i = 0; i < nx_; i++)
            for (int j = 0; j < ny_; j++)
                for (int k = 0; k < nz_; k++)
                    conv(i, j, k) = (int)std::llround(P[((size_t)i * Y_ + j) * Z_ + k].real());
        flip3(conv, out);
    }
};

} // namespace nsp
#endif // NSP_CORRELATOR_HPP
