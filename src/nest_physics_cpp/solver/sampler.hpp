// Faithful C++ port of solver/src/sample/* :
//   UniformBBoxSampler, BestSamples pool, coordinate-descent refinement, search_placement.
#pragma once
#include "constants.hpp"
#include "evaluator.hpp"
#include "rng.hpp"
#include "../geometry/scene.hpp"

namespace nest {
using namespace nest;

struct SampleConfig {
    usize n_container_samples;
    usize n_focussed_samples;
    usize n_coord_descents;
};
inline constexpr SampleConfig INITIAL_SAMPLE_CONFIG{1000, 0, 3};

struct FRange { f32 start, end; bool is_empty() const { return start >= end; } };
inline FRange intersect_range(FRange a, FRange b) { return {max_f(a.start, b.start), min_f(a.end, b.end)}; }

// ---- uniform_sampler.rs ----
// Global orientation samples for continuous-rotation parts. Default 16 matches the Rust port;
// the nester raises it (e.g. 32) for elongated ribbon parts where orientation dominates density.
inline usize ROT_N_SAMPLES = 16;

struct RotEntry { f32 r; FRange x_range, y_range; };

struct UniformBBoxSampler {
    std::vector<RotEntry> rot_entries;

    static std::optional<UniformBBoxSampler> make(Rect sample_bbox, const Part& part, Rect container_bbox) {
        std::vector<f32> rotations;
        switch (part.allowed_rotation.kind) {
            case RotationKind::None: rotations = {0.0f}; break;
            case RotationKind::Discrete: rotations = part.allowed_rotation.discrete; break;
            case RotationKind::Continuous: {
                f32 step = (2.0f * PI_F) / static_cast<f32>(ROT_N_SAMPLES);
                for (usize i = 0; i < ROT_N_SAMPLES; ++i) rotations.push_back(static_cast<f32>(i) * step);
                break;
            }
        }
        Polygon shape_buffer = *part.collision_shape;
        FRange sample_x{sample_bbox.x_min, sample_bbox.x_max};
        FRange sample_y{sample_bbox.y_min, sample_bbox.y_max};

        std::vector<RotEntry> rot_entries;
        for (f32 r : rotations) {
            shape_buffer.transform_from(*part.collision_shape, AffineTransform::from_rotation(r));
            Rect rb = shape_buffer.bbox;
            FRange cont_x{container_bbox.x_min - rb.x_min, container_bbox.x_max - rb.x_max};
            FRange cont_y{container_bbox.y_min - rb.y_min, container_bbox.y_max - rb.y_max};
            FRange x_range = intersect_range(cont_x, sample_x);
            FRange y_range = intersect_range(cont_y, sample_y);
            if (x_range.is_empty() || y_range.is_empty()) continue;
            rot_entries.push_back({r, x_range, y_range});
        }
        if (rot_entries.empty()) return std::nullopt;
        UniformBBoxSampler s;
        s.rot_entries = std::move(rot_entries);
        return s;
    }

    RigidTransform sample(Rng& rng) const {
        const RotEntry& e = rng.choose(rot_entries);
        f32 x = rng.random_range_f32(e.x_range.start, e.x_range.end);
        f32 y = rng.random_range_f32(e.y_range.start, e.y_range.end);
        return RigidTransform(e.r, x, y);
    }
};

inline RigidTransform convert_sample_to_closest_feasible(RigidTransform dt, const Part& part) {
    f32 fr;
    switch (part.allowed_rotation.kind) {
        case RotationKind::None: fr = 0.0f; break;
        case RotationKind::Continuous: fr = dt.rotation; break;
        case RotationKind::Discrete: {
            fr = part.allowed_rotation.discrete[0];
            f32 best = std::numeric_limits<f32>::infinity();
            for (f32 r : part.allowed_rotation.discrete) {
                f32 d = std::fabs(normalize_rotation(dt.rotation - r));
                if (d < best) { best = d; fr = r; }
            }
            break;
        }
    }
    return RigidTransform(fr, dt.tx, dt.ty);
}

// ---- best_samples.rs ----
inline bool dtransfs_are_similar(RigidTransform d1, RigidTransform d2, f32 xth, f32 yth) {
    f32 xdiff = std::fabs(d1.tx - d2.tx), ydiff = std::fabs(d1.ty - d2.ty);
    if (xdiff < xth && ydiff < yth) {
        f32 r1 = std::fmod(d1.rotation, 2.0f * PI_F), r2 = std::fmod(d2.rotation, 2.0f * PI_F);
        return std::fabs(r1 - r2) < to_radians(1.0f);
    }
    return false;
}

struct BestSamples {
    usize size;
    std::vector<std::pair<RigidTransform, SampleEval>> samples;
    f32 unique_thresh;

