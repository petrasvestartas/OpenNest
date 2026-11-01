// fit_mode 2: GLOBAL BIN PACKING as a "k-column strip".
//
// greedy_fill (driver.hpp) strip-packs ALL remaining parts, cuts the first sheet-wide column off as a
// finished sheet and carries every part crossing x = W to the next iteration: strip packing misused as
// bin packing (parts straddling the cut are lost from a nearly full sheet, later sheets only get the
// spill, nothing ever moves back). Here k sheets are ONE strip of height H holding k columns of width W,
// separated by keep-out gaps of width G:
//
//     column c = [c*P, c*P + W]   (P = W + G, rounded up to a multiple of 2^-8)
//     gap c    = keep-out hole [c*P + W - guard, (c+1)*P + guard] x [0, H]
//     strip width for k columns = (k-1)*P + W - guard
//
// The gap holes are ordinary sheet holes (Strip::set_holes -> quality-0 zone -> ObstacleKind::Hole, a
// container-loss term in the OverlapTracker), so the unchanged separator/sampler machinery does the
// rest: the uniform container sampler proposes cross-sheet moves and perturb_layout swaps large parts
// between sheets. The objective is "empty the LAST column": exploration shrinks only the last column
// and, once a feasible layout leaves it empty, SNAPS to k-1 columns. Compression then squeezes the last
// column (a usable remnant). Everything is derived from W, H and the part areas - no instance constants.
//
// The guard (4 ulps of the widest coordinate) widens every keep-out and pulls the right wall in, so every
// part stays >= guard inside its column and the column -> sheet translation (x - c*P, exact for P a
// multiple of 2^-8) lands it inside [0, W] after f32 rounding.
//
// Dev env knobs: NP_BIN_GAP_FRAC (G as a fraction of W, default 0.10), NP_BIN_QT_DEPTH (quadtree depth
// of the wide strip; default = the engine depth), NP_BIN_STEP_CAP (max exploration shrink step as a fraction of W, default 0.02),
// NP_BIN_ROTATE_COL=1 (rotate the least-filled column to the end; default off), NP_BIN_EVICT=1 (+ NP_BIN_EVICT_EVERY,
// default 4: targeted eviction of the last column), NP_BIN_VERBOSE=1 (progress lines on stderr).
#pragma once
#include "driver.hpp"
#include <cstdlib>
#include <cmath>
#include <limits>
#include <algorithm>
#include <map>
#include <functional>
#include <memory>

namespace nest {

inline int bin_popcount(uint32_t v) { int n = 0; while (v) { v &= v - 1; ++n; } return n; }

struct ColumnGeom {
    f32 W = 0.0f, H = 0.0f, G = 0.0f, P = 0.0f, guard = 0.0f;
    int k_hi = 1;
};

inline f32 bin_env_f32(const char* name, f32 dflt) {
    if (const char* e = std::getenv(name)) { f32 v = (f32)std::atof(e); if (v > 0.0f) return v; }
    return dflt;
}
inline bool bin_verbose() { const char* e = std::getenv("NP_BIN_VERBOSE"); return e && std::atoi(e) != 0; }

inline ColumnGeom make_column_geom(f32 W, f32 H, int k_hi) {
    ColumnGeom cg;
    cg.W = W; cg.H = H; cg.k_hi = k_hi > 0 ? k_hi : 1;
    const f32 frac = bin_env_f32("NP_BIN_GAP_FRAC", 0.10f);
    cg.G = std::max(frac * W, 1e-3f);
    // P rounded UP to a multiple of 2^-8 (in double, then the f32 at or above it). Below 2^16 every
    // multiple of 2^-8 is an f32; above it every f32 is one. So c*P is exact for small c and x - c*P
    // introduces no error beyond the rounding of x itself.
    double pd = std::ceil(((double)W + (double)cg.G) * 256.0) / 256.0;
    f32 pf = (f32)pd;
    if ((double)pf < pd) pf = std::nextafter(pf, std::numeric_limits<f32>::infinity());
    cg.P = pf;
    const f32 xmax = (f32)cg.k_hi * cg.P + W;
    const f32 ulp = std::nextafter(xmax, std::numeric_limits<f32>::infinity()) - xmax;
    cg.guard = 4.0f * ulp;
    return cg;
}

// Strip width holding k columns: the right wall sits `guard` inside column k-1's right edge.
inline f32 columns_width(const ColumnGeom& cg, int k) {
    if (k < 1) k = 1;
    return (f32)((double)(k - 1) * (double)cg.P + (double)cg.W - (double)cg.guard);
}

// Keep-out set for k columns: the k-1 gaps between them, plus each column's own sheet holes
// (per_sheet_holes[min(c, last)]) translated by +c*P. Generated in a fixed order so equal k gives an
// equal hole list (Strip::operator== compares them).
// `walls` (optional, NP_BIN_COLWALLS): per-column local right wall w_c <= W; the keep-out after column c
// then starts at c*P + w_c - guard. nullptr (or w_c == W) gives exactly the plain gap.
inline std::vector<Polygon> column_holes(const ColumnGeom& cg, int k,
                                         const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                         const std::vector<f32>* walls = nullptr) {
    std::vector<Polygon> out;
    for (int c = 0; c + 1 < k; ++c) {
        const f32 wc = (walls && c < (int)walls->size()) ? (*walls)[(usize)c] : cg.W;
        f32 x0 = (f32)((double)c * cg.P + wc - cg.guard);
        f32 x1 = (f32)((double)(c + 1) * cg.P + cg.guard);
        out.push_back(Polygon::from_rect(Rect::try_new(x0, 0.0f, x1, cg.H)));
    }
    if (!per_sheet_holes.empty()) {
        for (int c = 0; c < k; ++c) {
            usize hi = (c < (int)per_sheet_holes.size()) ? (usize)c : per_sheet_holes.size() - 1;
            f32 dx = (f32)((double)c * cg.P);
            for (const auto& h : per_sheet_holes[hi]) {
                if (c == 0) { out.push_back(h); continue; }
                std::vector<Point> v = h.vertices;
                for (auto& p : v) p.x += dx;
                try { out.push_back(Polygon::create(std::move(v))); } catch (...) {}
            }
        }
    }
    return out;
}

inline int column_of(const ColumnGeom& cg, f32 x, int k) {
    int c = (int)std::floor(x / cg.P);
    if (c < 0) c = 0;
    if (c > k - 1) c = k - 1;
    return c;
}

// Parts not wholly inside the first k-1 columns (x_max beyond the (k-1)-column wall).
inline int last_column_count(const Layout& l, const ColumnGeom& cg, int k) {
    if (k <= 1) return (int)l.placed_parts.size();
    const f32 thr = columns_width(cg, k - 1);
    int n = 0;
    l.placed_parts.for_each([&](PartKey, const PlacedPart& pi) { if (pi.shape->bbox.x_max > thr) ++n; });
    return n;
}

// Can `part` fit a w x h column at some allowed orientation? (A part lies inside a rectangle iff its
// bbox does.) Continuous parts are probed at 360 orientations. Parts that cannot are left unplaced
// instead of sending the LBF builder into an endless widening loop.
inline bool fits_column(const Part& part, f32 w, f32 h) {
    std::vector<f32> rots;
    switch (part.allowed_rotation.kind) {
        case RotationKind::None: rots = {0.0f}; break;
        case RotationKind::Discrete: rots = part.allowed_rotation.discrete; break;
        case RotationKind::Continuous:
            for (int i = 0; i < 360; ++i) rots.push_back(2.0f * PI_F * (f32)i / 360.0f);
            break;
    }
    const auto& V = part.collision_shape->vertices;
    for (f32 r : rots) {
        f32 c = std::cos(r), s = std::sin(r);
        f32 x0 = F32_MAX, x1 = -F32_MAX, y0 = F32_MAX, y1 = -F32_MAX;
        for (const auto& v : V) {
            f32 x = c * v.x - s * v.y, y = s * v.x + c * v.y;
            x0 = min_f(x0, x); x1 = max_f(x1, x); y0 = min_f(y0, y); y1 = max_f(y1, y);
        }
        if (x1 - x0 <= w && y1 - y0 <= h) return true;
    }
    return false;
}

struct BinSolution {
    StripSolution sol;
    int k = 0;
    f32 last_w = 0.0f;       // extent of the last column used by the strip (strip width - (k-1)*P)
    std::vector<f32> walls;  // NP_BIN_COLWALLS: local right wall of every column (empty => all W)
};

struct BinParams {
    f32 step_cap = 0.02f;    // max shrink step (fraction of W) the adaptive step may grow to
    bool bisect = true;      // feasibility-wall bisection (g_expl_bisect logic), on by default here
    bool rotate_col = false; // NP_BIN_ROTATE_COL=1 (default OFF: measured neutral-to-harmful, see README;
                             //   forced off when per-column holes differ)
    bool evict = false;      // NP_BIN_EVICT=1
    int evict_every = 4;     // NP_BIN_EVICT_EVERY: evict after every N failed relaxations since the last SNAP
    int colwalls = 0;        // NP_BIN_COLWALLS: 0 off; 1 = per-column wall compaction sweep on a stall, then
                             //   release the walls (free right-end bands); 2 = sweep and KEEP the walls
    int sweep_after = 1;     // NP_BIN_SWEEP_AFTER: a stall = this many failed relaxations in a row
                             //   (a failed relaxation already costs ~1000 rounds, so 1 by default)
};

// Smallest strip width the last of k columns may shrink to (keeps the column a non-degenerate region).
inline f32 last_column_floor(const ColumnGeom& cg, int k) {
    return (f32)((double)(k - 1) * cg.P + 4.0 * cg.guard + 1e-4 * cg.W);
}

// Drop the (empty) last column: remove its gap + sheet holes and pull the wall to columns_width(k-1).
// Nothing shifts (split beyond every part); the removed holes lie wholly beyond the new wall, so the
// incremental tracker rebuild stays exact.
// With column walls the removed gap may start left of the new wall, but a SNAP only happens on a
// loss-0 layout, where every hole loss is 0 before and after: the incremental rebuild stays exact.
// The new last column's wall resets to W (its extent is the strip wall again).
inline void snap_down(Relaxer& relaxer, const ColumnGeom& cg, int& k,
                      const std::vector<std::vector<Polygon>>& per_sheet_holes,
                      std::vector<f32>* walls = nullptr) {
    k -= 1;
    if (walls && (usize)(k - 1) < walls->size()) (*walls)[(usize)(k - 1)] = cg.W;
    std::vector<Polygon> holes = column_holes(cg, k, per_sheet_holes, walls);
    relaxer.change_strip_width(columns_width(cg, k), std::numeric_limits<f32>::max(), &holes);
}

// Run statistics (NP_BIN_VERBOSE summary).
struct BinStats {
    int snaps = 0;
    int evict_tried = 0, evict_ok = 0;
    int fails = 0;          // failed (loss > 0) relaxations during exploration
    int sweeps = 0, wall_tries = 0, wall_ok = 0;   // NP_BIN_COLWALLS
    int col_rotations = 0;
    unsigned long long start_round = 0, last_snap_round = 0;
};

// Placed part AREA per column (bucketed by centroid column).
inline std::vector<double> column_areas(const Relaxer& relaxer, const ColumnGeom& cg, int k) {
    std::vector<double> a((usize)std::max(k, 1), 0.0);
    relaxer.prob.layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
        a[(usize)column_of(cg, pi.shape->centroid().x, k)] += relaxer.instance.part(pi.part_id).area();
    });
    return a;
}
inline int column_count(const Relaxer& relaxer, const ColumnGeom& cg, int k, int c) {
    int n = 0;
    relaxer.prob.layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
        if (column_of(cg, pi.shape->centroid().x, k) == c) ++n;
    });
    return n;
}

