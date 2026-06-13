// Faithful C++ port of solver/src/overlap/* (overlap proxy, pair matrix, GLS tracker).
// The collision objective ("loss") used by the separator/optimizer.
#pragma once
#include "constants.hpp"
#include "../geometry/surrogate.hpp"
#include "../geometry/polygon.hpp"
#include "../geometry/scene.hpp"
#include <limits>

namespace nest {
using namespace nest;

// ---- overlap_proxy.rs : Algorithm 3 ----
// SoA + omp-simd rewrite of the hot pole-pair loop (mirrors the Rust std::simd CirclesSoA path the
// port had dropped): copying sp2's poles into contiguous float arrays + `#pragma omp simd` lets the
// compiler vectorize the inner reduction under -O3 -march=native -fopenmp-simd WITHOUT the global
// FP reordering of -ffast-math. TUNING-FORK NOTE: the per-p1 grouped reduction differs from the
// scalar version only in FP rounding order (numerically equivalent; affects only the heuristic
// gradient, never the exact final overlap check). Thread-safe (all buffers are stack-local).
inline f32 overlap_area_proxy(const Surrogate& sp1, const Surrogate& sp2, f32 epsilon) {
    constexpr usize CAP = 128; // >= max poles generated (the pole-limit caps generation below this)
    f32 x2[CAP], y2[CAP], r2[CAP];
    usize n2 = sp2.poles.size();
    if (n2 > CAP) n2 = CAP;
    for (usize j = 0; j < n2; ++j) {
        x2[j] = sp2.poles[j].center.x;
        y2[j] = sp2.poles[j].center.y;
        r2[j] = sp2.poles[j].radius;
    }
    const f32 two_eps = 2.0f * epsilon, eps2 = epsilon * epsilon;
    f32 total_overlap = 0.0f;
    for (const auto& p1 : sp1.poles) {
        const f32 x1 = p1.center.x, y1 = p1.center.y, r1 = p1.radius;
        f32 acc = 0.0f;
        #pragma omp simd reduction(+ : acc)
        for (usize j = 0; j < n2; ++j) {
            f32 dx = x1 - x2[j], dy = y1 - y2[j];
            f32 dist = std::sqrt(dx * dx + dy * dy);
            f32 pd = (r1 + r2[j]) - dist;
            f32 pd_decay = (pd >= epsilon) ? pd : eps2 / (two_eps - pd);
            acc += pd_decay * min_f(r1, r2[j]);
        }
        total_overlap += acc;
    }
    total_overlap *= PI_F;
    return total_overlap;
}

// Penetration-depth proxy (A/B alternative to the area proxy): aggregates the SAME pole-pair
// penetration pd=(r1+r2)-dist as DEPTH-DOMINANT (max + small*sum) instead of area-weighted, so a
// deep thin interpenetration reads large (not ~0 as in the area proxy) and the relax loop is pulled
// toward true separability. Gated by USE_DEPTH_PROXY so the area path stays bit-identical.
#ifndef USE_DEPTH_PROXY
#define USE_DEPTH_PROXY 1  // default ON: penetration-depth proxy packs tighter (44 vs 42 @5000i).
#endif                     // build with -DUSE_DEPTH_PROXY=0 for the faithful area-proxy baseline.
inline constexpr f32 DEPTH_SUM_W = 0.05f;

inline f32 overlap_depth_proxy(const Surrogate& sp1, const Surrogate& sp2, f32 epsilon) {
    constexpr usize CAP = 128;
    f32 x2[CAP], y2[CAP], r2[CAP];
    usize n2 = sp2.poles.size();
    if (n2 > CAP) n2 = CAP;
    for (usize j = 0; j < n2; ++j) { x2[j] = sp2.poles[j].center.x; y2[j] = sp2.poles[j].center.y; r2[j] = sp2.poles[j].radius; }
    const f32 two_eps = 2.0f * epsilon, eps2 = epsilon * epsilon;
    f32 depth_sum = 0.0f, depth_max = 0.0f;
    f32 dist[CAP];
    for (const auto& p1 : sp1.poles) {
        const f32 x1 = p1.center.x, y1 = p1.center.y, r1 = p1.radius;
        // Phase 1: isolate the hot sqrt into a pure elementwise loop. With no branch/division in
        // the body GCC auto-vectorizes it to SSE2 sqrtps (4-wide), and since sqrt is a per-element
        // IEEE op the results are bit-identical to the scalar sqrtss path. (The fused loop the port
        // shipped never vectorized: the omp-simd pragma is inert without -fopenmp-simd, and GCC
        // won't reorder the float reduction without -ffast-math, so every sqrt ran scalar.)
        for (usize j = 0; j < n2; ++j) {
            f32 dx = x1 - x2[j], dy = y1 - y2[j];
            dist[j] = std::sqrt(dx * dx + dy * dy);
        }
        // Phase 2 (scalar, sequential j=0..n2): the select's conditional division keeps this part
        // scalar, but it preserves the original reduction's exact FP order -> bit-identical sum/max.
        f32 acc = 0.0f, mx = 0.0f;
        for (usize j = 0; j < n2; ++j) {
            f32 pd = (r1 + r2[j]) - dist[j];
            f32 pdc = (pd >= epsilon) ? pd : eps2 / (two_eps - pd);
            acc += pdc;
            mx = max_f(mx, pdc);
        }
        depth_sum += acc;
        depth_max = max_f(depth_max, mx);
    }
    return depth_max + DEPTH_SUM_W * depth_sum;
}

// ---- overlap/mod.rs ----
inline f32 shape_size_penalty(const Polygon& s1, const Polygon& s2) {
    f32 p1 = std::sqrt(s1.surrogate_ref().convex_hull_area);
    f32 p2 = std::sqrt(s2.surrogate_ref().convex_hull_area);
    return std::sqrt(p1 * p2);
}

// Algorithm 4
inline f32 pairwise_overlap_loss(const Polygon& s1, const Polygon& s2) {
    f32 epsilon = max_f(s1.diameter, s2.diameter) * OVERLAP_PROXY_EPSILON_DIAM_RATIO;
#if USE_DEPTH_PROXY
    // depth is ~linear (a length) -> drop the outer sqrt that linearized area; shrink the hull patch
    f32 p = overlap_depth_proxy(s1.surrogate_ref(), s2.surrogate_ref(), epsilon) + epsilon * epsilon;
    return p * std::sqrt(shape_size_penalty(s1, s2));
#else
    f32 overlap_proxy = overlap_area_proxy(s1.surrogate_ref(), s2.surrogate_ref(), epsilon) + epsilon * epsilon;
    f32 penalty = shape_size_penalty(s1, s2);
    return std::sqrt(overlap_proxy) * penalty;
#endif
}

inline f32 containment_overlap_loss(const Polygon& s, Rect c_bbox) {
    Rect s_bbox = s.bbox;
    f32 overlap;
    auto inter = Rect::intersection(s_bbox, c_bbox);
    if (inter) {
        overlap = (s_bbox.area() - inter->area()) + 0.0001f * s_bbox.area();
    } else {
        overlap = s_bbox.area() + s_bbox.centroid().distance_to(c_bbox.centroid());
    }
    f32 penalty = shape_size_penalty(s, s);
    return 2.0f * std::sqrt(overlap) * penalty;
}

// ---- pair_matrix.rs ----
struct OverlapEntry {
    f32 loss = 0.0f;
    f32 weight = 1.0f;
    f32 weighted_loss() const { return weight * loss; }
};

struct PairMatrix {
    usize size = 0;
    std::vector<OverlapEntry> data;
    PairMatrix() = default;
    explicit PairMatrix(usize size_) : size(size_), data(size_ * (size_ + 1) / 2, OverlapEntry{0.0f, 1.0f}) {}