    BestSamples(usize size_, f32 ut) : size(size_), unique_thresh(ut) {}

    SampleEval upper_bound() const {
        if (samples.size() >= size && size > 0) return samples[size - 1].second;
        return SampleEval::invalid();
    }

    bool report(RigidTransform dt, SampleEval eval) {
        bool accept;
        if (!(eval < upper_bound())) {
            accept = false;
        } else {
            bool any_similar = false;
            for (auto& [d, _] : samples)
                if (dtransfs_are_similar(d, dt, unique_thresh, unique_thresh)) { any_similar = true; break; }
            if (!any_similar) {
                if (samples.size() == size) samples.pop_back();
                accept = true;
            } else {
                bool better_than_all = true;
                for (auto& [d, e] : samples)
                    if (dtransfs_are_similar(d, dt, unique_thresh, unique_thresh) && !(eval < e)) { better_than_all = false; break; }
                if (better_than_all) {
                    samples.erase(std::remove_if(samples.begin(), samples.end(),
                                  [&](const std::pair<RigidTransform, SampleEval>& s) {
                                      return dtransfs_are_similar(s.first, dt, unique_thresh, unique_thresh);
                                  }), samples.end());
                    accept = true;
                } else accept = false;
            }
        }
        if (accept) {
            samples.emplace_back(dt, eval);
            std::stable_sort(samples.begin(), samples.end(),
                             [](const auto& a, const auto& b) { return a.second < b.second; });
            return true;
        }
        return false;
    }

    std::optional<std::pair<RigidTransform, SampleEval>> best() const {
        if (samples.empty()) return std::nullopt;
        return samples.front();
    }
};

// ---- coord_descent.rs ----
struct CDConfig {
    f32 t_step_init, t_step_limit, r_step_init, r_step_limit;
    bool wiggle;
};

enum class CDAxis { Horizontal, Vertical, ForwardDiag, BackwardDiag, Wiggle };
inline CDAxis cdaxis_random(Rng& rng, bool rotate) {
    int v = rng.random_range_int(0, rotate ? 6 : 4);
    switch (v) {
        case 0: return CDAxis::Horizontal;
        case 1: return CDAxis::Vertical;
        case 2: return CDAxis::ForwardDiag;
        case 3: return CDAxis::BackwardDiag;
        default: return CDAxis::Wiggle; // 4,5
    }
}

struct CoordinateDescent {
    RigidTransform pos;
    SampleEval eval;
    CDAxis axis;
    f32 t_step_x, t_step_y;
    f32 r_step;
    f32 t_step_limit, r_step_limit;
    bool wiggle;

    std::optional<std::array<RigidTransform, 2>> ask() const {
        f32 sx = t_step_x, sy = t_step_y, sr = r_step;
        if (sx < t_step_limit && sy < t_step_limit && (sr < r_step_limit || !wiggle)) return std::nullopt;
        f32 tx = pos.tx, ty = pos.ty, r = pos.rotation;
        std::array<std::array<f32, 3>, 2> tr;
        switch (axis) {
            case CDAxis::Horizontal: tr = {{{tx + sx, ty, r}, {tx - sx, ty, r}}}; break;
            case CDAxis::Vertical: tr = {{{tx, ty + sy, r}, {tx, ty - sy, r}}}; break;
            case CDAxis::ForwardDiag: tr = {{{tx + sx, ty + sy, r}, {tx - sx, ty - sy, r}}}; break;
            case CDAxis::BackwardDiag: tr = {{{tx - sx, ty + sy, r}, {tx + sx, ty - sy, r}}}; break;
            case CDAxis::Wiggle: tr = {{{tx, ty, r + sr}, {tx, ty, r - sr}}}; break;
        }
        return std::array<RigidTransform, 2>{RigidTransform(tr[0][2], tr[0][0], tr[0][1]),
                                              RigidTransform(tr[1][2], tr[1][0], tr[1][1])};
    }

