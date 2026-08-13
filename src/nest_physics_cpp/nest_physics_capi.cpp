// Implementation of the nest_physics C ABI (see nest_physics_capi.h). Drives the SAME solver as the
// CLI exe via solver/driver.hpp (penetration-depth overlap proxy, USE_DEPTH_PROXY=1 by default).
#include "solver/driver.hpp"
#include "nest_physics_capi.h"
#include <vector>
#include <cmath>
#include <thread>
#include <algorithm>
#include <cstdlib>
#include <atomic>
#include <optional>

using namespace nest;

// Sheet sizes are UNIFORM when every entry matches the first (or none were supplied). Three post-passes
// below RENUMBER sheets to close gaps left by cross-sheet relocation; that is only safe when the sheets
// are interchangeable. With mixed sizes, moving a part from sheet 3's label to sheet 1's would hand the
// host the wrong sheet origin, so the renumbering is skipped there and an emptied sheet is simply
// emitted empty.
static bool dims_uniform(const SheetDims* d) {
    if (!d || d->size() < 2) return true;
    for (usize i = 1; i < d->size(); ++i)
        if (std::fabs((*d)[i].first - (*d)[0].first) > 1e-4f || std::fabs((*d)[i].second - (*d)[0].second) > 1e-4f)
            return false;
    return true;
}

// ...but SIZE ALONE IS NOT ENOUGH. Sheet keep-out holes are indexed BY SHEET ID (per_sheet_holes), so
// same-size sheets with DIFFERENT holes are not interchangeable either: renumbering relabels a part onto
// a sheet whose keep-outs it was never checked against. Measured before this guard existed: three
// identical 1000x1000 sheets, sheet 0 carrying a 96% keep-out, 10 rectangles -> the one part on sheet 0
// was demoted, the renumber pulled sheets 1,2 down to 0,1, and 9 parts landed on the keep-out
// (85-100% of each part inside it) while np_last_quality still said overlap_pairs=0 oob=0.
// Identical hole sets (the ordinary "N copies of the same sheet" case) stay interchangeable, so the
// renumbering still does its job there.
static bool holes_identical(const std::vector<Polygon>& a, const std::vector<Polygon>& b) {
    if (a.size() != b.size()) return false;
    for (usize i = 0; i < a.size(); ++i) {
        if (a[i].n_vertices() != b[i].n_vertices()) return false;
        for (usize k = 0; k < a[i].n_vertices(); ++k)
            if (std::fabs(a[i].vertices[k].x - b[i].vertices[k].x) > 1e-4f ||
                std::fabs(a[i].vertices[k].y - b[i].vertices[k].y) > 1e-4f) return false;
    }
    return true;
}
static bool sheets_interchangeable(const SheetDims* d, const std::vector<std::vector<Polygon>>* holes) {
    if (!dims_uniform(d)) return false;
    if (!holes || holes->size() < 2) return true;      // no sheet carries a keep-out (or only one sheet)
    for (usize i = 1; i < holes->size(); ++i)
        if (!holes_identical((*holes)[0], (*holes)[i])) return false;
    return true;
}

// dedup consecutive points + drop the closing duplicate (Polygon::create rejects duplicates).
static std::vector<Point> clean_ring(std::vector<Point> pts) {
    if (pts.size() >= 2 && pts.front() == pts.back()) pts.pop_back();
    std::vector<Point> clean;
    for (auto& p : pts) if (clean.empty() || !(clean.back() == p)) clean.push_back(p);
    if (!clean.empty() && clean.size() >= 2 && clean.front() == clean.back()) clean.pop_back();
    return clean;
}

static std::vector<Point> read_poly(const double* xy, int nverts, int& cursor) {
    std::vector<Point> pts;
    pts.reserve((usize)nverts);
    for (int k = 0; k < nverts; ++k) {
        pts.push_back(Point((f32)xy[2 * cursor], (f32)xy[2 * cursor + 1]));
        ++cursor;
    }
    return pts;
}

// Live-preview support: np_poll_layout (called from the host's UI thread) needs the part-centering
// offsets (craw) + the solver-part -> original-input index mapping that np_nest builds. Stash them
// globally for the duration of a solve, guarded by g_live.mtx.
static std::vector<int>   g_poll_orig_index;
static std::vector<Point> g_poll_craw;

// ---- Quality verdict of the LAST completed np_nest (see np_last_quality) -----------------------
// greedy_fill already brute-force-verifies every sheet it emits (Sheets::bf / Sheets::inf / Sheets::ok)
// and the cancelled-snapshot path explicitly returns ok=false with "positions are as-is, may still
// overlap" — but until now none of that crossed the ABI, so the host had no way to tell a clean nest
// from an overlapping one and published both identically. These counters are recomputed on the FINAL
// placements (after every post-pass) so they describe exactly what the host is about to draw.
//
// NOT RE-ENTRANT, BY DESIGN AND ONLY JUST SO. The verdict is PROCESS-GLOBAL: np_last_quality reports
// whichever solve wrote these last, attributed by timing, not by solve. That is safe today only because
// every caller is serialised — the Grasshopper component takes EngineGate.Physics for the whole solve,
// and a batch run's inner solves are sequential — and because the rest of the library (g_live, g_cancel,
// g_iter_pub, the g_* option flags np_nest sets on entry) is process-global for the same reason.
// If OpenNestCollision is ever parallelised across solves, DO NOT just add a mutex here: the verdict must
// become a per-call OUT-PARAM of np_nest, or one solve will publish another's verdict.
static std::atomic<int> g_q_overlap_pairs{0};
static std::atomic<int> g_q_out_of_bounds{0};
static std::atomic<int> g_q_unplaced{0};
static std::atomic<int> g_q_cancelled{0};
// Placements DEMOTED to unplaced by drop_invalid_placements (they hung out of their sheet or sat in a
// sheet hole). They are already inside g_q_unplaced; this breaks out how many of those were "the solver
// put it on a sheet and we took it back off" rather than "it never got a sheet at all", which is the only
// honest thing the host can say about them. See np_last_quality's out_demoted.
static std::atomic<int> g_q_demoted{0};

// ---- HARNESS TEST SEAMS (bench/np_bench.cpp) — COMPILED OUT of a release build --------------------
// The two defects the quality gate exists to catch cannot be produced by the fixed solver on purpose, so
// the only way to regression-test the GATE through the real shipped ABI is to inject them:
//   NP_TEST_POKE=<frac>   shove a hole-nested child out through its host's cavity wall by frac x the
//                         cavity/host width. The displacement is a fraction of the CAVITY WIDTH, so it
//                         only becomes a defect once it exceeds the clearance between child and wall:
//                         with the bench's default --fhole=1.15 (7.5% clearance per side) everything
//                         below frac ~= 0.075 is legitimately inert. Use a snug cavity (--fhole=1.01)
//                         to exercise small displacements. frac >= 1 always escapes.
//   NP_TEST_SHIFT=<frac>  shove the right-most placement +x by frac x its sheet width, so it hangs out
//                         of the sheet. Proves the bounds tolerance (and the demotion it drives) is
//                         relative: the same relative defect must be caught in mm and in m.
//
// UNLIKE the NP_COMPACT / NP_FILL_GAPS / NP_DROP_INVALID dev A-B switches read below, these two make the
// library emit PROVABLY BROKEN GEOMETRY — NP_TEST_POKE=0.5 produces genuinely interpenetrating pairs. A
// released DLL must not carry that seam at all, so they exist only when NP_TEST_SEAMS is defined, which
// CMakeLists does exactly when the np_bench harness is being built (never in the plain library build).
#if defined(NP_TEST_SEAMS)
static f32 g_test_poke = 0.0f;
static f32 g_test_shift = 0.0f;
#endif