    static usize calc_idx(usize row, usize col, usize size) {
        if (row <= col) return (row * size) + col - ((row * (row + 1)) / 2);
        return (col * size) + row - ((col * (col + 1)) / 2);
    }
    OverlapEntry& at(usize row, usize col) { return data[calc_idx(row, col, size)]; }
    const OverlapEntry& at(usize row, usize col) const { return data[calc_idx(row, col, size)]; }
};

// ---- tracker.rs : OverlapTracker ----
struct OverlapTracker {
    usize size = 0;
    SecondaryMap<usize> pk_idx_map;        // PartKey -> index
    PairMatrix pair_collisions;
    std::vector<OverlapEntry> container_collisions;
    std::vector<OverlapEntry> hole_collisions;     // per-part: aggregated penetration into sheet holes

    OverlapTracker() = default;

    explicit OverlapTracker(const Layout& l) {
        size = l.placed_parts.size();
        usize i = 0;
        l.placed_parts.for_each([&](PartKey pk, const PlacedPart&) { pk_idx_map.insert(pk, i++); });
        pair_collisions = PairMatrix(size);
        container_collisions.assign(size, OverlapEntry{0.0f, 1.0f});
        hole_collisions.assign(size, OverlapEntry{0.0f, 1.0f});
        l.placed_parts.for_each([&](PartKey pk, const PlacedPart&) { recompute_loss_for_item(pk, l); });
    }

