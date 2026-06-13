// Faithful C++ port of nest-rs probs/spp/entities/* (Strip, StripInstance, StripProblem,
// StripSolution, Placement). Strip Packing Problem: fixed-height strip, variable width.
#pragma once
#include "scene.hpp"
#include <chrono>

namespace nest {

inline double now_seconds() {
    using namespace std::chrono;
    return duration<double>(steady_clock::now().time_since_epoch()).count();
}

struct Strip {
    f32 fixed_height = 0.0f;
    CollisionConfig collision_config;
    ShapeModifyConfig shape_modify_config;
    f32 width = 0.0f;
    // Forbidden interior regions (sheet holes), in container coords [0,width]x[0,fixed_height].
    // Empty by default => the container is a plain rectangle (original behaviour, bit-identical).
    std::vector<Polygon> holes;

    Strip() = default;
    Strip(f32 h, CollisionConfig engine, ShapeModifyConfig smc, f32 w)
        : fixed_height(h), collision_config(engine), shape_modify_config(smc), width(w) {}

    void set_width(f32 w) { assert(w > 0.0f); width = w; }
    void set_holes(std::vector<Polygon> h) { holes = std::move(h); }

    Container to_container() const {
        usize id;
        uint32_t bits;
        std::memcpy(&bits, &width, sizeof(bits));
        id = static_cast<usize>(bits);
        SourceShape os;
        os.shape = Polygon::from_rect(Rect::try_new(0.0f, 0.0f, width, fixed_height));
        os.pre_transform = RigidTransform::empty();
        os.modify_mode = ShapeModifyMode::Deflate;
        os.modify_config = shape_modify_config;
        if (holes.empty()) return Container::make(id, std::move(os), {}, collision_config);
        // Sheet holes => a quality-0 QualityZone (its obstacles are ObstacleKind::Hole, interior
        // scope, so placements inside are rejected). Inflate so `offset` spacing is kept from holes
        // too. Generate surrogates because the overlap proxy (pairwise_overlap_loss) reads
        // surrogate_ref() when the optimizer pushes parts out of holes during relaxation.
        std::vector<SourceShape> hole_shapes;
        hole_shapes.reserve(holes.size());
        for (const auto& h : holes) {
            SourceShape hs;
            hs.shape = h;
            hs.pre_transform = RigidTransform::empty();
            hs.modify_mode = ShapeModifyMode::Inflate;
            hs.modify_config = shape_modify_config;
            hole_shapes.push_back(std::move(hs));
        }
        QualityZone qz = QualityZone::make(0, std::move(hole_shapes));
        for (auto& cs : qz.collision_shapes) cs->generate_surrogate(collision_config.part_surrogate_config);
        std::vector<QualityZone> qzs;
        qzs.push_back(std::move(qz));
        return Container::make(id, std::move(os), std::move(qzs), collision_config);
    }

    bool operator==(const Strip& o) const {
        if (!(fixed_height == o.fixed_height && width == o.width &&
              collision_config.quadtree_depth == o.collision_config.quadtree_depth &&
              collision_config.cd_threshold == o.collision_config.cd_threshold &&
              collision_config.part_surrogate_config == o.collision_config.part_surrogate_config &&
              shape_modify_config == o.shape_modify_config))
            return false;
        if (holes.size() != o.holes.size()) return false;
        for (usize i = 0; i < holes.size(); ++i) {
            if (holes[i].vertices.size() != o.holes[i].vertices.size()) return false;
            for (usize j = 0; j < holes[i].vertices.size(); ++j)
                if (!(holes[i].vertices[j] == o.holes[i].vertices[j])) return false;
        }
        return true;
    }
};

struct StripInstance : Instance {
    std::vector<std::pair<Part, usize>> parts; // (part, demand)
    Strip base_strip;

    StripInstance() = default;
    StripInstance(std::vector<std::pair<Part, usize>> items_, Strip base_strip_)
        : parts(std::move(items_)), base_strip(base_strip_) {}

    f32 part_area() const {
        f32 s = 0.0f;
        for (auto& [part, qty] : parts) s += part.source_shape->area() * static_cast<f32>(qty);
        return s;
    }
    usize part_qty(usize id) const { return parts[id].second; }
    usize total_part_qty() const {
        usize s = 0;
        for (auto& [_, qty] : parts) s += qty;
        return s;
    }
    const Part& part(usize id) const override { return parts[id].first; }
    usize n_parts() const override { return parts.size(); }
};

struct Placement {
    usize part_id = 0;
    RigidTransform d_transf;
};

struct StripSolution {
    Strip strip;
    LayoutSnapshot layout_snapshot;
    double time_stamp = 0.0;

    f32 density(const StripInstance& instance) const { return layout_snapshot.density(instance); }
    f32 strip_width() const { return strip.width; }
};

struct StripProblem {
    StripInstance instance;
    Strip strip;
    Layout layout;
    std::vector<usize> part_demand_qtys;

    explicit StripProblem(StripInstance inst)
        : instance(std::move(inst)) {
        for (auto& [_, qty] : instance.parts) part_demand_qtys.push_back(qty);
        strip = instance.base_strip;
        layout = Layout(strip.to_container());
    }

    void change_strip_width(f32 new_width) {
        strip.set_width(new_width);
        layout.swap_container_incremental(strip.to_container());
    }

    void fit_strip() {
        f32 item_x_max = -std::numeric_limits<f32>::infinity();
        layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            item_x_max = max_f(item_x_max, pi.shape->bbox.x_max);
        });
        item_x_max *= 1.00001f;
        f32 fitted = item_x_max + (strip.shape_modify_config.offset.value_or(0.0f));
        change_strip_width(fitted);
    }

    PartKey place_item(const Placement& placement) {
        part_demand_qtys[placement.part_id] -= 1;
        const Part& part = instance.part(placement.part_id);
        return layout.place_item(part, placement.d_transf);
    }

    Placement remove_item(PartKey pkey) {
        PlacedPart pi = layout.remove_item(pkey);
        part_demand_qtys[pi.part_id] += 1;
        return Placement{pi.part_id, pi.d_transf};
    }

    StripSolution save() const {
        StripSolution sol;
        sol.layout_snapshot = layout.save();
        sol.strip = strip;
        sol.time_stamp = now_seconds();
        return sol;
    }

    void restore(const StripSolution& solution) {
        if (strip == solution.strip) {
            layout.restore(solution.layout_snapshot);
        } else {
            layout = Layout::from_snapshot(solution.layout_snapshot);
            strip = solution.strip;
        }
        for (usize id = 0; id < part_demand_qtys.size(); ++id) part_demand_qtys[id] = instance.part_qty(id);
        layout.placed_parts.for_each([&](PartKey, const PlacedPart& pi) { part_demand_qtys[pi.part_id] -= 1; });
    }

    f32 density() const { return layout.density(instance); }
    f32 strip_width() const { return strip.width; }
    usize n_placed_items() const { return layout.placed_parts.size(); }
};

} // namespace nest