// LEVER (NP_BIN_ROTATE_COL=1, default off): move the least-filled column's parts to the LAST column and the
// last column's parts to its place (rigid +-delta*P translations through relocate_part, which keeps the
// tracker exact). Only legal when columns are interchangeable (identical per-column holes) and the last
// column is full width (exploration start / right after a SNAP). The width shrink then attacks the
// emptiest column instead of whatever happened to be last. If the swap is not loss-free (f32 rounding
// against a keep-out), it is rolled back. Returns true if a swap was made.
inline bool rotate_emptiest_to_end(Relaxer& relaxer, const ColumnGeom& cg, int k) {
    if (k < 2) return false;
    std::vector<double> a = column_areas(relaxer, cg, k);
    int cmin = 0;
    for (int c = 1; c < k; ++c) if (a[(usize)c] < a[(usize)cmin]) cmin = c;
    if (cmin == k - 1 || !(a[(usize)cmin] < a[(usize)(k - 1)])) return false;
    StripSolution before = relaxer.prob.save();
    const f32 delta = (f32)((double)(k - 1 - cmin) * cg.P);
    std::vector<std::pair<PartKey, RigidTransform>> mv;
    relaxer.prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
        int c = column_of(cg, pi.shape->centroid().x, k);
        RigidTransform dt = pi.d_transf;
        if (c == cmin) { dt.tx += delta; mv.emplace_back(pk, dt); }
        else if (c == k - 1) { dt.tx -= delta; mv.emplace_back(pk, dt); }
    });
    for (auto& [pk, dt] : mv) relaxer.relocate_part(pk, dt);
    if (relaxer.ct.get_total_loss() != 0.0f) { relaxer.rollback(before, nullptr); return false; }
    return true;
}

// Best sample for `part` drawn from `regions` (each with its own sample count), scored by `evaluator`
// (RelaxEvaluator = weighted overlap): the container sampler of search_placement restricted to the
// regions, no focussed sampling, then the usual two coordinate-descent refinements.
inline std::optional<std::pair<RigidTransform, SampleEval>> search_placement_in(
        const Layout& l, const Part& part, SampleEvaluator& evaluator, SampleConfig sc, Rng& rng,
        const std::vector<std::pair<Rect, usize>>& regions) {
    f32 part_min_dim = min_f(part.collision_shape->bbox.width(), part.collision_shape->bbox.height());
    BestSamples best_samples(sc.n_coord_descents, part_min_dim * UNIQUE_SAMPLE_THRESHOLD);
    for (const auto& [region, n] : regions) {
        auto sampler = UniformBBoxSampler::make(region, part, l.container.collision_outer->bbox);
        if (!sampler) continue;
        for (usize i = 0; i < n; ++i) {
            RigidTransform dt = sampler->sample(rng);
            SampleEval eval = evaluator.evaluate_sample(dt, best_samples.upper_bound());
            best_samples.report(dt, eval);
        }
    }
    std::vector<std::pair<RigidTransform, SampleEval>> starts = best_samples.samples;
    for (auto& st : starts) {
        auto d = refine_coord_desc(st, evaluator, prerefine_cd_config(part), rng);
        best_samples.report(d.first, d.second);
    }
    if (auto bb = best_samples.best()) return refine_coord_desc(*bb, evaluator, final_refine_cd_config(part), rng);
    return std::nullopt;
}

// Free right-end band of every non-last column: [x_max of its parts, c*P + W - guard] (strip coords).
inline std::vector<std::pair<f32, f32>> column_free_bands(const Relaxer& relaxer, const ColumnGeom& cg, int k) {
    std::vector<f32> used((usize)std::max(k, 1), 0.0f);
    for (int c = 0; c < k; ++c) used[(usize)c] = (f32)((double)c * cg.P);
    relaxer.prob.layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
        int c = column_of(cg, pi.shape->centroid().x, k);
        used[(usize)c] = max_f(used[(usize)c], pi.shape->bbox.x_max);
    });
    std::vector<std::pair<f32, f32>> bands;
    for (int c = 0; c + 1 < k; ++c) {
        f32 x1 = (f32)((double)c * cg.P + cg.W - cg.guard);
        if (x1 - used[(usize)c] > 1e-3f * cg.W) bands.emplace_back(used[(usize)c], x1);
    }
    return bands;
}

// LEVER (NP_BIN_EVICT=1): relocate every part of the last column to its best (least weighted overlap)
// pose inside columns 0..k-2; the caller then relaxes. With `bands` (after a column-wall sweep), half the
// samples go to the freed right-end bands (split by band width), half to the whole region.
inline int evict_last_column(Relaxer& relaxer, const ColumnGeom& cg, int k, SampleConfig sc,
                             const std::vector<std::pair<f32, f32>>* bands = nullptr) {
    if (k < 2) return 0;
    const f32 thr = columns_width(cg, k - 1);
    std::vector<PartKey> pks;
    relaxer.prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
        if (pi.shape->bbox.x_max > thr) pks.push_back(pk);
    });
    const usize n_total = sc.n_container_samples * 4;   // a one-off global move: sample harder
    std::vector<std::pair<Rect, usize>> regions;
    if (bands && !bands->empty()) {
        f32 tot = 0.0f;
        for (const auto& [x0, x1] : *bands) tot += x1 - x0;
        for (const auto& [x0, x1] : *bands)
            regions.emplace_back(Rect(x0, 0.0f, x1, cg.H), (usize)std::ceil((double)n_total * 0.5 * (x1 - x0) / tot));
        regions.emplace_back(Rect(0.0f, 0.0f, thr, cg.H), n_total / 2);
    } else {
        regions.emplace_back(Rect(0.0f, 0.0f, thr, cg.H), n_total);
    }
    int moved = 0;
    for (PartKey pk : pks) {
        const Part& part = relaxer.instance.part(relaxer.prob.layout.placed_parts[pk].part_id);
        RelaxEvaluator ev(relaxer.prob.layout, part, pk, relaxer.ct);
        auto best = search_placement_in(relaxer.prob.layout, part, ev, sc, relaxer.rng, regions);
        if (!best) continue;
        relaxer.relocate_part(pk, best->first);
        ++moved;
    }
    return moved;
}

// ---- LEVER (NP_BIN_COLWALLS): per-column right walls ----
// Install `walls` as the keep-out set (container rebuilt) and rebuild the tracker FROM SCRATCH: a wall
// moving left grows a keep-out INTO column c, which the incremental width-change rebuild cannot cover.
inline void apply_walls(Relaxer& relaxer, const ColumnGeom& cg, int k, const std::vector<f32>& walls,
                        const std::vector<std::vector<Polygon>>& per_sheet_holes) {
    relaxer.prob.strip.set_holes(column_holes(cg, k, per_sheet_holes, &walls));
    relaxer.prob.change_strip_width(relaxer.prob.strip_width());
    relaxer.ct = OverlapTracker(relaxer.prob.layout);
}

// Move column c's wall to new_w: parts of column c right of its middle shift by (new_w - w_c) (like the
// strip-width split), then the walls are re-installed.
inline void set_column_wall(Relaxer& relaxer, const ColumnGeom& cg, int k, int c, f32 new_w,
                            std::vector<f32>& walls, const std::vector<std::vector<Polygon>>& per_sheet_holes) {
    const f32 split = (f32)((double)c * cg.P + 0.5 * walls[(usize)c]);
    const f32 delta = new_w - walls[(usize)c];
    std::vector<std::pair<PartKey, RigidTransform>> mv;
    relaxer.prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
        Point ct = pi.shape->centroid();
        if (column_of(cg, ct.x, k) == c && ct.x > split) mv.emplace_back(pk, pi.d_transf);
    });
    for (auto& [pk, dt] : mv) {
        usize part_id = relaxer.prob.layout.placed_parts[pk].part_id;
        relaxer.prob.remove_item(pk);
        RigidTransform nd = dt; nd.tx += delta;
        relaxer.prob.place_item(Placement{part_id, nd});
    }
    walls[(usize)c] = new_w;
    apply_walls(relaxer, cg, k, walls, per_sheet_holes);
}