// FILL-HOLES post-pass: relocate smaller placed parts INTO larger parts' cavities wherever they fit
// with no overlap. The width-minimizing relaxation only uses a hole when it shrinks the strip; on a
// roomy sheet it leaves parts in open space. This pass actively pulls them into cavities (material
// efficiency) — deterministic, and it only commits a move that is verified collision-free, so it can
// never create an overlap. Operates on the final placements (sheet-local frame).
static void fill_cavities(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                          const std::vector<std::vector<Polygon>>& part_holes_centered,
                          const std::vector<std::vector<Polygon>>& per_sheet_holes,
                          const SheetDims* dims = nullptr) {
    auto& P = res.placements;
    int N = (int)P.size();
    if (N == 0) return;
    const f32 TWO_PI = 6.28318530718f;
    const int NROT = 16;    // orientations tried when fitting a part into a cavity (handles parts that only fit rotated)
    const int G = 24;       // position-grid resolution inside a cavity

    // Every cavity (a placed hole) across ALL sheets. Hole geometry comes from the side-table (the
    // engine is solid/hole-unaware), placed via the host's transform.
    struct Cavity { int host; Polygon poly; int sheet; std::vector<Polygon> occ; f32 area; };
    std::vector<Cavity> cavities;
    for (int bi = 0; bi < N; ++bi) {
        usize bc = P[bi].color_id; if (bc >= parts.size() || bc >= part_holes_centered.size()) continue;
        const std::vector<Polygon>& bholes = part_holes_centered[bc];
        if (bholes.empty()) continue;
        AffineTransform bT = P[bi].d_transf.compose();
        for (const auto& h : bholes) {
            Cavity cv{bi, h.transform_clone(bT), P[bi].sheet, {}, 0.0f};
            cv.area = cv.poly.area;
            // Seed occupants with any part the RELAXATION already placed (partly) inside this cavity, so
            // a relocated part is checked against them too (otherwise the move could overlap one of them).
            for (int k = 0; k < N; ++k) {
                if (k == bi || P[k].sheet != cv.sheet || (usize)P[k].color_id >= parts.size()) continue;
                Polygon kp = parts[P[k].color_id].first.collision_shape->transform_clone(P[k].d_transf.compose());
                if (brute_overlap(kp, cv.poly)) cv.occ.push_back(std::move(kp));
            }
            cavities.push_back(std::move(cv));
        }
    }
    if (cavities.empty()) return;

    // Seat parts LONGEST-dimension-first (then area): long/thin parts need a contiguous slot, so claim
    // them before the grid fragments the cavity; bulky parts and small fillers follow.
    std::vector<int> order(N);
    for (int i = 0; i < N; ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) {
        const Polygon* pa = ((usize)P[a].color_id < parts.size()) ? parts[P[a].color_id].first.collision_shape.get() : nullptr;
        const Polygon* pb = ((usize)P[b].color_id < parts.size()) ? parts[P[b].color_id].first.collision_shape.get() : nullptr;
        f32 da = pa ? pa->diameter : 0.0f, db = pb ? pb->diameter : 0.0f;
        if (da != db) return da > db;
        return (pa ? pa->area : 0.0f) > (pb ? pb->area : 0.0f); });

    std::vector<char> relocated(N, 0);
    for (int oi = 0; oi < N; ++oi) {
        int si = order[oi];
        if (relocated[si]) continue;
        usize sc = P[si].color_id; if (sc >= parts.size()) continue;
        const Polygon& sshape = *parts[sc].first.collision_shape;
        bool done = false;
        for (auto& cav : cavities) {
            if (done) break;
            if (cav.host == si || sshape.area >= cav.area) continue;     // not its own hole; must be smaller than the cavity
            for (int r = 0; r < NROT && !done; ++r) {
                f32 ang = TWO_PI * (f32)r / (f32)NROT;
                Polygon srot = sshape.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());   // rotate about origin
                if (srot.bbox.width() > cav.poly.bbox.width() + 1e-3f ||
                    srot.bbox.height() > cav.poly.bbox.height() + 1e-3f) continue;
                f32 spanx = cav.poly.bbox.width() - srot.bbox.width(), spany = cav.poly.bbox.height() - srot.bbox.height();
                for (int iy = 0; iy <= G && !done; ++iy)
                    for (int ix = 0; ix <= G && !done; ++ix) {
                        f32 tx = cav.poly.bbox.x_min - srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                        f32 ty = cav.poly.bbox.y_min - srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                        Polygon cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        bool inside = true;
                        for (const auto& v : cand.vertices) if (!collides(cav.poly, v)) { inside = false; break; }
                        if (!inside) continue;
                        // No poking through the hole wall into the host's material: with every cand vertex
                        // inside the cavity ring, additionally require no cand edge to cross a ring edge.
                        // Together these certify cand lies WHOLLY within the cavity. (The engine is now
                        // solid / hole-unaware, so a donut-overlap test would wrongly reject an in-cavity part.)
                        bool crosses = false;
                        for (usize ei = 0; ei < cand.n_vertices() && !crosses; ++ei)
                            for (usize ej = 0; ej < cav.poly.n_vertices(); ++ej)
                                if (collides(cand.edge(ei), cav.poly.edge(ej))) { crosses = true; break; }
                        if (crosses) continue;
                        bool clash = false;
                        for (const auto& o : cav.occ) if (brute_overlap(cand, o)) { clash = true; break; }
                        if (clash) continue;
#if defined(NP_TEST_SEAMS)
                        // HARNESS SEAM (NP_TEST_POKE, default 0 = no-op): deliberately shove the seated
                        // child sideways out through the cavity wall, so np_bench can prove the final
                        // verification still reports a hole-nested part that has escaped its host's hole.
                        // The containment exemption must not swallow that case.
                        if (g_test_poke > 0.0f) {
                            tx += g_test_poke * cav.poly.bbox.width();
                            cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        }
#endif
                        P[si].d_transf = RigidTransform(ang, tx, ty);            // commit: new pose of the centered shape
                        P[si].verts = cand.vertices;
                        P[si].sheet = cav.sheet;
                        cav.occ.push_back(std::move(cand));
                        relocated[si] = 1; done = true;
                    }
            }
        }
    }

    // Compact sheet indices (a sheet emptied by cross-sheet relocation must not be emitted) + recount.
    // Skipped unless the sheets are interchangeable — see sheets_interchangeable().
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    if (!sheets_interchangeable(dims, &per_sheet_holes)) {
        int last = 0; for (auto& p : P) if (p.sheet + 1 > last) last = p.sheet + 1;
        res.n_sheets = last > 0 ? last : res.n_sheets;
        return;
    }
    std::vector<int> used(ns, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns) used[p.sheet] = 1;
    std::vector<int> remap(ns, -1); int next = 0;
    for (int s = 0; s < ns; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next > 0 ? next : res.n_sheets;
}

