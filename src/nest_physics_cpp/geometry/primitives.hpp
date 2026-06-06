// Faithful C++ port of nest-rs geometry/primitives/{point,edge,rect,circle}.rs
// The Rust traits CollidesWith<T>/DistanceTo<T>/SeparationDistance<T>/AlmostCollidesWith<T>
// are rendered as overloaded free functions: collides(a,b), distance_to(a,b),
// sq_distance_to(a,b), separation_distance(a,b), sq_separation_distance(a,b),
// almost_collides(a,b).  Transformable -> member transform()/transform_from().
#pragma once
#include "common.hpp"
#include "transform.hpp"

namespace nest {

// ===================== Point =====================
struct Point {
    f32 x = 0.0f, y = 0.0f;
    Point() = default;
    Point(f32 x_, f32 y_) : x(x_), y(y_) {}

    f32 sq_distance_to(const Point& o) const { return sq(x - o.x) + sq(y - o.y); }
    f32 distance_to(const Point& o) const { return std::sqrt(sq_distance_to(o)); }

    void transform(const AffineTransform& t) {
        const Mat3& m = t.m;
        f32 nx = m[0][0] * x + m[0][1] * y + m[0][2];
        f32 ny = m[1][0] * x + m[1][1] * y + m[1][2];
        x = nx; y = ny;
    }
    void transform_from(const Point& r, const AffineTransform& t) {
        const Mat3& m = t.m;
        x = m[0][0] * r.x + m[0][1] * r.y + m[0][2];
        y = m[1][0] * r.x + m[1][1] * r.y + m[1][2];
    }
    Point transform_clone(const AffineTransform& t) const { Point p = *this; p.transform(t); return p; }

    bool operator==(const Point& o) const { return x == o.x && y == o.y; }
    bool operator!=(const Point& o) const { return !(*this == o); }
};

struct Rect; // fwd

// ===================== Edge =====================
struct Edge {
    Point start, end;
    Edge() = default;
    Edge(Point s, Point e) : start(s), end(e) {}
    // try_new: degenerate edges (start == end) are rejected in Rust; we assert.
    static Edge try_new(Point s, Point e) {
        assert(!(s == e) && "degenerate edge");
        return Edge(s, e);
    }

    Edge extend_at_front(f32 d) const {
        Edge r = *this;
        f32 dx = end.x - start.x, dy = end.y - start.y;
        f32 l = length();
        r.start.x -= dx * (d / l);
        r.start.y -= dy * (d / l);
        return r;
    }
    Edge extend_at_back(f32 d) const {
        Edge r = *this;
        f32 dx = end.x - start.x, dy = end.y - start.y;
        f32 l = length();
        r.end.x += dx * (d / l);
        r.end.y += dy * (d / l);
        return r;
    }
    Edge scale(f32 factor) const {
        Edge r = *this;
        f32 dx = end.x - start.x, dy = end.y - start.y;
        r.start.x -= dx * (factor - 1.0f) / 2.0f;
        r.start.y -= dy * (factor - 1.0f) / 2.0f;
        r.end.x += dx * (factor - 1.0f) / 2.0f;
        r.end.y += dy * (factor - 1.0f) / 2.0f;
        return r;
    }
    Edge reverse() const { return Edge(end, start); }

    Point closest_point_on_edge(const Point& point) const {
        f32 x1 = start.x, y1 = start.y, x2 = end.x, y2 = end.y;
        f32 x = point.x, y = point.y;
        f32 a = x - x1, b = y - y1, c = x2 - x1, d = y2 - y1;
        f32 dot = a * c + b * d;
        f32 len_sq = c * c + d * d;
        f32 param = -1.0f;
        if (len_sq != 0.0f) param = dot / len_sq;
        f32 xx, yy;
        if (param < 0.0f) { xx = x1; yy = y1; }
        else if (param > 1.0f) { xx = x2; yy = y2; }
        else { xx = x1 + param * c; yy = y1 + param * d; }
        return Point(xx, yy);
    }

    f32 x_min() const { return min_f(start.x, end.x); }
    f32 y_min() const { return min_f(start.y, end.y); }
    f32 x_max() const { return max_f(start.x, end.x); }
    f32 y_max() const { return max_f(start.y, end.y); }
    f32 length() const { return start.distance_to(end); }
    Point centroid() const { return Point(midpoint(start.x, end.x), midpoint(start.y, end.y)); }
    inline Rect bbox() const; // defined after Rect

