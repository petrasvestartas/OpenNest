#pragma once
// opennest.hpp — a small, header-only C++ API over the native OpenNest engines.
//
// It mirrors the compas_nest Python binding: build a `nest_geo` of parts and a `nest_sheets`, call an
// engine's .solve(geo, sheets), then read the placements (and the placed, transformed outlines):
//
//   #include "opennest.hpp"
//   using namespace opennest;
//
//   nest_geo geo;
//   geo.add_part({{0,0},{30,0},{30,12},{0,12}}, /*holes*/{}, /*copies*/4);
//   geo.add_part({{0,0},{20,0},{20,20},{0,20}}, {{{6,6},{14,6},{14,14},{6,14}}}, 3);
//
//   nest_sheets sheets;
//   sheets.add_sheet({{0,0},{120,0},{120,120},{0,120}}, {{{50,50},{65,50},{65,65},{50,65}}});
//
//   nest_result r = opennest_collision{}.solve(geo, sheets);   // physics   (Python: opennest_collision)
//   // nest_result r = opennest_nfp{}.solve(geo, sheets);      // NFP + GA  (Python: opennest)
//   for (auto& g : r.placed_polylines())
//       for (auto& part : g.parts) { /* part.shape.outer, part.shape.holes are placed (transformed) */ }
//
// The native engine libraries (nfp_nest, nest_physics) are loaded at RUNTIME and must sit next to the
// executable (the CMake superbuild copies them there) — there is no link step.
//
// `placement.angle` is normalized to RADIANS for BOTH engines (the underlying C ABI returns degrees for
// NFP and radians for physics; this header hides that difference). See docs/api/cpp.md for the raw ABI.

#include <cmath>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <vector>

#if defined(_WIN32)
  #include <windows.h>
#else
  #include <dlfcn.h>
#endif

namespace opennest {

struct Point { double x = 0, y = 0; };
using Ring = std::vector<Point>;                  // a ring as a list of points (not closed)
struct Polygon { Ring outer; std::vector<Ring> holes; };

// ---- input: parts & sheets -----------------------------------------------------------------------
class nest_geo {
public:
    // outline = outer ring; holes = interior rings; copies = how many of this part to nest.
    void add_part(const Ring& outline, const std::vector<Ring>& holes = {}, int copies = 1) {
        parts.push_back({outline, holes});
        quantities.push_back(copies < 1 ? 1 : copies);
    }
    std::vector<Polygon> parts;
    std::vector<int>     quantities;
};

class nest_sheets {
public:
    void add_sheet(const Ring& outline, const std::vector<Ring>& holes = {}) {
        items.push_back({outline, holes});
    }
    // A row of `count` identical W x H rectangular sheets, spaced by `gap`.
    static nest_sheets from_size(double w, double h, int count = 1, double gap = 0.0) {
        nest_sheets s;
        for (int i = 0; i < count; ++i) {
            double x = i * (w + gap);
            s.add_sheet({{x, 0}, {x + w, 0}, {x + w, h}, {x, h}});
        }
        return s;
    }
    std::vector<Polygon> items;
};

// ---- output --------------------------------------------------------------------------------------
struct Placement {
    int    part_index = -1;   // index of the source part in nest_geo.parts
    int    sheet_id   = -1;   // sheet it landed on (-1 = unplaced)
    double tx = 0, ty = 0;    // sheet-local translation
    double angle = 0;         // rotation in RADIANS
    bool placed() const { return sheet_id >= 0; }
};
struct PlacedPart  { int part_index; Polygon shape; };   // outline + holes already transformed
struct PlacedGroup { int sheet_id;   std::vector<PlacedPart> parts; };

class nest_result {
public:
    std::vector<Placement> placements;
    int             n_sheets = 0;
    double          fitness  = 0.0;
    const nest_geo* geo      = nullptr;   // source parts, for placed_polylines()

    std::vector<Placement> placed() const {
        std::vector<Placement> out;
        for (const auto& p : placements) if (p.placed()) out.push_back(p);
        return out;
    }

