// OpenNest — standalone C++ console example (no Rhino).
//
// Runs the 8 examples from the docs site (the same set as the compas_nest Python examples), through
// the header-only opennest.hpp binding which mirrors that Python API: build a nest_geo of parts +
// a nest_sheets, call an engine, read the placed (transformed) outlines. See docs/api/cpp/.

#include "opennest.hpp"
#include <chrono>
#include <cstdio>
#include <thread>

using namespace opennest;

// A couple of reusable shapes.
static Ring rect(double w, double h) { return {{0, 0}, {w, 0}, {w, h}, {0, h}}; }
static Point centroid(const Ring& r) {
    double x = 0, y = 0; for (auto& p : r) { x += p.x; y += p.y; } return {x / r.size(), y / r.size()};
}

int main() {
    try {
        // 01 · Collision — physics nest, parts (one with a hole) into a sheet (with a hole).
        {
            nest_geo geo;
            geo.add_part(rect(30, 12), {}, 4);
            geo.add_part(rect(20, 20), {rect(8, 8) /*hole, shifted below*/}, 3);
            geo.parts.back().holes[0] = {{6, 6}, {14, 6}, {14, 14}, {6, 14}};
            nest_sheets sheets;
            sheets.add_sheet(rect(120, 120), {{{50, 50}, {65, 50}, {65, 65}, {50, 65}}});
            nest_result r = opennest_collision{}.solve(geo, sheets);
            printf("01 collision    : placed %zu instance(s) on %d sheet(s)\n", r.placed().size(), r.n_sheets);
        }

        // 02 · NFP + GA — the NFP + genetic-algorithm engine (triangles + holes).
        {
            nest_geo geo;
            geo.add_part(rect(30, 12), {}, 4);
            geo.add_part(rect(20, 20), {{{6, 6}, {14, 6}, {14, 14}, {6, 14}}}, 2);
            geo.add_part({{0, 0}, {22, 0}, {0, 22}}, {}, 4);
            nest_sheets sheets;
            sheets.add_sheet(rect(120, 120), {{{50, 50}, {65, 50}, {65, 65}, {50, 65}}});
            nest_result r = opennest_nfp{20, 8, 7}.solve(geo, sheets);
            printf("02 nfp+ga       : placed %zu instance(s), fitness %.3f\n", r.placed().size(), r.fitness);
        }

        // 03 · Live animation — run the NFP engine on a thread; poll the evolving best layout.
        {
            nest_geo geo;
            for (int i = 0; i < 12; ++i) geo.add_part(rect(20 + i % 4 * 6, 14), {}, 1);
            nest_sheets sheets = nest_sheets::from_size(200, 200, 1);
            auto handle = opennest_nfp{200, 8, 7}.start(geo, sheets);
            for (int frame = 0; frame < 3 && handle->running(); ++frame) {
                std::this_thread::sleep_for(std::chrono::milliseconds(120));
                nest_result snap = handle->poll();
                printf("03 live         : gen %lld, fitness %.3f, placed %zu so far\n",
                       handle->progress(), handle->fitness(), snap.placed().size());
            }
            handle->cancel();
            handle->join();
        }

        // 04 · Clearance offset — grow parts / shrink sheets, then nest on the offset geometry.
        {
            nest_geo geo;
            geo.add_part(rect(30, 12), {}, 6);
            geo.add_part(rect(24, 24), {}, 4);
            nest_sheets sheets = nest_sheets::from_size(120, 120, 1);
            nest_geo    g = offset_geo(geo, 1.0);
            nest_sheets s = offset_sheets(sheets, 1.0);
            nest_result r = opennest_collision{}.solve(g, s);
            printf("04 offset       : placed %zu instance(s) with 1.0 clearance\n", r.placed().size());
        }

        // 05 · Attributes — carry a point at each part's centroid; read it at the placed pose.
        {
            Ring a = rect(30, 12), b = rect(20, 20);
            nest_geo geo;
            geo.add_part(a, {}, 4, {{centroid(a)}});   // attribute = a 1-point ring at the centroid
            geo.add_part(b, {}, 4, {{centroid(b)}});
            nest_sheets sheets; sheets.add_sheet(rect(120, 120));
            nest_result r = opennest_collision{}.solve(geo, sheets);
            int with_attr = 0;
            for (auto& grp : r.placed_polylines())
                for (auto& part : grp.parts) with_attr += !part.attributes.empty();
            printf("05 attributes   : %d placed part(s) carry their centroid marker\n", with_attr);
        }

        // 06 · Pack (array) — deterministic grid, a fixed number of columns per row.
        {
            nest_geo geo;
            geo.add_part(rect(30, 12), {}, 6);
            geo.add_part(rect(20, 20), {{{6, 6}, {14, 6}, {14, 14}, {6, 14}}}, 6);
            nest_result r = pack(geo, /*columns*/ 5, /*gap_x*/ 1.0, /*gap_y*/ 1.0);
            printf("06 pack array   : laid out %zu instance(s) in rows of 5\n", r.placed().size());
        }

        // 07 · Pack (distance) — fill a row up to max_width, then wrap.
        {
            nest_geo geo;
            geo.add_part(rect(30, 12), {}, 6);
            geo.add_part(rect(20, 20), {}, 6);
            nest_result r = pack(geo, /*columns*/ 0, /*gap_x*/ 5.0, /*gap_y*/ 5.0, /*max_width*/ 120.0);
            printf("07 pack dist    : laid out %zu instance(s), wrapping at width 120\n", r.placed().size());
        }

        // 08 · Text — render a label to single-stroke engraving polylines (OpenNest VDA font).
        {
            std::vector<Ring> strokes = text_to_polylines("compas_nest\n0 1 2 3", /*height*/ 10.0);
            size_t pts = 0; for (auto& s : strokes) pts += s.size();
            printf("08 text         : %zu stroke(s), %zu point(s)\n", strokes.size(), pts);
        }

        return 0;
    } catch (const std::exception& e) {
        printf("error: %s\n", e.what());
        return 1;
    }
}
