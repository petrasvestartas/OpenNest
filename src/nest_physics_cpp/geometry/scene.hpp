// Faithful C++ port of nest-rs entities/* (Part, Container, QualityZone,
// PlacedPart, Layout, Instance, LayoutSnapshot).
#pragma once
#include "shape.hpp"
#include "collision_engine.hpp"
#include "obstacle.hpp"
#include <memory>

namespace nest {

constexpr usize N_QUALITIES = 10;

// RotationRange (geo_enums.rs)
enum class RotationKind { None, Continuous, Discrete };
struct RotationRange {
    RotationKind kind = RotationKind::None;
    std::vector<f32> discrete; // valid iff kind == Discrete (radians)
    static RotationRange none() { return RotationRange{RotationKind::None, {}}; }
    static RotationRange continuous() { return RotationRange{RotationKind::Continuous, {}}; }
    static RotationRange discrete_of(std::vector<f32> angles) { return RotationRange{RotationKind::Discrete, std::move(angles)}; }
};

struct Part {
    usize id = 0;
    std::shared_ptr<SourceShape> source_shape;
    std::shared_ptr<Polygon> collision_shape;
    RotationRange allowed_rotation;
    std::optional<usize> min_quality;
    SurrogateConfig surrogate_config;

    static Part make(usize id, SourceShape original_shape, RotationRange allowed_rotation,
                     std::optional<usize> min_quality, SurrogateConfig surrogate_config) {
        auto source_shape = std::make_shared<SourceShape>(std::move(original_shape));
        Polygon shape_int = source_shape->convert_to_internal();
        shape_int.generate_surrogate(surrogate_config);
        Part it;
        it.id = id;
        it.source_shape = source_shape;
        it.collision_shape = std::make_shared<Polygon>(std::move(shape_int));
        it.allowed_rotation = std::move(allowed_rotation);
        it.min_quality = min_quality;
        it.surrogate_config = surrogate_config;
        return it;
    }
    f32 area() const { return source_shape->area(); }
};

struct QualityZone {
    usize quality = 0;
    std::vector<std::shared_ptr<SourceShape>> source_shapes;
    std::vector<std::shared_ptr<Polygon>> collision_shapes;

    static QualityZone make(usize quality, std::vector<SourceShape> original_shapes) {
        QualityZone z;
        z.quality = quality;
        for (auto& orig : original_shapes) {
            z.collision_shapes.push_back(std::make_shared<Polygon>(orig.convert_to_internal()));
            z.source_shapes.push_back(std::make_shared<SourceShape>(std::move(orig)));
        }
        return z;
    }
    std::vector<Obstacle> to_obstacles() const {
        std::vector<Obstacle> out;
        for (usize idx = 0; idx < collision_shapes.size(); ++idx) {
            ObstacleRef e = (quality == 0) ? ObstacleRef::hole(idx)
                                            : ObstacleRef::inferior_quality_zone(quality, idx);
            out.emplace_back(e, collision_shapes[idx], false);
        }
        return out;
    }
    f32 area() const {
        f32 s = 0.0f;
        for (auto& sh : source_shapes) s += sh->area();
        return s;
    }
};

struct Container {
    usize id = 0;
    std::shared_ptr<SourceShape> source_outer;
    std::shared_ptr<Polygon> collision_outer;
    std::array<std::optional<QualityZone>, N_QUALITIES> quality_zones;
    std::shared_ptr<CollisionEngine> base_engine;

    static Container make(usize id, SourceShape original_outer,
                          std::vector<QualityZone> qzones, CollisionConfig collision_config) {
        Container c;
        c.id = id;
        auto outer = std::make_shared<Polygon>(original_outer.convert_to_internal());
        c.collision_outer = outer;
        c.source_outer = std::make_shared<SourceShape>(std::move(original_outer));
        for (auto& q : qzones) { usize ql = q.quality; c.quality_zones[ql] = std::move(q); }

        std::vector<Obstacle> obstacles;
        obstacles.emplace_back(ObstacleRef::exterior(), outer, false);
        for (auto& qz : c.quality_zones)
            if (qz) for (auto& h : qz->to_obstacles()) obstacles.push_back(h);
        c.base_engine = std::make_shared<CollisionEngine>(outer->bbox.inflate_to_square(), std::move(obstacles), collision_config);
        return c;
    }
    f32 area() const {
        f32 a = source_outer->area();
        if (quality_zones[0]) a -= quality_zones[0]->area();
        return a;
    }
};

struct PlacedPart {
    usize part_id = 0;
    RigidTransform d_transf;
    std::shared_ptr<Polygon> shape;

