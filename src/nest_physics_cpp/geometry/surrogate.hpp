// Faithful C++ port of nest-rs geometry/fail_fast/sp_surrogate.rs (struct + config).
// Surrogate is a light-weight representation of an Polygon (poles + piers + convex
// hull) used for fail-fast collision detection. Its methods that need the full Polygon
// (new/transform) are declared here and defined in polygon.hpp after Polygon.
#pragma once
#include "primitives.hpp"

namespace nest {

constexpr int N_POLE_LIMITS = 3;

struct SurrogateConfig {
    // (max_poles, coverage_threshold) limits at increasing coverage levels.
    std::array<std::pair<usize, f32>, N_POLE_LIMITS> n_pole_limits{};
    usize n_ff_poles = 0; // number of poles tested during fail-fast
    usize n_ff_piers = 0; // number of piers tested during fail-fast

    static SurrogateConfig none() {
        SurrogateConfig c;
        for (auto& l : c.n_pole_limits) l = {0, 0.0f};
        c.n_ff_poles = 0;
        c.n_ff_piers = 0;
        return c;
    }
    bool operator==(const SurrogateConfig& o) const {
        return n_pole_limits == o.n_pole_limits && n_ff_poles == o.n_ff_poles && n_ff_piers == o.n_ff_piers;
    }
    bool operator!=(const SurrogateConfig& o) const { return !(*this == o); }
};

struct Polygon; // fwd

struct Surrogate {
    std::vector<Circle> poles;          // poles[0] is the POI; sorted by generation
    std::vector<Edge> piers;
    std::vector<usize> convex_hull_indices;
    f32 convex_hull_area = 0.0f;
    SurrogateConfig config;

    Surrogate() = default;
    // Expensive: builds convex hull, poles and piers.
    static Surrogate make(const Polygon& simple_poly, const SurrogateConfig& config);

    // poles[0..n_ff_poles] are the ones tested during fail-fast
    const Circle* ff_poles_begin() const { return poles.data(); }
    usize ff_poles_count() const { return config.n_ff_poles; }
    const std::vector<Edge>& ff_piers() const { return piers; }

    void transform(const AffineTransform& t) {
        for (auto& c : poles) c.transform(t);
        for (auto& p : piers) p.transform(t);
    }
    void transform_from(const Surrogate& ref, const AffineTransform& t) {
        assert(poles.size() == ref.poles.size());
        assert(piers.size() == ref.piers.size());
        for (usize i = 0; i < poles.size(); ++i) poles[i].transform_from(ref.poles[i], t);
        for (usize i = 0; i < piers.size(); ++i) piers[i].transform_from(ref.piers[i], t);
    }
};

} // namespace nest
