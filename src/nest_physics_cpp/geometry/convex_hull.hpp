// Faithful C++ port of nest-rs geometry/convex_hull.rs (Andrew's monotone chain).
#pragma once
#include "primitives.hpp"

namespace nest {

inline f32 ch_cross(const Point& a, const Point& b, const Point& c) {
    return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x);
}

inline void grow_convex_hull(std::vector<Point>& h, const Point& next) {
    while (h.size() >= 2 && ch_cross(h[h.size() - 2], h[h.size() - 1], next) <= 0.0f)
        h.pop_back();
    h.push_back(next);
}

// Filters a set of points to only those on the convex hull (CCW order).
inline std::vector<Point> convex_hull_from_points(std::vector<Point> points) {
    // sort by x coordinate (stable, matching Rust sort_by_key)
    std::stable_sort(points.begin(), points.end(),
                     [](const Point& a, const Point& b) { return a.x < b.x; });

    std::vector<Point> lower_hull;
    for (const auto& p : points) grow_convex_hull(lower_hull, p);
    std::vector<Point> upper_hull;
    for (auto it = points.rbegin(); it != points.rend(); ++it) grow_convex_hull(upper_hull, *it);

    upper_hull.pop_back();
    lower_hull.pop_back();
    lower_hull.insert(lower_hull.end(), upper_hull.begin(), upper_hull.end());
    return lower_hull;
}

} // namespace nest