    // Placed (transformed) outlines + holes, grouped per sheet — mirrors Python's placed_polylines().
    std::vector<PlacedGroup> placed_polylines() const {
        std::vector<PlacedGroup> groups;
        auto group_for = [&](int sid) -> PlacedGroup& {
            for (auto& g : groups) if (g.sheet_id == sid) return g;
            groups.push_back({sid, {}});
            return groups.back();
        };
        for (const auto& p : placements) {
            if (!p.placed() || !geo) continue;
            const Polygon& src = geo->parts[p.part_index];
            PlacedPart pp; pp.part_index = p.part_index;
            pp.shape.outer = xform(src.outer, p);
            for (const auto& h : src.holes) pp.shape.holes.push_back(xform(h, p));
            group_for(p.sheet_id).parts.push_back(std::move(pp));
        }
        return groups;
    }

private:
    static Ring xform(const Ring& r, const Placement& p) {
        Ring out; out.reserve(r.size());
        const double c = std::cos(p.angle), s = std::sin(p.angle);
        for (const auto& pt : r) out.push_back({pt.x * c - pt.y * s + p.tx, pt.x * s + pt.y * c + p.ty});
        return out;
    }
};

// ---- internals: C ABI structs, runtime loader, flattening ----------------------------------------
namespace detail {

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

inline void* load_symbol(const char* const* lib_names, const char* symbol) {
    for (const char* const* n = lib_names; *n; ++n) {
#if defined(_WIN32)
        if (HMODULE h = LoadLibraryA(*n)) {
            if (void* s = (void*)GetProcAddress(h, symbol)) return s;
        }
#else
        if (void* h = dlopen(*n, RTLD_NOW)) {
            if (void* s = dlsym(h, symbol)) return s;
        }
#endif
    }
    throw std::runtime_error(std::string("opennest: could not load symbol '") + symbol +
                             "' — is the engine library next to the executable?");
}

inline nfp_nest_fn nfp_nest() {
    static const char* names[] = {"nfp_nest.dll", "./nfp_nest.dylib", "nfp_nest.dylib",
                                  "./nfp_nest.so", "nfp_nest.so", nullptr};
    static auto fn = (nfp_nest_fn)load_symbol(names, "nfp_nest");
    return fn;
}
inline np_nest_fn np_nest() {
    static const char* names[] = {"nest_physics.dll", "./nest_physics.dylib", "nest_physics.dylib",
                                  "./nest_physics.so", "nest_physics.so", nullptr};
    static auto fn = (np_nest_fn)load_symbol(names, "np_nest");
    return fn;
}

// Append one polygon set (parts or sheets) into the flat (counts, xy, hole-counts, ...) arrays.
struct FlatPolys {
    std::vector<int>    vcount;       // outer vertex count per polygon
    std::vector<double> xy;           // outer points, x,y,...
    std::vector<int>    hole_count;   // holes per polygon
    std::vector<int>    hole_vcount;  // vertex count per hole
    std::vector<double> hole_xy;      // hole points
};
inline void push_ring(std::vector<int>& vc, std::vector<double>& xy, const Ring& r) {
    vc.push_back((int)r.size());
    for (const auto& p : r) { xy.push_back(p.x); xy.push_back(p.y); }
}
inline void push_poly(FlatPolys& f, const Polygon& poly) {
    push_ring(f.vcount, f.xy, poly.outer);
    f.hole_count.push_back((int)poly.holes.size());
    for (const auto& h : poly.holes) push_ring(f.hole_vcount, f.hole_xy, h);
}
inline const int*    ip(const std::vector<int>& v)    { return v.empty() ? nullptr : v.data(); }
inline const double* dp(const std::vector<double>& v) { return v.empty() ? nullptr : v.data(); }

} // namespace detail

// ---- engines -------------------------------------------------------------------------------------

// Physics / overlap-relaxation engine (Python: opennest_collision). Honors part copies by expanding them.
struct opennest_collision {
    int    iterations    = 2000;   // relaxation rounds (deterministic)
    int    num_rotations = 64;     // orientations sampled per part
    int    seed          = 100;
    double spacing       = 0.0;
    int    n_starts      = 1;       // multi-start: keep the densest of N seeds
    int    pole_max      = 16;
    int    final_compact = 2;
    int    fit_mode      = 0;       // 0 = fewest sheets; 1 = one sheet, max fill
    int    part_holes_mode = 0;     // 1 = nest parts into other parts' holes
    int    max_sheets    = 0;
    double simplify_tolerance = 0.0;

