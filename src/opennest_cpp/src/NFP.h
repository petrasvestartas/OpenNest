#pragma once

#include <vector>
#include <string>
#include <optional>
#include <cmath>
#include <algorithm>
#include <sstream>
#include <memory>
#include <limits>

#include "Point.h"

namespace nest {

/// Mirrors C# NFP class — the core polygon data structure for nesting.
class NFP {
public:
    int Z = 0;
    NFP* sheet = nullptr;  // non-owning; null means not fitted
    std::string Name;
    bool isBin = false;

    double x = 0;
    double y = 0;
    std::optional<double> offsetx;
    std::optional<double> offsety;
    std::optional<int> source;
    float Rotation = 0;
    int Id = 0;

    std::vector<Point> Points;
    std::vector<bool> exactFlags;  // per-vertex "exact" flag (lazy: empty = all exact)
    std::vector<std::shared_ptr<NFP>> children;

    NFP() = default;

    // --- Properties matching C# ---

    bool fitted() const { return sheet != nullptr; }

    int id() const { return Id; }
    void setId(int v) { Id = v; }

    float rotation() const { return Rotation; }
    void setRotation(float v) { Rotation = v; }

    int Length() const { return static_cast<int>(Points.size()); }
    int length() const { return static_cast<int>(Points.size()); }

    const Point& operator[](int ind) const { return Points[ind]; }
    Point& operator[](int ind) { return Points[ind]; }

    double WidthCalculated() const {
        if (Points.empty()) return 0;
        double maxx = -std::numeric_limits<double>::max();
        double minx = std::numeric_limits<double>::max();
        for (const auto& p : Points) {
            if (p.x > maxx) maxx = p.x;
            if (p.x < minx) minx = p.x;
        }
        return maxx - minx;
    }

    double HeightCalculated() const {
        if (Points.empty()) return 0;
        double maxy = -std::numeric_limits<double>::max();
        double miny = std::numeric_limits<double>::max();
        for (const auto& p : Points) {
            if (p.y > maxy) maxy = p.y;
            if (p.y < miny) miny = p.y;
        }
        return maxy - miny;
    }

    /// Signed area (shoelace) — returns float like C#.
    float Area() const {
        if (Points.size() < 3) return 0;
        float ret = 0;
        for (size_t i = 0; i < Points.size(); i++) {
            size_t j = (i + 1) % Points.size();
            ret += static_cast<float>(Points[i].x * Points[j].y - Points[i].y * Points[j].x);
        }
        return std::fabs(ret / 2.0f);
    }

    /// Center of bounding box after applying rotation and translation.
    /// C# uses System.Drawing.Drawing2D.Matrix — we replicate manually.
    Point Center() const {
        float rad = Rotation * 3.14159265358979323846f / 180.0f;
        float cosA = std::cos(rad);
        float sinA = std::sin(rad);
        float tx = static_cast<float>(x);
        float ty = static_cast<float>(y);

        float maxx = -std::numeric_limits<float>::max();
        float minx = std::numeric_limits<float>::max();
        float maxy = -std::numeric_limits<float>::max();
        float miny = std::numeric_limits<float>::max();

        for (const auto& pt : Points) {
            float px = static_cast<float>(pt.x);
            float py = static_cast<float>(pt.y);
            // Matrix.Rotate then Matrix.Translate:
            // System.Drawing.Drawing2D.Matrix applies translate first in its internal order,
            // but the C# code calls Translate then Rotate, which means:
            // result = point * RotateMatrix * TranslateMatrix
            // i.e., rotate first, then translate
            float rx = px * cosA - py * sinA + tx;
            float ry = px * sinA + py * cosA + ty;
            if (rx > maxx) maxx = rx;
            if (rx < minx) minx = rx;
            if (ry > maxy) maxy = ry;
            if (ry < miny) miny = ry;
        }
        return Point(static_cast<double>(maxx + minx) / 2.0,
                        static_cast<double>(maxy + miny) / 2.0);
    }

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

    NFP slice(int v) const {
        NFP ret;
        for (int i = v; i < length(); i++) {
            ret.Points.push_back(Point(Points[i].x, Points[i].y));
        }
        return ret;
    }

    std::string ToString() const {
        std::ostringstream ss;
        ss << "nfp: id: " << Id << "; source: ";
        if (source.has_value())
            ss << source.value();
        else
            ss << "null";
        ss << "; rotation: " << Rotation
           << "; points: " << Points.size();
        return ss.str();
    }

    std::string GetXml() const {
        std::ostringstream sb;
        sb << "<?xml version=\"1.0\"?>\n";
        sb << "<root>\n";
        sb << "<region>\n";
        for (const auto& p : Points) {
            sb << "<point x=\"" << p.x << "\" y=\"" << p.y << "\"/>\n";
        }
        sb << "</region>\n";
        for (const auto& child : children) {
            sb << "<region>\n";
            for (const auto& cp : child->Points) {
                sb << "<point x=\"" << cp.x << "\" y=\"" << cp.y << "\"/>\n";
            }
            sb << "</region>\n";
        }
        sb << "</root>\n";
        return sb.str();
    }
};

/// Sheet extends NFP with Width/Height.
class Sheet : public NFP {
public:
    double Width = 0;
    double Height = 0;
};

/// RectangleSheet: auto-builds 4-point rectangle from x, y, Width, Height.
class RectangleSheet : public Sheet {
public:
    void Rebuild() {
        Points.clear();
        AddPoint(Point(x, y));
        AddPoint(Point(x + Width, y));
        AddPoint(Point(x + Width, y + Height));
        AddPoint(Point(x, y + Height));
    }
};

} // namespace nest
