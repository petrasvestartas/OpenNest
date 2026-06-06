// Faithful C++ port of nest-rs geometry/primitives/simple_polygon.rs plus the
// cross-cutting fail_fast generators (pole.rs, piers.rs) and Surrogate::new,
// which are defined here because they need the complete Polygon type.
#pragma once
#include "primitives.hpp"
#include "convex_hull.hpp"
#include "surrogate.hpp"
#include <deque>
#include <stdexcept>

namespace nest {

struct Polygon {
    std::vector<Point> vertices;
    Rect bbox;
    f32 area = 0.0f;
    f32 diameter = 0.0f;
    Circle poi;                          // pole of inaccessibility
    std::optional<Surrogate> surrogate;

    Polygon() = default;

    // ---- static geometry helpers ----
    static f32 calculate_area(const std::vector<Point>& points) {
        f32 sigma = 0.0f;
        usize n = points.size();
        for (usize i = 0; i < n; ++i) {
            usize j = (i + 1) % n;
            f32 xi = points[i].x, yi = points[i].y, xj = points[j].x, yj = points[j].y;
            sigma += (yi + yj) * (xi - xj);
        }
        return 0.5f * sigma;
    }

    static Rect generate_bounding_box(const std::vector<Point>& points) {
        f32 x_min = F32_MAX, y_min = F32_MAX, x_max = F32_MIN, y_max = F32_MIN;
        for (const auto& p : points) {
            x_min = min_f(x_min, p.x); y_min = min_f(y_min, p.y);
            x_max = max_f(x_max, p.x); y_max = max_f(y_max, p.y);
        }
        return Rect::try_new(x_min, y_min, x_max, y_max);
    }

    static f32 calculate_diameter(std::vector<Point> points) {
        std::vector<Point> ch = convex_hull_from_points(std::move(points));
        f32 sq_diam = 0.0f;
        for (usize i = 0; i < ch.size(); ++i)
            for (usize j = i + 1; j < ch.size(); ++j)
                sq_diam = max_f(sq_diam, ch[i].sq_distance_to(ch[j]));
        return std::sqrt(sq_diam);
    }

    static Circle calculate_poi(const std::vector<Point>& points, f32 diameter); // after compute_pole

    // build a simple polygon (CCW, validated). Throws on invalid input (mirrors Rust bail/unwrap).
    static Polygon create(std::vector<Point> points) {
        if (points.size() < 3) throw std::runtime_error("Simple polygon must have at least 3 points");
        // duplicate check
        for (usize i = 0; i < points.size(); ++i)
            for (usize j = i + 1; j < points.size(); ++j)
                if (points[i] == points[j]) throw std::runtime_error("Simple polygon has duplicate points");
        if (find_self_intersection(points)) throw std::runtime_error("Simple polygon self-intersects");

        f32 area = calculate_area(points);
        if (area == 0.0f) throw std::runtime_error("Simple polygon has no area");
        if (area < 0.0f) { std::reverse(points.begin(), points.end()); area = -area; }

        Polygon sp;
        sp.diameter = calculate_diameter(points);
        sp.bbox = generate_bounding_box(points);
        sp.poi = calculate_poi(points, sp.diameter);
        sp.area = area;
        sp.vertices = std::move(points);
        sp.surrogate = std::nullopt;
        return sp;
    }

    static Polygon from_rect(const Rect& r) {
        return create({Point(r.x_min, r.y_min), Point(r.x_max, r.y_min),
                       Point(r.x_max, r.y_max), Point(r.x_min, r.y_max)});
    }

    void generate_surrogate(const SurrogateConfig& config); // after Surrogate::make

    usize n_vertices() const { return vertices.size(); }
    Point vertex(usize i) const { return vertices[i]; }
    Edge edge(usize i) const {
        usize j = (i == n_vertices() - 1) ? 0 : i + 1;
        return Edge(vertices[i], vertices[j]);
    }
    const Surrogate& surrogate_ref() const {
        assert(surrogate.has_value() && "surrogate not generated");
        return *surrogate;
    }