// COMPACTION SWEEP over columns 0..k-2, densest first: shrink each column's wall by its adaptive step
// (relative to W) while the relaxation stays feasible (up to 8 times per column), revert the failing
// attempt. Sweep relaxations use a short separator budget (1 strike x 50 rounds) so a failed attempt is
// cheap. Starts from and returns to a FEASIBLE layout; `best` is updated on every accepted shrink.
inline int sweep_columns(Relaxer& relaxer, const ColumnGeom& cg, int k, std::vector<f32>& walls,
                         std::vector<f32>& col_step, const std::vector<std::vector<Polygon>>& per_sheet_holes,
                         const Terminator& term, SolutionListener& listener, const BinParams& bp,
                         BinStats& st, BinSolution& best) {
    if (k < 2) return 0;
    st.sweeps++;
    std::vector<double> a = column_areas(relaxer, cg, k);
    std::vector<int> order;
    for (int c = 0; c + 1 < k; ++c) order.push_back(c);
    std::stable_sort(order.begin(), order.end(), [&](int x, int y) {
        return a[(usize)x] / walls[(usize)x] > a[(usize)y] / walls[(usize)y];
    });
    const RelaxConfig saved = relaxer.config;
    relaxer.config.strike_limit = 1;
    relaxer.config.iter_no_imprv_limit = std::min<usize>(saved.iter_no_imprv_limit, 50);
    int accepted = 0;
    for (int c : order) {
        for (int t = 0; t < 8 && !term.kill(); ++t) {
            f32 nw = walls[(usize)c] - col_step[(usize)c] * cg.W;
            if (nw < 0.05f * cg.W) break;
            StripSolution pre = relaxer.prob.save();
            std::vector<f32> pre_w = walls;
            st.wall_tries++;
            set_column_wall(relaxer, cg, k, c, nw, walls, per_sheet_holes);
            auto r = relaxer.relax(term, listener);
            if (r.second.get_total_loss() == 0.0f) {
                accepted++; st.wall_ok++;
                col_step[(usize)c] = std::min(bp.step_cap, col_step[(usize)c] * 2.0f);
                best = BinSolution{relaxer.prob.save(), k, best.last_w, walls};
            } else {
                walls = pre_w;
                relaxer.rollback(pre, nullptr);
                col_step[(usize)c] = std::max(0.0005f, col_step[(usize)c] * 0.5f);
                break;
            }
        }
    }
    relaxer.config = saved;
    return accepted;
}

// Copy of exploration_phase (optimizer.hpp) with (a) a shrink step of cur_step * W, (b) the split inside
// the last column, (c) the SNAP to k-1 columns once a feasible layout leaves the last column empty, plus
// the optional column-rotation and eviction levers (BinParams). Returns the best FEASIBLE solution
// (fewest columns, then narrowest last column).
inline BinSolution column_exploration_phase(const StripInstance& instance, Relaxer& relaxer,
                                            SolutionListener& listener, const Terminator& term,
                                            const ExplorationConfig& config, const ColumnGeom& cg, int k,
                                            const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                            const BinParams& bp, BinStats& st) {
    const bool verbose = bin_verbose();
    st.start_round = st.last_snap_round = g_iter_count;
    if (bp.rotate_col && rotate_emptiest_to_end(relaxer, cg, k)) st.col_rotations++;
    f32 current_width = relaxer.prob.strip_width();
    std::vector<f32> walls((usize)std::max(cg.k_hi, k), cg.W);   // all W => identical to the plain gaps
    std::vector<f32> col_step(walls.size(), 0.005f);
    int dead_sweeps = 0;                                          // consecutive sweeps that accepted nothing
    BinSolution best{relaxer.prob.save(), k, current_width - (f32)((double)(k - 1) * cg.P), walls};
    f32 best_width = current_width;
    listener.report(ReportType::ExplFeas, best.sol, instance);

    std::vector<std::pair<StripSolution, f32>> infeas_pool;
    const f32 base_step = config.shrink_step;
    f32 cur_step = base_step;
    int halvings = 0;
    const int BISECT_K = 6;
    int n_fail = 0;              // failed relaxations since the last SNAP (eviction cadence)
    bool just_evicted = false;   // the relax about to run follows an eviction

    // shrink the last column to `next` (split at the middle of the last column's current extent)
    auto shrink_to = [&](f32 next) {
        f32 w = relaxer.prob.strip_width();
        f32 col0 = (f32)((double)(k - 1) * cg.P);
        f32 split = col0 + 0.5f * (w - col0);
        relaxer.change_strip_width(next, split);
        current_width = next;
    };
    auto perturb_from_pool = [&]() {
        if (bp.bisect) { cur_step = base_step; halvings = 0; }
        if (!infeas_pool.empty()) {
            f32 sample = std::min(std::fabs(config.solution_pool_distribution_stddev * relaxer.rng.gen_normal()), 0.999f);
            usize idx = (usize)(sample * (f32)infeas_pool.size());
            relaxer.rollback(infeas_pool[idx].first, nullptr);
            current_width = relaxer.prob.strip_width();
        }
        perturb_layout(relaxer, config);
    };
    auto evict = [&](const std::vector<std::pair<f32, f32>>* bands = nullptr) {
        st.evict_tried++;
        evict_last_column(relaxer, cg, k, config.separator_config.sample_config, bands);
        just_evicted = true;
    };
    auto rollback_best = [&]() {
        relaxer.rollback(best.sol, nullptr);
        walls = best.walls;
        current_width = relaxer.prob.strip_width();
    };
    // STALL handler (NP_BIN_COLWALLS): from the best feasible layout, compact columns 0..k-2 against their
    // own walls; mode 1 then releases the walls so the squeezed-out right-end bands become free space (and
    // eviction, if on, samples them first); mode 2 keeps the walls. Returns false when sweeping is spent.
    auto stall_sweep = [&]() -> bool {
        if (!bp.colwalls || k < 2 || dead_sweeps >= 2) return false;
        rollback_best();
        int acc = sweep_columns(relaxer, cg, k, walls, col_step, per_sheet_holes, term, listener, bp, st, best);
        dead_sweeps = acc > 0 ? 0 : dead_sweeps + 1;
        const std::vector<f32> swept = walls;   // for the verbose line (mode 1 releases them below)
        std::vector<std::pair<f32, f32>> bands;
        if (bp.colwalls == 1) {
            std::fill(walls.begin(), walls.end(), cg.W);
            apply_walls(relaxer, cg, k, walls, per_sheet_holes);    // keep-outs only shrink: stays feasible
            best = BinSolution{relaxer.prob.save(), k, best.last_w, walls};
            bands = column_free_bands(relaxer, cg, k);
        }
        current_width = relaxer.prob.strip_width();
        infeas_pool.clear();
        cur_step = base_step; halvings = 0;
        if (verbose) {
            std::fprintf(stderr, "[bin] SWEEP round=%llu k=%d accepted=%d walls:", (unsigned long long)g_iter_count, k, acc);
            for (int c = 0; c + 1 < k; ++c) std::fprintf(stderr, " %.1f%%", 100.0 * swept[(usize)c] / cg.W);
            if (!bands.empty()) {
                f32 tot = 0.0f; for (auto& [x0, x1] : bands) tot += x1 - x0;
                std::fprintf(stderr, " | free bands %zu, %.1f%% of W total", bands.size(), 100.0 * tot / cg.W);
            }
            std::fprintf(stderr, "\n");
        }
        if (bp.evict) evict(bands.empty() ? nullptr : &bands);
        return true;
    };

    while (!term.kill()) {
        auto local_best = relaxer.relax(term, listener);
        f32 total_loss = local_best.second.get_total_loss();
        const bool after_evict = just_evicted;
        just_evicted = false;

        if (total_loss == 0.0f) {
            if (k > 1 && last_column_count(relaxer.prob.layout, cg, k) == 0) {
                snap_down(relaxer, cg, k, per_sheet_holes, bp.colwalls ? &walls : nullptr);
                st.snaps++;
                n_fail = 0;
                dead_sweeps = 0;
                if (after_evict) st.evict_ok++;
                bool rot = bp.rotate_col && rotate_emptiest_to_end(relaxer, cg, k);
                if (rot) st.col_rotations++;
                current_width = relaxer.prob.strip_width();
                best_width = current_width;
                best = BinSolution{relaxer.prob.save(), k, current_width - (f32)((double)(k - 1) * cg.P), walls};
                listener.report(ReportType::ExplFeas, best.sol, instance);
                if (verbose) {
                    std::vector<double> a = column_areas(relaxer, cg, k);
                    std::fprintf(stderr, "[bin] SNAP round=%llu -> k=%d (%llu rounds since last)%s%s; new last column: %d parts, %.1f%% of a sheet\n",
                                 (unsigned long long)g_iter_count, k, (unsigned long long)(g_iter_count - st.last_snap_round),
                                 after_evict ? " [after eviction]" : "", rot ? " [rotated emptiest column to end]" : "",
                                 column_count(relaxer, cg, k, k - 1), 100.0 * a[(usize)(k - 1)] / ((double)cg.W * cg.H));
                }
                st.last_snap_round = g_iter_count;
            } else if (current_width < best_width) {
                best_width = current_width;
                best = BinSolution{local_best.first, k, current_width - (f32)((double)(k - 1) * cg.P), walls};
                listener.report(ReportType::ExplFeas, best.sol, instance);
            }
            infeas_pool.clear();
            if (bp.bisect) { cur_step = std::min(bp.step_cap, cur_step * 2.0f); halvings = 0; }
            f32 next = std::max(current_width - cur_step * cg.W, last_column_floor(cg, k));
            if (next < current_width) shrink_to(next);
            else if (stall_sweep()) {}              // last column at its floor: compact the other columns
            else if (bp.evict && k > 1 && last_column_count(relaxer.prob.layout, cg, k) <= 3) evict();
            else perturb_layout(relaxer, config);   // last column at its floor yet not empty: shake it
        } else {
            listener.report(ReportType::ExplInfeas, local_best.first, instance);
            auto pos = std::lower_bound(infeas_pool.begin(), infeas_pool.end(), total_loss,
                                        [](const std::pair<StripSolution, f32>& e, f32 v) { return e.second < v; });
            infeas_pool.insert(pos, {local_best.first, total_loss});

            usize cap = config.max_conseq_failed_attempts.value_or(std::numeric_limits<usize>::max());
            if (infeas_pool.size() >= cap) break;
            st.fails++;

            ++n_fail;
            if (bp.colwalls && halvings + 1 >= bp.sweep_after && stall_sweep()) {
                // stalled squeeze: the sweep compacted the other columns (and evicted, if on)
            } else if (bp.evict && k > 1 && (n_fail % bp.evict_every) == 0) {
                // every N failures: from the best feasible layout, move the last column's parts into the
                // other columns' free space, then relax (pool/perturb stays the fallback path).
                rollback_best();
                evict();
            } else if (bp.bisect && halvings < BISECT_K) {
                halvings++;
                cur_step *= 0.5f;
                rollback_best();   // same k => same hole set
                f32 next = std::max(best_width - cur_step * cg.W, last_column_floor(cg, k));
                if (next < current_width) shrink_to(next);
                else perturb_from_pool();
            } else {
                perturb_from_pool();
            }
        }
    }
    return best;
}

