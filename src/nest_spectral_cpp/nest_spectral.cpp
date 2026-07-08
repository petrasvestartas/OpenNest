// nest_spectral CLI — self-test for the CPU spectral packer (no GPU). Builds synthetic voxel shapes
// (boxes + L-pieces), packs them into a tray via the FFT-correlation pipeline, and reports placement
// count + density. Proves the cuFFT->pocketfft port end-to-end. Usage:
//     nest_spectral [--tray T] [--orient {1,4,6,24}] [--items N] [--threads K]
#include <iostream>
#include <vector>
#include <string>
#include <chrono>
#include "spectral/grid.hpp"
#include "spectral/packer.hpp"
#include "spectral/voxelize.hpp"

using namespace nsp;

// An axis-aligned box mesh [0,a]x[0,b]x[0,c] as 8 verts + 12 triangles (winding-agnostic voxelizer).
static void make_box_mesh(double a, double b, double c, std::vector<double>& V, std::vector<int>& T) {
    V = {0,0,0, a,0,0, a,b,0, 0,b,0, 0,0,c, a,0,c, a,b,c, 0,b,c};
    T = {0,1,2, 0,2,3,  4,6,5, 4,7,6,  0,4,5, 0,5,1,
         3,2,6, 3,6,7,  0,3,7, 0,7,4,  1,5,6, 1,6,2};
}

static int mesh_test() {
    std::vector<double> V; std::vector<int> T;
    make_box_mesh(10, 10, 10, V, T);
    VoxelizeResult r = voxelize_mesh(V.data(), (int)V.size() / 3, T.data(), (int)T.size() / 3, 1.0);
    long long occ = r.grid.occupied();
    long long expect = (long long)r.grid.nx * r.grid.ny * r.grid.nz;
    std::cout << "meshtest: 10^3 cube @ pitch 1 -> grid " << r.grid.nx << "x" << r.grid.ny << "x" << r.grid.nz
              << "  occupied=" << occ << "/" << expect
              << (occ == expect ? "  OK (solid fill)\n" : "  FILL ERROR\n");
    // Pack a few voxelized boxes of different sizes to exercise the mesh->pack path.
    std::vector<Grid> items;
    const double bx[][3] = {{10,8,6}, {6,6,12}, {14,4,4}, {8,8,8}};
    for (int n = 0; n < 8; n++) {
        const double* s = bx[n % 4];
        make_box_mesh(s[0], s[1], s[2], V, T);
        items.push_back(voxelize_mesh(V.data(), (int)V.size()/3, T.data(), (int)T.size()/3, 1.0).grid);
    }
    SpectralParams p; p.num_orientations = 6; p.nthreads = 4;
    Grid tray;
    auto places = pack(items, 30, 30, 30, p, tray);
    long long np = 0, pv = 0;
    for (auto& pl : places) if (pl.placed) { np++; pv += items[pl.item_index].occupied(); }
    std::cout << "meshtest pack: placed " << np << "/8  density " << (int)(100*packing_density(tray)+0.5)
              << "%  integrity " << (tray.occupied()==pv ? "OK" : "FAIL") << "\n";
    return (occ == expect && tray.occupied() == pv) ? 0 : 1;
}

static Grid make_box(int a, int b, int c) {
    Grid g(a, b, c);
    for (auto& v : g.data) v = 1;
    return g;
}

// An L-shaped prism: a*b*c box with one quadrant column removed (a non-convex test piece).
static Grid make_L(int a, int b, int c) {
    Grid g(a, b, c);
    for (int i = 0; i < a; i++)
        for (int j = 0; j < b; j++)
            for (int k = 0; k < c; k++)
                if (!(i >= a / 2 && j >= b / 2)) g(i, j, k) = 1;   // notch out the +x+y corner column
    return g;
}

int main(int argc, char** argv) {
    int    tray      = 32;
    int    orient    = 1;
    int    nitems    = 24;
    size_t threads   = 1;
    int    clearance = 0;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        auto next = [&](int def) { return (i + 1 < argc) ? std::atoi(argv[++i]) : def; };
        if      (a == "--tray")      tray      = next(tray);
        else if (a == "--orient")    orient    = next(orient);
        else if (a == "--items")     nitems    = next(nitems);
        else if (a == "--threads")   threads   = (size_t)next((int)threads);
        else if (a == "--clearance") clearance = next(clearance);
        else if (a == "--meshtest")  return mesh_test();
    }

    // Deterministic synthetic item set: cycle through a few box/L sizes.
    std::vector<Grid> items;
    const int sizes[][3] = {{8, 6, 5}, {7, 7, 4}, {10, 4, 4}, {6, 6, 6}, {9, 5, 3}, {5, 5, 8}};
    for (int n = 0; n < nitems; n++) {
        const int* s = sizes[n % 6];
        items.push_back((n % 3 == 0) ? make_L(s[0], s[1], s[2]) : make_box(s[0], s[1], s[2]));
    }

    SpectralParams p;
    p.num_orientations = orient;
    p.nthreads = threads;
    p.clearance_voxels = clearance;

    std::cout << "nest_spectral self-test: " << nitems << " items into a " << tray << "^3 tray, "
              << orient << " orientation(s), " << threads << " FFT thread(s)\n";

    auto t0 = std::chrono::high_resolution_clock::now();
    Grid trayg;
    std::vector<ItemPlacement> places = pack(items, tray, tray, tray, p, trayg);
    auto t1 = std::chrono::high_resolution_clock::now();
    double secs = std::chrono::duration<double>(t1 - t0).count();

    // Validate: tray occupancy must equal the summed volume of placed items (no overlap, no loss).
    long long placed_vol = 0, n_placed = 0;
    for (const auto& pl : places)
        if (pl.placed) { n_placed++; placed_vol += items[pl.item_index].occupied(); }
    long long tray_vol = trayg.occupied();

    std::cout << "placed " << n_placed << "/" << nitems
              << "  density " << (int)(100.0 * packing_density(trayg) + 0.5) << "%"
              << "  (" << secs << " s)\n";
    std::cout << "integrity: tray_occupied=" << tray_vol << " expected=" << placed_vol
              << (tray_vol == placed_vol ? "  OK (no overlap)\n" : "  MISMATCH!\n");

    // With a clearance, verify the gap: no two DIFFERENT parts (distinct tray tags) may sit within
    // `clearance` voxels (Chebyshev) of each other — matching the box dilation used by the packer.
    bool gap_ok = true;
    if (clearance > 0) {
        for (int i = 0; i < trayg.nx && gap_ok; i++)
            for (int j = 0; j < trayg.ny && gap_ok; j++)
                for (int k = 0; k < trayg.nz && gap_ok; k++) {
                    int t = trayg(i, j, k);
                    if (t <= 0) continue;
                    for (int di = -clearance; di <= clearance && gap_ok; di++)
                        for (int dj = -clearance; dj <= clearance && gap_ok; dj++)
                            for (int dk = -clearance; dk <= clearance && gap_ok; dk++) {
                                int a = i + di, b = j + dj, c = k + dk;
                                if (a < 0 || b < 0 || c < 0 || a >= trayg.nx || b >= trayg.ny || c >= trayg.nz) continue;
                                int u = trayg(a, b, c);
                                if (u > 0 && u != t) gap_ok = false;
                            }
                }
        std::cout << "clearance " << clearance << "vx: "
                  << (gap_ok ? "OK (parts kept >= gap apart)\n" : "VIOLATED!\n");
    }
    return (tray_vol == placed_vol && gap_ok) ? 0 : 1;
}