    void transform(const AffineTransform& t) { start.transform(t); end.transform(t); }
    void transform_from(const Edge& ref, const AffineTransform& t) {
        start.transform_from(ref.start, t);
        end.transform_from(ref.end, t);
    }
    Edge transform_clone(const AffineTransform& t) const { Edge e = *this; e.transform(t); return e; }

    // edge_intersection helper result + collides_at (returns intersection point)
    std::optional<Point> collides_at(const Edge& other) const;
};

// edge-edge intersection (Wikipedia line-line); calc_loc controls whether to compute the point.
inline bool edge_intersection(const Edge& e1, const Edge& e2, bool calc_loc, Point& out) {
    f32 x1 = e1.start.x, y1 = e1.start.y, x2 = e1.end.x, y2 = e1.end.y;
    f32 x3 = e2.start.x, y3 = e2.start.y, x4 = e2.end.x, y4 = e2.end.y;
    {
        f32 x_min_e1 = min_f(x1, x2), x_max_e1 = max_f(x1, x2);
        f32 y_min_e1 = min_f(y1, y2), y_max_e1 = max_f(y1, y2);
        f32 x_min_e2 = min_f(x3, x4), x_max_e2 = max_f(x3, x4);
        f32 y_min_e2 = min_f(y3, y4), y_max_e2 = max_f(y3, y4);
        bool x_no = max_f(x_min_e1, x_min_e2) > min_f(x_max_e1, x_max_e2);
        bool y_no = max_f(y_min_e1, y_min_e2) > min_f(y_max_e1, y_max_e2);
        if (x_no || y_no) return false;
    }
    f32 t_nom = (x2 - x4) * (y4 - y3) - (y2 - y4) * (x4 - x3);
    f32 t_denom = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    f32 u_nom = (x2 - x4) * (y2 - y1) - (y2 - y4) * (x2 - x1);
    f32 u_denom = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    if (t_denom == 0.0f || u_denom == 0.0f) return false; // parallel
    f32 t = t_nom / t_denom;
    f32 u = u_nom / u_denom;
    if (t >= 0.0f && t <= 1.0f && u >= 0.0f && u <= 1.0f) {
        if (calc_loc) out = Point(x2 + t * (x1 - x2), y2 + t * (y1 - y2));
        return true;
    }
    return false;
}

inline std::optional<Point> Edge::collides_at(const Edge& other) const {
    Point p;
    if (edge_intersection(*this, other, true, p)) return p;
    return std::nullopt;
}

// Edge collisions / distances
inline bool collides(const Edge& a, const Edge& b) { Point p; return edge_intersection(a, b, false, p); }
inline f32 sq_distance_to(const Edge& e, const Point& point) {
    Point cp = e.closest_point_on_edge(point);
    return sq(point.x - cp.x) + sq(point.y - cp.y);
}
inline f32 distance_to(const Edge& e, const Point& point) { return std::sqrt(sq_distance_to(e, point)); }

// ===================== Rect =====================
struct Rect {
    f32 x_min, y_min, x_max, y_max;
    Rect() : x_min(0), y_min(0), x_max(0), y_max(0) {}
    Rect(f32 xn, f32 yn, f32 xm, f32 ym) : x_min(xn), y_min(yn), x_max(xm), y_max(ym) {}

    static Rect try_new(f32 xn, f32 yn, f32 xm, f32 ym) {
        assert(xn < xm && yn < ym && "invalid rectangle");
        return Rect(xn, yn, xm, ym);
    }
    static Rect from_diagonal_corners(Point c1, Point c2) {
        return try_new(min_f(c1.x, c2.x), min_f(c1.y, c2.y), max_f(c1.x, c2.x), max_f(c1.y, c2.y));
    }

    f32 width() const { return x_max - x_min; }
    f32 height() const { return y_max - y_min; }
    f32 area() const { return (x_max - x_min) * (y_max - y_min); }
    f32 diameter() const { f32 dx = x_max - x_min, dy = y_max - y_min; return std::sqrt(dx * dx + dy * dy); }
    Point centroid() const { return Point(midpoint(x_min, x_max), midpoint(y_min, y_max)); }

