// Faithful C++ port of solver/src/eval/* :
//   SampleEval ordering, the SampleEvaluator interface, the LBF evaluator, the
//   separation evaluator, and the specialized early-terminating collision pipeline.
#pragma once
#include "overlap.hpp"
#include "../geometry/scene.hpp"
#include "../geometry/collision_engine.hpp"
#include <optional>

namespace nest {
using namespace nest;

// ---- sample_eval.rs : SampleEval (Clear < Collision < Invalid) ----
enum class SampleKind { Clear, Collision, Invalid };
struct SampleEval {
    SampleKind kind = SampleKind::Invalid;
    f32 loss = 0.0f;
    static SampleEval clear(f32 l) { return {SampleKind::Clear, l}; }
    static SampleEval collision(f32 l) { return {SampleKind::Collision, l}; }
    static SampleEval invalid() { return {SampleKind::Invalid, 0.0f}; }
};

inline int rank_of(SampleKind k) { return k == SampleKind::Clear ? 0 : (k == SampleKind::Collision ? 1 : 2); }

// returns <0 if a<b, 0 if equal, >0 if a>b (a "less" means better)
inline int sample_cmp(const SampleEval& a, const SampleEval& b) {
    int ra = rank_of(a.kind), rb = rank_of(b.kind);
    if (ra != rb) return ra < rb ? -1 : 1;
    if (a.kind == SampleKind::Invalid) return 0;
    if (FPA(a.loss) == FPA(b.loss)) return 0;
    return a.loss < b.loss ? -1 : 1;
}
inline bool operator<(const SampleEval& a, const SampleEval& b) { return sample_cmp(a, b) < 0; }
inline bool operator==(const SampleEval& a, const SampleEval& b) { return sample_cmp(a, b) == 0; }
inline bool operator!=(const SampleEval& a, const SampleEval& b) { return sample_cmp(a, b) != 0; }

struct SampleEvaluator {
    virtual ~SampleEvaluator() = default;
    virtual SampleEval evaluate_sample(RigidTransform dt, std::optional<SampleEval> upper_bound) = 0;
    virtual usize n_evals() const = 0;
};

// ---- bit_reversal_iterator.rs ----
inline uint64_t reverse_bits64(uint64_t x) {
    x = ((x & 0x5555555555555555ull) << 1) | ((x >> 1) & 0x5555555555555555ull);
    x = ((x & 0x3333333333333333ull) << 2) | ((x >> 2) & 0x3333333333333333ull);
    x = ((x & 0x0F0F0F0F0F0F0F0Full) << 4) | ((x >> 4) & 0x0F0F0F0F0F0F0F0Full);
    x = ((x & 0x00FF00FF00FF00FFull) << 8) | ((x >> 8) & 0x00FF00FF00FF00FFull);
    x = ((x & 0x0000FFFF0000FFFFull) << 16) | ((x >> 16) & 0x0000FFFF0000FFFFull);
    x = (x << 32) | (x >> 32);
    return x;
}
inline std::vector<usize> bit_reversal_order(usize n) {
    std::vector<usize> out;
    if (n == 0) return out;
    int k = 0; { uint64_t v = n; while (v) { ++k; v >>= 1; } } // bit length of n == 64 - leading_zeros
    out.reserve(n);
    usize i = 0;
    while (out.size() < n) {
        uint64_t rev = reverse_bits64(static_cast<uint64_t>(i)) >> (64 - k);
        ++i;
        if (rev < n) out.push_back(static_cast<usize>(rev));
    }
    return out;
}

// ---- specialized overlap pipeline : SpecializedHazardCollector ----
struct SpecializedHazardCollector {
    const Layout* layout;
    const OverlapTracker* ct;
    PartKey current_pk;
    ObstacleKey current_obstacle_key;
    SecondaryMap<std::pair<ObstacleRef, usize>> detected;
    usize idx_counter = 0;
    std::pair<usize, f32> loss_cache{0, 0.0f};
    f32 loss_bound = std::numeric_limits<f32>::infinity();

    SpecializedHazardCollector(const Layout& l, const OverlapTracker& ct_, PartKey current_pk_)
        : layout(&l), ct(&ct_), current_pk(current_pk_) {
        current_obstacle_key = l.engine_ref().obstacle_key_from_part_key(current_pk_);
    }

    void reload(f32 lb) {
        detected.clear();
        idx_counter = 0;
        loss_cache = {0, 0.0f};
        loss_bound = lb;
    }

    // Collector interface (used by QuadtreeNode::collect_collisions)
    bool contains_key(ObstacleKey okey) const {
        return const_cast<SecondaryMap<std::pair<ObstacleRef, usize>>&>(detected).contains(okey) ||
               okey == current_obstacle_key;
    }
    void insert(ObstacleKey okey, const ObstacleRef& entity) {
        detected.insert(okey, {entity, idx_counter});
        ++idx_counter;
    }
    void remove_by_key(ObstacleKey okey) {
        auto* v = detected.get(okey);
        usize idx = v->second;
        detected.remove(okey);
        if (idx < loss_cache.first) loss_cache = {0, 0.0f};
    }
    bool is_empty() const { return detected.size() == 0; }
    usize len() const { return detected.size(); }

