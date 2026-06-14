// Faithful C++ port of nest-rs collision_detection/cd_engine.rs (the CollisionEngine).
// Query methods are templated on the Filter/Collector type (matching Rust generics).
#pragma once
#include "quadtree.hpp"
#include "obstacle.hpp"
#include "surrogate.hpp"
#include <vector>

namespace nest {

struct CollisionConfig {
    uint8_t quadtree_depth = 0;
    uint8_t cd_threshold = 0;
    SurrogateConfig part_surrogate_config;
};

struct CollisionSnapshot {
    std::vector<Obstacle> dynamic_hazards;
};

struct CollisionEngine {
    QuadtreeNode quadtree;
    SlotMap<Obstacle> obstacles_map;
    CollisionConfig config;
    ObstacleKey okey_exterior = NULL_KEY;

    CollisionEngine() = default;

    CollisionEngine(Rect bbox, std::vector<Obstacle> static_obstacles, CollisionConfig cfg)
        : quadtree(cfg.quadtree_depth, bbox, cfg.cd_threshold), config(cfg) {
        for (auto& obs : static_obstacles) {
            ObstacleKey okey = obstacles_map.insert(std::move(obs));
            QuadtreeObstacle qt_obs = QuadtreeObstacle::from_root(quadtree.bbox, obstacles_map[okey], okey);
            quadtree.register_obstacle(std::move(qt_obs), obstacles_map);
        }
        okey_exterior = NULL_KEY;
        obstacles_map.for_each([&](ObstacleKey okey, const Obstacle& h) {
            if (h.entity.kind == ObstacleKind::Exterior) okey_exterior = okey;
        });
        assert(!okey_exterior.is_null() && "No exterior obstacle registered in the CDE");
    }

    Rect bbox() const { return quadtree.bbox; }

    void register_obstacle(Obstacle obstacle) {
        ObstacleKey okey = obstacles_map.insert(std::move(obstacle));
        QuadtreeObstacle qt_hazard = QuadtreeObstacle::from_root(bbox(), obstacles_map[okey], okey);
        quadtree.register_obstacle(std::move(qt_hazard), obstacles_map);
    }

    Obstacle deregister_obstacle_by_entity(const ObstacleRef& hazard_entity) {
        ObstacleKey okey = NULL_KEY;
        obstacles_map.for_each([&](ObstacleKey k, const Obstacle& h) { if (h.entity == hazard_entity) okey = k; });
        assert(!okey.is_null() && "Cannot deregister obstacle that is not registered");
        quadtree.deregister_obstacle(okey);
        return std::move(*obstacles_map.remove(okey));
    }

    Obstacle deregister_hazard_by_key(ObstacleKey okey) {
        auto h = obstacles_map.remove(okey);
        assert(h.has_value() && "Cannot deregister obstacle that is not registered");
        quadtree.deregister_obstacle(okey);
        return std::move(*h);
    }

    // Replace the exterior obstacle (container outline) IN PLACE, keeping the existing quadtree
    // root/partition and every registered part. Used by the incremental container swap on strip
    // width changes: only the container's right wall moves, so rebuilding the whole engine (and
    // re-registering all N parts) to re-root the tree is wasted work. The kept root bbox is a
    // strict superset of the new container (callers guarantee Surrounding), which is semantically
    // safe: regions outside the new outline become Entire-presence exterior nodes during this
    // re-registration, exactly as a fresh tree would mark them — only the quadrant geometry
    // (inherited from the old root) differs.
    void swap_exterior(std::shared_ptr<Polygon> new_outer) {
        deregister_hazard_by_key(okey_exterior);
        ObstacleKey okey = obstacles_map.insert(Obstacle(ObstacleRef::exterior(), std::move(new_outer), false));
        QuadtreeObstacle qt_obs = QuadtreeObstacle::from_root(quadtree.bbox, obstacles_map[okey], okey);
        quadtree.register_obstacle(std::move(qt_obs), obstacles_map);
        okey_exterior = okey;
    }

    CollisionSnapshot save() const {
        CollisionSnapshot snap;
        obstacles_map.for_each([&](ObstacleKey, const Obstacle& h) { if (h.dynamic) snap.dynamic_hazards.push_back(h); });
        return snap;
    }

    void restore(const CollisionSnapshot& snapshot) {
        // diff of dynamic obstacles
        std::vector<std::pair<ObstacleKey, ObstacleRef>> hazards_to_remove;
        obstacles_map.for_each([&](ObstacleKey k, const Obstacle& h) {
            if (h.dynamic) hazards_to_remove.emplace_back(k, h.entity);
        });
        std::vector<Obstacle> hazards_to_add;
        for (const auto& obstacle : snapshot.dynamic_hazards) {
            int idx = -1;
            for (usize i = 0; i < hazards_to_remove.size(); ++i)
                if (hazards_to_remove[i].second == obstacle.entity) { idx = (int)i; break; }
            if (idx >= 0) {
                hazards_to_remove[idx] = hazards_to_remove.back();
                hazards_to_remove.pop_back();
            } else {
                hazards_to_add.push_back(obstacle);
            }
        }
        for (auto& [okey, _] : hazards_to_remove) deregister_hazard_by_key(okey);
        for (auto& obstacle : hazards_to_add) register_obstacle(obstacle);
    }

