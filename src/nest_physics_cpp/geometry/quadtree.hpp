// Faithful C++ port of nest-rs collision_detection/quadtree/*.
// QTQueryable::collides_with_quadrants is rendered as a free function using the
// exact per-quadrant collision test (the Rust Edge specialisation is a perf-only
// optimisation that is debug-asserted to be consistent with this exact check).
#pragma once
#include "primitives.hpp"
#include "polygon.hpp"
#include "obstacle.hpp"
#include "slotmap.hpp"
#include <memory>
#include <array>
#include <optional>
#include <initializer_list>

namespace nest {

// Circle/Rect vs the four child quadrants — exact per-quadrant test.
template <class T>
inline std::array<bool, 4> collides_with_quadrants(const T& e, const Rect&, const std::array<Rect, 4>& qs) {
    return {collides(e, qs[0]), collides(e, qs[1]), collides(e, qs[2]), collides(e, qs[3])};
}

// Edge vs four quadrants — faithful port of qt_traits.rs Edge::collides_with_quadrants. Resolves
// most quadrants via cheap bbox/endpoint checks, then shares bisector intersection tests across
// adjacent quadrants. Conservative by construction (may over-report a quadrant, never under-report),
// so it cannot miss a obstacle; the final brute-force overlap check guards against any error.
enum class CollisionHalf { FirstHalf, Halfway, SecondHalf };

inline std::optional<CollisionHalf> edge_intersection_half(const Edge& e1, const Edge& e2) {
    f32 x1 = e1.start.x, y1 = e1.start.y, x2 = e1.end.x, y2 = e1.end.y;
    f32 x3 = e2.start.x, y3 = e2.start.y, x4 = e2.end.x, y4 = e2.end.y;
    f32 denom = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    if (denom == 0.0f) return std::nullopt; // parallel
    f32 u_nom = (x2 - x4) * (y2 - y1) - (y2 - y4) * (x2 - x1);
    f32 t_nom = (x2 - x4) * (y4 - y3) - (y2 - y4) * (x4 - x3);
    f32 t = t_nom / denom, u = u_nom / denom;
    if (t >= 0.0f && t <= 1.0f && u >= 0.0f && u <= 1.0f) {
        if (u > 0.5f) return CollisionHalf::FirstHalf;
        if (u < 0.5f) return CollisionHalf::SecondHalf;
        return CollisionHalf::Halfway;
    }
    return std::nullopt;
}

inline std::array<bool, 4> collides_with_quadrants(const Edge& e, const Rect& r, const std::array<Rect, 4>& qs) {
    f32 ex_min = min_f(e.start.x, e.end.x), ex_max = max_f(e.start.x, e.end.x);
    f32 ey_min = min_f(e.start.y, e.end.y), ey_max = max_f(e.start.y, e.end.y);
    std::optional<bool> c[4];
    for (int i = 0; i < 4; ++i) {
        const Rect& q = qs[i];
        bool x_no = max_f(ex_min, q.x_min) > min_f(ex_max, q.x_max);
        bool y_no = max_f(ey_min, q.y_min) > min_f(ey_max, q.y_max);
        if (x_no || y_no) c[i] = false;
        else if (collides(q, e.start) || collides(q, e.end)) c[i] = true;
        else c[i] = std::nullopt;
    }
    if (c[0] && c[1] && c[2] && c[3]) return {*c[0], *c[1], *c[2], *c[3]}; // all determined

    Point ctr = r.centroid();
    auto sd = r.sides(); // [top, left, bottom, right]
    Edge h_bisect(Point(r.x_min, ctr.y), Point(r.x_max, ctr.y));
    Edge v_bisect(Point(ctr.x, r.y_min), Point(ctr.x, r.y_max));

    auto hi = [&](const Edge& e2, std::initializer_list<int> fst, std::initializer_list<int> sec) {
        bool need = false;
        for (int i : fst) if (!c[i].has_value()) need = true;
        for (int i : sec) if (!c[i].has_value()) need = true;
        if (!need) return;
        auto loc = edge_intersection_half(e, e2);
        if (!loc) return;
        if (*loc == CollisionHalf::FirstHalf) { for (int i : fst) c[i] = true; }
        else if (*loc == CollisionHalf::SecondHalf) { for (int i : sec) c[i] = true; }
        else { for (int i : fst) c[i] = true; for (int i : sec) c[i] = true; }
    };
    hi(sd[1], {1}, {2});            // left:   TL->BL
    hi(sd[3], {3}, {0});            // right:  BR->TR
    hi(sd[0], {0}, {1});            // top:    TR->TL
    hi(sd[2], {2}, {3});            // bottom: BL->BR
    hi(h_bisect, {1, 2}, {0, 3});
    hi(v_bisect, {2, 3}, {0, 1});

    return {c[0].value_or(false), c[1].value_or(false), c[2].value_or(false), c[3].value_or(false)};
}

// ----- QuadtreePartial -----
struct QuadtreePartial {
    std::vector<Edge> edges;
    Rect ff_bbox;

