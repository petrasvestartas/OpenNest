// Faithful C++ port of solver/src/config.rs (configuration tree + defaults).
#pragma once
#include "constants.hpp"
#include "sampler.hpp"
#include "../geometry/collision_engine.hpp"

namespace nest {
using namespace nest;

struct RelaxConfig {
    usize iter_no_imprv_limit;
    usize strike_limit;
    usize n_workers;
    int log_level; // unused (logging dropped); kept for fidelity
    SampleConfig sample_config;
};

struct ExplorationConfig {
    f32 shrink_step;
    double time_limit_secs;
    std::optional<usize> max_conseq_failed_attempts;
    f32 solution_pool_distribution_stddev;
    RelaxConfig separator_config;
    f32 large_item_ch_area_cutoff_percentile;
};

enum class ShrinkDecayKind { TimeBased, FailureBased };
struct ShrinkDecayStrategy {
    ShrinkDecayKind kind;
    f32 failure_ratio; // valid iff FailureBased
    static ShrinkDecayStrategy time_based() { return {ShrinkDecayKind::TimeBased, 0.0f}; }
    static ShrinkDecayStrategy failure_based(f32 r) { return {ShrinkDecayKind::FailureBased, r}; }
};

struct CompressionConfig {
    std::pair<f32, f32> shrink_range;
    double time_limit_secs;
    ShrinkDecayStrategy shrink_decay;
    RelaxConfig separator_config;
};

struct SolverConfig {
    std::optional<usize> rng_seed;
    ExplorationConfig expl_cfg;
    CompressionConfig cmpr_cfg;
    CollisionConfig collision_config;
    std::optional<f32> poly_simpl_tolerance;
    std::optional<f32> min_part_separation;
    std::optional<std::pair<f32, f32>> narrow_concavity_cutoff_ratio;
};

inline SolverConfig default_solver_config() {
    SolverConfig c;
    c.rng_seed = std::nullopt;
    c.expl_cfg = ExplorationConfig{
        0.001f, 9.0 * 60.0, std::nullopt, 0.25f,
        RelaxConfig{200, 3, 3, 0, SampleConfig{50, 25, 3}},
        0.75f};
    c.cmpr_cfg = CompressionConfig{
        {0.0005f, 0.00001f}, 60.0, ShrinkDecayStrategy::time_based(),
        RelaxConfig{100, 5, 3, 0, SampleConfig{50, 25, 3}}};
    c.collision_config = CollisionConfig{};
    c.collision_config.quadtree_depth = 4;
    c.collision_config.cd_threshold = 16;
    c.collision_config.part_surrogate_config = SurrogateConfig{{{{64, 0.0f}, {16, 0.8f}, {8, 0.9f}}}, 1, 0};
    c.poly_simpl_tolerance = 0.001f;
    c.narrow_concavity_cutoff_ratio = std::make_pair(0.01f, 0.01f);
    c.min_part_separation = std::nullopt;
    return c;
}

} // namespace nest