// DROP INVALID placements (g_drop_invalid): a part the solver could not actually fit can be left reported
// on a sheet while it hangs OUT OF BOUNDS (x/y outside [0,sheet_w]x[0,sheet_h]) or overlaps a sheet hole —
// i.e. it consumed a sheet for nothing. Demote every such placement to UNPLACED (remove it from the result)
// so the host lays it out OUTSIDE the sheets. Sheets are recounted (a sheet emptied this way is dropped).
static void drop_invalid_placements(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                                    const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                    f32 sheet_w, f32 sheet_h, const SheetDims* dims = nullptr) {
    if (!g_drop_invalid) return;
    auto& P = res.placements;
    // Tolerance is per-part and RELATIVE (driver.hpp bounds_tol): the old absolute TOL = 1.0f model units
    // made this guard blind in any document whose sheets are a few units across — a metre-scale sheet let
    // a part hang most of its own length outside and still pass. Same helper as the final verification,
    // so the two cannot drift apart.
    auto holes_for = [&](int s) -> const std::vector<Polygon>* {
        if (per_sheet_holes.empty()) return nullptr;
        usize hi = (s >= 0 && s < (int)per_sheet_holes.size()) ? (usize)s : per_sheet_holes.size() - 1;
        return &per_sheet_holes[hi];
    };
    std::vector<SheetPlacement> keep; keep.reserve(P.size());
    for (auto& pl : P) {
        usize c = pl.color_id;
        if (c >= parts.size()) { keep.push_back(pl); continue; }
        Polygon poly = parts[c].first.collision_shape->transform_clone(pl.d_transf.compose());
        // Bounds of THE SHEET THIS PART LANDED ON — not sheet 0's. Checking every part against sheet 0
        // is what let parts on a smaller later sheet survive this guard while hanging outside it.
        auto [bw, bh] = dims_for(dims, pl.sheet, sheet_w, sheet_h);
        const f32 TOL = bounds_tol(bw, bh, poly.diameter);
        bool valid = poly.bbox.x_min >= -TOL && poly.bbox.y_min >= -TOL &&
                     poly.bbox.x_max <= bw + TOL && poly.bbox.y_max <= bh + TOL;
        if (valid) {
            if (const std::vector<Polygon>* H = holes_for(pl.sheet))
                for (const auto& h : *H)
                    if (bbox_overlap(poly.bbox, h.bbox) && brute_overlap(poly, h)) { valid = false; break; }
        }
        if (valid) keep.push_back(pl);   // else: dropped -> the part stays unplaced (out_sheet_id == -1)
    }
    // Report how many were taken back off a sheet. Without this the host can only say "N parts didn't
    // fit", which is true but hides the more specific (and more actionable) "the solver had put them on a
    // sheet and they didn't actually fit it".
    g_q_demoted.fetch_add((int)(P.size() - keep.size()), std::memory_order_relaxed);
    if (keep.size() == P.size()) return;
    P.swap(keep);
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    if (!sheets_interchangeable(dims, &per_sheet_holes)) {   // see sheets_interchangeable: never renumber
        int last = 0; for (auto& p : P) if (p.sheet + 1 > last) last = p.sheet + 1;
        res.n_sheets = last;
        return;
    }
    std::vector<int> used(ns, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns) used[p.sheet] = 1;
    std::vector<int> remap(ns, -1); int next = 0;
    for (int s = 0; s < ns; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next;
}

// (min_bounding_height now lives in solver/driver.hpp — greedy_fill needs it too, to defer a part that
//  is too tall for THIS sheet to a later, taller one.)

// TANGENTIAL CONTACT vs REAL INTERPENETRATION. brute_overlap() is a boolean edge-intersection test, so two
// parts that merely TOUCH count as overlapping — and touching is the desired outcome of a tight nest. (Wired
// straight to a user-facing warning it fired on ~2/3 of provably clean layouts in the bench.) Shrinking both
// polygons by a thin skin opens tangential contacts while leaving any interpenetration deeper than the skin
// untouched, which is the question a host actually wants answered.
//
// The skin thickness is OVERLAP_EPS_FRAC x HALF THE SHORTER BBOX SIDE, so it is relative to the part and
// means the same thing in a millimetre document and a metre one. On a 400x300 part that is 0.15 units.
// Two parts are reported as overlapping from a penetration of d_a + d_b upward and stay quiet below it:
// measured on a 500-wide U against a 20-thick bar pushed into its channel floor, quiet at 0.10 units of
// penetration and reported from 0.26 = 0.25 + 0.01, exactly the sum of the two skins.
static const f32 OVERLAP_EPS_FRAC = 1e-3f;

// UNIFORM INWARD OFFSET (miter joins, limited). This replaces "scale the polygon toward its area
// centroid", which is NOT an erosion for a concave part: the centroid of a U / C / ring segment lies
// OUTSIDE the material, so scaling toward it moves the concave walls OUTWARD into the channel. Measured
// on a 500x500 U with 150-thick legs, 4 of the 8 "eroded" vertices ended up OUTSIDE the original, up to
// 0.10 units into the channel (0.15 on a C) — so a bar lying in that channel with a real 0.01-0.05 unit
// gap was reported as an overlapping pair. A genuine inward offset gives 0.0000 excursion on the same
// shapes, i.e. shrink(P) really is a subset of P.
// Reproducible end-to-end through the shipped ABI with np_bench: --shape=u / --shape=c at
// --holes=0 (no holes anywhere, so the hole exemption is not involved) disagreed with the harness's
// independent material-overlap measurement on 7 of 24 U/C runs with the centroid version and 0 of 24
// with this one, on byte-identical layouts.
//
// CCW is guaranteed by Polygon::create (it reverses on negative area), so the interior is on the LEFT of
// every edge and the inward unit normal of edge (v_i -> v_i+1) is (-dy, dx)/|e|. The offset vertex is the
// intersection of the two offset edge lines (the standard miter formula); the limit clamps the spike at
// very sharp corners, which only makes the shrink more conservative there.
static std::optional<Polygon> offset_inward(const Polygon& p, f32 d) {
    usize n = p.n_vertices();
    if (n < 3 || !(d > 0.0f)) return std::nullopt;
    std::vector<Point> nrm(n);
    for (usize i = 0; i < n; ++i) {
        const Point& a = p.vertices[i];
        const Point& b = p.vertices[(i + 1) % n];
        f32 dx = b.x - a.x, dy = b.y - a.y;
        f32 len = std::sqrt(dx * dx + dy * dy);
        if (!(len > 0.0f)) return std::nullopt;
        nrm[i] = Point(-dy / len, dx / len);
    }
    const f32 MITER_LIMIT = 4.0f;
    std::vector<Point> out(n);
    for (usize i = 0; i < n; ++i) {
        const Point& n0 = nrm[(i + n - 1) % n];   // edge INTO vertex i
        const Point& n1 = nrm[i];                 // edge OUT of vertex i
        f32 den = 1.0f + (n0.x * n1.x + n0.y * n1.y);
        f32 sx, sy;
        if (den <= 1e-6f) { sx = n1.x; sy = n1.y; }          // ~180 deg spike: step along one normal
        else {
            sx = (n0.x + n1.x) / den; sy = (n0.y + n1.y) / den;
            f32 m = std::sqrt(sx * sx + sy * sy);
            if (m > MITER_LIMIT) { sx = sx / m * MITER_LIMIT; sy = sy / m * MITER_LIMIT; }
        }
        out[i] = Point(p.vertices[i].x + d * sx, p.vertices[i].y + d * sy);
    }
    try { return Polygon::create(std::move(out)); } catch (...) { return std::nullopt; }
}

// Certificate that `in` lies wholly within `out`: every vertex inside and no edge crossing. For two
// simple polygons that is exactly the subset relation.
static bool poly_subset(const Polygon& in, const Polygon& out) {
    for (const auto& v : in.vertices) if (!collides(out, v)) return false;
    for (usize i = 0; i < in.n_vertices(); ++i)
        for (usize j = 0; j < out.n_vertices(); ++j)
            if (collides(in.edge(i), out.edge(j))) return false;
    return true;
}

// Shrink a placed part by the contact skin, VERIFIED to be a subset. The offset can only fail on a
// feature thinner than twice the skin (it would self-intersect, or the step falls below f32 resolution
// at sheet-scale coordinates); back the distance off and, if even that fails, decline — every caller
// then falls back to the strict, un-shrunk test, which is the conservative direction.
// (Measured: a 1000 x 0.05 sliver declines and falls back — its nominal skin, 2.5e-5, is below the f32
// resolution of a coordinate near 1000; U, C, L, plus and rect all verify as a subset on the first try.)
static std::optional<Polygon> shrink_verified(const Polygon& p) {
    f32 d = OVERLAP_EPS_FRAC * 0.5f * min_f(p.bbox.width(), p.bbox.height());
    for (int k = 0; k < 4 && d > 0.0f; ++k, d *= 0.25f) {
        auto s = offset_inward(p, d);
        if (s && s->area > 0.0f && poly_subset(*s, p)) return s;
    }
    return std::nullopt;
}

// `sa`/`sb` are the pre-shrunk copies of `a`/`b` (nullptr => the shrink declined for that one, so use
// the strict test). Callers shrink ONCE per placed part and reuse it across every pair.
static bool real_overlap(const Polygon& a, const Polygon& b, const Polygon* sa, const Polygon* sb) {
    if (!sa || !sb) return brute_overlap(a, b);   // degenerate: fall back to the strict test
    return brute_overlap(*sa, *sb);
}

// HOLE-NESTED CONTAINMENT — "is `inner` legitimately sitting INSIDE `hole`?"
//
// The final verification below tests parts against each other using `collision_shape`, which is the SOLID
// outer ring: the relaxation engine is deliberately hole-unaware (see the part_holes_centered note in
// np_nest) and part holes live only in that side-table. brute_overlap() reports FULL CONTAINMENT as an
// overlap (its point-in-polygon test fires even with no edge crossing). So without this, every part that
// fill_cavities / precompute_hole_pairs legitimately seats inside a bigger part's cavity came back as an
// "overlapping pair" — and part_holes_mode 1 is the DEFAULT, so the gate cried wolf on correct layouts in
// the default configuration.
//
// The certificate is re-derived here from the final geometry rather than trusted from the pass that made
// the move: every vertex of `inner` inside the hole ring AND no edge of `inner` crossing the ring. That is
// exactly the pair of tests fill_cavities commits on, so a legal seating always passes — while a child
// that pokes out THROUGH the hole wall fails the edge test (and usually the vertex test too) and is still
// reported. That case is a genuine defect and must survive this exemption.
//
// `inner_shrunk` is the SAME shrink real_overlap uses (shrink_verified, passed in so it is computed once
// per part), so a child seated flush against the hole wall is not knife-edged by f32 round-off:
// fill_cavities builds its candidate as rotate-then-translate while the verification re-composes one
// rigid transform, which is not bit-identical. It also means the containment test and the overlap test
// agree on one single definition of "touching does not count".
//
// This shrink MUST be a genuine subset or the exemption breaks for non-convex children: the old
// scale-toward-the-centroid version pushed a U child's channel walls outward into the host material, so
// a U child correctly seated in a U-shaped cavity was reported as an overlapping pair at every uniform
// clearance up to 1.0 units on a 460-wide cavity, in BOTH hole modes. Measured through the ABI with
// np_bench --frames=1 --fshape=u --cl=... : dirty at cl <= 1.0 before, clean at every cl from 0.1 to 5
// after, with the harness's independent measurement saying "child wholly inside the hole" throughout.
// The other half of the contract still holds: pushing that same child out through the cavity wall (--poke)
// is still reported. There is no fixed unit threshold to quote, and this note used to quote one ("23 units
// at cl=2") that was merely the largest value in the original sweep, not its threshold — the real one at
// cl=2 is about a quarter of that. The DURABLE statement is the relationship: the displacement needed
// TRACKS THE CLEARANCE THE CHILD HAS, because what the gate detects is the loss of the containment
// certificate, and the child must first cross its own --cl inset for that to happen. Bisected through the
// ABI (np_bench --frames=2 --parts=8 --fshape=u --cl=C --poke=F; F is a fraction of the cavity bbox width,
// so on this 460-wide cavity the displacement is 460F units) — quiet up to / reported from:
//     cl=0.1 -> 0.27 / 0.29 u    cl=0.5 -> 1.55 / 1.57 u    cl=1 -> 2.77 / 2.79 u
//     cl=2   -> 5.32 / 5.35 u    cl=5   -> 13.00 / 13.05 u
// i.e. ~2.6-3.1 x cl across a 50x range of clearances, never a constant. Two things move the absolute
// numbers, so re-bisect rather than trust one: the GRID you sweep (a coarse sweep of the cl=2 case reports
// its first REPORTING sample, 5.52 u, not the ~5.33 u threshold), and the bench configuration itself, via
// how many children get seated and where. Pick a configuration that actually seats one — --frames=2
// --parts=8 seats 2 at every cl from 0.1 to 5, whereas --frames=1 at the default part count seats NONE,
// which leaves --poke inert at every value and the run trivially CLEAN. What must hold is the
// proportionality to cl, not any of these numbers.
// One asymmetry worth knowing when reading a np_bench run: the GATE flips slightly before the harness's
// exact material-overlap measurement does (a band of eng>0 with harness=0 just below each threshold
// above). That is the intended conservatism, not a bug — once containment can no longer be certified the
// pair is judged against the host's SOLID outer ring, which reports before real material actually meets.
// nullptr => the shrink declined, and the raw polygon is used, i.e. the strict test.
static bool contained_in_hole(const Polygon& inner, const Polygon* inner_shrunk, const Polygon& hole) {
    // Shrink FIRST, then bbox-reject on the shrunk shape. Doing the cheap reject on the raw polygon would
    // reintroduce exactly the knife-edge the shrink exists to remove: a child seated flush against the
    // hole wall has bboxes equal to within f32 noise, so a 1e-6 overshoot would reject it and the pair
    // would be reported as an overlap again.
    const Polygon& in = inner_shrunk ? *inner_shrunk : inner;
    if (in.bbox.x_min < hole.bbox.x_min || in.bbox.x_max > hole.bbox.x_max ||
        in.bbox.y_min < hole.bbox.y_min || in.bbox.y_max > hole.bbox.y_max) return false;  // cheap reject
    for (const auto& v : in.vertices) if (!collides(hole, v)) return false;
    for (usize ei = 0; ei < in.n_vertices(); ++ei)
        for (usize ej = 0; ej < hole.n_vertices(); ++ej)
            if (collides(in.edge(ei), hole.edge(ej))) return false;
    return true;
}

// HOLES-FIRST pre-pairing (g_holes_first): BEFORE the sheet nest, assign smaller parts INTO bigger parts'
// holes (in the host's CENTERED frame, where the holes live in part_holes_centered). Each assigned small is
// removed from the main nest and rides inside its host; after the host is placed, the small's sheet pose =
// host_transform o rel. Mirrors fill_cavities' fit test (vertices inside the hole ring + no edge crossing +
// no clash with siblings already placed in that hole) but in centered (un-placed) coordinates. Returns the
// pairs (small,host,rel); `deferred[i]` marks the smalls removed from the main nest.
struct HolePair { int small; int host; RigidTransform rel; f32 hole_w; };
static std::vector<HolePair> precompute_hole_pairs(
        const std::vector<std::pair<Part, usize>>& parts,
        const std::vector<std::vector<Polygon>>& part_holes_centered,
        std::vector<char>& deferred) {
    int N = (int)parts.size();
    deferred.assign((size_t)N, 0);
    std::vector<HolePair> pairs;
    const f32 TWO_PI = 6.28318530718f;
    const int NROT = 16, G = 24;
    for (int h = 0; h < N; ++h) {
        if ((usize)h >= part_holes_centered.size()) continue;
        const std::vector<Polygon>& holes = part_holes_centered[(usize)h];
        for (const auto& hole : holes) {
            std::vector<Polygon> occ;                  // smalls already seated in THIS hole
            f32 harea = hole.area;
            std::vector<int> cand;                     // unpaired parts smaller than the hole, biggest first
            for (int s = 0; s < N; ++s)
                if (s != h && !deferred[(size_t)s] && parts[(size_t)s].first.collision_shape &&
                    parts[(size_t)s].first.collision_shape->area < harea)
                    cand.push_back(s);
            std::stable_sort(cand.begin(), cand.end(), [&](int a, int b) {
                return parts[(size_t)a].first.collision_shape->area > parts[(size_t)b].first.collision_shape->area; });
            for (int s : cand) {
                if (deferred[(size_t)s]) continue;
                const Polygon& ss = *parts[(size_t)s].first.collision_shape;
                bool done = false;
                for (int r = 0; r < NROT && !done; ++r) {
                    f32 ang = TWO_PI * (f32)r / (f32)NROT;
                    Polygon srot = ss.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());
                    if (srot.bbox.width() > hole.bbox.width() + 1e-3f || srot.bbox.height() > hole.bbox.height() + 1e-3f) continue;
                    f32 spanx = hole.bbox.width() - srot.bbox.width(), spany = hole.bbox.height() - srot.bbox.height();
                    for (int iy = 0; iy <= G && !done; ++iy)
                        for (int ix = 0; ix <= G && !done; ++ix) {
                            f32 tx = hole.bbox.x_min - srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                            f32 ty = hole.bbox.y_min - srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                            Polygon cand_poly = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                            bool inside = true;
                            for (const auto& v : cand_poly.vertices) if (!collides(hole, v)) { inside = false; break; }
                            if (!inside) continue;
                            bool cross = false;
                            for (usize ei = 0; ei < cand_poly.n_vertices() && !cross; ++ei)
                                for (usize ej = 0; ej < hole.n_vertices(); ++ej)
                                    if (collides(cand_poly.edge(ei), hole.edge(ej))) { cross = true; break; }
                            if (cross) continue;
                            bool clash = false;
                            for (const auto& o : occ) if (brute_overlap(cand_poly, o)) { clash = true; break; }
                            if (clash) continue;
                            // hole.bbox.width() is carried only so the NP_TEST_POKE harness seam can
                            // express its displacement in the SAME units as the fill_cavities seam.
                            pairs.push_back({s, h, RigidTransform(ang, tx, ty), hole.bbox.width()});
                            occ.push_back(std::move(cand_poly));
                            deferred[(size_t)s] = 1; done = true;
                        }
                }
            }
        }
    }
    return pairs;
}