    static QuadtreePartial from_entire_shape(const Polygon& shape) {
        QuadtreePartial p;
        for (usize i = 0; i < shape.n_vertices(); ++i) p.edges.push_back(shape.edge(i));
        p.ff_bbox = shape.bbox;
        return p;
    }
    static QuadtreePartial from_parent(const QuadtreePartial& parent, std::vector<Edge> restricted_edges) {
        Rect ff;
        if (parent.edges.size() == restricted_edges.size()) {
            ff = parent.ff_bbox;
        } else {
            f32 x_min = std::numeric_limits<f32>::infinity(), y_min = std::numeric_limits<f32>::infinity();
            f32 x_max = -std::numeric_limits<f32>::infinity(), y_max = -std::numeric_limits<f32>::infinity();
            for (const auto& e : restricted_edges) {
                x_min = min_f(x_min, min_f(e.start.x, e.end.x));
                y_min = min_f(y_min, min_f(e.start.y, e.end.y));
                x_max = max_f(x_max, max_f(e.start.x, e.end.x));
                y_max = max_f(y_max, max_f(e.start.y, e.end.y));
            }
            ff = (x_min < x_max && y_min < y_max) ? Rect(x_min, y_min, x_max, y_max) : parent.ff_bbox;
        }
        QuadtreePartial p;
        p.edges = std::move(restricted_edges);
        p.ff_bbox = ff;
        return p;
    }
    usize n_edges() const { return edges.size(); }

    template <class T>
    bool collides_with(const T& entity) const {
        if (!collides(entity, ff_bbox)) return false;
        for (const auto& e : edges) if (collides(entity, e)) return true;
        return false;
    }
};

enum class QuadtreePresence { None, Partial, Entire };

struct QuadtreeObstacle {
    Rect qt_bbox;
    ObstacleKey okey = NULL_KEY;
    ObstacleRef entity;
    QuadtreePresence presence = QuadtreePresence::None;
    std::optional<QuadtreePartial> partial; // valid iff presence == Partial

    usize n_edges() const { return presence == QuadtreePresence::Partial ? partial->n_edges() : 0; }

    static QuadtreeObstacle from_root(Rect qt_root_bbox, const Obstacle& obs, ObstacleKey okey) {
        QuadtreeObstacle h;
        h.qt_bbox = qt_root_bbox;
        h.okey = okey;
        h.entity = obs.entity;
        h.presence = QuadtreePresence::Partial;
        h.partial = QuadtreePartial::from_entire_shape(*obs.shape);
        return h;
    }