    void recompute_loss_for_item(PartKey pk, const Layout& l) {
        usize idx = *pk_idx_map.get(pk);
        const PlacedPart& pi = l.placed_parts[pk];
        const Polygon& shape = *pi.shape;

        for (usize i = 0; i < size; ++i) pair_collisions.at(idx, i).loss = 0.0f;
        container_collisions[idx].loss = 0.0f;
        hole_collisions[idx].loss = 0.0f;

        ObstacleCollector collector;
        l.engine_ref().collect_poly_collisions(shape, collector);
        collector.remove_by_entity(ObstacleRef::placed_item(pi.part_id, pi.d_transf, pk));

        collector.for_each([&](ObstacleKey okey, const ObstacleRef& obs) {
            if (obs.kind == ObstacleKind::PlacedPart) {
                PartKey other_pk = obs.pk;
                const Polygon& shape_other = *l.placed_parts[other_pk].shape;
                usize idx_other = *pk_idx_map.get(other_pk);
                f32 loss = pairwise_overlap_loss(shape, shape_other);
                pair_collisions.at(idx, idx_other).loss = loss;
            } else if (obs.kind == ObstacleKind::Exterior) {
                f32 loss = containment_overlap_loss(shape, l.container.collision_outer->bbox);
                container_collisions[idx].loss = loss;
            } else if (obs.kind == ObstacleKind::Hole) {
                // Avoiding a sheet hole is identical to avoiding a static part: the part must lie
                // OUTSIDE the hole. Aggregate penetration into all holes (a part may straddle more
                // than one) so the relaxation gets a gradient pushing it out. (No-hole runs never
                // collect a Hole, so this stays exactly 0 and the math below is unchanged.)
                const Polygon& hole_shape = *l.engine_ref().obstacles_map[okey].shape;
                hole_collisions[idx].loss += pairwise_overlap_loss(shape, hole_shape);
            }
        });
    }