    GeoRelation relation_to(const Rect& other) const;
    GeoRelation almost_relation_to(const Rect& other) const;

    Rect inflate_to_square() const {
        f32 w = x_max - x_min, h = y_max - y_min;
        f32 dx = 0.0f, dy = 0.0f;
        if (h < w) dy = (w - h) / 2.0f;
        else if (w < h) dx = (h - w) / 2.0f;
        return Rect(x_min - dx, y_min - dy, x_max + dx, y_max + dy);
    }
    std::optional<Rect> resize_by(f32 dx, f32 dy) const {
        Rect r(x_min - dx, y_min - dy, x_max + dx, y_max + dy);
        if (r.x_min < r.x_max && r.y_min < r.y_max) return r;
        return std::nullopt;
    }
    Rect scale(f32 factor) const {
        f32 dx = (x_max - x_min) * (factor - 1.0f) / 2.0f;
        f32 dy = (y_max - y_min) * (factor - 1.0f) / 2.0f;
        return resize_by(dx, dy).value();
    }

    // corners, in the same order as quadrants (Q1=+,+ ; Q2=-,+ ; Q3=-,- ; Q4=+,-)
    std::array<Point, 4> corners() const {
        return {Point(x_max, y_max), Point(x_min, y_max), Point(x_min, y_min), Point(x_max, y_min)};
    }
    std::array<Rect, 4> quadrants() const {
        Point mid = centroid();
        auto c = corners();
        return {from_diagonal_corners(c[0], mid), from_diagonal_corners(c[1], mid),
                from_diagonal_corners(c[2], mid), from_diagonal_corners(c[3], mid)};
    }
    std::array<Edge, 4> edges() const {
        auto c = corners();
        return {Edge(c[0], c[1]), Edge(c[1], c[2]), Edge(c[2], c[3]), Edge(c[3], c[0])};
    }
    std::array<Edge, 4> sides() const { return edges(); }