    std::array<QuadtreeObstacle, 4> constrict(const std::array<Rect, 4>& quadrants, const SlotMap<Obstacle>& haz_map) const {
        if (presence == QuadtreePresence::Entire) return {*this, *this, *this, *this};
        // Partial:
        const Polygon& haz_shape = *haz_map[okey].shape;

        int enclosed = -1;
        for (int i = 0; i < 4; ++i)
            if (haz_shape.bbox.relation_to(quadrants[i]) == GeoRelation::Enclosed) { enclosed = i; break; }

        if (enclosed >= 0) {
            std::array<QuadtreeObstacle, 4> out;
            for (int i = 0; i < 4; ++i) {
                QuadtreeObstacle h;
                h.qt_bbox = quadrants[i]; h.okey = okey; h.entity = entity;
                if (i == enclosed) { h.presence = presence; h.partial = partial; }
                else h.presence = QuadtreePresence::None;
                out[i] = h;
            }
            return out;
        }

        std::array<std::optional<QuadtreeObstacle>, 4> constricted;
        for (int i = 0; i < 4; ++i) {
            const Rect& q = quadrants[i];
            std::vector<Edge> colliding;
            for (const auto& edge : partial->edges) if (collides(q, edge)) colliding.push_back(edge);
            if (!colliding.empty()) {
                QuadtreeObstacle h;
                h.qt_bbox = q; h.okey = okey; h.entity = entity; h.presence = QuadtreePresence::Partial;
                h.partial = QuadtreePartial::from_parent(*partial, std::move(colliding));
                constricted[i] = h;
            }
        }
        for (int i = 0; i < 4; ++i) {
            if (constricted[i].has_value()) continue;
            const Rect& quadrant = quadrants[i];
            bool none_neighbor = false, entire_neighbor = false;
            for (int nb : {QUADRANT_NEIGHBOR_LAYOUT[i][0], QUADRANT_NEIGHBOR_LAYOUT[i][1]}) {
                if (constricted[nb].has_value()) {
                    if (constricted[nb]->presence == QuadtreePresence::None) none_neighbor = true;
                    if (constricted[nb]->presence == QuadtreePresence::Entire) entire_neighbor = true;
                }
            }
            QuadtreePresence pres;
            if (none_neighbor) pres = QuadtreePresence::None;
            else if (entire_neighbor) pres = QuadtreePresence::Entire;
            else {
                bool colliding = collides(haz_shape, quadrant.centroid());
                if (entity.scope() == GeoPosition::Interior && colliding) pres = QuadtreePresence::Entire;
                else if (entity.scope() == GeoPosition::Exterior && !colliding) pres = QuadtreePresence::Entire;
                else pres = QuadtreePresence::None;
            }
            QuadtreeObstacle h;
            h.qt_bbox = quadrant; h.okey = okey; h.entity = entity; h.presence = pres;
            constricted[i] = h;
        }
        std::array<QuadtreeObstacle, 4> out;
        for (int i = 0; i < 4; ++i) out[i] = *constricted[i];
        return out;
    }
};

// ----- QuadtreeObstacleVec: always sorted by descending presence strength -----
inline int presence_sortkey(const QuadtreeObstacle& h) {
    switch (h.presence) {
        case QuadtreePresence::None: return 0;
        case QuadtreePresence::Partial: return 1;
        case QuadtreePresence::Entire: return 2;
    }
    return 0;
}

struct QuadtreeObstacleVec {
    std::vector<QuadtreeObstacle> obstacles;
    usize n_active_edges_ = 0;

    void add(QuadtreeObstacle obs) {
        auto pos = std::lower_bound(obstacles.begin(), obstacles.end(), obs,
                                    [](const QuadtreeObstacle& a, const QuadtreeObstacle& b) {
                                        return presence_sortkey(a) > presence_sortkey(b);
                                    });
        n_active_edges_ += obs.n_edges();
        obstacles.insert(pos, std::move(obs));
    }
    std::optional<QuadtreeObstacle> remove(ObstacleKey okey) {
        for (usize i = 0; i < obstacles.size(); ++i) {
            if (obstacles[i].okey == okey) {
                QuadtreeObstacle h = std::move(obstacles[i]);
                n_active_edges_ -= h.n_edges();
                obstacles.erase(obstacles.begin() + i);
                return h;
            }
        }
        return std::nullopt;
    }
    template <class Filter>
    const QuadtreeObstacle* strongest(const Filter& filter) const {
        for (const auto& hz : obstacles) if (!filter.is_irrelevant(hz.okey)) return &hz;
        return nullptr;
    }
    bool is_empty() const { return obstacles.empty(); }
    usize len() const { return obstacles.size(); }
    bool no_partial_hazards() const {
        for (const auto& hz : obstacles) if (hz.presence == QuadtreePresence::Partial) return false;
        return true;
    }
    usize n_active_edges() const { return n_active_edges_; }
};

// ----- QuadtreeNode -----
struct QuadtreeNode {
    uint8_t level = 0;
    Rect bbox;
    std::unique_ptr<std::array<QuadtreeNode, 4>> children;
    QuadtreeObstacleVec obstacles;
    uint8_t cd_threshold = 0;

    QuadtreeNode() = default;
    QuadtreeNode(uint8_t level_, Rect bbox_, uint8_t cd_threshold_)
        : level(level_), bbox(bbox_), cd_threshold(cd_threshold_) {}

    QuadtreeNode(const QuadtreeNode& o) : level(o.level), bbox(o.bbox), obstacles(o.obstacles), cd_threshold(o.cd_threshold) {
        if (o.children) children = std::make_unique<std::array<QuadtreeNode, 4>>(*o.children);
    }
    QuadtreeNode& operator=(const QuadtreeNode& o) {
        if (this != &o) {
            level = o.level; bbox = o.bbox; obstacles = o.obstacles; cd_threshold = o.cd_threshold;
            children = o.children ? std::make_unique<std::array<QuadtreeNode, 4>>(*o.children) : nullptr;
        }
        return *this;
    }
    QuadtreeNode(QuadtreeNode&&) = default;
    QuadtreeNode& operator=(QuadtreeNode&&) = default;