    f32 calc_weighted_loss(ObstacleKey okey, const ObstacleRef& obs, const Polygon& shape) const {
        if (obs.kind == ObstacleKind::PlacedPart) {
            const Polygon& other_shape = *layout->placed_parts[obs.pk].shape;
            f32 loss = pairwise_overlap_loss(other_shape, shape);
            f32 weight = ct->get_pair_weight(current_pk, obs.pk);
            return loss * weight;
        } else if (obs.kind == ObstacleKind::Exterior) {
            f32 loss = containment_overlap_loss(shape, layout->container.collision_outer->bbox);
            f32 weight = ct->get_container_weight(current_pk);
            return loss * weight;
        } else if (obs.kind == ObstacleKind::Hole) {
            // sheet hole: penalize like a static part the candidate must stay clear of, so the
            // sample search avoids placing inside holes (not just rejecting at final feasibility).
            const Polygon& hole_shape = *layout->engine_ref().obstacles_map[okey].shape;
            f32 loss = pairwise_overlap_loss(hole_shape, shape);
            f32 weight = ct->get_hole_weight(current_pk);
            return loss * weight;
        }
        return 0.0f; // QualityZone (quality > 0): not penalized (preserves prior behaviour)
    }

    f32 loss(const Polygon& shape) {
        auto [cache_idx, cached_loss] = loss_cache;
        if (cache_idx < idx_counter) {
            f32 extra = 0.0f;
            detected.for_each([&](ObstacleKey okey, const std::pair<ObstacleRef, usize>& v) {
                if (v.second >= cache_idx) extra += calc_weighted_loss(okey, v.first, shape);
            });
            loss_cache = {idx_counter, cached_loss + extra};
        }
        return loss_cache.second;
    }
    bool early_terminate(const Polygon& shape) { return loss(shape) > loss_bound; }
};

inline void collect_poly_collisions_in_detector_custom(const CollisionEngine& engine, const RigidTransform& dt,
                                                       Polygon& shape_buffer, const Polygon& reference_shape,
                                                       SpecializedHazardCollector& collector) {
    AffineTransform t = dt.compose();
    shape_buffer.transform_from(reference_shape, t);
    Polygon& shape = shape_buffer;

    {
        // pole pre-check up to 50% area coverage to fail fast
        f32 area_threshold = shape.area * 0.5f / PI_F;
        f32 area_sum = 0.0f;
        for (const auto& pole : shape.surrogate_ref().poles) {
            engine.quadtree.collect_collisions(pole, collector);
            if (collector.early_terminate(shape)) return;
            area_sum += pole.radius * pole.radius;
            if (area_sum > area_threshold) break;
        }
    }
    const QuadtreeNode* v = engine.get_virtual_root(shape.bbox);
    for (usize idx : bit_reversal_order(shape.n_vertices())) {
        Edge e = shape.edge(idx);
        v->collect_collisions(e, collector);
        if (collector.early_terminate(shape)) return;
    }
    for (const auto& qt_obs : v->obstacles.obstacles) {
        if (qt_obs.presence == QuadtreePresence::Partial) {
            if (!collector.contains_key(qt_obs.okey)) {
                const Polygon& h_shape = *engine.obstacles_map[qt_obs.okey].shape;
                if (engine.detect_containment_collision(shape, h_shape, qt_obs.entity)) {
                    collector.insert(qt_obs.okey, qt_obs.entity);
                    if (collector.early_terminate(shape)) return;
                }
            }
        }
    }
}

// ---- lbf_evaluator.rs ----
struct InitialEvaluator : SampleEvaluator {
    const Layout* layout;
    const Part* part;
    Polygon shape_buff;
    usize n_evals_ = 0;

    InitialEvaluator(const Layout& l, const Part& it) : layout(&l), part(&it), shape_buff(*it.collision_shape) {}

    SampleEval evaluate_sample(RigidTransform dt, std::optional<SampleEval>) override {
        ++n_evals_;
        const CollisionEngine& engine = layout->engine_ref();
        AffineTransform transf = dt.compose();
        NoFilter nf;
        if (engine.detect_surrogate_collision(part->collision_shape->surrogate_ref(), transf, nf)) return SampleEval::invalid();
        shape_buff.transform_from(*part->collision_shape, transf);
        if (engine.detect_poly_collision(shape_buff, nf)) return SampleEval::invalid();
        Point poi = shape_buff.poi.center;
        Point c0 = shape_buff.bbox.corners()[0];
        f32 loss = 10.0f * (poi.x + c0.x) + 1.0f * (poi.y + c0.y);
        return SampleEval::clear(loss);
    }
    usize n_evals() const override { return n_evals_; }
};

// ---- sep_evaluator.rs : Algorithm 7 ----
struct RelaxEvaluator : SampleEvaluator {
    const Layout* layout;
    const Part* part;
    SpecializedHazardCollector collector;
    Polygon shape_buff;
    usize n_evals_ = 0;

    RelaxEvaluator(const Layout& l, const Part& it, PartKey current_pk, const OverlapTracker& ct)
        : layout(&l), part(&it), collector(l, ct, current_pk), shape_buff(*it.collision_shape) {}

    SampleEval evaluate_sample(RigidTransform dt, std::optional<SampleEval> upper_bound) override {
        ++n_evals_;
        const CollisionEngine& engine = layout->engine_ref();
        f32 loss_bound;
        if (upper_bound && upper_bound->kind == SampleKind::Collision) loss_bound = upper_bound->loss;
        else if (upper_bound && upper_bound->kind == SampleKind::Clear) loss_bound = 0.0f;
        else loss_bound = std::numeric_limits<f32>::infinity();

        collector.reload(loss_bound);
        collect_poly_collisions_in_detector_custom(engine, dt, shape_buff, *part->collision_shape, collector);

        if (collector.early_terminate(shape_buff)) return SampleEval::invalid();
        if (collector.is_empty()) return SampleEval::clear(0.0f);
        return SampleEval::collision(collector.loss(shape_buff));
    }
    usize n_evals() const override { return n_evals_; }
};

} // namespace nest