    static std::optional<Rect> intersection(const Rect& a, const Rect& b) {
        f32 xn = max_f(a.x_min, b.x_min), yn = max_f(a.y_min, b.y_min);
        f32 xm = min_f(a.x_max, b.x_max), ym = min_f(a.y_max, b.y_max);
        if (xn < xm && yn < ym) return Rect(xn, yn, xm, ym);
        return std::nullopt;
    }
    static Rect bounding_rect(const Rect& a, const Rect& b) {
        return Rect(min_f(a.x_min, b.x_min), min_f(a.y_min, b.y_min),
                    max_f(a.x_max, b.x_max), max_f(a.y_max, b.y_max));
    }
};

// For all quadrants, indices of the two neighbours of the quadrant at that index.
inline constexpr int QUADRANT_NEIGHBOR_LAYOUT[4][2] = {{1, 3}, {0, 2}, {1, 3}, {0, 2}};

inline Rect Edge::bbox() const { return Rect(x_min(), y_min(), x_max(), y_max()); }

// ---- Rect collisions ----
inline bool collides(const Rect& a, const Rect& b) {
    return max_f(a.x_min, b.x_min) <= min_f(a.x_max, b.x_max) &&
           max_f(a.y_min, b.y_min) <= min_f(a.y_max, b.y_max);
}
inline bool almost_collides(const Rect& a, const Rect& b) {
    return FPA(max_f(a.x_min, b.x_min)) <= FPA(min_f(a.x_max, b.x_max)) &&
           FPA(max_f(a.y_min, b.y_min)) <= FPA(min_f(a.y_max, b.y_max));
}
inline bool collides(const Rect& r, const Point& p) {
    return p.x >= r.x_min && p.x <= r.x_max && p.y >= r.y_min && p.y <= r.y_max;
}
inline bool almost_collides(const Rect& r, const Point& p) {
    return FPA(p.x) >= FPA(r.x_min) && FPA(p.x) <= FPA(r.x_max) &&
           FPA(p.y) >= FPA(r.y_min) && FPA(p.y) <= FPA(r.y_max);
}
inline bool collides(const Rect& self, const Edge& edge) {
    f32 e_x_min = edge.x_min(), e_x_max = edge.x_max(), e_y_min = edge.y_min(), e_y_max = edge.y_max();
    bool x_no = max_f(e_x_min, self.x_min) > min_f(e_x_max, self.x_max);
    bool y_no = max_f(e_y_min, self.y_min) > min_f(e_y_max, self.y_max);
    if (x_no || y_no) return false;
    if (collides(self, edge.start) || collides(self, edge.end)) return true;
    f32 s_x = edge.start.x, s_y = edge.start.y, e_x = edge.end.x, e_y = edge.end.y;
    f32 edx = e_x - s_x, edy = e_y - s_y;
    auto c = self.corners();
    f32 sides[4] = {
        (c[0].x - s_x) * edy - (c[0].y - s_y) * edx,
        (c[1].x - s_x) * edy - (c[1].y - s_y) * edx,
        (c[2].x - s_x) * edy - (c[2].y - s_y) * edx,
        (c[3].x - s_x) * edy - (c[3].y - s_y) * edx,
    };
    bool all_pos = sides[0] > 0 && sides[1] > 0 && sides[2] > 0 && sides[3] > 0;
    bool all_neg = sides[0] < 0 && sides[1] < 0 && sides[2] < 0 && sides[3] < 0;
    return !(all_pos || all_neg);
}
inline bool collides(const Edge& e, const Rect& r) { return collides(r, e); }

inline f32 sq_distance_to(const Rect& r, const Point& p) {
    f32 d = 0.0f;
    if (p.x < r.x_min) d += sq(p.x - r.x_min);
    else if (p.x > r.x_max) d += sq(p.x - r.x_max);
    if (p.y < r.y_min) d += sq(p.y - r.y_min);
    else if (p.y > r.y_max) d += sq(p.y - r.y_max);
    return std::fabs(d);
}
inline f32 distance_to(const Rect& r, const Point& p) { return std::sqrt(sq_distance_to(r, p)); }

inline std::pair<GeoPosition, f32> sq_separation_distance(const Rect& r, const Point& p) {
    if (collides(r, p)) {
        f32 m = min_f(min_f(std::fabs(p.x - r.x_min), std::fabs(p.x - r.x_max)),
                      min_f(std::fabs(p.y - r.y_min), std::fabs(p.y - r.y_max)));
        return {GeoPosition::Interior, m * m};
    }
    return {GeoPosition::Exterior, sq_distance_to(r, p)};
}
inline std::pair<GeoPosition, f32> separation_distance(const Rect& r, const Point& p) {
    auto [pos, d] = sq_separation_distance(r, p);
    return {pos, std::sqrt(d)};
}

inline GeoRelation Rect::relation_to(const Rect& other) const {
    if (!collides(*this, other)) return GeoRelation::Disjoint;
    if (x_min <= other.x_min && y_min <= other.y_min && x_max >= other.x_max && y_max >= other.y_max)
        return GeoRelation::Surrounding;
    if (x_min >= other.x_min && y_min >= other.y_min && x_max <= other.x_max && y_max <= other.y_max)
        return GeoRelation::Enclosed;
    return GeoRelation::Intersecting;
}
inline GeoRelation Rect::almost_relation_to(const Rect& other) const {
    if (!almost_collides(*this, other)) return GeoRelation::Disjoint;
    if (FPA(x_min) <= FPA(other.x_min) && FPA(y_min) <= FPA(other.y_min) &&
        FPA(x_max) >= FPA(other.x_max) && FPA(y_max) >= FPA(other.y_max))
        return GeoRelation::Surrounding;
    if (FPA(x_min) >= FPA(other.x_min) && FPA(y_min) >= FPA(other.y_min) &&
        FPA(x_max) <= FPA(other.x_max) && FPA(y_max) <= FPA(other.y_max))
        return GeoRelation::Enclosed;
    return GeoRelation::Intersecting;
}

// ===================== Circle =====================
struct Circle {
    Point center;
    f32 radius = 0.0f;
    Circle() = default;
    Circle(Point c, f32 r) : center(c), radius(r) {}
    static Circle try_new(Point c, f32 r) {
        assert(std::isfinite(r) && r >= 0.0f && "invalid circle radius");
        return Circle(c, r);
    }

    f32 area() const { return radius * radius * PI_F; }
    f32 diameter() const { return radius * 2.0f; }
    Rect bbox() const { return Rect(center.x - radius, center.y - radius, center.x + radius, center.y + radius); }