// SHEET GAP-FILL post-pass (g_fill_gaps): the greedy packs big-first and carries the spill to the next
// sheet, but a SMALLER part can be stranded on a later sheet while it would fit a GAP a bigger part left
// on an earlier sheet. This pass pulls such parts FORWARD: for each sheet (earliest first) it grid-searches
// every part currently on a LATER sheet (smallest-area first, with rotation) into the sheet's open free
// space. A move is committed only if the candidate lies wholly within [0,sheet_w]x[0,sheet_h], clear of
// every part already on that sheet AND every sheet hole (exact brute_overlap) — verified moves only, so it
// can never create an overlap; it can only fill gaps and empty later sheets. Sheets are recounted after.
static void fill_sheet_gaps(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                            const std::vector<std::vector<Polygon>>& per_sheet_holes,
                            f32 sheet_w, f32 sheet_h, const SheetDims* dims = nullptr) {
    if (!g_fill_gaps) return;
    auto& P = res.placements;
    int N = (int)P.size();
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    if (N < 2 || ns < 2) return;                 // only meaningful when there's a later sheet to pull from
    const f32 TWO_PI = 6.28318530718f;
    int NROT = 24, G = 40;                        // rotations x position-grid resolution per candidate (fine,
                                                  // so fragmented frame gaps are actually found). Env-tunable.
    if (const char* e = std::getenv("NP_GAP_ROT")) { int v = std::atoi(e); if (v > 0) NROT = v; }
    if (const char* e = std::getenv("NP_GAP_GRID")) { int v = std::atoi(e); if (v > 0) G = v; }

    auto holes_for = [&](int s) -> const std::vector<Polygon>* {
        if (per_sheet_holes.empty()) return nullptr;
        usize hi = (s >= 0 && s < (int)per_sheet_holes.size()) ? (usize)s : per_sheet_holes.size() - 1;
        return &per_sheet_holes[hi];
    };

    // smallest-area first: a small part is the one most likely to drop into a leftover gap.
    std::vector<int> order(N);
    for (int i = 0; i < N; ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) {
        const Polygon* pa = ((usize)P[a].color_id < parts.size()) ? parts[P[a].color_id].first.collision_shape.get() : nullptr;
        const Polygon* pb = ((usize)P[b].color_id < parts.size()) ? parts[P[b].color_id].first.collision_shape.get() : nullptr;
        f32 aa = pa ? pa->area : 0.0f, ab = pb ? pb->area : 0.0f;
        return aa < ab; });

    for (int ts = 0; ts < ns; ++ts) {
        const std::vector<Polygon>* holes = holes_for(ts);
        // the TARGET sheet's own size (pulling a part forward must respect the sheet it lands on)
        auto [tw, th] = dims_for(dims, ts, sheet_w, sheet_h);
        std::vector<Polygon> occ;                // parts already on the target sheet (grows as we add)
        for (int k = 0; k < N; ++k)
            if (P[k].sheet == ts && (usize)P[k].color_id < parts.size())
                occ.push_back(parts[P[k].color_id].first.collision_shape->transform_clone(P[k].d_transf.compose()));

        for (int oi = 0; oi < N; ++oi) {
            int si = order[oi];
            if (P[si].sheet <= ts) continue;     // only pull a part FORWARD from a later sheet
            usize sc = P[si].color_id; if (sc >= parts.size()) continue;
            const Polygon& sshape = *parts[sc].first.collision_shape;
            bool placed = false;
            for (int r = 0; r < NROT && !placed; ++r) {
                f32 ang = TWO_PI * (f32)r / (f32)NROT;
                Polygon srot = sshape.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());
                if (srot.bbox.width() > tw + 1e-3f || srot.bbox.height() > th + 1e-3f) continue;
                f32 spanx = tw - srot.bbox.width(), spany = th - srot.bbox.height();
                for (int iy = 0; iy <= G && !placed; ++iy)
                    for (int ix = 0; ix <= G && !placed; ++ix) {
                        f32 tx = -srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                        f32 ty = -srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                        Polygon cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        if (cand.bbox.x_min < -1e-3f || cand.bbox.y_min < -1e-3f ||
                            cand.bbox.x_max > tw + 1e-3f || cand.bbox.y_max > th + 1e-3f) continue;
                        bool bad = false;
                        if (holes) for (const auto& h : *holes) {
                            if (!bbox_overlap(cand.bbox, h.bbox)) continue;
                            if (brute_overlap(cand, h)) { bad = true; break; }
                        }
                        if (bad) continue;
                        for (const auto& o : occ) {
                            if (!bbox_overlap(cand.bbox, o.bbox)) continue;
                            if (brute_overlap(cand, o)) { bad = true; break; }
                        }
                        if (bad) continue;
                        P[si].d_transf = RigidTransform(ang, tx, ty);   // commit: pull si onto sheet ts
                        P[si].verts = cand.vertices;
                        P[si].sheet = ts;
                        occ.push_back(std::move(cand));
                        placed = true;
                    }
            }
        }
    }

    int ns2 = res.n_sheets > 0 ? res.n_sheets : 1;
    if (!sheets_interchangeable(dims, &per_sheet_holes)) {   // see sheets_interchangeable: never renumber
        int last = 0; for (auto& p : P) if (p.sheet + 1 > last) last = p.sheet + 1;
        res.n_sheets = last > 0 ? last : res.n_sheets;
        return;
    }
    std::vector<int> used(ns2, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns2) used[p.sheet] = 1;
    std::vector<int> remap(ns2, -1); int next = 0;
    for (int s = 0; s < ns2; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns2 && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next > 0 ? next : res.n_sheets;
}