    Point centroid() const {
        f32 cx = 0.0f, cy = 0.0f;
        usize n = n_vertices();
        for (usize i = 0; i < n; ++i) {
            usize j = (i == n - 1) ? 0 : i + 1;
            f32 xi = vertices[i].x, yi = vertices[i].y, xj = vertices[j].x, yj = vertices[j].y;
            f32 cross = xi * yj - xj * yi;
            cx += (xi + xj) * cross;
            cy += (yi + yj) * cross;
        }
        cx /= 6.0f * area;
        cy /= 6.0f * area;
        return Point(cx, cy);
    }

    static bool find_self_intersection(const std::vector<Point>& points, usize* e1 = nullptr, usize* e2 = nullptr) {
        usize n = points.size();
        auto edge_at = [&](usize i) { return Edge(points[i], points[(i + 1) % n]); };
        auto neighboring = [&](usize i, usize j) { return i + 1 == j || (i == 0 && j == n - 1); };
        for (usize i = 0; i < n; ++i)
            for (usize j = i + 1; j < n; ++j)
                if (!neighboring(i, j) && collides(edge_at(i), edge_at(j))) {
                    if (e1) *e1 = i;
                    if (e2) *e2 = j;
                    return true;
                }
        return false;
    }

    void transform(const AffineTransform& t) {
        for (auto& p : vertices) p.transform(t);
        poi.transform(t);
        if (surrogate) surrogate->transform(t);
        bbox = generate_bounding_box(vertices);
    }
    void transform_from(const Polygon& ref, const AffineTransform& t) {
        for (usize i = 0; i < vertices.size(); ++i) vertices[i].transform_from(ref.vertices[i], t);
        poi.transform_from(ref.poi, t);
        if (surrogate) surrogate->transform_from(ref.surrogate_ref(), t);
        bbox = generate_bounding_box(vertices);
    }
    Polygon transform_clone(const AffineTransform& t) const { Polygon s = *this; s.transform(t); return s; }
};

// ---- Polygon point collision / distance (ray casting) ----
inline bool collides(const Polygon& poly, const Point& point) {
    if (!collides(poly.bbox, point)) return false;
    Point point_outside(poly.bbox.x_max + poly.bbox.width(), point.y);
    Edge ray(point, point_outside);
    int n_intersections = 0;
    usize n = poly.n_vertices();
    for (usize i = 0; i < n; ++i) {
        Edge e = poly.edge(i);
        FPA s_x(e.start.x), s_y(e.start.y), e_x(e.end.x), e_y(e.end.y);
        FPA p_x(point.x), p_y(point.y);
        if ((s_y == p_y && s_x > p_x) || (e_y == p_y && e_x > p_x)) {
            if (s_y < p_y || e_y < p_y) n_intersections += 1; // FPA '<' (matches Rust)
        } else if (collides(ray, e)) {
            n_intersections += 1;
        }
    }
    return (n_intersections % 2) == 1;
}

inline f32 sq_distance_to(const Polygon& poly, const Point& point) {
    if (collides(poly, point)) return 0.0f;
    f32 best = F32_MAX;
    for (usize i = 0; i < poly.n_vertices(); ++i) best = min_f(best, sq_distance_to(poly.edge(i), point));
    return best;
}
inline f32 distance_to(const Polygon& poly, const Point& point) { return std::sqrt(sq_distance_to(poly, point)); }

inline std::pair<GeoPosition, f32> sq_separation_distance(const Polygon& poly, const Point& point) {
    f32 d = F32_MAX;
    for (usize i = 0; i < poly.n_vertices(); ++i) d = min_f(d, sq_distance_to(poly.edge(i), point));
    return {collides(poly, point) ? GeoPosition::Interior : GeoPosition::Exterior, d};
}
inline std::pair<GeoPosition, f32> separation_distance(const Polygon& poly, const Point& point) {
    auto [pos, d] = sq_separation_distance(poly, point);
    return {pos, std::sqrt(d)};
}

// ---- convex hull indices on a polygon ----
inline std::vector<usize> convex_hull_indices(const Polygon& shape) {
    std::vector<Point> ch = convex_hull_from_points(shape.vertices);
    std::vector<usize> indices;
    indices.reserve(ch.size());
    for (const auto& p : ch) {
        for (usize i = 0; i < shape.vertices.size(); ++i)
            if (shape.vertices[i] == p) { indices.push_back(i); break; }
    }
    return indices;
}

// =================== pole.rs ===================
constexpr int MAX_POI_TREE_DEPTH = 10;

struct POINode {
    usize level;
    Rect bbox;
    f32 radius;
    f32 distance;