// Copy of attempt_to_compress / compression_phase (optimizer.hpp) that only shrinks the LAST column
// (split inside it, step r_shrink * W). A compressed layout that empties the last column snaps to k-1.
inline std::optional<StripSolution> attempt_to_compress_column(Relaxer& relaxer, const BinSolution& init,
                                                               const ColumnGeom& cg, f32 r_shrink,
                                                               const Terminator& term, SolutionListener& listener) {
    // Same column count => same hole list; only pass holes when they differ (set_holes drops the cached
    // hole zone). The width change just lines the strip up so the rollback below takes the fast path.
    const bool same = relaxer.prob.strip.holes.size() == init.sol.strip.holes.size() &&
                      std::equal(relaxer.prob.strip.holes.begin(), relaxer.prob.strip.holes.end(),
                                 init.sol.strip.holes.begin(), [](const Polygon& a, const Polygon& b) {
                                     return a.vertices.size() == b.vertices.size() &&
                                            std::equal(a.vertices.begin(), a.vertices.end(), b.vertices.begin(),
                                                       [](const Point& p, const Point& q) { return p == q; });
                                 });
    relaxer.change_strip_width(init.sol.strip_width(), std::numeric_limits<f32>::max(),
                               same ? nullptr : &init.sol.strip.holes);
    relaxer.rollback(init.sol, nullptr);
    const int k = init.k;
    f32 w = relaxer.prob.strip_width();
    f32 new_width = std::max(w - r_shrink * cg.W, last_column_floor(cg, k));
    if (!(new_width < w)) return std::nullopt;
    f32 col0 = (f32)((double)(k - 1) * cg.P);
    f32 split_pos = relaxer.rng.random_range_f32(col0, w);
    relaxer.change_strip_width(new_width, split_pos);
    auto [compacted, ot] = relaxer.relax(term, listener);
    if (ot.get_total_loss() == 0.0f) return compacted;
    return std::nullopt;
}

inline BinSolution column_compression_phase(const StripInstance& instance, Relaxer& relaxer, BinSolution best,
                                            SolutionListener& listener, const Terminator& term,
                                            const CompressionConfig& config, const ColumnGeom& cg,
                                            const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                            const BinParams* bp = nullptr, BinStats* st = nullptr) {
    // NP_BIN_COLWALLS: one wall sweep over every non-last column first (minimises the used width of each
    // sheet; walls are kept), then the usual last-column compression on that layout.
    if (bp && bp->colwalls && st && best.k > 1 && !term.kill()) {
        std::vector<f32> walls = best.walls.empty() ? std::vector<f32>((usize)std::max(cg.k_hi, best.k), cg.W) : best.walls;
        std::vector<f32> col_step(walls.size(), 0.005f);
        relaxer.rollback(best.sol, nullptr);
        int acc = sweep_columns(relaxer, cg, best.k, walls, col_step, per_sheet_holes, term, listener, *bp, *st, best);
        if (bin_verbose()) std::fprintf(stderr, "[bin] final SWEEP accepted=%d\n", acc);
    }
    auto progress_now = []() -> double { return g_iter_mode ? (double)g_iter_count : now_seconds(); };
    double start = progress_now();
    int n_failed = 0;
    auto shrink_step_size = [&](int n_failed_attempts) -> f32 {
        if (config.shrink_decay.kind == ShrinkDecayKind::TimeBased) {
            f32 range = config.shrink_range.second - config.shrink_range.first;
            f32 ratio = (f32)((progress_now() - start) / config.time_limit_secs);
            return config.shrink_range.first + ratio * range;
        } else {
            return config.shrink_range.first * std::pow(config.shrink_decay.failure_ratio, (f32)n_failed_attempts);
        }
    };
    while (!term.kill()) {
        f32 step = shrink_step_size(n_failed);
        if (step < config.shrink_range.second) break;
        auto compacted = attempt_to_compress_column(relaxer, best, cg, step, term, listener);
        if (compacted) {
            listener.report(ReportType::CmprFeas, *compacted, instance);
            int k = best.k;
            std::vector<f32> walls = best.walls;
            if (k > 1 && last_column_count(relaxer.prob.layout, cg, k) == 0) {
                snap_down(relaxer, cg, k, per_sheet_holes, walls.empty() ? nullptr : &walls);
                best = BinSolution{relaxer.prob.save(), k, relaxer.prob.strip_width() - (f32)((double)(k - 1) * cg.P), walls};
            } else {
                best = BinSolution{*compacted, k, compacted->strip_width() - (f32)((double)(k - 1) * cg.P), walls};
            }
        } else n_failed += 1;
    }
    return best;
}

// Turn a k-column solution into per-sheet placements: bucket parts by centroid column, rebuild each in
// its sheet-local frame (x - c*P), run the existing compact_left per sheet, and brute-force verify
// exactly like greedy_fill. Columns >= max_bins are not emitted (their parts stay unplaced).
inline Sheets columns_to_sheets(const BinSolution& best, const StripInstance& inst, const ColumnGeom& cg,
                                const CollisionConfig& sheet_cfg, int max_bins,
                                const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                const std::vector<usize>& sub2glob) {
    Sheets out;
    const int k = best.k > 0 ? best.k : 1;
    Layout wide = Layout::from_snapshot(best.sol.layout_snapshot);
    std::vector<std::vector<std::pair<usize, RigidTransform>>> cols((usize)k);
    wide.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
        int c = column_of(cg, pi.shape->centroid().x, k);
        cols[(usize)c].emplace_back(pi.part_id, pi.d_transf);
    });
    usize n_emitted = 0;
    for (int c = 0; c < k; ++c) {
        if (c >= max_bins) { if (!cols[(usize)c].empty()) out.ok = false; continue; }
        Strip s(cg.H, sheet_cfg, part_modify(), cg.W);
        Layout L(s.to_container());
        const f32 dx = (f32)((double)c * cg.P);
        for (const auto& [pid, dt0] : cols[(usize)c]) {
            RigidTransform dt = dt0; dt.tx -= dx;
            L.place_item(inst.part(pid), dt);
        }
        const std::vector<Polygon>* holes = nullptr;
        if (!per_sheet_holes.empty()) {
            usize hi = (c < (int)per_sheet_holes.size()) ? (usize)c : per_sheet_holes.size() - 1;
            holes = &per_sheet_holes[hi];
        }
        compact_left(L, cg.W, cg.H, holes);

        f32 wmax = 0.0f;
        std::vector<Polygon> shapes;
        usize cnt = 0;
        L.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            SheetPlacement p; p.color_id = sub2glob[pi.part_id]; p.sheet = c;
            p.verts = pi.shape->vertices; p.d_transf = pi.d_transf;
            for (auto& pt : p.verts) wmax = max_f(wmax, pt.x);
            try { shapes.push_back(Polygon::create(p.verts)); } catch (...) {}
            out.placements.push_back(std::move(p));
            ++cnt;
        });
        n_emitted += cnt;
        for (auto& sh : shapes)
            if (sh.bbox.x_max > cg.W + 1e-2f || sh.bbox.x_min < -1e-2f ||
                sh.bbox.y_max > cg.H + 1e-2f || sh.bbox.y_min < -1e-2f) out.inf++;
        for (usize i = 0; i < shapes.size(); ++i)
            for (usize j = i + 1; j < shapes.size(); ++j)
                if (brute_overlap(shapes[i], shapes[j])) out.bf++;
        out.counts.push_back(cnt);
        out.widths.push_back(wmax);
    }
    int last_used = 0;
    for (const auto& p : out.placements) if (p.sheet + 1 > last_used) last_used = p.sheet + 1;
    out.counts.resize((usize)last_used);
    out.widths.resize((usize)last_used);
    out.n_sheets = last_used;
    if (n_emitted != inst.parts.size()) out.ok = false;
    if (out.inf != 0 || out.bf != 0) out.ok = false;
    return out;
}