extern "C" NP_EXPORT int np_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_rotations,
    int           sheet_count,
    const int*    sheet_outer_vertex_counts,
    const double* sheet_outer_xy,
    const int*    sheet_hole_counts,
    const int*    hole_vertex_counts,
    const double* hole_xy,
    const int*    part_hole_counts,
    const int*    part_hole_vertex_counts,
    const double* part_hole_xy,
    const NpParams* params,
    double*       out_tx,
    double*       out_ty,
    double*       out_angle,
    int*          out_sheet_id,
    int*          out_n_sheets)
{
    try {
        if (out_n_sheets) *out_n_sheets = 0;
        for (int i = 0; i < part_count; ++i) {
            if (out_tx) out_tx[i] = 0.0;
            if (out_ty) out_ty[i] = 0.0;
            if (out_angle) out_angle[i] = 0.0;
            if (out_sheet_id) out_sheet_id[i] = -1;
        }
        if (!params || part_count <= 0 || sheet_count <= 0) return 0;

        NpParams P = *params;
        ROT_N_SAMPLES = (usize)(P.num_rotations < 1 ? 1 : P.num_rotations);
        g_iter_mode = (P.iter_mode != 0);
        g_iter_count = 0;
        g_iter_pub.store(0, std::memory_order_relaxed);   // reset the host-facing progress sum
        g_cancel.store(false);   // clear any stale cancel from a previous (cancelled) run

        // Adaptive preview vs quality (the C ABI owns these globals; set them every call). A tiny
        // iteration budget runs a FAST rough look — few best-of-N workers + lean poles, ~sub-second —
        // because the per-round cost is dominated by copying collision state to every worker. A large
        // budget uses the full best-of-N + dense poles for the tight all-on-one-sheet pack.
        // STEP 1 post-relaxation compaction: driven by the component's "compact" option (P.final_compact);
        // NP_COMPACT env overrides for dev A/B. Monotone + collision-checked, so OFF == baseline.
        g_final_compact = (P.final_compact != 0);
        if (const char* e = std::getenv("NP_COMPACT")) g_final_compact = (std::atoi(e) != 0);
        // Multi-directional slide-to-contact variant (CG-SHOP 2024 / Shadoks). Component selects it with
        // final_compact==2 (1 = plain bottom-left baseline). NP_COMPACT_MULTIDIR env overrides for dev A/B.
        g_compact_multidir = (P.final_compact >= 2);
        if (const char* e = std::getenv("NP_COMPACT_MULTIDIR")) g_compact_multidir = (std::atoi(e) != 0);
        // Sheet-hole keep-out guard for compaction (default on). NP_COMPACT_HOLE_GUARD=0 disables it for A/B.
        g_compact_hole_guard = true;
        if (const char* e = std::getenv("NP_COMPACT_HOLE_GUARD")) g_compact_hole_guard = (std::atoi(e) != 0);
        // Sheet gap-fill post-pass (default on). NP_FILL_GAPS=0 disables it for A/B.
        g_fill_gaps = true;
        if (const char* e = std::getenv("NP_FILL_GAPS")) g_fill_gaps = (std::atoi(e) != 0);
        // Drop out-of-bounds / in-hole placements -> unplaced (default on). NP_DROP_INVALID=0 disables for A/B.
        g_drop_invalid = true;
        if (const char* e = std::getenv("NP_DROP_INVALID")) g_drop_invalid = (std::atoi(e) != 0);
        g_q_demoted.store(0, std::memory_order_relaxed);   // per-solve; drop_invalid_placements adds to it
#if defined(NP_TEST_SEAMS)
        // Harness-only defect injection (see the g_test_* notes at the top). Not compiled into a release
        // build at all; in a harness build, unset => 0 => exact no-op.
        g_test_poke = 0.0f;  if (const char* e = std::getenv("NP_TEST_POKE"))  g_test_poke  = (f32)std::atof(e);
        g_test_shift = 0.0f; if (const char* e = std::getenv("NP_TEST_SHIFT")) g_test_shift = (f32)std::atof(e);
#endif
        // STEP 3 feasibility-wall bisection: dev-only env (no measured benefit on near-frontier instances;
        // the limiter there is the arrangement/basin, not the width-step — so it is NOT a component option).
        g_expl_bisect = false;
        if (const char* e = std::getenv("NP_BISECT")) g_expl_bisect = (std::atoi(e) != 0);

        bool preview = g_iter_mode && P.iter_budget > 0 && P.iter_budget < 500;
        g_n_workers = preview ? 4u : 0u;   // 0 => resolve to ~75% of cores
        // Surrogate pole cap: preview => 12 (fast rough look); else the component's "poles" option
        // (P.pole_max>0, ~16 is ~2.7x faster per round and not worse), else default 48. NP_POLES env overrides.
        g_pole_max  = preview ? 12u : (P.pole_max > 0 ? (unsigned)P.pole_max : 0u);
        if (const char* e = std::getenv("NP_POLES")) { int p = std::atoi(e); if (p > 0) g_pole_max = (unsigned)p; }

        // NOTE: part spacing/offset is applied UPSTREAM by the host (Grasshopper offsets parts &
        // sheets with their dedicated components and passes the already-offset outline here). The
        // solver does NOT offset: ShapeModifyConfig::offset stays unset so the unported offset_shape
        // stub is never reached. P.spacing is accepted for ABI stability but intentionally ignored.
        ShapeModifyConfig mc = part_modify();
        if (P.simplify_tolerance > 0.0) mc.simplify_tolerance = (f32)P.simplify_tolerance;

        // Build parts. Degenerate parts are skipped and stay unplaced (sheet id -1). `craw`/`orig_index`
        // stay parallel to the solver parts list (whose ids are the greedy_fill color_id).
        std::vector<std::pair<Part, usize>> parts;
        std::vector<Point> craw;
        std::vector<int> orig_index;
        std::vector<std::vector<Polygon>> part_holes_centered;  // per-part interior holes (centered frame) for the cavity-fill post-pass
        int pcur = 0;
        // FILL-HOLES mode: read each part's interior holes so other parts may nest inside them. Cursors
        // advance whenever the arrays are present (mode-independent) so they stay aligned; the holes are
        // only ATTACHED when part_holes_mode==1 (else this is byte-identical to the keep-empty path).
        bool have_part_holes = part_hole_counts && part_hole_vertex_counts && part_hole_xy;
        bool fill_holes = (P.part_holes_mode >= 1) && have_part_holes;   // 1 = post-pass, 2 = holes-first
        g_holes_first = (P.part_holes_mode >= 2) && have_part_holes;
        if (const char* e = std::getenv("NP_HOLES_FIRST")) g_holes_first = (std::atoi(e) != 0) && have_part_holes;
        int phvcur = 0, phcur = 0;
        for (int i = 0; i < part_count; ++i) {
            std::vector<Point> pts = read_poly(part_xy, part_vertex_counts[i], pcur);
            std::vector<std::vector<Point>> phs;
            if (have_part_holes) {
                int nh = part_hole_counts[i];
                for (int h = 0; h < nh; ++h) {
                    int nv = part_hole_vertex_counts[phvcur++];
                    std::vector<Point> hp = read_poly(part_hole_xy, nv, phcur);
                    if (fill_holes) phs.push_back(std::move(hp));
                }
            }
            Point cc;
            // Per-part rotation override: N>0 restricts this part to N discrete orientations
            // (RotationRange::discrete_of); 0/NULL keeps free continuous rotation as before.
            int rot_n = (part_rotations && part_rotations[i] > 0) ? part_rotations[i] : 0;
            auto p = build_part(parts.size(), std::move(pts), &cc, &mc, rot_n);
            if (p) {
                parts.emplace_back(std::move(*p), 1); craw.push_back(cc); orig_index.push_back(i);
                // Centered part-holes (input hole - input centroid) kept in a SIDE-TABLE parallel to
                // `parts`, NOT attached to the Polygon: the relaxation engine stays solid / hole-unaware
                // (byte-identical no-hole behaviour); only the isolated cavity-fill post-pass reads these
                // to relocate small parts into cavities.
                std::vector<Polygon> chs;
                if (fill_holes) for (auto& hin : phs) {
                    std::vector<Point> hc;
                    hc.reserve(hin.size());
                    for (auto pt : hin) hc.push_back(Point(pt.x - cc.x, pt.y - cc.y));
                    std::vector<Point> cl = clean_ring(std::move(hc));
                    if (cl.size() >= 3) { try { chs.push_back(Polygon::create(std::move(cl))); } catch (...) {} }
                }
                part_holes_centered.push_back(std::move(chs));
            }
        }
        if (parts.empty()) return 0;

        // Publish the centering/index mapping + open the live-preview channel for np_poll_layout.
        {
            std::lock_guard<std::mutex> lk(g_live.mtx);
            g_poll_orig_index = orig_index;
            g_poll_craw = craw;
            g_live.items.clear();
            g_live.gen = 0;
            g_live.active = true;
        }

        // PER-SHEET dimensions: each sheet's own bbox. (Until the mixed-size fix this read sheet 0's
        // bbox and applied it to every sheet — "v1" — which silently reported parts as placed on a
        // smaller later sheet while they hung outside its outline, and starved a larger later sheet.
        // sheet_w/sheet_h stay as the sheet-0 scalars because the fallbacks and the live preview band
        // by one width.) Holes are translated into each sheet's local frame (origin at that sheet's
        // bbox min) so they line up with the strip-packing frame.
        int ocur = 0, hcur = 0, hszcur = 0;
        f32 sheet_w = 0.0f, sheet_h = 0.0f;
        SheetDims sheet_dims;
        sheet_dims.reserve((usize)sheet_count);
        std::vector<std::vector<Polygon>> per_sheet_holes((usize)sheet_count);
        bool any_hole = false;
        for (int s = 0; s < sheet_count; ++s) {
            std::vector<Point> outer = read_poly(sheet_outer_xy, sheet_outer_vertex_counts[s], ocur);
            f32 minx = F32_MAX, miny = F32_MAX, maxx = F32_MIN, maxy = F32_MIN;
            for (auto& pt : outer) {
                minx = min_f(minx, pt.x); miny = min_f(miny, pt.y);
                maxx = max_f(maxx, pt.x); maxy = max_f(maxy, pt.y);
            }
            sheet_dims.push_back({maxx - minx, maxy - miny});
            if (s == 0) { sheet_w = maxx - minx; sheet_h = maxy - miny; }
            int nh = sheet_hole_counts ? sheet_hole_counts[s] : 0;
            for (int h = 0; h < nh; ++h) {
                int nv = hole_vertex_counts[hszcur++];
                std::vector<Point> hp = read_poly(hole_xy, nv, hcur);
                for (auto& pt : hp) { pt.x -= minx; pt.y -= miny; }   // -> sheet-local
                std::vector<Point> cl = clean_ring(std::move(hp));
                if (cl.size() < 3) continue;
                try { per_sheet_holes[(usize)s].push_back(Polygon::create(std::move(cl))); any_hole = true; }
                catch (...) { /* skip degenerate/self-intersecting hole */ }
            }
        }

        CollisionConfig engine;
        engine.quadtree_depth = 4; engine.cd_threshold = 16;
        engine.part_surrogate_config = surrogate_config();

        double budget = g_iter_mode ? (double)P.iter_budget : P.time_budget_secs;
        int max_bins = P.max_sheets > 0 ? P.max_sheets : 6;
        const int fit_mode = P.fit_mode;
        if (fit_mode == 1) max_bins = 1;   // one-sheet max-fill: never open a 2nd sheet; overflow stays unplaced
        uint64_t seed = (uint64_t)(P.seed >= 0 ? P.seed : 0);
        int n_starts = P.n_starts > 0 ? P.n_starts : 1;
        const std::vector<std::vector<Polygon>> empty_holes;
        const std::vector<std::vector<Polygon>>& holes_arg = any_hole ? per_sheet_holes : empty_holes;

        // Build the list the multi-start actually packs (nest_parts), EXCLUDING:
        //  (a) OVERSIZED parts whose minimum width > the strip height -> they can't fit at ANY rotation and
        //      would poison the whole relaxation; set aside as unplaced (the host lays them OUTSIDE).
        //  (b) HOLES-FIRST deferred smalls -> pre-paired inside a bigger part's hole, re-added after placement.
        // hf_keep maps each kept (nested) part back to its full index for the post-nest remap.
        int NP_all = (int)parts.size();
        // "Fits no sheet at all" is measured against the TALLEST sheet — with a mixed set, a part that is
        // too tall for sheet 0 may still fit sheet 3, and greedy_fill defers it there.
        f32 tallest = sheet_h;
        for (const auto& d : sheet_dims) tallest = max_f(tallest, d.second);
        std::vector<char> oversized((size_t)NP_all, 0);
        for (int i = 0; i < NP_all; ++i)
            if (parts[(size_t)i].first.collision_shape &&
                min_bounding_height(*parts[(size_t)i].first.collision_shape, 180) > tallest * 1.001f)
                oversized[(size_t)i] = 1;
        std::vector<char> hf_deferred((size_t)NP_all, 0);
        std::vector<HolePair> hf_pairs;
        if (g_holes_first) hf_pairs = precompute_hole_pairs(parts, part_holes_centered, hf_deferred);
        std::vector<int> hf_keep;
        std::vector<std::pair<Part, usize>> parts_reduced;
        for (int i = 0; i < NP_all; ++i)
            if (!oversized[(size_t)i] && !hf_deferred[(size_t)i]) { hf_keep.push_back(i); parts_reduced.push_back(parts[(size_t)i]); }
        bool reduced = ((int)hf_keep.size() != NP_all);
        const std::vector<std::pair<Part, usize>>& nest_parts = reduced ? parts_reduced : parts;

        // Multi-start: run n_starts independent seeds (each at the given budget) and keep the densest
        // (fewest sheets, then smallest last-sheet width). Robust against this instance's run-to-run
        // variance; n_starts==1 is the original single run.
        const size_t n_to_place = nest_parts.size();
        auto score = [&](const Sheets& r) -> double {
            double w = r.widths.empty() ? 0.0 : r.widths.back();
            if (fit_mode == 1) {
                // one-sheet max-fill: MAXIMISE parts on the single sheet (fewest left off), then tighter.
                long overflow = (long)n_to_place - (long)r.placements.size();
                if (overflow < 0) overflow = 0;
                return (double)overflow * 1e9 + w;     // lower = fewer left off = fuller sheet
            }
            double pen = r.ok ? 0.0 : 1e15;            // unplaced parts => very bad
            return pen + (double)r.n_sheets * 1e9 + w; // lower is better
        };
        Sheets res;
        bool have = false;
        if (n_starts <= 1) {
            res = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins, holes_arg, seed,
                              /*publish_live=*/true, &sheet_dims);
            have = true;
        } else {
            // MULTI-START portfolio. DEFAULT = SEQUENTIAL full-worker starts: each start gets the FULL
            // best-of-~75%-cores worker pool (strongest per start; the proven 47/1 recipe), run one at a
            // time, keeping the densest by `score`, with EARLY-EXIT the instant a start lands one sheet
            // (the goal -> no point continuing). On a ~1/6-per-start instance this needs ~6 starts on
            // average to hit one sheet, but stops as soon as it does. Set NP_PARALLEL_STARTS to force the
            // old concurrent portfolio (shorter wall, but each start is weaker -> less reliable here).
            const bool parallel_starts = (std::getenv("NP_PARALLEL_STARTS") != nullptr);
            if (!parallel_starts) {
                g_n_workers = preview ? 4u : 0u;   // full strength per start (0 => ~75% cores)
                if (const char* e = std::getenv("NP_STARTS_WORKERS")) { int w = std::atoi(e); if (w > 0) g_n_workers = (unsigned)w; }
                for (int s = 0; s < n_starts && !g_cancel.load(); ++s) {
                    uint64_t st_seed = seed + (uint64_t)s * 1009ull;
                    Sheets r = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins, holes_arg, st_seed,
                                           /*publish_live=*/true, &sheet_dims);
                    int rn = r.n_sheets; double rw = r.widths.empty() ? 0.0 : r.widths.back();
                    if (!have || score(r) < score(res)) { res = std::move(r); have = true; }
                    std::fprintf(stderr, "[np] multistart %d/%d seed=%llu -> %d sheet(s) w=%.1f (best so far=%d)\n",
                                 s + 1, n_starts, (unsigned long long)st_seed, rn, rw, res.n_sheets);
                    // stop early when "done enough": all parts on one sheet (max-fill) or a single sheet (min-sheets)
                    if (fit_mode == 1 ? (res.placements.size() >= n_to_place) : (res.n_sheets <= 1)) break;
                }
            } else {
                // ---- concurrent portfolio: faster wall, weaker per start. Early-exits per wave. ----
                unsigned hw = std::thread::hardware_concurrency(); if (hw == 0) hw = 4;
                unsigned cores75 = (hw * 3) / 4; if (cores75 < 1) cores75 = 1;
                int conc = (int)std::min<unsigned>((unsigned)n_starts, cores75);
                if (!preview) { g_n_workers = cores75 / (unsigned)conc; if (g_n_workers < 1) g_n_workers = 1; }
                if (const char* e = std::getenv("NP_STARTS_WORKERS")) {
                    int w = std::atoi(e);
                    if (w > 0) { g_n_workers = (unsigned)w; conc = (int)std::min<unsigned>((unsigned)n_starts, std::max(1u, cores75 / g_n_workers)); }
                }
                std::vector<Sheets> results((size_t)n_starts);
                std::vector<char> done((size_t)n_starts, 0);
                for (int base = 0; base < n_starts && !g_cancel.load(); base += conc) {
                    int hi = std::min(n_starts, base + conc);
                    std::vector<std::thread> ths;
                    for (int s = base; s < hi; ++s) {
                        uint64_t st_seed = seed + (uint64_t)s * 1009ull;
                        bool pub = (s == 0);   // only the first start streams the live preview
                        ths.emplace_back([&, s, st_seed, pub]() {
                            results[(size_t)s] = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins,
                                                             holes_arg, st_seed, pub, &sheet_dims);
                            done[(size_t)s] = 1;
                        });
                    }
                    for (auto& t : ths) t.join();
                    for (int s = base; s < hi; ++s) {           // fold in this wave, then early-exit on one sheet
                        if (!done[(size_t)s]) continue;
                        if (!have || score(results[(size_t)s]) < score(res)) { res = results[(size_t)s]; have = true; }
                    }
                    if (have && (fit_mode == 1 ? (res.placements.size() >= n_to_place) : (res.n_sheets <= 1))) break;
                }
            }
        }

        // If the nest list was REDUCED (oversized parts set aside and/or holes-first deferrals), res.placements
        // color_ids index parts_reduced -> remap them back to full part indices. (Oversized parts simply stay
        // unplaced; the host lays them outside.)
        if (reduced) {
            for (auto& pl : res.placements)
                if ((usize)pl.color_id < hf_keep.size()) pl.color_id = (usize)hf_keep[(size_t)pl.color_id];
        }
        // HOLES-FIRST: re-add each deferred small INSIDE its now-placed host:
        // small_sheet_pose = host_pose o rel (rel applied first, then the host transform).
        if (g_holes_first) {
            for (const auto& pr : hf_pairs) {
                int hsheet = -1; RigidTransform hT;
                for (const auto& pl : res.placements)
                    if ((int)pl.color_id == pr.host) { hsheet = pl.sheet; hT = pl.d_transf; break; }
                if (hsheet < 0) continue;     // host unplaced -> leave the small unplaced (host shows it outside)
                RigidTransform rel = pr.rel;
#if defined(NP_TEST_SEAMS)
                // HARNESS SEAM (NP_TEST_POKE, default 0 = no-op) — the holes-first twin of the poke in
                // fill_cavities: shove the pre-paired small out through its host's hole wall so np_bench
                // can prove the gate reports an escaped child on part_holes_mode 2 as well as mode 1.
                if (g_test_poke > 0.0f) rel.tx += g_test_poke * pr.hole_w;
#endif
                RigidTransform sd = rel.compose().transform(hT.compose()).decompose();
                SheetPlacement sp; sp.color_id = (usize)pr.small; sp.sheet = hsheet; sp.d_transf = sd;
                sp.verts = parts[(size_t)pr.small].first.collision_shape->transform_clone(sd.compose()).vertices;
                res.placements.push_back(std::move(sp));
            }
        }

        // FILL-HOLES: actively pull smaller parts into larger parts' cavities (the relaxation only does
        // so under width pressure). Verified collision-free moves only — cannot create overlaps.
        // (Skipped under holes-first: the pre-pairing above already seated the smalls in their holes.)
        if (fill_holes && !g_holes_first) fill_cavities(res, parts, part_holes_centered, holes_arg, &sheet_dims);
        // GAP-FILL: pull smaller parts FORWARD into earlier sheets' open gaps (so a small part isn't left
        // on a later sheet / outside while it fits where a bigger part couldn't). Verified moves only.
        fill_sheet_gaps(res, parts, holes_arg, sheet_w, sheet_h, &sheet_dims);
