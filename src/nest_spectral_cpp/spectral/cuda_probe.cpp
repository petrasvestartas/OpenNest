// nest_spectral — runtime detection of a usable CUDA backend. cudart is STATICALLY linked (no DLL
// dependency), so the device probe is always safe; cufft is DELAY-loaded, so its probe is guarded with
// SEH on Windows — if cufft64_*.dll is absent the exception is caught and we report "no GPU", and the
// dispatcher in fft.hpp uses the CPU path. We also best-effort preload cufft from the CUDA toolkit
// (CUDA_PATH) so the GPU path works even when CUDA's bin is not on PATH. Compiled only with NSP_CUDA.
#include <cuda_runtime.h>
#include <cufft.h>
#if defined(_WIN32)
#include <windows.h>
#include <string>
#endif

namespace nsp {

#if defined(_WIN32)
// Load cufft64_*.dll by full path from the CUDA toolkit (CUDA_PATH) so the delay-loaded import resolves
// even when CUDA's bin\x64 is not on PATH. Once loaded by path, the delay-load finds it by name. No-op
// if CUDA isn't installed.
static void preload_cufft() {
    char buf[1024];
    DWORD n = GetEnvironmentVariableA("CUDA_PATH", buf, (DWORD)sizeof(buf));
    if (n == 0 || n >= sizeof(buf)) return;
    std::string base(buf, n);
    const char* subdirs[] = { "\\bin\\x64", "\\bin" };   // CUDA 13 moved runtime DLLs to bin\x64
    for (const char* sd : subdirs) {
        std::string dir = base + sd;
        WIN32_FIND_DATAA fd;
        HANDLE h = FindFirstFileA((dir + "\\cufft64_*.dll").c_str(), &fd);
        if (h == INVALID_HANDLE_VALUE) continue;
        do {
            if (std::string(fd.cFileName).rfind("cufftw", 0) == 0) continue;   // skip the FFTW shim
            if (LoadLibraryA((dir + "\\" + fd.cFileName).c_str())) { FindClose(h); return; }
        } while (FindNextFileA(h, &fd));
        FindClose(h);
    }
}
#endif

static bool device_present() {
    int cnt = 0;
    return cudaGetDeviceCount(&cnt) == cudaSuccess && cnt > 0;
}

#if defined(_WIN32)
static bool cufft_loads() {
    __try {
        cufftHandle p;
        if (cufftCreate(&p) != CUFFT_SUCCESS) return false;
        cufftDestroy(p);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;   // cufft64_*.dll not present -> delay-load raised; fall back to CPU
    }
}
#else
static bool cufft_loads() {
    cufftHandle p;
    if (cufftCreate(&p) != CUFFT_SUCCESS) return false;
    cufftDestroy(p);
    return true;
}
#endif

// True only when an NVIDIA device AND a loadable cuFFT are both present.
bool cuda_backend_available() {
#if defined(_WIN32)
    preload_cufft();
#endif
    return device_present() && cufft_loads();
}

} // namespace nsp
