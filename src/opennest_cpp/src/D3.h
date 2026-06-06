#pragma once

#include <vector>
#include <algorithm>
#include <cmath>

namespace nest {

/// Convex hull computation via monotone chain algorithm.
/// Mirrors C# D3 class.
class D3 {
public:
    /// 2D cross product of vectors AB and AC.
    static double cross(const double* a, const double* b, const double* c) {
        return (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
    }

    /// Upper convex hull indices (monotone chain).
    static std::vector<int> computeUpperHullIndexes(const std::vector<std::vector<double>>& points) {
        int n = static_cast<int>(points.size());
        std::vector<int> indexes(n);
        indexes[0] = 0;
        indexes[1] = 1;
        int size = 2;

        for (int i = 2; i < n; ++i) {
            while (size > 1 && cross(points[indexes[size - 2]].data(),
                                     points[indexes[size - 1]].data(),
                                     points[i].data()) <= 0)
                --size;
            if (size >= static_cast<int>(indexes.size()))
                indexes.resize(size + 1);
            indexes[size++] = i;
        }

        indexes.resize(size);
        return indexes;
    }

    struct HullInfoPoint {
        double x, y;
        int index;
    };

    /// Compute convex hull of a set of 2D points.
    /// Returns hull vertices in order, or empty if < 3 points.
    static std::vector<std::vector<double>> polygonHull(const std::vector<std::vector<double>>& points) {
        int n = static_cast<int>(points.size());
        if (n < 3) return {};

        std::vector<HullInfoPoint> sortedPoints(n);
        for (int i = 0; i < n; ++i) {
            sortedPoints[i] = {points[i][0], points[i][1], i};
        }

        std::sort(sortedPoints.begin(), sortedPoints.end(),
            [](const HullInfoPoint& a, const HullInfoPoint& b) {
                if (a.x != b.x) return a.x < b.x;
                return a.y < b.y;
            });

        std::vector<std::vector<double>> flippedPoints(n);
        for (int i = 0; i < n; ++i) {
            flippedPoints[i] = {sortedPoints[i].x, -sortedPoints[i].y};
        }

        std::vector<std::vector<double>> sortedPts(n);
        for (int i = 0; i < n; ++i) {
            sortedPts[i] = {sortedPoints[i].x, sortedPoints[i].y,
                            static_cast<double>(sortedPoints[i].index)};
        }

        auto upperIndexes = computeUpperHullIndexes(sortedPts);
        auto lowerIndexes = computeUpperHullIndexes(flippedPoints);

        bool skipLeft = (lowerIndexes[0] == upperIndexes[0]);
        bool skipRight = (lowerIndexes.back() == upperIndexes.back());

        std::vector<std::vector<double>> hull;

        // Upper hull in right-to-left order
        for (int i = static_cast<int>(upperIndexes.size()) - 1; i >= 0; --i) {
            hull.push_back(points[sortedPoints[upperIndexes[i]].index]);
        }

        // Lower hull in left-to-right order
        int lstart = skipLeft ? 1 : 0;
        int lend = static_cast<int>(lowerIndexes.size()) - (skipRight ? 1 : 0);
        for (int i = lstart; i < lend; ++i) {
            hull.push_back(points[sortedPoints[lowerIndexes[i]].index]);
        }

        return hull;
    }
};

} // namespace nest