// Parts of `inst` in the LBF order (convex-hull area x diameter, descending).
inline std::vector<usize> lbf_order(const StripInstance& inst) {
    std::vector<usize> ids;
    for (usize i = 0; i < inst.parts.size(); ++i) ids.push_back(i);
    std::stable_sort(ids.begin(), ids.end(), [&](usize a, usize b) {
        const Polygon& sa = *inst.part(a).collision_shape;
        const Polygon& sb = *inst.part(b).collision_shape;
        return sa.surrogate_ref().convex_hull_area * sa.diameter > sb.surrogate_ref().convex_hull_area * sb.diameter;
    });
    return ids;
}

// Best-fit-decreasing fill (NP_BIN_BFD start, NP_BIN_GROUPFILL fillers): each part of `ids` (in order)
// tries the open columns with the most free area first (least placed_area) and is placed by the LBF
// evaluator sampled inside that column (collision-free only); a part no column accepts opens column kc+1.
// Returns false if some part could not be placed even with k_max columns.
inline bool bfd_fill(StripProblem& prob, Rng& rng, const StripInstance& inst, const ColumnGeom& cg,
                     const std::vector<usize>& ids, int& kc, int k_max, std::vector<double>& placed_area) {
    bool ok = true;
    for (usize id : ids) {
        while (prob.part_demand_qtys[id] > 0) {
            const Part& part = inst.part(id);
            std::vector<int> order;
            for (int c = 0; c < kc; ++c) order.push_back(c);
            std::stable_sort(order.begin(), order.end(), [&](int x, int y) { return placed_area[(usize)x] < placed_area[(usize)y]; });
            bool done = false;
            for (int c : order) {
                Rect col((f32)((double)c * cg.P), 0.0f, (f32)((double)c * cg.P + cg.W), cg.H);
                InitialEvaluator ev(prob.layout, part);
                std::vector<std::pair<Rect, usize>> reg{{col, INITIAL_SAMPLE_CONFIG.n_container_samples}};
                auto best = search_placement_in(prob.layout, part, ev, INITIAL_SAMPLE_CONFIG, rng, reg);
                if (best && best->second.kind == SampleKind::Clear) {
                    PartKey pk = prob.place_item(Placement{id, best->first});
                    int cc = column_of(cg, prob.layout.placed_parts[pk].shape->centroid().x, k_max);
                    placed_area[(usize)cc] += part.area();
                    done = true;
                    break;
                }
            }
            if (done) continue;
            if (kc >= k_max) { ok = false; break; }   // cannot place: leave unplaced
            kc += 1;
            prob.change_strip_width(columns_width(cg, kc));
        }
    }
    return ok;
}

// ---- LEVER (NP_BIN_GROUPFILL=1): group-and-fill start ----
struct GroupTest {
    bool feasible = false;
    f32 w = 0.0f;                                             // used width of the single-sheet test pack
    std::vector<std::pair<usize, RigidTransform>> layout;     // (inst part id, pose in a [0,W]x[0,H] sheet)
};
struct GroupPartition {
    std::vector<std::vector<usize>> groups;                   // inst part ids per column (big parts only)
    double sum_w = 0.0;
    int k = 0;
};

// Single-sheet pack test of `g` (inst ids): LBF into a W' x H strip, then (if wider than W') the plain
// exploration strip shrink for `rounds` relaxation rounds. Feasible iff the best layout's x_max <= W'.
// `compact`: run the shrink even when LBF already fits (minimise the group's used width so the column's
// free space is one band on the right, where the fillers go).
inline GroupTest test_group(const StripInstance& inst, const std::vector<usize>& g, const CollisionConfig& engine,
                            f32 Wt, f32 H, usize rounds, unsigned nw, Rng rng, bool compact = false) {
    GroupTest t;
    auto gp = subset_items(inst.parts, g);
    Strip s(H, engine, part_modify(), Wt);
    StripInstance gi(gp, s);
    InitialPlacer gip(gi, Rng(rng.next_u64()), INITIAL_SAMPLE_CONFIG);
    gip.construct();
    auto xmax_of = [](const LayoutSnapshot& ls) {
        f32 m = 0.0f;
        ls.placed_parts.for_each([&](PartKey, const PlacedPart& pi) { m = max_f(m, pi.shape->bbox.x_max); });
        return m;
    };
    StripSolution sol = gip.prob.save();
    f32 w = xmax_of(sol.layout_snapshot);
    if ((w > Wt || compact) && rounds > 0 && !g_cancel.load(std::memory_order_relaxed)) {
        SolverConfig sc = default_solver_config();
        ExplorationConfig ec = sc.expl_cfg;
        ec.max_conseq_failed_attempts = std::nullopt;
        ec.separator_config.n_workers = nw;
        IterationTerminator it;
        it.new_timeout((double)rounds);
        Relaxer rr(gi, std::move(gip.prob), Rng(rng.next_u64()), ec.separator_config);
        DummySolListener dl;
        auto sols = exploration_phase(gi, rr, dl, it, ec);
        sol = sols.back();
        w = xmax_of(sol.layout_snapshot);
    }
    t.w = w;
    t.feasible = (w <= Wt) && sol.layout_snapshot.placed_parts.size() == g.size();
    if (t.feasible)
        sol.layout_snapshot.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            t.layout.emplace_back(g[pi.part_id], pi.d_transf);
        });
    return t;
}

