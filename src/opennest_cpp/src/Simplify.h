#pragma once

#include <cmath>
#include <optional>
#include "Point.h"
#include "NFP.h"

namespace nest {

/// Douglas-Peucker polygon simplification.
/// Mirrors C# Simplify class.
class Simplify {
public:
    /// Square distance between 2 points.
    static double getSqDist(const Point& p1, const Point& p2) {
        double dx = p1.x - p2.x;
        double dy = p1.y - p2.y;
        return dx * dx + dy * dy;
    }

    /// Square distance from a point to a segment.
    static double getSqSegDist(const Point& p, const Point& p1, const Point& p2) {
        double x = p1.x;
        double y = p1.y;
        double dx = p2.x - x;
        double dy = p2.y - y;

        if (dx != 0 || dy != 0) {
            double t = ((p.x - x) * dx + (p.y - y) * dy) / (dx * dx + dy * dy);
            if (t > 1) {
                x = p2.x;
                y = p2.y;
            } else if (t > 0) {
                x += dx * t;
                y += dy * t;
            }
        }

        dx = p.x - x;
        dy = p.y - y;
        return dx * dx + dy * dy;
    }

    /// Basic distance-based simplification.
    static NFP simplifyRadialDist(const NFP& points, double sqTolerance,
                                  const std::vector<bool>& markedFlags = {}) {
        Point prevPoint = points[0];
        NFP newPoints;
        newPoints.AddPoint(prevPoint);

        Point point = points[0];
        for (int i = 1; i < points.length(); i++) {
            point = points[i];
            bool isMarked = (!markedFlags.empty() && i < static_cast<int>(markedFlags.size())) ? markedFlags[i] : false;
            if (isMarked || getSqDist(point, prevPoint) > sqTolerance) {
                newPoints.AddPoint(point);
                prevPoint = point;
            }
        }

        // Add last point if not already added
        if (getSqDist(prevPoint, point) > 1e-20) {
            newPoints.AddPoint(point);
        }

        return newPoints;
    }

    /// Douglas-Peucker step (recursive).
    static void simplifyDPStep(const NFP& points, int first, int last,
                               double sqTolerance, NFP& simplified) {
        double maxSqDist = sqTolerance;
        int index = -1;

        for (int i = first + 1; i < last; i++) {
            double sqDist = getSqSegDist(points[i], points[first], points[last]);
            if (sqDist > maxSqDist) {
                index = i;
                maxSqDist = sqDist;
            }
        }

        if (maxSqDist > sqTolerance) {
            if (index - first > 1)
                simplifyDPStep(points, first, index, sqTolerance, simplified);
            simplified.push(points[index]);
            if (last - index > 1)
                simplifyDPStep(points, index, last, sqTolerance, simplified);
        }
    }

    /// Simplification using Ramer-Douglas-Peucker algorithm.
    static NFP simplifyDouglasPeucker(const NFP& points, double sqTolerance) {
        int last = points.length() - 1;
        NFP simplified;
        simplified.AddPoint(points[0]);
        simplifyDPStep(points, 0, last, sqTolerance, simplified);
        simplified.push(points[last]);
        return simplified;
    }

    /// Both algorithms combined.
    static NFP simplify(const NFP& points, std::optional<double> tolerance, bool highestQuality,
                        const std::vector<bool>& markedFlags = {}) {
        if (points.length() <= 2) return points;

        double sqTolerance = tolerance.has_value() ? (tolerance.value() * tolerance.value()) : 1.0;

        NFP result = highestQuality ? points : simplifyRadialDist(points, sqTolerance, markedFlags);
        result = simplifyDouglasPeucker(result, sqTolerance);

        return result;
    }
};

} // namespace nest