    POINode(const Rect& bbox_, usize level_, const Polygon& poly, const std::vector<Circle>& poles)
        : level(level_), bbox(bbox_) {
        radius = bbox.diameter() / 2.0f;
        Point ctr = bbox.centroid();
        bool centroid_inside = collides(poly, ctr);
        if (centroid_inside)
            for (const auto& c : poles)
                if (collides(c, ctr)) { centroid_inside = false; break; }

        f32 dist_to_border = F32_MAX;
        for (usize i = 0; i < poly.n_vertices(); ++i)
            dist_to_border = min_f(dist_to_border, distance_to(poly.edge(i), ctr));
        for (const auto& c : poles)
            dist_to_border = min_f(dist_to_border, separation_distance(c, ctr).second);

        distance = centroid_inside ? dist_to_border : -dist_to_border;
    }

    f32 distance_upperbound() const { return radius + distance; }
    bool can_split() const { return level != 0; }
    std::array<POINode, 4> split(const Polygon& poly, const std::vector<Circle>& poles) const {
        auto qd = bbox.quadrants();
        return {POINode(qd[0], level - 1, poly, poles), POINode(qd[1], level - 1, poly, poles),
                POINode(qd[2], level - 1, poly, poles), POINode(qd[3], level - 1, poly, poles)};
    }
};

inline Circle compute_pole(const Polygon& shape, const std::vector<Circle>& poles) {
    Rect square_bbox = shape.bbox.inflate_to_square();
    std::deque<POINode> queue;
    queue.emplace_back(square_bbox, (usize)MAX_POI_TREE_DEPTH, shape, poles);
    std::optional<Circle> best;
    auto best_dist = [&]() -> f32 { return best ? best->radius : 0.0f; };

    while (!queue.empty()) {
        POINode node = queue.front();
        queue.pop_front();
        if (node.distance > best_dist())
            best = Circle::try_new(node.bbox.centroid(), node.distance);
        if (node.distance_upperbound() > best_dist() && node.can_split()) {
            auto children = node.split(shape, poles);
            for (auto& ch : children) queue.push_back(ch);
        }
    }
    if (!best) throw std::runtime_error("no pole found");
    return *best;
}

inline std::vector<Circle> generate_surrogate_poles(const Polygon& shape,
                                                    const std::array<std::pair<usize, f32>, N_POLE_LIMITS>& n_pole_limits) {
    std::vector<Circle> all_poles = {shape.poi};
    f32 total_pole_area = shape.poi.area();
    while (true) {
        Circle next = compute_pole(shape, all_poles);
        total_pole_area += next.area();
        all_poles.push_back(next);
        f32 coverage = total_pole_area / shape.area;

        // among limits whose threshold < coverage, take the smallest n_poles
        bool have_limit = false;
        usize active_limit = 0;
        for (const auto& [n_poles, threshold] : n_pole_limits) {
            if (coverage > threshold) {
                if (!have_limit || n_poles < active_limit) { active_limit = n_poles; have_limit = true; }
            }
        }
        if (have_limit && all_poles.size() >= active_limit) break;
        assert(all_poles.size() < 1000 && "More than 1000 poles generated; check SurrogateConfig");
    }
    return all_poles;
}

// =================== piers.rs ===================
namespace piers_detail {
constexpr usize RAYS_PER_ANGLE = 200;
constexpr usize N_ANGLES = 90;
constexpr usize N_POINTS_PER_DIMENSION = 100;
constexpr f32 CLIPPING_TRIM = 0.999f;
constexpr f32 ACTION_RADIUS_RATIO = 0.10f;

inline std::vector<f32> linspace(f32 a, f32 b, usize n) {
    std::vector<f32> v;
    v.reserve(n);
    if (n == 1) { v.push_back(a); return v; }
    f32 step = (b - a) / static_cast<f32>(n - 1);
    for (usize i = 0; i < n; ++i) v.push_back(a + step * static_cast<f32>(i));
    return v;
}

inline std::vector<AffineTransform> generate_ray_transformations(const Rect& bbox, usize rays_per_angle, usize n_angles) {
    f32 dx = bbox.width() / static_cast<f32>(rays_per_angle);
    std::vector<AffineTransform> translations;
    translations.reserve(rays_per_angle);
    for (usize i = 0; i < rays_per_angle; ++i)
        translations.push_back(AffineTransform::from_translation(bbox.x_min + dx * static_cast<f32>(i), 0.0f));

    std::vector<f32> angles = linspace(0.0f, PI_F, n_angles + 1); // skip last (== first)
    std::vector<AffineTransform> out;
    out.reserve(rays_per_angle * n_angles);
    for (usize a = 0; a < n_angles; ++a)
        for (const auto& tr : translations) out.push_back(tr.rotate(angles[a]));
    return out;
}

inline std::vector<Edge> clip(const Polygon& shape, const Edge& ray) {
    assert(!collides(shape, ray.start) && !collides(shape, ray.end));
    std::vector<Point> intersections;
    for (usize i = 0; i < shape.n_vertices(); ++i)
        if (auto p = shape.edge(i).collides_at(ray)) intersections.push_back(*p);
    std::stable_sort(intersections.begin(), intersections.end(), [&](const Point& a, const Point& b) {
        return ray.start.distance_to(a) < ray.start.distance_to(b);
    });
    std::vector<Edge> out;
    for (usize i = 0; i + 1 < intersections.size(); i += 2) {
        Point s = intersections[i], e = intersections[i + 1];
        if (!(s == e)) out.push_back(Edge::try_new(s, e).scale(CLIPPING_TRIM));
    }
    return out;
}

inline std::vector<Point> generate_unrepresented_point_grid(const Rect& bbox, const Polygon& shape,
                                                            const std::vector<Circle>& poles, usize n) {
    std::vector<f32> x_range = linspace(bbox.x_min, bbox.x_max, n);
    std::vector<f32> y_range = linspace(bbox.y_min, bbox.y_max, n);
    std::vector<Point> out;
    for (f32 x : x_range)
        for (f32 y : y_range) {
            Point p(x, y);
            if (!collides(shape, p)) continue;
            bool clear = true;
            for (const auto& c : poles) if (collides(c, p)) { clear = false; break; }
            if (clear) out.push_back(p);
        }
    return out;
}

inline f32 loss_function(const Edge& new_ray, const std::vector<Point>& grid,
                         const std::vector<f32>& min_dist_rays, const std::vector<f32>& min_dist_poles,
                         f32 radius_influence) {
    f32 sum = 0.0f;
    for (usize i = 0; i < grid.size(); ++i) {
        f32 d_new = distance_to(new_ray, grid[i]);
        f32 min_to_ray = min_f(min_dist_rays[i], d_new);
        f32 score = (min_to_ray < radius_influence) ? min_f(min_dist_poles[i], min_to_ray) : min_dist_poles[i];
        sum += score * score;
    }
    return sum;
}

inline std::vector<f32> min_distances_to_rays(const std::vector<Point>& points, const std::vector<Edge>& rays, f32 forfeit) {
    std::vector<f32> out;
    out.reserve(points.size());
    for (const auto& p : points) {
        f32 m = forfeit;
        for (const auto& r : rays) m = min_f(m, distance_to(r, p));
        out.push_back(m);
    }
    return out;
}
inline std::vector<f32> min_distances_to_poles(const std::vector<Point>& points, const std::vector<Circle>& poles, f32 forfeit) {
    std::vector<f32> out;
    out.reserve(points.size());
    for (const auto& p : points) {
        f32 m = forfeit;
        for (const auto& c : poles) m = min_f(m, distance_to(c, p));
        out.push_back(m);
    }
    return out;
}
} // namespace piers_detail

inline std::vector<Edge> generate_piers(const Polygon& shape, usize n, const std::vector<Circle>& poles) {
    using namespace piers_detail;
    if (n == 0) return {};
    Rect bbox = shape.bbox;
    Rect expanded_bbox = bbox.inflate_to_square();
    Point centroid = shape.centroid();
    Edge base_ray = Edge::try_new(Point(centroid.x, centroid.y - 2.0f * expanded_bbox.height()),
                                  Point(centroid.x, centroid.y + 2.0f * expanded_bbox.height()));
    std::vector<AffineTransform> transformations = generate_ray_transformations(expanded_bbox, RAYS_PER_ANGLE, N_ANGLES);
    std::vector<Edge> clipped_rays;
    for (const auto& t : transformations) {
        Edge ray = base_ray.transform_clone(t);
        for (auto& e : clip(shape, ray)) clipped_rays.push_back(e);
    }
    std::vector<Point> grid = generate_unrepresented_point_grid(expanded_bbox, shape, poles, N_POINTS_PER_DIMENSION);

    std::vector<Edge> selected_piers;
    f32 radius_influence = ACTION_RADIUS_RATIO * expanded_bbox.width();
    f32 forfeit = std::sqrt(sq(bbox.width()) * sq(bbox.height()));

    for (usize k = 0; k < n; ++k) {
        std::vector<f32> mdsr = min_distances_to_rays(grid, selected_piers, forfeit);
        std::vector<f32> mdp = min_distances_to_poles(grid, poles, forfeit);
        f32 best_loss = F32_MAX;
        int best_idx = -1;
        for (usize i = 0; i < clipped_rays.size(); ++i) {
            f32 l = loss_function(clipped_rays[i], grid, mdsr, mdp, radius_influence);
            if (l < best_loss) { best_loss = l; best_idx = (int)i; }
        }
        if (best_idx < 0) throw std::runtime_error("no ray found");
        selected_piers.push_back(clipped_rays[best_idx]);
    }
    return selected_piers;
}

// =================== Surrogate::make ===================
inline Surrogate Surrogate::make(const Polygon& simple_poly, const SurrogateConfig& config) {
    Surrogate s;
    s.convex_hull_indices = nest::convex_hull_indices(simple_poly);
    std::vector<Point> ch_pts;
    ch_pts.reserve(s.convex_hull_indices.size());
    for (usize i : s.convex_hull_indices) ch_pts.push_back(simple_poly.vertices[i]);
    s.convex_hull_area = Polygon::calculate_area(ch_pts);
    s.poles = generate_surrogate_poles(simple_poly, config.n_pole_limits);
    usize n_ff_poles = std::min(config.n_ff_poles, s.poles.size());
    std::vector<Circle> relevant(s.poles.begin(), s.poles.begin() + n_ff_poles);
    s.piers = generate_piers(simple_poly, config.n_ff_piers, relevant);
    s.config = config;
    return s;
}

inline void Polygon::generate_surrogate(const SurrogateConfig& config) {
    if (surrogate && surrogate->config == config) return;
    surrogate = Surrogate::make(*this, config);
}

inline Circle Polygon::calculate_poi(const std::vector<Point>& points, f32 diameter) {
    Polygon dummy;
    dummy.bbox = generate_bounding_box(points);
    dummy.area = calculate_area(points);
    dummy.diameter = diameter;
    dummy.poi = Circle::try_new(Point(F32_MAX, F32_MAX), F32_MAX);
    dummy.vertices = points;
    dummy.surrogate = std::nullopt;
    return compute_pole(dummy, {});
}

} // namespace nest
