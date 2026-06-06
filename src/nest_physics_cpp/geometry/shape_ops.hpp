// Faithful C++ port of nest-rs geometry/shape_modification.rs
//   * simplify_shape        - edge-reduction within an area-change tolerance
//   * close_narrow_concavities - close thin concavities with a straight edge
//   * offset_shape          - NOT PORTED: requires the geo-buffer crate. nest's
//     default config sets min_part_separation = None, so offset is never invoked.
#pragma once
#include "polygon.hpp"

namespace nest {

enum class ShapeModifyMode { Inflate, Deflate };

struct ShapeModifyConfig {
    std::optional<f32> simplify_tolerance;
    std::optional<f32> offset;
    std::optional<std::pair<f32, f32>> narrow_concavity_cutoff;
    bool operator==(const ShapeModifyConfig& o) const {
        return simplify_tolerance == o.simplify_tolerance && offset == o.offset &&
               narrow_concavity_cutoff == o.narrow_concavity_cutoff;
    }
};

namespace shapemod_detail {

// Corner: left-hand side of points 0-1-2 (indices into the vertex array).
struct Corner {
    int a, b, c;
    void flip() { std::swap(a, c); }
};
enum class CornerType { Concave, Convex, Collinear };

inline CornerType corner_type(const Point& p1, const Point& p2, const Point& p3) {
    f32 p1p2x = p2.x - p1.x, p1p2y = p2.y - p1.y;
    f32 p1p3x = p3.x - p1.x, p1p3y = p3.y - p1.y;
    f32 cross = p1p2x * p1p3y - p1p2y * p1p3x;
    if (cross < 0.0f) return CornerType::Concave;
    if (cross > 0.0f) return CornerType::Convex;
    return CornerType::Collinear;
}

inline int rem_euclid(int a, int n) { int r = a % n; return r < 0 ? r + n : r; }

enum class CandKind { Concave, ConvexConvex, Collinear };
struct Candidate {
    CandKind kind;
    Corner c1; // Concave/Collinear: the corner; ConvexConvex: prev corner
    Corner c2; // ConvexConvex: the corner
};

// intersection of l1 and l2 extended "in front" (t<0 && u<0 branch), else nullopt.
inline std::optional<Point> intersection_in_front(const Edge& l1, const Edge& l2) {
    f32 x1 = l1.start.x, y1 = l1.start.y, x2 = l1.end.x, y2 = l1.end.y;
    f32 x3 = l2.start.x, y3 = l2.start.y, x4 = l2.end.x, y4 = l2.end.y;
    f32 t_nom = (x2 - x4) * (y4 - y3) - (y2 - y4) * (x4 - x3);
    f32 t_denom = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    f32 u_nom = (x2 - x4) * (y2 - y1) - (y2 - y4) * (x2 - x1);
    f32 u_denom = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    f32 t = t_denom == 0.0f ? 0.0f : t_nom / t_denom;
    f32 u = u_denom == 0.0f ? 0.0f : u_nom / u_denom;
    if (t < 0.0f && u < 0.0f) return Point(x2 + t * (x1 - x2), y2 + t * (y1 - y2));
    return std::nullopt;
}

inline std::optional<Point> replacing_vertex(const std::vector<Point>& shape, const Corner& c1, const Corner& c2) {
    Edge edge_prev = Edge::try_new(shape[c1.a], shape[c1.b]);
    Edge edge_next = Edge::try_new(shape[c2.c], shape[c2.b]);
    return intersection_in_front(edge_prev, edge_next);
}

inline f32 tri_area(Point a, Point b, Point c) {
    return std::fabs(a.x * b.y + b.x * c.y + c.x * a.y - a.x * c.y - b.x * a.y - c.x * b.y) / 2.0f;
}

inline f32 area_delta(const std::vector<Point>& shape, const Candidate& cand) {
    if (cand.kind == CandKind::Collinear) return 0.0f;
    if (cand.kind == CandKind::Concave) return tri_area(shape[cand.c1.a], shape[cand.c1.b], shape[cand.c1.c]);
    // ConvexConvex
    auto rv = replacing_vertex(shape, cand.c1, cand.c2);
    if (!rv) return std::numeric_limits<f32>::infinity();
    return tri_area(shape[cand.c1.b], *rv, shape[cand.c2.b]);
}

inline Edge edge_at(const std::vector<Point>& pts, int i) {
    int n = (int)pts.size();
    return Edge::try_new(pts[i], pts[(i + 1) % n]);
}

inline bool contains_point(const Point arr[], int n, const Point& p) {
    for (int i = 0; i < n; ++i) if (arr[i] == p) return true;
    return false;
}

inline bool candidate_is_valid(const std::vector<Point>& shape, const Candidate& cand) {
    int n = (int)shape.size();
    if (cand.kind == CandKind::Collinear) return true;
    if (cand.kind == CandKind::Concave) {
        const Corner& c = cand.c1;
        Edge new_edge = Edge::try_new(shape[c.a], shape[c.c]);
        Point affected[3] = {shape[c.a], shape[c.b], shape[c.c]};
        for (int i = 0; i < n; ++i) {
            Edge l = edge_at(shape, i);
            if (contains_point(affected, 3, l.start) || contains_point(affected, 3, l.end)) continue;
            if (collides(l, new_edge)) return false;
        }
        return true;
    }
    // ConvexConvex
    auto rv = replacing_vertex(shape, cand.c1, cand.c2);
    if (!rv) return false;
    Edge ne1 = Edge::try_new(shape[cand.c1.a], *rv);
    Edge ne2 = Edge::try_new(*rv, shape[cand.c2.c]);
    Point affected[4] = {shape[cand.c1.b], shape[cand.c1.a], shape[cand.c2.b], shape[cand.c2.c]};
    for (int i = 0; i < n; ++i) {
        Edge l = edge_at(shape, i);
        if (contains_point(affected, 4, l.start) || contains_point(affected, 4, l.end)) continue;
        if (collides(l, ne1) || collides(l, ne2)) return false;
    }
    return true;
}

inline std::vector<Point> execute_candidate(const std::vector<Point>& shape, const Candidate& cand) {
    std::vector<Point> points = shape;
    if (cand.kind == CandKind::Collinear || cand.kind == CandKind::Concave) {
        points.erase(points.begin() + cand.c1.b);
    } else {
        Point rv = *replacing_vertex(shape, cand.c1, cand.c2);
        int i1 = cand.c1.b, i2 = cand.c2.b;
        points.erase(points.begin() + i1);
        int other = (i1 < i2) ? i2 - 1 : i2;
        points.erase(points.begin() + other);
        points.insert(points.begin() + other, rv);
    }
    return points;
}

} // namespace shapemod_detail

inline Polygon simplify_shape(const Polygon& shape, ShapeModifyMode mode, f32 max_area_change_ratio) {
    using namespace shapemod_detail;
    f32 original_area = shape.area;
    std::vector<Point> ref_points = shape.vertices;

    for (usize iter = 0; iter < shape.n_vertices(); ++iter) {
        int n = (int)ref_points.size();
        if (n < 4) break;
        std::vector<Corner> corners;
        corners.reserve(n);
        for (int i = 0; i < n; ++i)
            corners.push_back(Corner{rem_euclid(i - 1, n), i, rem_euclid(i + 1, n)});
        if (mode == ShapeModifyMode::Deflate) {
            std::reverse(corners.begin(), corners.end());
            for (auto& c : corners) c.flip();
        }

        std::vector<Candidate> candidates;
        Corner prev_corner = corners.back();
        CornerType prev_type = corner_type(ref_points[prev_corner.a], ref_points[prev_corner.b], ref_points[prev_corner.c]);
        for (const auto& corner : corners) {
            CornerType ct = corner_type(ref_points[corner.a], ref_points[corner.b], ref_points[corner.c]);
            if (ct == CornerType::Concave) candidates.push_back({CandKind::Concave, corner, corner});
            else if (ct == CornerType::Collinear) candidates.push_back({CandKind::Collinear, corner, corner});
            else if (ct == CornerType::Convex && prev_type == CornerType::Convex)
                candidates.push_back({CandKind::ConvexConvex, prev_corner, corner});
            prev_corner = corner;
            prev_type = ct;
        }

        // sort by area delta (ascending), pick first valid
        std::stable_sort(candidates.begin(), candidates.end(), [&](const Candidate& a, const Candidate& b) {
            return area_delta(ref_points, a) < area_delta(ref_points, b);
        });
        int best = -1;
        for (usize i = 0; i < candidates.size(); ++i)
            if (candidate_is_valid(ref_points, candidates[i])) { best = (int)i; break; }
        if (best < 0) break;

        std::vector<Point> new_shape = execute_candidate(ref_points, candidates[best]);
        f32 new_area = Polygon::calculate_area(new_shape);
        f32 delta = std::fabs(new_area - original_area) / original_area;
        if (delta <= max_area_change_ratio) ref_points = std::move(new_shape);
        else break;
    }
    return Polygon::create(std::move(ref_points));
}

inline Polygon close_narrow_concavities(const Polygon& orig_shape, ShapeModifyMode mode,
                                         std::pair<f32, f32> cutoff) {
    f32 cutoff_distance_ratio = cutoff.first, cutoff_area_ratio = cutoff.second;
    Polygon shape = orig_shape;

    for (usize iter = 0; iter < orig_shape.n_vertices(); ++iter) {
        int n_points = (int)shape.n_vertices();
        auto calc_vert_elim = [&](int i, int j) { return j > i ? (j - i - 1) : (n_points - i + j - 1); };

        std::optional<std::pair<int, int>> best_candidate;
        for (int i = 0; i < n_points; ++i) {
            for (int j = 0; j < n_points; ++j) {
                if (i == j || (i + 1) % n_points == j || (j + 1) % n_points == i) continue;
                Edge c_edge = Edge::try_new(shape.vertex(i), shape.vertex(j)).scale(0.9999f);
                if (c_edge.length() > cutoff_distance_ratio * shape.diameter) continue;
                if (mode == ShapeModifyMode::Inflate &&
                    (collides(shape, c_edge.start) || collides(shape, c_edge.end))) continue;
                if (mode == ShapeModifyMode::Deflate &&
                    !(collides(shape, c_edge.start) && collides(shape, c_edge.end))) continue;
                bool hits = false;
                for (int e = 0; e < (int)shape.n_vertices(); ++e)
                    if (collides(shape.edge(e), c_edge)) { hits = true; break; }
                if (hits) continue;
                std::vector<Point> sub;
                if (j > i) sub.assign(shape.vertices.begin() + i, shape.vertices.begin() + j);
                else {
                    sub.assign(shape.vertices.begin() + i, shape.vertices.end());
                    sub.insert(sub.end(), shape.vertices.begin(), shape.vertices.begin() + j);
                }
                f32 sub_area = Polygon::calculate_area(sub);
                if (sub_area >= 0.0f) continue;
                if (std::fabs(sub_area) > cutoff_area_ratio * shape.area) continue;
                if (!best_candidate || calc_vert_elim(i, j) > calc_vert_elim(best_candidate->first, best_candidate->second))
                    best_candidate = std::make_pair(i, j);
            }
        }
        if (!best_candidate) break;
        int i = best_candidate->first, j = best_candidate->second;
        std::vector<Point> ref_points = shape.vertices;
        int start = i + 1, end = j - 1;
        if (start <= end) {
            ref_points.erase(ref_points.begin() + start, ref_points.begin() + end + 1);
        } else {
            if (start < n_points) ref_points.erase(ref_points.begin() + start, ref_points.end());
            if (end >= 0) ref_points.erase(ref_points.begin(), ref_points.begin() + end + 1);
        }
        shape = Polygon::create(std::move(ref_points));
    }
    return shape;
}

inline Polygon offset_shape(const Polygon&, ShapeModifyMode, f32) {
    throw std::runtime_error("offset_shape not ported (geo-buffer); nest default uses min_part_separation=None");
}

} // namespace nest
