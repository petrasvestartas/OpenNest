// OpenNest — standalone C++ console example (no Rhino, no C#).
//
// Calls the two native nesting engines through their plain C ABI:
//   nfp_nest   (nfp_nest.dll / nfp_nest.dylib / nfp_nest.so)        NFP + genetic algorithm
//   np_nest    (nest_physics.dll / nest_physics.dylib / .so)        physics / overlap-relaxation
//
// It loads the engine library at RUNTIME (LoadLibrary / dlopen), so it links against nothing and
// runs on every OS as long as the engine library sits next to the executable (the CMake superbuild
// copies it there). Linking the import library instead is equally valid — the call site is identical,
// only the symbol lookup differs.
//
// Geometry crosses as flat arrays: one polygon = a vertex count + a flat x,y list; a list of polygons
// = a count + a lengths array + one concatenated xy buffer. See docs/api/cpp.md.

#include <cstdio>
#include <vector>

#if defined(_WIN32)
  #include <windows.h>
  static void* load_lib(const char* n) { return (void*)LoadLibraryA(n); }
  static void* load_sym(void* h, const char* n) { return (void*)GetProcAddress((HMODULE)h, n); }
  static const char* NFP_LIB = "nfp_nest.dll";
  static const char* NP_LIB  = "nest_physics.dll";
#else
  #include <dlfcn.h>
  static void* load_lib(const char* n) { return dlopen(n, RTLD_NOW); }
  static void* load_sym(void* h, const char* n) { return dlsym(h, n); }
  #if defined(__APPLE__)
    static const char* NFP_LIB = "./nfp_nest.dylib";
    static const char* NP_LIB  = "./nest_physics.dylib";
  #else
    static const char* NFP_LIB = "./nfp_nest.so";
    static const char* NP_LIB  = "./nest_physics.so";
  #endif
#endif

// --- C ABI parameter blocks (verbatim from the engine headers; POD, natural x64 alignment) ---
struct NfpParams {
    int    placementType, rotations, mutationRate, populationSize, seed;
    double curveTolerance, clipperScale, spacing, sheetSpacing, rotationLimit;
    int    useHoles, exploreConcave, clipByHull, clipByRects, simplify,
           mode, generations, numSeeds, useParallel;
    double timeBudgetSecs;
    int    maxSheets, edgeSamples, compactionPasses, tryAllRotations, exactNfp;
};
struct NpParams {
    int       num_rotations;
    double    spacing, simplify_tolerance;
    int       seed;
    double    time_budget_secs;
    long long iter_budget;
    int       iter_mode, max_sheets, n_starts, part_holes_mode, pole_max, final_compact, fit_mode;
};

using nfp_nest_fn = int (*)(int, const int*, const double*, const int*, const int*, const int*, const double*,
                            int, const int*, const double*, const int*, const int*, const double*,
                            const NfpParams*, double*, double*, double*, int*, int*, int*, double*);
using np_nest_fn  = int (*)(int, const int*, const double*, int, const int*, const double*,
                            const int*, const int*, const double*, const int*, const int*, const double*,
                            const NpParams*, double*, double*, double*, int*, int*);

static void add_rect(std::vector<int>& vc, std::vector<double>& xy, double w, double h) {
    vc.push_back(4);
    const double pts[8] = {0, 0, w, 0, w, h, 0, h};
    for (double v : pts) xy.push_back(v);
}

int main() {
    // ---- build the problem: 3 rectangles + a triangle as parts, one 150x150 sheet ----
    std::vector<int> pvc;  std::vector<double> pxy;
    add_rect(pvc, pxy, 30, 12);
    add_rect(pvc, pxy, 20, 20);
    add_rect(pvc, pxy, 40, 8);
    pvc.push_back(3); { const double t[6] = {0, 0, 24, 0, 0, 24}; for (double v : t) pxy.push_back(v); }
    const int part_count = (int)pvc.size();
    std::vector<int> pqty(part_count, 3);   // 3 copies of each part
    std::vector<int> phc(part_count, 0);    // no part holes

    std::vector<int> svc;  std::vector<double> sxy;
    add_rect(svc, sxy, 150, 150);
    const int sheet_count = 1;
    std::vector<int> shc(sheet_count, 0);   // no sheet holes

    int rc = 0;

    // ---- nfp_nest (NFP + GA): output has one slot per INSTANCE = sum(quantities) ----
    int instances = 0; for (int q : pqty) instances += q;
    if (void* lib = load_lib(NFP_LIB)) {
        auto nfp_nest = (nfp_nest_fn)load_sym(lib, "nfp_nest");
        NfpParams p{}; p.placementType = 1; p.rotations = 4; p.mutationRate = 10; p.populationSize = 10;
        p.seed = 1; p.curveTolerance = 0.3; p.clipperScale = 1e7; p.mode = 1; p.generations = 10; p.useParallel = 1;
        std::vector<double> tx(instances), ty(instances), ang(instances);
        std::vector<int> sid(instances), pidx(instances);
        int n_sheets = 0; double fitness = 0;
        int placed = nfp_nest(part_count, pvc.data(), pxy.data(), pqty.data(), phc.data(), nullptr, nullptr,
                              sheet_count, svc.data(), sxy.data(), shc.data(), nullptr, nullptr,
                              &p, tx.data(), ty.data(), ang.data(), sid.data(), pidx.data(), &n_sheets, &fitness);
        printf("nfp_nest : placed %d/%d instances on %d sheet(s), fitness %.3f\n", placed, instances, n_sheets, fitness);
        for (int i = 0; i < instances; ++i)
            printf("   part %d -> sheet %d  (%7.2f, %7.2f) @ %5.1f deg\n", pidx[i], sid[i], tx[i], ty[i], ang[i]);
    } else {
        printf("nfp_nest : could not load %s (build the engines via the superbuild first)\n", NFP_LIB);
        rc = 1;
    }

    // ---- np_nest (physics): output has one slot per PART (original order) ----
    if (void* lib = load_lib(NP_LIB)) {
        auto np_nest = (np_nest_fn)load_sym(lib, "np_nest");
        NpParams q{}; q.num_rotations = 16; q.seed = 1; q.iter_mode = 0; q.time_budget_secs = 2.0;
        q.n_starts = 1; q.max_sheets = 6; q.fit_mode = 0;
        std::vector<double> tx(part_count), ty(part_count), ang(part_count);
        std::vector<int> sid(part_count); int n_sheets = 0;
        int ret = np_nest(part_count, pvc.data(), pxy.data(), sheet_count, svc.data(), sxy.data(),
                          shc.data(), nullptr, nullptr, phc.data(), nullptr, nullptr,
                          &q, tx.data(), ty.data(), ang.data(), sid.data(), &n_sheets);
        printf("np_nest  : rc=%d, %d sheet(s)\n", ret, n_sheets);
        for (int i = 0; i < part_count; ++i)
            printf("   part %d -> sheet %d  (%7.2f, %7.2f) @ %6.3f rad\n", i, sid[i], tx[i], ty[i], ang[i]);
    } else {
        printf("np_nest  : could not load %s (build the engines via the superbuild first)\n", NP_LIB);
        rc = 1;
    }

    return rc;
}