#if defined(NP_TEST_SEAMS)
        // HARNESS SEAM (NP_TEST_SHIFT, default 0 = no-op): push the RIGHT-MOST placement further right by
        // a FRACTION of its sheet's width — the same relative defect at every model scale, which is the
        // whole point: np_bench runs it at mm and at m and requires the same verdict, the check an
        // absolute 1.0-model-unit tolerance cannot pass. Right-most so the shift adds directly to the
        // sheet excursion instead of possibly landing the part back inside.
        if (g_test_shift > 0.0f && !res.placements.empty()) {
            usize vic = 0; f32 vx = -F32_MAX;
            for (usize i = 0; i < res.placements.size(); ++i) {
                usize c = res.placements[i].color_id;
                if (c >= parts.size() || !parts[c].first.collision_shape) continue;
                f32 xm = parts[c].first.collision_shape->transform_clone(res.placements[i].d_transf.compose()).bbox.x_max;
                if (xm > vx) { vx = xm; vic = i; }
            }
            SheetPlacement& pv = res.placements[vic];
            auto [bw0, bh0] = dims_for(&sheet_dims, pv.sheet, sheet_w, sheet_h);
            (void)bh0;
            pv.d_transf.tx += g_test_shift * bw0;
            if ((usize)pv.color_id < parts.size() && parts[pv.color_id].first.collision_shape)
                pv.verts = parts[pv.color_id].first.collision_shape->transform_clone(pv.d_transf.compose()).vertices;
        }
