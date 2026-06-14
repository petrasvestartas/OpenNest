#pragma once

#include <vector>
#include <optional>
#include <algorithm>
#include <memory>

#include "Point.h"

namespace nest {

/// The core polygon data structure for nesting (mirrors the C# NFP class).
class NFP {
public:
    NFP* sheet = nullptr;  // non-owning; null means not fitted

    double x = 0;
    double y = 0;
    std::optional<int> source;
    float Rotation = 0;
    int Id = 0;
    // Per-part rotation-count override: this part may only use N discrete orientations
    // (360/N step). 0 = inherit the global config.rotations (the default for all parts).
    // 1 = fixed at 0° (no rotation, e.g. grain direction).
    int rotationCount = 0;

    std::vector<Point> Points;
    std::vector<bool> exactFlags;  // per-vertex "exact" flag (lazy: empty = all exact)
    std::vector<std::shared_ptr<NFP>> children;

    // Role of a CHILD loop inside an outer-NFP result. Concave NFPs are often
    // MULTI-REGION: Clipper's Minkowski union can return several disjoint (or
    // pinch-touching) loops. The extra forbidden regions are stored as children
    // with forbiddenLobe=true; children with forbiddenLobe=false are holes
    // (pockets where B legitimately fits). Discarding the extra loops (the old
    // "largest area only" behavior) let parts be placed inside a dropped lobe —
    // including exactly on top of an identical part.
    bool forbiddenLobe = false;

    NFP() = default;

    bool fitted() const { return sheet != nullptr; }

    int Length() const { return static_cast<int>(Points.size()); }
    int length() const { return static_cast<int>(Points.size()); }

    const Point& operator[](int ind) const { return Points[ind]; }
    Point& operator[](int ind) { return Points[ind]; }

    bool getExact(int i) const {
        if (exactFlags.empty()) return true;  // default: all exact
        return exactFlags[i];
    }

    void setExact(int i, bool v) {
        if (exactFlags.empty() && v) return;  // all exact and setting exact — no-op
        if (exactFlags.empty()) {
            exactFlags.assign(Points.size(), true);
        }
        exactFlags[i] = v;
    }

    void AddPoint(const Point& point) {
        Points.push_back(point);
    }

    void reverse() {
        std::reverse(Points.begin(), Points.end());
    }

    void push(const Point& point) {
        Points.push_back(point);
    }
};

} // namespace nest