    void tell(RigidTransform new_pos, SampleEval new_eval, Rng& rng) {
        int c = sample_cmp(new_eval, eval);
        bool better = c < 0, worse = c > 0;
        if (!worse) { pos = new_pos; eval = new_eval; }
        f32 m = better ? CD_STEP_SUCCESS : CD_STEP_FAIL;
        switch (axis) {
            case CDAxis::Horizontal: t_step_x *= m; break;
            case CDAxis::Vertical: t_step_y *= m; break;
            case CDAxis::ForwardDiag:
            case CDAxis::BackwardDiag: t_step_x *= std::sqrt(m); t_step_y *= std::sqrt(m); break;
            case CDAxis::Wiggle: r_step *= m; break;
        }
        if (!better) axis = cdaxis_random(rng, wiggle);
    }
};

inline std::pair<RigidTransform, SampleEval> refine_coord_desc(
    std::pair<RigidTransform, SampleEval> start, SampleEvaluator& evaluator, CDConfig cfg, Rng& rng) {
    CoordinateDescent cd;
    cd.pos = start.first; cd.eval = start.second;
    cd.axis = cdaxis_random(rng, cfg.wiggle);
    cd.t_step_x = cfg.t_step_init; cd.t_step_y = cfg.t_step_init;
    cd.t_step_limit = cfg.t_step_limit;
    cd.r_step = cfg.r_step_init; cd.r_step_limit = cfg.r_step_limit;
    cd.wiggle = cfg.wiggle;

    while (auto c = cd.ask()) {
        SampleEval e0 = evaluator.evaluate_sample((*c)[0], cd.eval);
        SampleEval e1 = evaluator.evaluate_sample((*c)[1], cd.eval);
        if (sample_cmp(e0, e1) <= 0) cd.tell((*c)[0], e0, rng);
        else cd.tell((*c)[1], e1, rng);
    }
    return {cd.pos, cd.eval};
}

// ---- search.rs : Algorithm 6 ----
inline CDConfig prerefine_cd_config(const Part& part) {
    f32 md = min_f(part.collision_shape->bbox.width(), part.collision_shape->bbox.height());
    bool wiggle = part.allowed_rotation.kind == RotationKind::Continuous;
    return {md * PRE_REFINE_CD_TL_RATIOS.first, md * PRE_REFINE_CD_TL_RATIOS.second,
            PRE_REFINE_CD_R_STEPS.first, PRE_REFINE_CD_R_STEPS.second, wiggle};
}
inline CDConfig final_refine_cd_config(const Part& part) {
    f32 md = min_f(part.collision_shape->bbox.width(), part.collision_shape->bbox.height());
    bool wiggle = part.allowed_rotation.kind == RotationKind::Continuous;
    return {md * SND_REFINE_CD_TL_RATIOS.first, md * SND_REFINE_CD_TL_RATIOS.second,
            SND_REFINE_CD_R_STEPS.first, SND_REFINE_CD_R_STEPS.second, wiggle};
}

inline std::pair<std::optional<std::pair<RigidTransform, SampleEval>>, usize> search_placement(
    const Layout& l, const Part& part, std::optional<PartKey> ref_pk, SampleEvaluator& evaluator,
    SampleConfig sample_config, Rng& rng) {
    f32 part_min_dim = min_f(part.collision_shape->bbox.width(), part.collision_shape->bbox.height());
    BestSamples best_samples(sample_config.n_coord_descents, part_min_dim * UNIQUE_SAMPLE_THRESHOLD);

    std::optional<UniformBBoxSampler> focussed_sampler;
    if (ref_pk) {
        RigidTransform dt = l.placed_parts[*ref_pk].d_transf;
        SampleEval eval = evaluator.evaluate_sample(dt, best_samples.upper_bound());
        best_samples.report(dt, eval);
        Rect pi_bbox = l.placed_parts[*ref_pk].shape->bbox;
        focussed_sampler = UniformBBoxSampler::make(pi_bbox, part, l.container.collision_outer->bbox);
    }
    std::optional<UniformBBoxSampler> container_sampler =
        UniformBBoxSampler::make(l.container.collision_outer->bbox, part, l.container.collision_outer->bbox);

    if (focussed_sampler) {
        for (usize i = 0; i < sample_config.n_focussed_samples; ++i) {
            RigidTransform dt = focussed_sampler->sample(rng);
            SampleEval eval = evaluator.evaluate_sample(dt, best_samples.upper_bound());
            best_samples.report(dt, eval);
        }
    }
    if (container_sampler) {
        for (usize i = 0; i < sample_config.n_container_samples; ++i) {
            RigidTransform dt = container_sampler->sample(rng);
            SampleEval eval = evaluator.evaluate_sample(dt, best_samples.upper_bound());
            best_samples.report(dt, eval);
        }
    }

    std::vector<std::pair<RigidTransform, SampleEval>> starts = best_samples.samples;
    for (auto& start : starts) {
        auto descended = refine_coord_desc(start, evaluator, prerefine_cd_config(part), rng);
        best_samples.report(descended.first, descended.second);
    }

    std::optional<std::pair<RigidTransform, SampleEval>> final_sample;
    if (auto b = best_samples.best())
        final_sample = refine_coord_desc(*b, evaluator, final_refine_cd_config(part), rng);

    return {final_sample, evaluator.n_evals()};
}

} // namespace nest