#endif  // NP_TEST_SEAMS
        // DROP INVALID: a part that doesn't actually fit (out of bounds / in a hole) is demoted to unplaced
        // (host shows it OUTSIDE the sheets) instead of wasting a fresh sheet on it.
        drop_invalid_placements(res, parts, holes_arg, sheet_w, sheet_h, &sheet_dims);

        // FINAL VERIFICATION of what the host is about to draw. Exact brute-force overlap between parts
        // sharing a sheet, plus a bounds test and a SHEET-KEEP-OUT test against THAT sheet, on the
        // post-pass placements. Published through np_last_quality so the component can warn instead of
        // shipping a bad layout silently.
        {
            std::vector<std::vector<usize>> by_sheet;
            for (usize i = 0; i < res.placements.size(); ++i) {
                int s = res.placements[i].sheet;
                if (s < 0) continue;
                if ((int)by_sheet.size() <= s) by_sheet.resize((usize)s + 1);
                by_sheet[(usize)s].push_back(i);
            }
            // Same index-clamping rule the demotion pass uses, so the two agree on which holes belong to
            // which sheet.
            auto voids_of = [&](int s) -> const std::vector<Polygon>* {
                if (holes_arg.empty()) return nullptr;
                usize hi = (s >= 0 && s < (int)holes_arg.size()) ? (usize)s : holes_arg.size() - 1;
                return &holes_arg[hi];
            };
            int q_ovl = 0, q_oob = 0;
            for (usize s = 0; s < by_sheet.size(); ++s) {
                auto [bw, bh] = dims_for(&sheet_dims, (int)s, sheet_w, sheet_h);
                const std::vector<Polygon>* svoids = voids_of((int)s);
                std::vector<Polygon> shp;
                std::vector<usize> shp_pl;      // placement index behind shp[k] (some placements are skipped)
                // Contact-skin copy of each shape, computed ONCE per part and reused across every pair and
                // by the hole-containment exemption, so both speak the same "touching is not overlapping".
                std::vector<std::optional<Polygon>> shr;
                shp.reserve(by_sheet[s].size());
                shp_pl.reserve(by_sheet[s].size());
                shr.reserve(by_sheet[s].size());
                for (usize k : by_sheet[s]) {
                    usize c = res.placements[k].color_id;
                    if (c >= parts.size() || !parts[c].first.collision_shape) continue;
                    shp.push_back(parts[c].first.collision_shape->transform_clone(res.placements[k].d_transf.compose()));
                    shp_pl.push_back(k);
                    shr.push_back(shrink_verified(shp.back()));
                }
                for (const auto& p : shp) {
                    // Tolerance is per-part and RELATIVE (driver.hpp bounds_tol) — the absolute 1.0 model
                    // units this used to be made the whole check a no-op in a metre-unit document.
                    const f32 BTOL = bounds_tol(bw, bh, p.diameter);
                    if (p.bbox.x_min < -BTOL || p.bbox.y_min < -BTOL ||
                        p.bbox.x_max > bw + BTOL || p.bbox.y_max > bh + BTOL) { q_oob++; continue; }
                    // ...and a part sitting on the sheet's KEEP-OUT is exactly as uncuttable as one
                    // hanging off the edge. This is the safety net for the sheet-id renumbering: the
                    // demotion pass tests holes BEFORE its own renumber, so before sheets_interchangeable
                    // was made hole-aware a part could be relabelled onto a keep-out it was never checked
                    // against and this verification would still call the layout clean.
                    if (svoids)
                        for (const auto& h : *svoids)
                            if (bbox_overlap(p.bbox, h.bbox) && brute_overlap(p, h)) { q_oob++; break; }
                }
                // "does one of b's placed holes hold the whole of a?" — the legitimate hole-nesting the
                // two hole passes produce. shp[] carries SOLID outer rings, so an in-cavity child reads
                // as an overlap until it is excused here; see contained_in_hole for why this cannot also
                // excuse a child that has poked out through the hole wall.
                auto hole_nested = [&](usize a, usize b) -> bool {
                    usize cb = res.placements[shp_pl[b]].color_id;
                    if (cb >= part_holes_centered.size() || part_holes_centered[cb].empty()) return false;
                    AffineTransform bT = res.placements[shp_pl[b]].d_transf.compose();
                    for (const auto& h : part_holes_centered[cb])
                        if (contained_in_hole(shp[a], shr[a] ? &*shr[a] : nullptr, h.transform_clone(bT))) return true;
                    return false;
                };
                for (usize i = 0; i < shp.size(); ++i)
                    for (usize j = i + 1; j < shp.size(); ++j) {
                        if (!bbox_overlap(shp[i].bbox, shp[j].bbox)) continue;
                        if (!real_overlap(shp[i], shp[j], shr[i] ? &*shr[i] : nullptr, shr[j] ? &*shr[j] : nullptr)) continue;
                        if (hole_nested(i, j) || hole_nested(j, i)) continue;   // one sits inside the other's hole
                        q_ovl++;
                    }
            }
            int q_unplaced = part_count;
            for (const auto& pl : res.placements) if (pl.sheet >= 0) q_unplaced--;
            if (q_unplaced < 0) q_unplaced = 0;
            g_q_overlap_pairs.store(q_ovl, std::memory_order_relaxed);
            g_q_out_of_bounds.store(q_oob, std::memory_order_relaxed);
            g_q_unplaced.store(q_unplaced, std::memory_order_relaxed);
            g_q_cancelled.store(g_cancel.load(std::memory_order_relaxed) ? 1 : 0, std::memory_order_relaxed);
        }

        for (const auto& pl : res.placements) {
            usize ci = pl.color_id;            // solver part index
            if (ci >= orig_index.size()) continue;
            int oi = orig_index[ci];           // original input index
            // Fold the part-centering (translate(-input_centroid)) into the placement transform so
            // the host applies ONE rigid transform (rotate about origin, then translate) to its
            // original geometry. M_pre.transform(D) = D . M_pre  => center first, then place.
            AffineTransform mpre = AffineTransform::from_translation(-craw[ci].x, -craw[ci].y);
            AffineTransform finalT = mpre.transform(pl.d_transf.compose());
            RigidTransform rd = finalT.decompose();
            if (out_angle) out_angle[oi] = (double)rd.rotation;
            if (out_tx) out_tx[oi] = (double)rd.tx;
            if (out_ty) out_ty[oi] = (double)rd.ty;
            if (out_sheet_id) out_sheet_id[oi] = pl.sheet;
        }
        if (out_n_sheets) *out_n_sheets = res.n_sheets;
        { std::lock_guard<std::mutex> lk(g_live.mtx); g_live.active = false; }
        return 0;
    } catch (...) {
        { std::lock_guard<std::mutex> lk(g_live.mtx); g_live.active = false; }
        return 1;
    }
}

