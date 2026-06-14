#pragma once

#include <vector>
#include <optional>
#include <cmath>
#include <algorithm>
#include <limits>

#include "Point.h"
#include "NFP.h"

namespace nest {

/// Bounding box of a polygon.
struct PolygonBounds {
    double x, y, width, height;
    PolygonBounds() : x(0), y(0), width(0), height(0) {}
    PolygonBounds(double _x, double _y, double _w, double _h)
        : x(_x), y(_y), width(_w), height(_h) {}
};

/// NFP subclass with bounding box fields (returned by rotatePolygon).
class PolygonWithBounds : public NFP {
public:
    double bx = 0;  // bounds x (shadows NFP::x)
    double by = 0;  // bounds y
    double width = 0;
    double height = 0;
};

/// Geometry utilities used by the engine. (The JS-port orbital-NFP method that used to
/// live here was never wired in — the engine computes NFPs via Minkowski convolution
/// (Boost.Polygon) and Clipper2 MinkowskiSum — and was removed as dead code.)
class GeometryUtil {
public:
    static constexpr double TOL = 1e-9;

    static bool _almostEqual(double a, double b, double tolerance = TOL);

    // Polygon bounds
    static PolygonBounds getPolygonBounds(const NFP& polygon);
    static PolygonBounds getPolygonBounds(const std::vector<Point>& polygon);

    // Rotate polygon (returns new polygon with bounds)
    static PolygonWithBounds rotatePolygon(const NFP& polygon, float angle);

    // Polygon area (signed, shoelace formula)
    static double polygonArea(const NFP& polygon);

    // Point-in-polygon test. Returns true=inside, false=outside, nullopt=on edge/vertex.
    static std::optional<bool> pointInPolygon(const Point& point, const NFP& polygon);

    // Point on segment test (not at endpoints)
    static bool _onSegment(const Point& A, const Point& B, const Point& p);
};

} // namespace nest