    // Incremental rebuild after Relaxer::change_strip_width. The from-scratch constructor re-queries
    // EVERY part against the rebuilt engine, but a width change only moves (a) the parts right of the
    // split (rigidly co-translated by delta) and (b) the container's RIGHT wall. Everything else is
    // bit-unchanged:
    //   - unshifted-vs-unshifted pair losses: pairwise_overlap_loss reads only pole-coordinate
    //     DIFFERENCES and per-shape constants -> identical inputs -> identical values; keep them.
    //   - unshifted hole losses: neither the part nor the holes moved; keep them.
    //   - unshifted container losses: containment_overlap_loss depends on the container bbox only
    //     through Rect::intersection(s_bbox, c_bbox); only the right wall moves, so a part with
    //     bbox.x_max <= new wall AND no stale exterior loss keeps an unchanged value. A part that
    //     straddles the new wall, or had a nonzero loss while the wall moved (grow case: the loss
    //     may now be 0), is re-queried.
    //   - shifted parts: full recompute_loss_for_item (covers shifted-vs-shifted and
    //     shifted-vs-unshifted in both directions via the symmetric PairMatrix, plus exterior+holes).
    // GLS weights are reset to 1.0 exactly like the from-scratch constructor (cold reset is the
    // measured optimum; warm-starting weights across a width change packs looser).
    void rebuild_after_width_change(const Layout& l, const std::vector<std::pair<PartKey, PartKey>>& remaps) {
        for (const auto& [old_pk, new_pk] : remaps) {
            usize idx = *pk_idx_map.get(old_pk);
            pk_idx_map.remove(old_pk);
            pk_idx_map.insert(new_pk, idx);
        }
        for (auto& e : pair_collisions.data) e.weight = 1.0f;
        for (auto& e : container_collisions) e.weight = 1.0f;
        for (auto& e : hole_collisions) e.weight = 1.0f;

        std::vector<bool> is_shifted(size, false);
        for (const auto& [old_pk, new_pk] : remaps) is_shifted[*pk_idx_map.get(new_pk)] = true;
        for (const auto& [old_pk, new_pk] : remaps) recompute_loss_for_item(new_pk, l);

        const f32 cont_x_max = l.container.collision_outer->bbox.x_max;
        l.placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
            usize idx = *pk_idx_map.get(pk);
            if (is_shifted[idx]) return;
            if (container_collisions[idx].loss != 0.0f || pi.shape->bbox.x_max > cont_x_max)
                recompute_loss_for_item(pk, l);
        });
    }

    void restore_but_keep_weights(const OverlapTracker& cts, const Layout&) {
        pk_idx_map = cts.pk_idx_map;
        for (usize i = 0; i < pair_collisions.data.size(); ++i) pair_collisions.data[i].loss = cts.pair_collisions.data[i].loss;
        for (usize i = 0; i < container_collisions.size(); ++i) container_collisions[i].loss = cts.container_collisions[i].loss;
        for (usize i = 0; i < hole_collisions.size(); ++i) hole_collisions[i].loss = cts.hole_collisions[i].loss;
    }

    OverlapTracker save() const { return *this; }

    void register_part_move(const Layout& l, PartKey old_pk, PartKey new_pk) {
        usize idx = *pk_idx_map.get(old_pk);
        pk_idx_map.remove(old_pk);
        pk_idx_map.insert(new_pk, idx);
        recompute_loss_for_item(new_pk, l);
    }

    // Algorithm 8
    void update_gls_weights() {
        f32 max_loss = 0.0f;
        for (const auto& e : pair_collisions.data) max_loss = max_f(max_loss, e.loss);
        for (const auto& e : container_collisions) max_loss = max_f(max_loss, e.loss);
        for (const auto& e : hole_collisions) max_loss = max_f(max_loss, e.loss);

        auto upd = [&](OverlapEntry& e) {
            f32 multiplier;
            if (e.loss == 0.0f) multiplier = GLS_WEIGHT_DECAY;
            else multiplier = GLS_WEIGHT_MIN_INC_RATIO + (GLS_WEIGHT_MAX_INC_RATIO - GLS_WEIGHT_MIN_INC_RATIO) * (e.loss / max_loss);
            e.weight = max_f(e.weight * multiplier, 1.0f);
        };
        for (auto& e : pair_collisions.data) upd(e);
        for (auto& e : container_collisions) upd(e);
        for (auto& e : hole_collisions) upd(e);
    }

    f32 get_pair_weight(PartKey pk1, PartKey pk2) const {
        return pair_collisions.at(*const_cast<SecondaryMap<usize>&>(pk_idx_map).get(pk1),
                                  *const_cast<SecondaryMap<usize>&>(pk_idx_map).get(pk2)).weight;
    }
    usize idx_of(PartKey pk) const { return *const_cast<SecondaryMap<usize>&>(pk_idx_map).get(pk); }

    f32 get_container_weight(PartKey pk) const { return container_collisions[idx_of(pk)].weight; }
    f32 get_hole_weight(PartKey pk) const { return hole_collisions[idx_of(pk)].weight; }
    f32 get_pair_loss(PartKey pk1, PartKey pk2) const { return pair_collisions.at(idx_of(pk1), idx_of(pk2)).loss; }
    f32 get_container_loss(PartKey pk) const { return container_collisions[idx_of(pk)].loss; }

    f32 get_loss(PartKey pk) const {
        usize idx = idx_of(pk);
        f32 pair_loss = 0.0f;
        for (usize i = 0; i < size; ++i) pair_loss += pair_collisions.at(idx, i).loss;
        return container_collisions[idx].loss + hole_collisions[idx].loss + pair_loss;
    }
    // Early-exit equivalent of `get_loss(pk) > 0.0f`. All loss terms are non-negative (penetration
    // depths / clipped areas), so the sum is positive iff ANY term is positive -> stop at the first
    // hit instead of summing the whole O(size) triangular row. Bit-identical decision; the relax loop
    // only ever asks "does this part still overlap?", and for overlapping parts (the candidates) this
    // returns on the first nonzero entry instead of scanning every neighbour.
    bool has_loss(PartKey pk) const {
        usize idx = idx_of(pk);
        if (container_collisions[idx].loss > 0.0f) return true;
        if (hole_collisions[idx].loss > 0.0f) return true;
        for (usize i = 0; i < size; ++i) if (pair_collisions.at(idx, i).loss > 0.0f) return true;
        return false;
    }
    f32 get_weighted_loss(PartKey pk) const {
        usize idx = idx_of(pk);
        f32 w = 0.0f;
        for (usize i = 0; i < size; ++i) w += pair_collisions.at(idx, i).weighted_loss();
        return container_collisions[idx].weighted_loss() + hole_collisions[idx].weighted_loss() + w;
    }
    f32 get_total_loss() const {
        f32 c = 0.0f, p = 0.0f, h = 0.0f;
        for (const auto& e : container_collisions) c += e.loss;
        for (const auto& e : pair_collisions.data) p += e.loss;
        for (const auto& e : hole_collisions) h += e.loss;
        return c + p + h;
    }
    f32 get_total_weighted_loss() const {
        f32 c = 0.0f, p = 0.0f, h = 0.0f;
        for (const auto& e : container_collisions) c += e.weighted_loss();
        for (const auto& e : pair_collisions.data) p += e.weighted_loss();
        for (const auto& e : hole_collisions) h += e.weighted_loss();
        return c + p + h;
    }
};
using OverlapSnapshot = OverlapTracker;

} // namespace nest