    void register_obstacle(QuadtreeObstacle new_qt_haz, const SlotMap<Obstacle>& haz_map) {
        auto constrict_and_register = [&](const QuadtreeObstacle& qt_hazard, std::array<QuadtreeNode, 4>& kids) {
            std::array<Rect, 4> child_bboxes = {kids[0].bbox, kids[1].bbox, kids[2].bbox, kids[3].bbox};
            auto child_hazards = qt_hazard.constrict(child_bboxes, haz_map);
            for (int i = 0; i < 4; ++i)
                if (child_hazards[i].presence != QuadtreePresence::None)
                    kids[i].register_obstacle(child_hazards[i], haz_map);
        };

        if (!children && level > 0 && new_qt_haz.presence == QuadtreePresence::Partial) {
            auto quads = bbox.quadrants();
            children = std::make_unique<std::array<QuadtreeNode, 4>>(std::array<QuadtreeNode, 4>{
                QuadtreeNode(static_cast<uint8_t>(level - 1), quads[0], cd_threshold),
                QuadtreeNode(static_cast<uint8_t>(level - 1), quads[1], cd_threshold),
                QuadtreeNode(static_cast<uint8_t>(level - 1), quads[2], cd_threshold),
                QuadtreeNode(static_cast<uint8_t>(level - 1), quads[3], cd_threshold)});
            for (const auto& qt_hazard : obstacles.obstacles) constrict_and_register(qt_hazard, *children);
        }
        if (children) constrict_and_register(new_qt_haz, *children);
        obstacles.add(std::move(new_qt_haz));
    }

    void deregister_obstacle(ObstacleKey okey) {
        bool modified = obstacles.remove(okey).has_value();
        if (modified) {
            if (obstacles.no_partial_hazards()) {
                children = nullptr;
            } else if (children) {
                for (auto& c : *children) c.deregister_obstacle(okey);
            }
        }
    }

    template <class T, class Filter>
    const ObstacleRef* detect_collision(const T& entity, const Filter& filter) const {
        const QuadtreeObstacle* strongest = obstacles.strongest(filter);
        if (!strongest) return nullptr;
        switch (strongest->presence) {
            case QuadtreePresence::None: return nullptr;
            case QuadtreePresence::Entire: return &strongest->entity;
            case QuadtreePresence::Partial: {
                if (children) {
                    std::array<Rect, 4> quads = {(*children)[0].bbox, (*children)[1].bbox,
                                                 (*children)[2].bbox, (*children)[3].bbox};
                    auto col = collides_with_quadrants(entity, bbox, quads);
                    for (int i = 0; i < 4; ++i)
                        if (col[i]) {
                            const ObstacleRef* r = (*children)[i].detect_collision(entity, filter);
                            if (r) return r;
                        }
                    return nullptr;
                } else {
                    for (const auto& hz : obstacles.obstacles) {
                        if (filter.is_irrelevant(hz.okey)) continue;
                        if (hz.presence == QuadtreePresence::Partial && hz.partial->collides_with(entity))
                            return &hz.entity;
                    }
                    return nullptr;
                }
            }
        }
        return nullptr;
    }

    template <class T, class Collector>
    void collect_collisions(const T& entity, Collector& collector) const {
        bool perform_cd_now = obstacles.n_active_edges() <= static_cast<usize>(cd_threshold);
        if (children && !perform_cd_now) {
            std::array<Rect, 4> quads = {(*children)[0].bbox, (*children)[1].bbox,
                                         (*children)[2].bbox, (*children)[3].bbox};
            auto col = collides_with_quadrants(entity, bbox, quads);
            for (int i = 0; i < 4; ++i)
                if (col[i]) (*children)[i].collect_collisions(entity, collector);
        } else {
            for (const auto& hz : obstacles.obstacles) {
                if (collector.contains_key(hz.okey)) continue;
                if (hz.presence == QuadtreePresence::Entire) collector.insert(hz.okey, hz.entity);
                else if (hz.presence == QuadtreePresence::Partial && hz.partial->collides_with(entity))
                    collector.insert(hz.okey, hz.entity);
            }
        }
    }
};

} // namespace nest
