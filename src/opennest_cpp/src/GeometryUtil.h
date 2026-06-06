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

/// Direction vector used in slide distance computation.
struct nVector {
    Point start;
    Point end;
    double x = 0;
    double y = 0;

    nVector() = default;
    nVector(double vx, double vy, const Point& s, const Point& e)
        : start(s), end(e), x(vx), y(vy) {}
};

/// Touch type for NFP orbit.
struct TouchingItem {
    int type;
    int A;
    int B;
    TouchingItem(int _type, int _a, int _b) : type(_type), A(_a), B(_b) {}
};

/// Geometry utility functions — mirrors C# GeometryUtil.
class GeometryUtil {
public:
    static constexpr double TOL = 1e-9;

    static float remap(float value, float low2, float high2,
                       float low1 = 0.0f, float high1 = 360.0f);

    static bool _withinDistance(const Point& p1, const Point& p2, double distance);

    static bool _almostEqual(double a, double b, double tolerance = TOL);

    static Point _normalizeVector(const Point& v);

    // Polygon bounds
    static PolygonBounds getPolygonBounds(const NFP& polygon);
    static PolygonBounds getPolygonBounds(const std::vector<Point>& polygon);

    // Rectangle test
    static bool isRectangle(const NFP& poly, double tolerance = TOL);

    // Rotate polygon (returns new polygon with bounds)
    static PolygonWithBounds rotatePolygon(const NFP& polygon, float angle);

    // Polygon area (signed, shoelace formula)
    static double polygonArea(const NFP& polygon);

    // Point-in-polygon test. Returns true=inside, false=outside, nullopt=on edge/vertex.
    static std::optional<bool> pointInPolygon(const Point& point, const NFP& polygon);

    // Point on segment test (not at endpoints)
    static bool _onSegment(const Point& A, const Point& B, const Point& p);

    // Line intersection
    static std::optional<Point> _lineIntersect(const Point& A, const Point& B,
                                                   const Point& E, const Point& F,
                                                   bool infinite = false);

    // Point-to-segment distance along a direction
    static std::optional<double> pointDistance(const Point& p, const Point& s1,
                                               const Point& s2, const Point& normal,
                                               bool infinite = false);

    // Segment-to-segment distance in a direction
    static std::optional<double> segmentDistance(const Point& A, const Point& B,
                                                 const Point& E, const Point& F,
                                                 const Point& direction);

    // Polygon slide distance
    static std::optional<double> polygonSlideDistance(NFP A, NFP B, const nVector& direction,
                                                      bool ignoreNegative);

    // Polygon projection distance
    static std::optional<double> polygonProjectionDistance(NFP A, NFP B,
                                                           const Point& direction);

    // Polygon intersection test
    static bool intersect(NFP A, NFP B);

    // Check if point exists in NFP array
    static bool inNfp(const Point& p, const std::vector<NFP>& nfp);

    // No-fit polygon for rectangles (special case)
    static std::vector<NFP> noFitPolygonRectangle(const NFP& A, const NFP& B);

    // Search start point for NFP orbit
    static std::optional<Point> searchStartPoint(NFP A, NFP B, bool inside,
                                                     const std::vector<NFP>& NFPlist = {});

    // Main NFP computation (orbital method)
    static std::vector<NFP> noFitPolygon(NFP A, NFP B, bool inside, bool searchEdges);

private:
    // Helper: close polygon loop if not already closed
    static void closeLoop(NFP& poly);
    // Helper: check if polygon is closed
    static bool isClosed(const NFP& poly);

    // Reusable visited-flags buffer (replaces per-point 'marked' field)
    static std::vector<bool> searchMarkedA;
};

} // namespace nest
