#include "GeometryUtil.h"
#include <cmath>
#include <algorithm>

namespace nest {

bool GeometryUtil::_almostEqual(double a, double b, double tolerance) {
    return std::fabs(a - b) < tolerance;
}

PolygonBounds GeometryUtil::getPolygonBounds(const NFP& polygon) {
    return getPolygonBounds(polygon.Points);
}

PolygonBounds GeometryUtil::getPolygonBounds(const std::vector<Point>& polygon) {
    if (polygon.size() < 3) {
        return PolygonBounds();
    }

    double xmin = polygon[0].x;
    double xmax = polygon[0].x;
    double ymin = polygon[0].y;
    double ymax = polygon[0].y;

    for (size_t i = 1; i < polygon.size(); i++) {
        if (polygon[i].x > xmax) xmax = polygon[i].x;
        else if (polygon[i].x < xmin) xmin = polygon[i].x;
        if (polygon[i].y > ymax) ymax = polygon[i].y;
        else if (polygon[i].y < ymin) ymin = polygon[i].y;
    }

    return PolygonBounds(xmin, ymin, xmax - xmin, ymax - ymin);
}

PolygonWithBounds GeometryUtil::rotatePolygon(const NFP& polygon, float angle) {
    float rad = static_cast<float>(angle * M_PI / 180.0);
    float cosA = std::cos(rad);
    float sinA = std::sin(rad);

    PolygonWithBounds ret;
    for (size_t i = 0; i < polygon.Points.size(); i++) {
        double x = polygon.Points[i].x;
        double y = polygon.Points[i].y;
        double x1 = static_cast<float>(x * cosA - y * sinA);
        double y1 = static_cast<float>(x * sinA + y * cosA);
        ret.AddPoint(Point(x1, y1));
    }

    auto bounds = getPolygonBounds(ret);
    ret.bx = bounds.x;
    ret.by = bounds.y;
    ret.width = bounds.width;
    ret.height = bounds.height;
    return ret;
}

double GeometryUtil::polygonArea(const NFP& polygon) {
    double area = 0;
    int n = static_cast<int>(polygon.Points.size());
    for (int i = 0, j = n - 1; i < n; j = i++) {
        area += (polygon.Points[j].x + polygon.Points[i].x) *
                (polygon.Points[j].y - polygon.Points[i].y);
    }
    return 0.5 * area;
}

std::optional<bool> GeometryUtil::pointInPolygon(const Point& point, const NFP& polygon) {
    if (polygon.Points.size() < 3) {
        return std::nullopt;
    }

    bool inside = false;

    int n = static_cast<int>(polygon.Points.size());
    for (int i = 0, j = n - 1; i < n; j = i++) {
        double xi = polygon.Points[i].x;
        double yi = polygon.Points[i].y;
        double xj = polygon.Points[j].x;
        double yj = polygon.Points[j].y;

        if (_almostEqual(xi, point.x) && _almostEqual(yi, point.y)) {
            return std::nullopt; // on vertex
        }

        if (_onSegment(Point(xi, yi), Point(xj, yj), point)) {
            return std::nullopt; // on edge
        }

        if (_almostEqual(xi, xj) && _almostEqual(yi, yj)) {
            continue; // ignore very small lines
        }

        bool intersects = ((yi > point.y) != (yj > point.y)) &&
                          (point.x < (xj - xi) * (point.y - yi) / (yj - yi) + xi);
        if (intersects) inside = !inside;
    }

    return inside;
}

bool GeometryUtil::_onSegment(const Point& A, const Point& B, const Point& p) {
    // vertical line
    if (_almostEqual(A.x, B.x) && _almostEqual(p.x, A.x)) {
        if (!_almostEqual(p.y, B.y) && !_almostEqual(p.y, A.y) &&
            p.y < std::max(B.y, A.y) && p.y > std::min(B.y, A.y)) {
            return true;
        }
        return false;
    }

    // horizontal line
    if (_almostEqual(A.y, B.y) && _almostEqual(p.y, A.y)) {
        if (!_almostEqual(p.x, B.x) && !_almostEqual(p.x, A.x) &&
            p.x < std::max(B.x, A.x) && p.x > std::min(B.x, A.x)) {
            return true;
        }
        return false;
    }

    // range check
    if ((p.x < A.x && p.x < B.x) || (p.x > A.x && p.x > B.x) ||
        (p.y < A.y && p.y < B.y) || (p.y > A.y && p.y > B.y)) {
        return false;
    }

    // exclude end points
    if ((_almostEqual(p.x, A.x) && _almostEqual(p.y, A.y)) ||
        (_almostEqual(p.x, B.x) && _almostEqual(p.y, B.y))) {
        return false;
    }

    double cross = (p.y - A.y) * (B.x - A.x) - (p.x - A.x) * (B.y - A.y);
    if (std::fabs(cross) > TOL) {
        return false;
    }

    double dot = (p.x - A.x) * (B.x - A.x) + (p.y - A.y) * (B.y - A.y);
    if (dot < 0 || _almostEqual(dot, 0)) {
        return false;
    }

    double len2 = (B.x - A.x) * (B.x - A.x) + (B.y - A.y) * (B.y - A.y);
    if (dot > len2 || _almostEqual(dot, len2)) {
        return false;
    }

    return true;
}

} // namespace nest