// fit_mode 2 entry point (uniform sheets W x H). Same signature family as greedy_fill; returns Sheets so
// every downstream post-pass in the C ABI runs unchanged.
inline Sheets bin_pack_columns(const std::vector<std::pair<Part, usize>>& parts, const CollisionConfig& engine,
                               f32 W, f32 H, double budget, int max_bins,
                               const std::vector<std::vector<Polygon>>& per_sheet_holes,
                               uint64_t seed, bool publish_live) {
    Sheets out;
    const bool verbose = bin_verbose();
    if (max_bins < 1) max_bins = 1;

    // 1. bounds from area; parts that fit no column at any orientation stay unplaced.
    f32 A_all = 0.0f;
    for (const auto& pr : parts) A_all += pr.first.area() * (f32)pr.second;
    const double sheet_area = (double)W * (double)H;
    int k_hi = std::max(max_bins, (int)std::ceil(1.6 * (double)A_all / sheet_area) + 1);
    ColumnGeom cg = make_column_geom(W, H, k_hi);
    std::vector<usize> idx;
    for (usize i = 0; i < parts.size(); ++i)
        if (parts[i].first.collision_shape && fits_column(parts[i].first, W - 2.0f * cg.guard, H)) idx.push_back(i);
    bool lost = idx.size() != parts.size();
    if (idx.empty()) { out.ok = false; return out; }

    f32 A = 0.0f;
    for (usize i : idx) A += parts[i].first.area() * (f32)parts[i].second;
    int k_lb = std::max(1, (int)std::ceil((double)A / sheet_area));

    // Quadtree depth of the wide strip. Collision answers are exact at any depth (measured: bit-identical
    // layouts for depth 4..7), so this is purely a speed knob. A deeper tree (the 4 + ceil(log2 k) rule,
    // keeping cells ~W/16 like a single sheet) was measured SLOWER on every case tried - 1000x1000 x 24
    // rects: depth 4 2.6s / 7 6.0s; 60 L-parts on 10 sheets: 5.6s / 14.0s; 58 long parts on 6000x2000:
    // 5.7s / 10.8s - because every part move re-registers the part down the tree. So keep the engine's
    // own depth; NP_BIN_QT_DEPTH overrides for experiments.
    CollisionConfig wcfg = engine;
    if (const char* e = std::getenv("NP_BIN_QT_DEPTH")) {
        int v = std::atoi(e);
        if (v > 0 && v < 16) wcfg.quadtree_depth = (uint8_t)v;
    }
    BinParams bp;
    bp.step_cap = bin_env_f32("NP_BIN_STEP_CAP", bp.step_cap);
    if (const char* e = std::getenv("NP_BIN_ROTATE_COL")) bp.rotate_col = (std::atoi(e) != 0);
    if (const char* e = std::getenv("NP_BIN_EVICT")) bp.evict = (std::atoi(e) != 0);
    if (const char* e = std::getenv("NP_BIN_EVICT_EVERY")) { int v = std::atoi(e); if (v > 0) bp.evict_every = v; }
    if (const char* e = std::getenv("NP_BIN_COLWALLS")) bp.colwalls = std::max(0, std::atoi(e));
    if (const char* e = std::getenv("NP_BIN_SWEEP_AFTER")) { int v = std::atoi(e); if (v > 0) bp.sweep_after = v; }
    // Columns are interchangeable only when every column carries the same keep-outs.
    bool cols_interchangeable = true;
    for (usize i = 1; i < per_sheet_holes.size() && cols_interchangeable; ++i) {
        const auto& h0 = per_sheet_holes[0]; const auto& hi = per_sheet_holes[i];
        if (h0.size() != hi.size()) { cols_interchangeable = false; break; }
        for (usize j = 0; j < h0.size() && cols_interchangeable; ++j)
            if (h0[j].vertices.size() != hi[j].vertices.size() ||
                !std::equal(h0[j].vertices.begin(), h0[j].vertices.end(), hi[j].vertices.begin(),
                            [](const Point& p, const Point& q) { return p == q; }))
                cols_interchangeable = false;
    }
    if (bp.rotate_col && !cols_interchangeable) {
        bp.rotate_col = false;
        if (verbose) std::fprintf(stderr, "[bin] column rotation skipped: per-sheet keep-outs differ\n");
    }

    auto sub = subset_items(parts, idx);

    // GROUP-AND-FILL (NP_BIN_GROUPFILL=1): the BIG set = parts with area >= NP_GROUP_MIN_FRAC (default 0.05)
    // of the sheet, at most NP_GROUP_MAX (default 24) of them by area. NP_BIG_PIN=1 pins them to their column.
    bool use_group = false;
    if (const char* e = std::getenv("NP_BIN_GROUPFILL")) use_group = (std::atoi(e) != 0);
    std::vector<usize> bigs;   // sub/inst ids, area descending
    if (use_group) {
        f32 fmin = bin_env_f32("NP_GROUP_MIN_FRAC", 0.05f);
        int gmax = 24;
        if (const char* e = std::getenv("NP_GROUP_MAX")) { int v = std::atoi(e); if (v > 0) gmax = std::min(v, 24); }
        for (usize i = 0; i < sub.size(); ++i)
            if ((double)sub[i].first.area() >= (double)fmin * sheet_area) bigs.push_back(i);
        std::stable_sort(bigs.begin(), bigs.end(), [&](usize a, usize b2) { return sub[a].first.area() > sub[b2].first.area(); });
        if ((int)bigs.size() > gmax) bigs.resize((usize)gmax);
    }

    // 2. LBF directly on the wide strip. Gap holes are pre-populated for 2*k_hi columns (holes beyond
    //    the strip are inert); the strip widens x1.2 when a part finds no clear spot, capped there.
    const int k_lbf = 2 * k_hi;
    Strip strip(H, wcfg, part_modify(), columns_width(cg, k_lb));
    strip.set_holes(column_holes(cg, k_lbf, per_sheet_holes));
    StripInstance inst(sub, strip);
    Rng rng(seed);
    auto next_rng = [&]() { return Rng(rng.next_u64()); };

    InitialPlacer ip(inst, next_rng(), INITIAL_SAMPLE_CONFIG);
    bool use_bfd = false;
    if (const char* e = std::getenv("NP_BIN_BFD")) use_bfd = (std::atoi(e) != 0);
    unsigned nw_tests = resolve_n_workers();
    // pool of partitions (group-and-fill), consumed by the start builder and the retry loop below
    std::vector<GroupPartition> gpool;
    std::map<std::vector<usize>, GroupTest> gcache;
    unsigned long long group_rounds = 0;
    double group_secs = 0.0;
    if (use_group && !bigs.empty()) {
        const unsigned long long r_start = g_iter_count;
        const double t_start = now_seconds();
        const f32 frac_budget = bin_env_f32("NP_GROUP_BUDGET_FRAC", 0.25f);
        usize test_rounds = 150;
        if (const char* e = std::getenv("NP_GROUP_TEST_ROUNDS")) { int v = std::atoi(e); if (v >= 0) test_rounds = (usize)v; }
        // Test caps: LBF-only tests consume wall time but no relaxation rounds, so the round budget alone
        // cannot bound them. Tests run most-promising first (largest combined area), so a cap keeps the
        // groups that matter.
        int max_tests = 4000;
        if (const char* e = std::getenv("NP_GROUP_MAX_TESTS")) { int v = std::atoi(e); if (v > 0) max_tests = v; }
        const double max_secs = (double)bin_env_f32("NP_GROUP_MAX_SECS", 120.0f);
        const f32 Wt = W - 2.0f * cg.guard;
        const double area_cap = 0.98 * sheet_area;
        auto over_budget = [&]() {
            if (g_cancel.load(std::memory_order_relaxed)) return true;
            if (now_seconds() - t_start > max_secs) return true;
            if (g_iter_mode) return (double)(g_iter_count - r_start) > frac_budget * budget;
            return now_seconds() - t_start > frac_budget * budget;
        };
        const usize m = bigs.size();
        auto area_of = [&](const std::vector<usize>& g) { double a = 0; for (usize i : g) a += inst.part(i).area(); return a; };
        int n_tests = 0, n_feas[5] = {0, 0, 0, 0, 0}, n_run[5] = {0, 0, 0, 0, 0}, n_up = 0, n_up_ok = 0;
        bool budget_hit = false;
        // Phase 1 test: LBF only (no relaxation rounds; bounded by the test count / wall caps).
        auto lbf_test = [&](const std::vector<usize>& g) {   // g = indices into bigs, ascending
            if (gcache.count(g)) return;
            if (n_tests >= max_tests || now_seconds() - t_start > max_secs || g_cancel.load(std::memory_order_relaxed)) { budget_hit = true; return; }
            std::vector<usize> ids; for (usize j : g) ids.push_back(bigs[j]);
            ++n_tests; n_run[g.size()]++;
            GroupTest t = test_group(inst, ids, engine, Wt, H, 0, nw_tests, next_rng());
            if (t.feasible) n_feas[g.size()]++;
            gcache[g] = std::move(t);
        };
        // Phase 2: re-test LBF failures with the strip-shrink exploration (NP_GROUP_TEST_ROUNDS each),
        // closest-to-fitting first (LBF width <= 1.25 W'), while the round budget lasts.
        auto upgrade = [&](const std::vector<std::vector<usize>>& cands) {
            // most-constrained first: groups whose least-connected member has the fewest feasible partners
            // (a big plate with no feasible pair forces a column of its own), then by LBF width.
            std::vector<int> partners(m, 0);
            for (const auto& [gg, t] : gcache) if (t.feasible && gg.size() >= 2) for (usize j : gg) partners[j]++;
            std::vector<std::pair<std::pair<int, f32>, std::vector<usize>>> todo;
            for (const auto& g : cands) {
                auto it = gcache.find(g);
                if (it != gcache.end() && !it->second.feasible && it->second.w <= 1.25f * Wt) {
                    int pc = std::numeric_limits<int>::max();
                    for (usize j : g) pc = std::min(pc, partners[j]);
                    todo.push_back({{pc, it->second.w}, g});
                }
            }
            std::stable_sort(todo.begin(), todo.end(), [](const auto& x, const auto& y) { return x.first < y.first; });
            for (auto& [key, g] : todo) {
                if (test_rounds == 0 || over_budget()) { if (test_rounds) budget_hit = true; break; }
                std::vector<usize> ids; for (usize j : g) ids.push_back(bigs[j]);
                ++n_up;
                GroupTest t = test_group(inst, ids, engine, Wt, H, test_rounds, nw_tests, next_rng());
                if (t.feasible) { n_feas[g.size()]++; n_up_ok++; gcache[g] = std::move(t); }
            }
        };
        auto known_feasible = [&](const std::vector<usize>& g) { auto it = gcache.find(g); return it != gcache.end() && it->second.feasible; };
        auto by_area_desc = [&](std::vector<std::vector<usize>>& v) {
            std::stable_sort(v.begin(), v.end(), [&](const std::vector<usize>& x, const std::vector<usize>& y) {
                double ax = 0, ay = 0; for (usize j : x) ax += inst.part(bigs[j]).area(); for (usize j : y) ay += inst.part(bigs[j]).area();
                return ax > ay; });
        };
        // singles; pairs; triples whose pairs all fit; quads whose triples all fit (largest area first)
        for (usize i = 0; i < m; ++i) lbf_test({i});
        std::vector<std::vector<usize>> cand;
        for (usize i = 0; i < m; ++i) for (usize j = i + 1; j < m; ++j)
            if (known_feasible({i}) && known_feasible({j})) {
                std::vector<usize> ids{bigs[i], bigs[j]};
                if (area_of(ids) <= area_cap) cand.push_back({i, j});
            }
        by_area_desc(cand);
        for (auto& g : cand) lbf_test(g);
        upgrade(cand);
        for (int size = 3; size <= 4; ++size) {
            cand.clear();
            std::vector<usize> g(size);
            std::function<void(usize, int)> gen = [&](usize from, int d) {
                if (d == size) {
                    std::vector<usize> ids; for (usize j : g) ids.push_back(bigs[j]);
                    if (area_of(ids) > area_cap) return;
                    for (int drop = 0; drop < size; ++drop) {   // every (size-1)-subset must be feasible
                        std::vector<usize> s2; for (int q = 0; q < size; ++q) if (q != drop) s2.push_back(g[(usize)q]);
                        if (!known_feasible(s2)) return;
                    }
                    cand.push_back(g);
                    return;
                }
                for (usize i = from; i < m; ++i) { g[(usize)d] = i; gen(i + 1, d + 1); }
            };
            gen(0, 0);
            by_area_desc(cand);
            for (auto& gg : cand) lbf_test(gg);
            upgrade(cand);
        }
        // PARTITION: cover the big set with <= k feasible groups (k from k_lb up), free area >= fillers x (1+slack),
        // minimise the sum of the groups' used widths. DFS with a node cap; keep the best few.
        std::vector<std::pair<uint32_t, std::pair<double, std::vector<usize>>>> groups;   // mask, (w, bigs idx)
        for (auto& [g, t] : gcache) if (t.feasible) {
            uint32_t mask = 0; for (usize j : g) mask |= (1u << j);
            groups.push_back({mask, {(double)t.w, g}});
        }
        std::vector<std::vector<int>> by_low(m);
        for (int gi = 0; gi < (int)groups.size(); ++gi) {
            uint32_t mk = groups[(usize)gi].first; int low = 0; while (!((mk >> low) & 1u)) ++low;
            by_low[(usize)low].push_back(gi);
        }
        for (auto& v : by_low) std::stable_sort(v.begin(), v.end(), [&](int x, int y) {
            return bin_popcount(groups[(usize)x].first) > bin_popcount(groups[(usize)y].first); });
        double big_area_all = 0; for (usize i : bigs) big_area_all += inst.part(i).area();
        const f32 slack = bin_env_f32("NP_GROUP_SLACK", 0.10f);
        const usize POOL = 5;
        long long nodes = 0;
        usize nb_used = 0;
        // k ascending; for each k try to cover the nb largest big parts (nb = m down to 1) with <= k feasible
        // groups such that the free area covers the rest (fillers + uncovered big parts) x (1 + slack).
        // pass 0: FULL cover of the big set at the smallest k; pass 1 (fallback): cover the nb largest.
        for (int pass = 0; pass < 2 && gpool.empty(); ++pass)
        for (int k = k_lb; k <= k_hi && gpool.empty(); ++k) {
            for (usize nb = m; nb >= 1 && gpool.empty(); --nb) {
                if (pass == 0 && nb < m) break;
                double cov_area = 0; for (usize j = 0; j < nb; ++j) cov_area += inst.part(bigs[j]).area();
                if ((double)k * sheet_area - cov_area < ((double)A - cov_area) * (1.0 + slack)) continue;
                const uint32_t full = (nb >= 32) ? 0xffffffffu : ((1u << nb) - 1u);
                std::vector<int> chosen;
                nodes = 0;
                std::function<void(uint32_t, double)> dfs = [&](uint32_t cov, double sw) {
                    if (++nodes > 200000) return;
                    if (cov == full) {
                        GroupPartition p; p.k = k; p.sum_w = sw;
                        for (int gi : chosen) { std::vector<usize> ids; for (usize j : groups[(usize)gi].second.second) ids.push_back(bigs[j]); p.groups.push_back(ids); }
                        gpool.push_back(std::move(p));
                        std::stable_sort(gpool.begin(), gpool.end(), [](const GroupPartition& x, const GroupPartition& y) { return x.sum_w < y.sum_w; });
                        if (gpool.size() > POOL) gpool.pop_back();
                        return;
                    }
                    if ((int)chosen.size() >= k) return;
                    int rem = (int)nb - bin_popcount(cov);
                    if ((int)chosen.size() + (rem + 3) / 4 > k) return;
                    if (gpool.size() >= POOL && sw >= gpool.back().sum_w) return;
                    int low = 0; while ((cov >> low) & 1u) ++low;
                    for (int gi : by_low[(usize)low]) {
                        if (groups[(usize)gi].first & (cov | ~full)) continue;   // overlap, or uses an uncovered-set part
                        chosen.push_back(gi);
                        dfs(cov | groups[(usize)gi].first, sw + groups[(usize)gi].second.first);
                        chosen.pop_back();
                    }
                };
                dfs(0u, 0.0);
                if (!gpool.empty()) nb_used = nb;
                if (nb == 1) break;
            }
        }
        const double big_area = big_area_all;
        group_rounds = g_iter_count - r_start;
        group_secs = now_seconds() - t_start;
        if (verbose) {
            if (!gpool.empty() && nb_used < m) std::fprintf(stderr, "[bin] GROUPFILL partition covers the %zu largest of %zu big parts (rest are fillers)\n", nb_used, m);
            std::fprintf(stderr, "[bin] GROUPFILL |B|=%zu (%.1f%% of material) LBF tests run/feasible-after-all: 1:%d/%d 2:%d/%d 3:%d/%d 4:%d/%d, exploration re-tests %d (%d now fit)%s, %llu rounds, %.1fs\n",
                         m, 100.0 * big_area / (double)A, n_run[1], n_feas[1], n_run[2], n_feas[2], n_run[3], n_feas[3],
                         n_run[4], n_feas[4], n_up, n_up_ok, budget_hit ? " [budget hit]" : "", group_rounds, group_secs);
            if (gpool.empty()) std::fprintf(stderr, "[bin] GROUPFILL no partition found -> fallback start\n");
            for (usize pi = 0; pi < gpool.size(); ++pi) {
                std::fprintf(stderr, "[bin] GROUPFILL partition %zu: k=%d sum_w=%.1f%% of W:", pi, gpool[pi].k, 100.0 * gpool[pi].sum_w / W);
                for (auto& g : gpool[pi].groups) {
                    std::vector<usize> key; for (usize id : g) for (usize j = 0; j < m; ++j) if (bigs[j] == id) key.push_back(j);
                    std::sort(key.begin(), key.end());
                    std::fprintf(stderr, " {");
                    for (usize q = 0; q < g.size(); ++q) std::fprintf(stderr, "%s%zu", q ? "," : "", idx[g[q]]);
                    std::fprintf(stderr, "}w=%.0f%%", 100.0 * gcache[key].w / W);
                }
                std::fprintf(stderr, "\n");
            }
        }
    }
    // Lay a partition into `prob` (fresh StripProblem of inst): group test layouts into columns 0..,
    // then the fillers by BFD. Returns the number of fillers that had to open a column beyond p.k.
    auto build_group_start = [&](const GroupPartition& p, StripProblem& prob, Rng& r, bool& lost_flag) -> int {
        int kc = p.k;
        prob.change_strip_width(columns_width(cg, kc));
        std::vector<double> placed_area((usize)k_lbf, 0.0);
        std::vector<char> is_big(inst.parts.size(), 0);   // = placed by its group layout
        for (const auto& g : p.groups) for (usize i : g) is_big[i] = 1;
        for (usize c = 0; c < p.groups.size(); ++c) {
            std::vector<usize> key; for (usize id : p.groups[c]) for (usize j = 0; j < bigs.size(); ++j) if (bigs[j] == id) key.push_back(j);
            std::sort(key.begin(), key.end());
            const GroupTest& t = gcache[key];
            for (const auto& [id, dt0] : t.layout) {
                RigidTransform dt = dt0; dt.tx += (f32)((double)c * cg.P) + cg.guard;
                prob.place_item(Placement{id, dt});
                placed_area[c] += inst.part(id).area();
            }
        }
        std::vector<usize> fill;
        for (usize id : lbf_order(inst)) if (!is_big[id]) fill.push_back(id);
        if (!bfd_fill(prob, r, inst, cg, fill, kc, k_lbf, placed_area)) lost_flag = true;
        return kc - p.k;
    };
    // Compact the chosen partition's group layouts (NP_GROUP_COMPACT_ROUNDS, default 300 per group).
    if (!gpool.empty()) {
        usize crounds = 300;
        if (const char* e = std::getenv("NP_GROUP_COMPACT_ROUNDS")) { int v = std::atoi(e); if (v >= 0) crounds = (usize)v; }
        const unsigned long long r_c = g_iter_count;
        const double t_c = now_seconds();
        if (crounds > 0)
            for (const auto& g : gpool[0].groups) {
                if (g_cancel.load()) break;
                std::vector<usize> key; for (usize id : g) for (usize j = 0; j < bigs.size(); ++j) if (bigs[j] == id) key.push_back(j);
                std::sort(key.begin(), key.end());
                GroupTest t = test_group(inst, g, engine, W - 2.0f * cg.guard, H, crounds, nw_tests, next_rng(), true);
                if (t.feasible && t.w < gcache[key].w) gcache[key] = std::move(t);
            }
        group_rounds += g_iter_count - r_c;
        group_secs += now_seconds() - t_c;
        if (verbose) {
            std::fprintf(stderr, "[bin] GROUPFILL compacted groups (w %% of W):");
            for (const auto& g : gpool[0].groups) {
                std::vector<usize> key; for (usize id : g) for (usize j = 0; j < bigs.size(); ++j) if (bigs[j] == id) key.push_back(j);
                std::sort(key.begin(), key.end());
                std::fprintf(stderr, " %.0f", 100.0 * gcache[key].w / W);
            }
            std::fprintf(stderr, "\n");
        }
    }
    // A group start must be FEASIBLE (the group tests ignore per-sheet keep-outs, and fillers are only
    // placed where clear): verify with a fresh tracker, else fall back to the BFD / LBF start.
    auto start_is_feasible = [&](const StripProblem& p) { return OverlapTracker(p.layout).get_total_loss() == 0.0f; };
    bool group_start = false;
    if (!gpool.empty()) {
        bool lost_g = false;
        int extra = build_group_start(gpool[0], ip.prob, ip.rng, lost_g);
        if (!lost_g && start_is_feasible(ip.prob)) {
            group_start = true;
            if (verbose) std::fprintf(stderr, "[bin] GROUPFILL start: k=%d, fillers needed %d extra column(s)\n", gpool[0].k, extra);
            // NP_BIG_PIN=1: pin the parts the partition placed in groups to their column (Relaxers copy `inst`
            // after this point; the start builder above used its own copy).
            bool pin = false;
            if (const char* e = std::getenv("NP_BIG_PIN")) pin = (std::atoi(e) != 0);
            if (pin) for (const auto& g : gpool[0].groups) for (usize i : g) { inst.parts[i].first.pin_pitch = cg.P; inst.parts[i].first.pin_w = W; }
        } else {
            if (verbose) std::fprintf(stderr, "[bin] GROUPFILL start infeasible (keep-outs / unplaced) -> fallback start\n");
            ip.prob = StripProblem(inst);
            gpool.clear();
        }
    }
    if (!group_start && use_bfd) {
        // BALANCED START (NP_BIN_BFD=1): best-fit-decreasing over k_lb+1 pre-created columns. Parts in the
        // LBF order; each tries the columns with the MOST free area first and is placed with the LBF
        // evaluator, sampled inside that column only (must be collision-free); a part no column accepts
        // opens a new column.
        int kc = std::min(k_lb + 1, k_lbf);
        ip.prob.change_strip_width(columns_width(cg, kc));
        std::vector<double> placed_area((usize)k_lbf, 0.0);
        if (!bfd_fill(ip.prob, ip.rng, inst, cg, lbf_order(inst), kc, k_lbf, placed_area)) lost = true;
    } else if (!group_start) {
        usize n_parts = inst.parts.size();
        std::vector<usize> ids;
        for (usize i = 0; i < n_parts; ++i) ids.push_back(i);
        std::stable_sort(ids.begin(), ids.end(), [&](usize a, usize b) {
            const Polygon& sa = *inst.part(a).collision_shape;
            const Polygon& sb = *inst.part(b).collision_shape;
            return sa.surrogate_ref().convex_hull_area * sa.diameter > sb.surrogate_ref().convex_hull_area * sb.diameter;
        });
        const f32 max_w = columns_width(cg, k_lbf);
        for (usize id : ids) {
            while (ip.prob.part_demand_qtys[id] > 0) {
                auto p = ip.find_placement(id);
                if (p) { ip.prob.place_item(*p); continue; }   // place_item decrements the demand
                f32 w = ip.prob.strip_width();
                if (w >= max_w) { lost = true; break; }        // cannot place: leave unplaced
                ip.prob.change_strip_width(std::min(w * 1.2f, max_w));
            }
        }
    }
    if ((use_bfd || group_start) && verbose) {
        std::vector<double> a((usize)k_lbf, 0.0);
        std::vector<int> n((usize)k_lbf, 0);
        int kk = 1;
        ip.prob.layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            int c = column_of(cg, pi.shape->centroid().x, k_lbf);
            a[(usize)c] += inst.part(pi.part_id).area(); n[(usize)c]++; kk = std::max(kk, c + 1);
        });
        std::fprintf(stderr, "[bin] %s start: %d columns, fill (parts / %% of sheet area):", group_start ? "GROUPFILL" : "BFD", kk);
        for (int c = 0; c < kk; ++c) std::fprintf(stderr, " %d/%.1f%%", n[(usize)c], 100.0 * a[(usize)c] / sheet_area);
        std::fprintf(stderr, "\n");
    }
    if (ip.prob.layout.placed_parts.size() == 0) { out.ok = false; return out; }
    int k0 = 1;
    ip.prob.layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
        k0 = std::max(k0, column_of(cg, pi.shape->bbox.x_max, k_lbf) + 1);
    });
    ip.prob.strip.set_holes(column_holes(cg, k0, per_sheet_holes));
    ip.prob.change_strip_width(columns_width(cg, k0));
    if (verbose) std::fprintf(stderr, "[bin] A/sheet=%.3f k_lb=%d k_hi=%d lbf k0=%d depth=%d G=%g P=%g guard=%g\n",
                              (double)A / sheet_area, k_lb, k_hi, k0, (int)wcfg.quadtree_depth,
                              (double)cg.G, (double)cg.P, (double)cg.guard);

    if (publish_live) {
        std::lock_guard<std::mutex> lk(g_live.mtx);
        g_live.sub2glob = idx; g_live.sheet_base = 0;
        g_live.sheet_w = W; g_live.col_pitch = cg.P; g_live.max_bins = k_hi;
    }
    ProgressListener prog;
    DummySolListener dummy;
    SolutionListener& listener = publish_live ? static_cast<SolutionListener&>(prog) : static_cast<SolutionListener&>(dummy);
    BasicTerminator bterm;
    IterationTerminator iterm;
    Terminator& term = g_iter_mode ? static_cast<Terminator&>(iterm) : static_cast<Terminator&>(bterm);

    SolverConfig cfg = default_solver_config();
    cfg.expl_cfg.time_limit_secs = budget * 0.8;
    cfg.cmpr_cfg.time_limit_secs = budget * 0.2;
    cfg.expl_cfg.max_conseq_failed_attempts = std::nullopt;
    unsigned nw = resolve_n_workers();
    cfg.expl_cfg.separator_config.n_workers = nw;
    cfg.cmpr_cfg.separator_config.n_workers = nw;
    // CHEAPER FAILURES (NP_BIN_STRIKES / NP_BIN_NOIMPR, fit 2 exploration only; defaults = today's 3 / 200):
    // a failed relaxation costs ~strikes x no-improvement rounds before the explorer may diversify.
    if (const char* e = std::getenv("NP_BIN_STRIKES")) { int v = std::atoi(e); if (v > 0) cfg.expl_cfg.separator_config.strike_limit = (usize)v; }
    if (const char* e = std::getenv("NP_BIN_NOIMPR")) { int v = std::atoi(e); if (v > 0) cfg.expl_cfg.separator_config.iter_no_imprv_limit = (usize)v; }

    // 3. column exploration (80%, minus what the group tests used)
    double expl_budget = cfg.expl_cfg.time_limit_secs;
    if (group_rounds > 0 || group_secs > 0.0)
        expl_budget = std::max(0.1 * expl_budget, expl_budget - (g_iter_mode ? (double)group_rounds : group_secs));
    BinStats st;
    std::unique_ptr<Relaxer> expl_relaxer;
    BinSolution best;
    long long retry_rounds = 0;
    if (const char* e = std::getenv("NP_GROUP_RETRY_ROUNDS")) retry_rounds = std::atoll(e);
    if (group_start && retry_rounds > 0 && g_iter_mode && gpool.size() > 1) {
        // RETRY: give each pooled partition `retry_rounds` to reach its k; keep the first that does (else the
        // best seen), then spend the rest of the exploration budget on it.
        const unsigned long long r0 = g_iter_count;
        StripProblem first = std::move(ip.prob);
        for (usize pi = 0; pi < gpool.size(); ++pi) {
            StripProblem sp = (pi == 0) ? std::move(first) : StripProblem(inst);
            int kk0 = k0;
            if (pi > 0) {
                bool lost_p = false;
                build_group_start(gpool[pi], sp, ip.rng, lost_p);
                if (lost_p || !start_is_feasible(sp)) continue;
                kk0 = 1;
                sp.layout.placed_parts.for_each([&](PartKey, const PlacedPart& q) { kk0 = std::max(kk0, column_of(cg, q.shape->bbox.x_max, k_lbf) + 1); });
                sp.strip.set_holes(column_holes(cg, kk0, per_sheet_holes));
                sp.change_strip_width(columns_width(cg, kk0));
            }
            auto rx = std::make_unique<Relaxer>(inst, std::move(sp), next_rng(), cfg.expl_cfg.separator_config);
            term.new_timeout((double)retry_rounds);
            BinSolution b = column_exploration_phase(inst, *rx, listener, term, cfg.expl_cfg, cg, kk0, per_sheet_holes, bp, st);
            if (verbose) std::fprintf(stderr, "[bin] GROUPFILL try partition %zu: k=%d after %lld rounds (target %d)\n", pi, b.k, retry_rounds, gpool[pi].k);
            bool better = !expl_relaxer || b.k < best.k || (b.k == best.k && b.last_w < best.last_w);
            if (better) { best = b; expl_relaxer = std::move(rx); }
            if (best.k <= gpool[pi].k || g_cancel.load()) break;
        }
        double left = expl_budget - (double)(g_iter_count - r0);
        if (left > 0 && !g_cancel.load()) {
            expl_relaxer->rollback(best.sol, nullptr);
            term.new_timeout(left);
            BinSolution b = column_exploration_phase(inst, *expl_relaxer, listener, term, cfg.expl_cfg, cg, best.k, per_sheet_holes, bp, st);
            if (b.k < best.k || (b.k == best.k && b.last_w <= best.last_w)) best = b;
        }
    } else {
        term.new_timeout(expl_budget);
        expl_relaxer = std::make_unique<Relaxer>(inst, std::move(ip.prob), next_rng(), cfg.expl_cfg.separator_config);
        best = column_exploration_phase(inst, *expl_relaxer, listener, term, cfg.expl_cfg, cg, k0, per_sheet_holes, bp, st);
    }
    const unsigned long long expl_rounds = g_iter_count - st.start_round;
    const int k_expl = best.k;
    // 4. last-column compression (20%)
    term.new_timeout(cfg.cmpr_cfg.time_limit_secs);
    Relaxer cmpr_relaxer(expl_relaxer->instance, expl_relaxer->prob, next_rng(), cfg.cmpr_cfg.separator_config);
    best = column_compression_phase(inst, cmpr_relaxer, best, listener, term, cfg.cmpr_cfg, cg, per_sheet_holes, &bp, &st);
    listener.report(ReportType::Final, best.sol, inst);
    if (verbose) std::fprintf(stderr, "[bin] done: k0=%d explore k=%d final k=%d last_w=%.1f (%.1f%% of W) rounds=%llu | "
                              "snaps=%d rounds/snap=%.0f failed_relax=%d col_rotations=%d evictions=%d/%d ok | rotate_col=%d evict=%d smart_rot=%d\n",
                              k0, k_expl, best.k, (double)best.last_w, 100.0 * best.last_w / W,
                              (unsigned long long)g_iter_count, st.snaps,
                              st.snaps > 0 ? (double)expl_rounds / st.snaps : 0.0, st.fails, st.col_rotations,
                              st.evict_ok, st.evict_tried, bp.rotate_col ? 1 : 0, bp.evict ? 1 : 0, g_smart_rot ? 1 : 0);
    if (verbose) std::fprintf(stderr, "[bin] config: bfd=%d strikes=%zu noimpr=%zu colwalls=%d\n", use_bfd ? 1 : 0,
                              (size_t)cfg.expl_cfg.separator_config.strike_limit,
                              (size_t)cfg.expl_cfg.separator_config.iter_no_imprv_limit, bp.colwalls);

    // 5. columns -> sheets
    out = columns_to_sheets(best, inst, cg, engine, max_bins, per_sheet_holes, idx);
    if (lost) out.ok = false;
    if (verbose) {
        std::fprintf(stderr, "[bin] sheets=%d used widths (%% of W):", out.n_sheets);
        for (usize i = 0; i < out.widths.size(); ++i) std::fprintf(stderr, " %.1f", 100.0 * out.widths[i] / W);
        if (bp.colwalls) {
            std::fprintf(stderr, " | sweeps=%d wall shrinks %d/%d ok | final walls:", st.sweeps, st.wall_ok, st.wall_tries);
            for (int c = 0; c + 1 < best.k; ++c)
                std::fprintf(stderr, " %.1f", best.walls.empty() ? 100.0 : 100.0 * best.walls[(usize)c] / W);
        }
        std::fprintf(stderr, "\n");
    }
    return out;
}

} // namespace nest
