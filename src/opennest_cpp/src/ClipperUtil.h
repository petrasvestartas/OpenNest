#pragma once

#include <vector>
#include <cmath>
#include "clipper2/clipper.h"
#include "Point.h"
#include "NFP.h"

namespace nest {

/// Clipper2 coordinate conversion utilities.
/// Mirrors C# ClipperUtil class.
class ClipperUtil {
public:
    /// Scale Point array to Clipper2 integer coordinates.
    /// C# uses (long)Math.Round((decimal)x * (decimal)scale).
    /// In C++, std::llround(x * scale) provides equivalent precision for our coordinate range.
    static Clipper2Lib::Path64 ScaleUpPaths(const std::vector<Point>& points, double scale = 1.0) {
        Clipper2Lib::Path64 result;
        result.reserve(points.size());
        for (const auto& pt : points) {
            result.emplace_back(
                std::llround(pt.x * scale),
                std::llround(pt.y * scale)
            );
        }
        return result;
    }

    /// Scale NFP points to Clipper2 integer coordinates.
    static Clipper2Lib::Path64 ScaleUpPaths(const NFP& p, double scale = 1.0) {
        return ScaleUpPaths(p.Points, scale);
    }

    /// Scale + shift NFP points to Clipper2 integer coordinates in one pass.
    /// Eliminates the need to clone+shift an NFP before converting.
    static Clipper2Lib::Path64 ScaleUpPathsShifted(const NFP& p, double scale, double dx, double dy) {
        Clipper2Lib::Path64 result;
        result.reserve(p.Points.size());
        for (const auto& pt : p.Points) {
            result.emplace_back(
                std::llround((pt.x + dx) * scale),
                std::llround((pt.y + dy) * scale)
            );
        }
        return result;
    }
};

} // namespace nest
