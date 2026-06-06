#pragma once

#include <string>
#include <cmath>

namespace nest {

class Point {
public:
    double x = 0;
    double y = 0;
    int id = 0;

    Point() = default;

    Point(double _x, double _y)
        : x(_x), y(_y) {}

    Point Clone() const {
        return Point(*this);
    }

    std::string ToString() const {
        return "x: " + std::to_string(x) + "; y: " + std::to_string(y);
    }

    double DistTo(const Point& other) const {
        return std::sqrt((x - other.x) * (x - other.x) + (y - other.y) * (y - other.y));
    }
};

} // namespace nest