    nest_result solve(const nest_geo& geo, const nest_sheets& sheets) const {
        using namespace detail;
        // np_nest has no quantity concept -> expand each part `copies` times, remember the source index.
        FlatPolys parts;
        std::vector<int> src;
        for (size_t i = 0; i < geo.parts.size(); ++i)
            for (int c = 0; c < geo.quantities[i]; ++c) { push_poly(parts, geo.parts[i]); src.push_back((int)i); }
        const int part_count = (int)src.size();

        FlatPolys sh;
        for (const auto& s : sheets.items) push_poly(sh, s);
        const int sheet_count = (int)sheets.items.size();

        NpParams q{}; q.num_rotations = num_rotations; q.spacing = spacing; q.simplify_tolerance = simplify_tolerance;
        q.seed = seed; q.iter_mode = 1; q.iter_budget = iterations; q.max_sheets = max_sheets; q.n_starts = n_starts;
        q.part_holes_mode = part_holes_mode; q.pole_max = pole_max; q.final_compact = final_compact; q.fit_mode = fit_mode;

        std::vector<double> tx(part_count), ty(part_count), ang(part_count);
        std::vector<int> sid(part_count); int n_sheets = 0;
        np_nest()(part_count, ip(parts.vcount), dp(parts.xy), sheet_count, ip(sh.vcount), dp(sh.xy),
                  ip(sh.hole_count), ip(sh.hole_vcount), dp(sh.hole_xy),
                  ip(parts.hole_count), ip(parts.hole_vcount), dp(parts.hole_xy),
                  &q, tx.data(), ty.data(), ang.data(), sid.data(), &n_sheets);

        nest_result r; r.geo = &geo; r.n_sheets = n_sheets;
        for (int i = 0; i < part_count; ++i)
            r.placements.push_back({src[i], sid[i], tx[i], ty[i], ang[i]});   // np angle is radians
        return r;
    }
};

// NFP + genetic-algorithm engine (Python: opennest). Expands copies internally; returns degrees.
struct opennest_nfp {
    int    generations   = 10;
    int    rotations     = 8;
    int    seed          = 30;
    double spacing       = 0.0;
    bool   use_holes     = true;
    bool   try_all_rotations = false;
    bool   exact_nfp     = false;
    int    mode          = 1;
    double clipper_scale = 1e7;
    double curve_tolerance = 0.3;

    nest_result solve(const nest_geo& geo, const nest_sheets& sheets) const {
        using namespace detail;
        FlatPolys parts;
        std::vector<int> qty;
        for (size_t i = 0; i < geo.parts.size(); ++i) { push_poly(parts, geo.parts[i]); qty.push_back(geo.quantities[i]); }
        const int part_count = (int)geo.parts.size();

        FlatPolys sh;
        for (const auto& s : sheets.items) push_poly(sh, s);
        const int sheet_count = (int)sheets.items.size();

        int instances = 0; for (int q : qty) instances += q;

        NfpParams p{}; p.placementType = 1; p.rotations = rotations; p.mutationRate = 10; p.populationSize = 10;
        p.seed = seed; p.curveTolerance = curve_tolerance; p.clipperScale = clipper_scale; p.spacing = spacing;
        p.rotationLimit = 360.0; p.useHoles = use_holes ? 1 : 0; p.mode = mode; p.generations = generations;
        p.numSeeds = 4; p.useParallel = 1; p.tryAllRotations = try_all_rotations ? 1 : 0; p.exactNfp = exact_nfp ? 1 : 0;

        std::vector<double> tx(instances), ty(instances), ang(instances);
        std::vector<int> sid(instances), pidx(instances); int n_sheets = 0; double fitness = 0;
        nfp_nest()(part_count, ip(parts.vcount), dp(parts.xy), qty.data(),
                   ip(parts.hole_count), ip(parts.hole_vcount), dp(parts.hole_xy),
                   sheet_count, ip(sh.vcount), dp(sh.xy), ip(sh.hole_count), ip(sh.hole_vcount), dp(sh.hole_xy),
                   &p, tx.data(), ty.data(), ang.data(), sid.data(), pidx.data(), &n_sheets, &fitness);

        nest_result r; r.geo = &geo; r.n_sheets = n_sheets; r.fitness = fitness;
        const double deg2rad = 3.14159265358979323846 / 180.0;
        for (int i = 0; i < instances; ++i)
            r.placements.push_back({pidx[i], sid[i], tx[i], ty[i], ang[i] * deg2rad});   // normalize deg -> rad
        return r;
    }
};

} // namespace opennest