    void transform(const AffineTransform& t) { center.transform(t); }
    void transform_from(const Circle& ref, const AffineTransform& t) { center.transform_from(ref.center, t); }
    Circle transform_clone(const AffineTransform& t) const { Circle c = *this; c.transform(t); return c; }

    // Smallest circle fully containing all given circles (iterative growth).
    template <class It>
    static Circle bounding_circle(It begin, It end) {
        assert(begin != end && "no circles provided");
        Circle bc = *begin;
        ++begin;
        for (; begin != end; ++begin) {
            const Circle& circle = *begin;
            f32 dbc = bc.center.distance_to(circle.center);
            if (bc.radius < dbc + circle.radius) {
                Edge diameter = Edge(bc.center, circle.center)
                                    .extend_at_front(bc.radius)
                                    .extend_at_back(circle.radius);
                bc = Circle(diameter.centroid(), diameter.length() / 2.0f);
            }
        }
        return bc;
    }
};

// ---- Circle collisions / distances / separations ----
inline bool collides(const Circle& a, const Circle& b) {
    f32 dx = a.center.x - b.center.x, dy = a.center.y - b.center.y;
    f32 sq_d = dx * dx + dy * dy;
    return sq_d <= (a.radius + b.radius) * (a.radius + b.radius);
}
inline bool collides(const Circle& c, const Edge& e) { return sq_distance_to(e, c.center) <= c.radius * c.radius; }
inline bool collides(const Circle& c, const Rect& rect) {
    f32 nx = max_f(rect.x_min, min_f(c.center.x, rect.x_max));
    f32 ny = max_f(rect.y_min, min_f(c.center.y, rect.y_max));
    return sq(nx - c.center.x) + sq(ny - c.center.y) <= c.radius * c.radius;
}
inline bool collides(const Circle& c, const Point& p) { return p.sq_distance_to(c.center) <= c.radius * c.radius; }

inline f32 distance_to(const Circle& c, const Point& p) {
    f32 sq_d = sq(p.x - c.center.x) + sq(p.y - c.center.y);
    if (sq_d < c.radius * c.radius) return 0.0f;
    return std::sqrt(sq_d) - c.radius;
}
inline f32 sq_distance_to(const Circle& c, const Point& p) { return sq(distance_to(c, p)); }

inline std::pair<GeoPosition, f32> separation_distance(const Circle& c, const Point& p) {
    f32 d_center = std::sqrt(sq(p.x - c.center.x) + sq(p.y - c.center.y));
    if (d_center <= c.radius) return {GeoPosition::Interior, c.radius - d_center};
    return {GeoPosition::Exterior, d_center - c.radius};
}
inline std::pair<GeoPosition, f32> sq_separation_distance(const Circle& c, const Point& p) {
    auto [pos, d] = separation_distance(c, p);
    return {pos, sq(d)};
}

inline std::pair<GeoPosition, f32> separation_distance(const Circle& a, const Circle& b) {
    f32 sq_center = a.center.sq_distance_to(b.center);
    f32 sq_radii = sq(a.radius + b.radius);
    if (sq_center < sq_radii) return {GeoPosition::Interior, std::sqrt(sq_radii) - std::sqrt(sq_center)};
    return {GeoPosition::Exterior, std::sqrt(sq_center) - std::sqrt(sq_radii)};
}
inline f32 distance_to(const Circle& a, const Circle& b) {
    auto [pos, d] = separation_distance(a, b);
    return pos == GeoPosition::Interior ? 0.0f : d;
}
inline f32 sq_distance_to(const Circle& a, const Circle& b) { return sq(distance_to(a, b)); }

inline std::pair<GeoPosition, f32> separation_distance(const Circle& c, const Edge& e) {
    f32 dc = distance_to(e, c.center);
    if (dc < c.radius) return {GeoPosition::Interior, c.radius - dc};
    return {GeoPosition::Exterior, dc - c.radius};
}
inline std::pair<GeoPosition, f32> sq_separation_distance(const Circle& c, const Edge& e) {
    auto [pos, d] = separation_distance(c, e);
    return {pos, sq(d)};
}
inline f32 distance_to(const Circle& c, const Edge& e) {
    auto [pos, d] = separation_distance(c, e);
    return pos == GeoPosition::Interior ? 0.0f : d;
}
inline f32 sq_distance_to(const Circle& c, const Edge& e) { return sq(distance_to(c, e)); }

} // namespace nest
