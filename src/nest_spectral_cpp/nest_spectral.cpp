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

using namespace nsp;

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
    int    tray    = 32;
    int    orient  = 1;
    int    nitems  = 24;
    size_t threads = 1;
    for (int i = 1; i < argc; i++) {
        std::string a = argv[i];
        auto next = [&](int def) { return (i + 1 < argc) ? std::atoi(argv[++i]) : def; };
        if      (a == "--tray")    tray    = next(tray);
        else if (a == "--orient")  orient  = next(orient);
        else if (a == "--items")   nitems  = next(nitems);
        else if (a == "--threads") threads = (size_t)next((int)threads);
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
    return (tray_vol == placed_vol) ? 0 : 1;
}