    PlacedPart() = default;
    static PlacedPart make(const Part& part, RigidTransform d_transf) {
        PlacedPart pi;
        pi.part_id = part.id;
        pi.d_transf = d_transf;
        pi.shape = std::make_shared<Polygon>(part.collision_shape->transform_clone(d_transf.compose()));
        return pi;
    }
};

// abstract Instance
struct Instance {
    virtual ~Instance() = default;
    virtual const Part& part(usize id) const = 0;
    virtual usize n_parts() const = 0;
};

struct LayoutSnapshot;

struct Layout {
    Container container;
    SlotMap<PlacedPart> placed_parts;
    CollisionEngine engine;

    Layout() = default;
    explicit Layout(Container c) : container(std::move(c)), engine(*container.base_engine) {}

    static Layout from_snapshot(const LayoutSnapshot& ls);

    void swap_container(Container c) {
        CollisionSnapshot snap = engine.save();
        container = std::move(c);
        engine = *container.base_engine;
        for (auto& obstacle : snap.dynamic_hazards) engine.register_obstacle(obstacle);
    }

    // Incremental variant for strip-width changes: when the new container fits inside the current
    // quadtree root and neither container carries quality zones (holes), only the exterior obstacle
    // needs replacing — the tree, its partition, and all N registered parts stay untouched (the
    // caller has already moved any shifted parts via remove/place). Falls back to the full
    // swap_container otherwise (width GROW past the root, e.g. the InitialPlacer 1.2x expansion,
    // or hole-bearing sheets). Cross-width rollbacks go through Layout::from_snapshot, which
    // re-roots the engine exactly, so the kept-root superset never outlives a rollback.
    void swap_container_incremental(Container c) {
        bool holes_old = false, holes_new = false;
        for (auto& qz : container.quality_zones) if (qz) holes_old = true;
        for (auto& qz : c.quality_zones) if (qz) holes_new = true;
        if (!holes_old && !holes_new &&
            engine.bbox().relation_to(c.collision_outer->bbox) == GeoRelation::Surrounding) {
            engine.swap_exterior(c.collision_outer);
            container = std::move(c);
        } else {
            swap_container(std::move(c));
        }
    }

    LayoutSnapshot save() const;
    void restore(const LayoutSnapshot& ls);

    PartKey place_item(const Part& part, RigidTransform d_transformation) {
        PartKey pk = placed_parts.insert(PlacedPart::make(part, d_transformation));
        const PlacedPart& pi = placed_parts[pk];
        Obstacle obstacle(ObstacleRef::placed_item(pi.part_id, pi.d_transf, pk), pi.shape, true);
        engine.register_obstacle(std::move(obstacle));
        return pk;
    }
    PlacedPart remove_item(PartKey pk) {
        PlacedPart pi = std::move(*placed_parts.remove(pk));
        engine.deregister_obstacle_by_entity(ObstacleRef::placed_item(pi.part_id, pi.d_transf, pk));
        return pi;
    }
    bool is_empty() const { return placed_parts.size() == 0; }

    f32 placed_item_area(const Instance& instance) const {
        f32 s = 0.0f;
        placed_parts.for_each([&](PartKey, const PlacedPart& pi) { s += instance.part(pi.part_id).area(); });
        return s;
    }
    f32 density(const Instance& instance) const { return placed_item_area(instance) / container.area(); }

    const CollisionEngine& engine_ref() const { return engine; }

    bool is_feasible() const {
        bool ok = true;
        placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
            ObstacleKey okey = engine.obstacle_key_from_part_key(pk);
            SelfKeyFilter filter(okey);
            if (engine.detect_poly_collision(*pi.shape, filter)) ok = false;
        });
        return ok;
    }
};

struct LayoutSnapshot {
    Container container;
    SlotMap<PlacedPart> placed_parts;
    CollisionSnapshot collision_snapshot;

    f32 placed_item_area(const Instance& instance) const {
        f32 s = 0.0f;
        placed_parts.for_each([&](PartKey, const PlacedPart& pi) { s += instance.part(pi.part_id).area(); });
        return s;
    }
    f32 density(const Instance& instance) const { return placed_item_area(instance) / container.area(); }
};

inline LayoutSnapshot Layout::save() const {
    LayoutSnapshot ls;
    ls.container = container;
    ls.placed_parts = placed_parts;
    ls.collision_snapshot = engine.save();
    return ls;
}
inline void Layout::restore(const LayoutSnapshot& ls) {
    assert(container.id == ls.container.id);
    placed_parts = ls.placed_parts;
    engine.restore(ls.collision_snapshot);
}
inline Layout Layout::from_snapshot(const LayoutSnapshot& ls) {
    Layout layout(ls.container);
    layout.restore(ls);
    return layout;
}

} // namespace nest