// --- Cooperative cancel + progress (for an interactive/async host like the Grasshopper component) ---
// np_cancel() asks the currently-running np_nest to stop at the next relaxation round and return the
// best layout it has so far (it does NOT abort mid-evaluation, so the returned nest is always valid).
// np_progress() returns the relaxation-round counter, monotonically increasing during a solve, so the
// host can show a live "rounds done" readout while np_nest runs on a background thread.
// Quality verdict of the last completed np_nest, measured on the FINAL placements (see the g_q_* notes).
// Any out-param may be NULL. Returns 1 when the layout is clean (no overlaps, nothing out of bounds,
// nothing left unplaced, not a cancelled snapshot), else 0 — so a host can gate on the return value alone.
extern "C" NP_EXPORT int np_last_quality(int* out_overlap_pairs, int* out_out_of_bounds,
                                         int* out_unplaced, int* out_cancelled, int* out_demoted) {
    int ovl = g_q_overlap_pairs.load(std::memory_order_relaxed);
    int oob = g_q_out_of_bounds.load(std::memory_order_relaxed);
    int unp = g_q_unplaced.load(std::memory_order_relaxed);
    int can = g_q_cancelled.load(std::memory_order_relaxed);
    int dem = g_q_demoted.load(std::memory_order_relaxed);
    if (out_overlap_pairs) *out_overlap_pairs = ovl;
    if (out_out_of_bounds) *out_out_of_bounds = oob;
    if (out_unplaced)      *out_unplaced      = unp;
    if (out_cancelled)     *out_cancelled     = can;
    if (out_demoted)       *out_demoted       = dem;
    return (ovl == 0 && oob == 0 && unp == 0 && can == 0) ? 1 : 0;
}

extern "C" NP_EXPORT void np_cancel()       { g_cancel.store(true); }
extern "C" NP_EXPORT void np_cancel_reset() { g_cancel.store(false); }
extern "C" NP_EXPORT long long np_progress() { return (long long)g_iter_pub.load(std::memory_order_relaxed); }

// Snapshot the CURRENT best layout mid-solve for an animated host preview. Bands the running strip
// into sheet-width columns and folds in part-centering, exactly like np_nest's final output, so the
// preview is in the same (angle, tx, ty, sheet) convention. Host preallocates buffers length
// part_count; unplaced parts come back as sheet_id -1. Returns the number of placed parts (0 if no
// solve is active yet). Thread-safe: reads g_live under its mutex while the solve thread writes it.
extern "C" NP_EXPORT int np_poll_layout(
    int part_count,
    double* out_tx, double* out_ty, double* out_angle, int* out_sheet_id, int* out_n_sheets)
{
    for (int i = 0; i < part_count; ++i) {
        if (out_tx) out_tx[i] = 0.0; if (out_ty) out_ty[i] = 0.0;
        if (out_angle) out_angle[i] = 0.0; if (out_sheet_id) out_sheet_id[i] = -1;
    }
    if (out_n_sheets) *out_n_sheets = 0;
    std::lock_guard<std::mutex> lk(g_live.mtx);
    if (!g_live.active || g_live.items.empty()) return 0;
    f32 sw = g_live.sheet_w > 0.0f ? g_live.sheet_w : 1.0f;
    int base = g_live.sheet_base, maxb = g_live.max_bins;
    int max_sheet = 0, written = 0;
    for (const auto& it : g_live.items) {
        usize ci = it.color_id;
        if (ci >= g_poll_orig_index.size() || ci >= g_poll_craw.size()) continue;
        int oi = g_poll_orig_index[ci];
        if (oi < 0 || oi >= part_count) continue;
        int band = (int)(it.xmin / sw); if (band < 0) band = 0;
        int sh = base + band; if (sh > maxb - 1) sh = maxb - 1;
        f32 dx = -(f32)(sh - base) * sw;
        AffineTransform mpre = AffineTransform::from_translation(-g_poll_craw[ci].x, -g_poll_craw[ci].y);
        RigidTransform dtb = it.dt; dtb.tx += dx;
        AffineTransform finalT = mpre.transform(dtb.compose());
        RigidTransform rd = finalT.decompose();
        if (out_angle) out_angle[oi] = (double)rd.rotation;
        if (out_tx) out_tx[oi] = (double)rd.tx;
        if (out_ty) out_ty[oi] = (double)rd.ty;
        if (out_sheet_id) out_sheet_id[oi] = sh;
        if (sh + 1 > max_sheet) max_sheet = sh + 1;
        ++written;
    }
    if (out_n_sheets) *out_n_sheets = max_sheet;
    return written;
}
