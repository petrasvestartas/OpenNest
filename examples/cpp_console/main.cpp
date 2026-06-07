// OpenNest — standalone C++ console example (no Rhino).
//
// Uses the header-only opennest.hpp binding, which mirrors the compas_nest Python API:
// build a nest_geo of parts + a nest_sheets, call an engine's .solve(), read the placements.
// (opennest.hpp wraps the native engines' C ABI; see docs/api/cpp.md for the raw ABI.)

#include "opennest.hpp"
#include <cstdio>

using namespace opennest;

int main() {
    try {
        // 1) parts (one with a hole) and a sheet (with a hole) — cf. examples/01_collision_viewer.py
        nest_geo geo;
        geo.add_part({{0, 0}, {30, 0}, {30, 12}, {0, 12}}, /*holes*/ {}, /*copies*/ 4);
        geo.add_part({{0, 0}, {20, 0}, {20, 20}, {0, 20}},
                     /*holes*/ {{{6, 6}, {14, 6}, {14, 14}, {6, 14}}}, /*copies*/ 3);

        nest_sheets sheets;
        sheets.add_sheet({{0, 0}, {120, 0}, {120, 120}, {0, 120}},
                         /*holes*/ {{{50, 50}, {65, 50}, {65, 65}, {50, 65}}});

        // 2) nest with the collision (physics) engine — swap to opennest_nfp{} for NFP + GA
        nest_result result = opennest_collision{}.solve(geo, sheets);

        // 3) read the result: placed (transformed) outlines, grouped per sheet
        printf("placed %zu instances on %d sheet(s)\n", result.placed().size(), result.n_sheets);
        for (const auto& group : result.placed_polylines())
            for (const auto& part : group.parts)
                printf("  sheet %d: part %d  outline %zu pts, %zu hole(s)\n",
                       group.sheet_id, part.part_index,
                       part.shape.outer.size(), part.shape.holes.size());

        return result.placed().empty() ? 1 : 0;
    } catch (const std::exception& e) {
        printf("error: %s\n", e.what());
        return 1;
    }
}