    // ----- queries -----
    template <class Filter>
    bool detect_poly_collision(const Polygon& shape, const Filter& filter) const {
        if (bbox().relation_to(shape.bbox) != GeoRelation::Surrounding) return true;
        const QuadtreeNode* v_qt_root = get_virtual_root(shape.bbox);
        for (usize i = 0; i < shape.n_vertices(); ++i) {
            Edge e = shape.edge(i);
            if (v_qt_root->detect_collision(e, filter) != nullptr) return true;
        }
        for (const auto& qt_hazard : v_qt_root->obstacles.obstacles) {
            if (qt_hazard.presence == QuadtreePresence::Partial) {
                if (!filter.is_irrelevant(qt_hazard.okey)) {
                    const Polygon& haz_shape = *obstacles_map[qt_hazard.okey].shape;
                    if (detect_containment_collision(shape, haz_shape, qt_hazard.entity)) return true;
                }
            }
        }
        return false;
    }

    template <class Filter>
    bool detect_surrogate_collision(const Surrogate& base_surrogate, const AffineTransform& transform,
                                    const Filter& filter) const {
        usize np = std::min(base_surrogate.config.n_ff_poles, base_surrogate.poles.size());
        for (usize i = 0; i < np; ++i) {
            Circle t_pole = base_surrogate.poles[i].transform_clone(transform);
            if (quadtree.detect_collision(t_pole, filter) != nullptr) return true;
        }
        for (const auto& pier : base_surrogate.piers) {
            Edge t_pier = pier.transform_clone(transform);
            if (quadtree.detect_collision(t_pier, filter) != nullptr) return true;
        }
        return false;
    }

    bool detect_containment_collision(const Polygon& shape, const Polygon& haz_shape,
                                      const ObstacleRef& haz_entity) const {
        GeoRelation rel = haz_shape.bbox.almost_relation_to(shape.bbox);
        bool contained;
        switch (rel) {
            case GeoRelation::Surrounding: contained = collides(haz_shape, shape.poi.center); break;
            case GeoRelation::Enclosed: contained = collides(shape, haz_shape.poi.center); break;
            default: contained = false; break;
        }
        GeoPosition scope = haz_entity.scope();
        if (scope == GeoPosition::Interior && contained) return true;
        if (scope == GeoPosition::Exterior && !contained) return true;
        return false;
    }

    template <class Collector>
    void collect_poly_collisions(const Polygon& shape, Collector& collector) const {
        if (bbox().relation_to(shape.bbox) != GeoRelation::Surrounding)
            collector.insert(okey_exterior, ObstacleRef::exterior());
        const QuadtreeNode* v_quadtree = get_virtual_root(shape.bbox);
        for (usize i = 0; i < shape.n_vertices(); ++i) {
            Edge e = shape.edge(i);
            v_quadtree->collect_collisions(e, collector);
        }
        for (const auto& qt_obs : v_quadtree->obstacles.obstacles) {
            if (qt_obs.presence == QuadtreePresence::Partial) {
                if (!collector.contains_key(qt_obs.okey)) {
                    const Polygon& h_shape = *obstacles_map[qt_obs.okey].shape;
                    if (detect_containment_collision(shape, h_shape, qt_obs.entity))
                        collector.insert(qt_obs.okey, qt_obs.entity);
                }
            }
        }
    }

    template <class Collector>
    void collect_surrogate_collisions(const Surrogate& base_surrogate, const AffineTransform& transform,
                                      Collector& collector) const {
        usize np = std::min(base_surrogate.config.n_ff_poles, base_surrogate.poles.size());
        for (usize i = 0; i < np; ++i) {
            Circle t_pole = base_surrogate.poles[i].transform_clone(transform);
            quadtree.collect_collisions(t_pole, collector);
        }
        for (const auto& pier : base_surrogate.piers) {
            Edge t_pier = pier.transform_clone(transform);
            quadtree.collect_collisions(t_pier, collector);
        }
    }

    const QuadtreeNode* get_virtual_root(Rect b) const {
        const QuadtreeNode* v_root = &quadtree;
        while (v_root->children) {
            const QuadtreeNode* surrounding = nullptr;
            for (const auto& child : *v_root->children)
                if (child.bbox.relation_to(b) == GeoRelation::Surrounding) { surrounding = &child; break; }
            if (surrounding) v_root = surrounding;
            else break;
        }
        return v_root;
    }

    ObstacleKey obstacle_key_from_part_key(PartKey pik) const {
        ObstacleKey out = NULL_KEY;
        obstacles_map.for_each([&](ObstacleKey key, const Obstacle& obstacle) {
            if (obstacle.entity.kind == ObstacleKind::PlacedPart && obstacle.entity.pk == pik) out = key;
        });
        return out;
    }
};

} // namespace nest
