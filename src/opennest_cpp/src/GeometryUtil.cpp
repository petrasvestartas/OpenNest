#include "GeometryUtil.h"
#include <cmath>
#include <algorithm>
#include <numeric>

namespace nest {

std::vector<bool> GeometryUtil::searchMarkedA;

float GeometryUtil::remap(float value, float low2, float high2, float low1, float high1) {
    return low2 + (value - low1) * (high2 - low2) / (high1 - low1);
}

bool GeometryUtil::_withinDistance(const Point& p1, const Point& p2, double distance) {
    double dx = p1.x - p2.x;
    double dy = p1.y - p2.y;
    return (dx * dx + dy * dy) < distance * distance;
}

bool GeometryUtil::_almostEqual(double a, double b, double tolerance) {
    return std::fabs(a - b) < tolerance;
}

Point GeometryUtil::_normalizeVector(const Point& v) {
    if (_almostEqual(v.x * v.x + v.y * v.y, 1)) {
        return v;
    }
    double len = std::sqrt(v.x * v.x + v.y * v.y);
    double inverse = 1.0 / len;
    return Point(v.x * inverse, v.y * inverse);
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

bool GeometryUtil::isRectangle(const NFP& poly, double tolerance) {
    auto bb = getPolygonBounds(poly);

    for (size_t i = 0; i < poly.Points.size(); i++) {
        if (!_almostEqual(poly.Points[i].x, bb.x, tolerance) &&
            !_almostEqual(poly.Points[i].x, bb.x + bb.width, tolerance)) {
            return false;
        }
        if (!_almostEqual(poly.Points[i].y, bb.y, tolerance) &&
            !_almostEqual(poly.Points[i].y, bb.y + bb.height, tolerance)) {
            return false;
        }
    }
    return true;
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
    double offsetx = polygon.offsetx.value_or(0);
    double offsety = polygon.offsety.value_or(0);

    int n = static_cast<int>(polygon.Points.size());
    for (int i = 0, j = n - 1; i < n; j = i++) {
        double xi = polygon.Points[i].x + offsetx;
        double yi = polygon.Points[i].y + offsety;
        double xj = polygon.Points[j].x + offsetx;
        double yj = polygon.Points[j].y + offsety;

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

std::optional<Point> GeometryUtil::_lineIntersect(const Point& A, const Point& B,
                                                      const Point& E, const Point& F,
                                                      bool infinite) {
    double a1 = B.y - A.y;
    double b1 = A.x - B.x;
    double c1 = B.x * A.y - A.x * B.y;
    double a2 = F.y - E.y;
    double b2 = E.x - F.x;
    double c2 = F.x * E.y - E.x * F.y;

    double denom = a1 * b2 - a2 * b1;

    double x = (b1 * c2 - b2 * c1) / denom;
    double y = (a2 * c1 - a1 * c2) / denom;

    if (!std::isfinite(x) || !std::isfinite(y)) {
        return std::nullopt;
    }

    if (!infinite) {
        if (std::fabs(A.x - B.x) > TOL &&
            ((A.x < B.x) ? x < A.x || x > B.x : x > A.x || x < B.x))
            return std::nullopt;
        if (std::fabs(A.y - B.y) > TOL &&
            ((A.y < B.y) ? y < A.y || y > B.y : y > A.y || y < B.y))
            return std::nullopt;
        if (std::fabs(E.x - F.x) > TOL &&
            ((E.x < F.x) ? x < E.x || x > F.x : x > E.x || x < F.x))
            return std::nullopt;
        if (std::fabs(E.y - F.y) > TOL &&
            ((E.y < F.y) ? y < E.y || y > F.y : y > E.y || y < F.y))
            return std::nullopt;
    }

    return Point(x, y);
}

std::optional<double> GeometryUtil::pointDistance(const Point& p, const Point& s1,
                                                   const Point& s2, const Point& normal_in,
                                                   bool infinite) {
    Point normal = _normalizeVector(normal_in);
    Point dir(normal.y, -normal.x);

    double pdot = p.x * dir.x + p.y * dir.y;
    double s1dot = s1.x * dir.x + s1.y * dir.y;
    double s2dot = s2.x * dir.x + s2.y * dir.y;

    double pdotnorm = p.x * normal.x + p.y * normal.y;
    double s1dotnorm = s1.x * normal.x + s1.y * normal.y;
    double s2dotnorm = s2.x * normal.x + s2.y * normal.y;

    if (!infinite) {
        if (((pdot < s1dot || _almostEqual(pdot, s1dot)) && (pdot < s2dot || _almostEqual(pdot, s2dot))) ||
            ((pdot > s1dot || _almostEqual(pdot, s1dot)) && (pdot > s2dot || _almostEqual(pdot, s2dot)))) {
            return std::nullopt;
        }
        if ((_almostEqual(pdot, s1dot) && _almostEqual(pdot, s2dot)) &&
            (pdotnorm > s1dotnorm && pdotnorm > s2dotnorm)) {
            return std::min(pdotnorm - s1dotnorm, pdotnorm - s2dotnorm);
        }
        if ((_almostEqual(pdot, s1dot) && _almostEqual(pdot, s2dot)) &&
            (pdotnorm < s1dotnorm && pdotnorm < s2dotnorm)) {
            return -std::min(s1dotnorm - pdotnorm, s2dotnorm - pdotnorm);
        }
    }

    return -(pdotnorm - s1dotnorm + (s1dotnorm - s2dotnorm) * (s1dot - pdot) / (s1dot - s2dot));
}

std::optional<double> GeometryUtil::segmentDistance(const Point& A, const Point& B,
                                                     const Point& E, const Point& F,
                                                     const Point& direction) {
    Point normal(direction.y, -direction.x);
    Point reverse(-direction.x, -direction.y);

    double dotA = A.x * normal.x + A.y * normal.y;
    double dotB = B.x * normal.x + B.y * normal.y;
    double dotE = E.x * normal.x + E.y * normal.y;
    double dotF = F.x * normal.x + F.y * normal.y;

    double crossA = A.x * direction.x + A.y * direction.y;
    double crossB = B.x * direction.x + B.y * direction.y;
    double crossE = E.x * direction.x + E.y * direction.y;
    double crossF = F.x * direction.x + F.y * direction.y;

    double crossABmin = std::min(crossA, crossB);
    double crossABmax = std::max(crossA, crossB);
    double crossEFmax = std::max(crossE, crossF);
    double crossEFmin = std::min(crossE, crossF);

    double ABmin = std::min(dotA, dotB);
    double ABmax = std::max(dotA, dotB);
    double EFmax = std::max(dotE, dotF);
    double EFmin = std::min(dotE, dotF);

    // Segments that will merely touch at one point
    if (_almostEqual(ABmax, EFmin, TOL) || _almostEqual(ABmin, EFmax, TOL)) {
        return std::nullopt;
    }
    // Segments miss each other completely
    if (ABmax < EFmin || ABmin > EFmax) {
        return std::nullopt;
    }

    double overlap;
    if ((ABmax > EFmax && ABmin < EFmin) || (EFmax > ABmax && EFmin < ABmin)) {
        overlap = 1;
    } else {
        double minMax = std::min(ABmax, EFmax);
        double maxMin = std::max(ABmin, EFmin);
        double maxMax = std::max(ABmax, EFmax);
        double minMin = std::min(ABmin, EFmin);
        overlap = (minMax - maxMin) / (maxMax - minMin);
    }

    double crossABE = (E.y - A.y) * (B.x - A.x) - (E.x - A.x) * (B.y - A.y);
    double crossABF = (F.y - A.y) * (B.x - A.x) - (F.x - A.x) * (B.y - A.y);

    // Lines are colinear
    if (_almostEqual(crossABE, 0) && _almostEqual(crossABF, 0)) {
        Point ABnorm(B.y - A.y, A.x - B.x);
        Point EFnorm(F.y - E.y, E.x - F.x);

        float ABnormlength = static_cast<float>(std::sqrt(ABnorm.x * ABnorm.x + ABnorm.y * ABnorm.y));
        ABnorm.x /= ABnormlength;
        ABnorm.y /= ABnormlength;

        float EFnormlength = static_cast<float>(std::sqrt(EFnorm.x * EFnorm.x + EFnorm.y * EFnorm.y));
        EFnorm.x /= EFnormlength;
        EFnorm.y /= EFnormlength;

        if (std::fabs(ABnorm.y * EFnorm.x - ABnorm.x * EFnorm.y) < TOL &&
            ABnorm.y * EFnorm.y + ABnorm.x * EFnorm.x < 0) {
            double normdot = ABnorm.y * direction.y + ABnorm.x * direction.x;
            if (_almostEqual(normdot, 0, TOL)) {
                return std::nullopt;
            }
            if (normdot < 0) {
                return 0.0;
            }
        }
        return std::nullopt;
    }

    std::vector<double> distances;

    // Coincident points
    if (_almostEqual(dotA, dotE)) {
        distances.push_back(crossA - crossE);
    } else if (_almostEqual(dotA, dotF)) {
        distances.push_back(crossA - crossF);
    } else if (dotA > EFmin && dotA < EFmax) {
        auto d = pointDistance(A, E, F, reverse);
        if (d.has_value() && _almostEqual(d.value(), 0)) {
            auto dB = pointDistance(B, E, F, reverse, true);
            if (dB.has_value() && (dB.value() < 0 || _almostEqual(dB.value() * overlap, 0))) {
                d = std::nullopt;
            }
        }
        if (d.has_value()) {
            distances.push_back(d.value());
        }
    }

    if (_almostEqual(dotB, dotE)) {
        distances.push_back(crossB - crossE);
    } else if (_almostEqual(dotB, dotF)) {
        distances.push_back(crossB - crossF);
    } else if (dotB > EFmin && dotB < EFmax) {
        auto d = pointDistance(B, E, F, reverse);
        if (d.has_value() && _almostEqual(d.value(), 0)) {
            auto dA = pointDistance(A, E, F, reverse, true);
            if (dA.has_value() && (dA.value() < 0 || _almostEqual(dA.value() * overlap, 0))) {
                d = std::nullopt;
            }
        }
        if (d.has_value()) {
            distances.push_back(d.value());
        }
    }

    if (dotE > ABmin && dotE < ABmax) {
        auto d = pointDistance(E, A, B, direction);
        if (d.has_value() && _almostEqual(d.value(), 0)) {
            auto dF = pointDistance(F, A, B, direction, true);
            if (dF.has_value() && (dF.value() < 0 || _almostEqual(dF.value() * overlap, 0))) {
                d = std::nullopt;
            }
        }
        if (d.has_value()) {
            distances.push_back(d.value());
        }
    }

    if (dotF > ABmin && dotF < ABmax) {
        auto d = pointDistance(F, A, B, direction);
        if (d.has_value() && _almostEqual(d.value(), 0)) {
            auto dE = pointDistance(E, A, B, direction, true);
            if (dE.has_value() && (dE.value() < 0 || _almostEqual(dE.value() * overlap, 0))) {
                d = std::nullopt;
            }
        }
        if (d.has_value()) {
            distances.push_back(d.value());
        }
    }

    if (distances.empty()) {
        return std::nullopt;
    }

    return *std::min_element(distances.begin(), distances.end());
}

std::optional<double> GeometryUtil::polygonSlideDistance(NFP A, NFP B, const nVector& direction,
                                                         bool ignoreNegative) {
    double Aoffsetx = A.offsetx.value_or(0);
    double Aoffsety = A.offsety.value_or(0);
    double Boffsetx = B.offsetx.value_or(0);
    double Boffsety = B.offsety.value_or(0);

    A = A.slice(0);
    B = B.slice(0);

    closeLoop(A);
    closeLoop(B);

    Point dir = _normalizeVector(Point(direction.x, direction.y));

    std::optional<double> distance;

    for (int i = 0; i < B.length() - 1; i++) {
        for (int j = 0; j < A.length() - 1; j++) {
            Point A1(A[j].x + Aoffsetx, A[j].y + Aoffsety);
            Point A2(A[j + 1].x + Aoffsetx, A[j + 1].y + Aoffsety);
            Point B1(B[i].x + Boffsetx, B[i].y + Boffsety);
            Point B2(B[i + 1].x + Boffsetx, B[i + 1].y + Boffsety);

            if ((_almostEqual(A1.x, A2.x) && _almostEqual(A1.y, A2.y)) ||
                (_almostEqual(B1.x, B2.x) && _almostEqual(B1.y, B2.y))) {
                continue;
            }

            auto d = segmentDistance(A1, A2, B1, B2, dir);

            if (d.has_value() && (!distance.has_value() || d.value() < distance.value())) {
                if (!ignoreNegative || d.value() > 0 || _almostEqual(d.value(), 0)) {
                    distance = d;
                }
            }
        }
    }
    return distance;
}

std::optional<double> GeometryUtil::polygonProjectionDistance(NFP A, NFP B,
                                                              const Point& direction) {
    double Boffsetx = B.offsetx.value_or(0);
    double Boffsety = B.offsety.value_or(0);
    double Aoffsetx = A.offsetx.value_or(0);
    double Aoffsety = A.offsety.value_or(0);

    A = A.slice(0);
    B = B.slice(0);

    closeLoop(A);
    closeLoop(B);

    auto edgeA = A;
    auto edgeB = B;

    std::optional<double> distance;

    for (int i = 0; i < edgeB.length(); i++) {
        std::optional<double> minprojection;
        for (int j = 0; j < edgeA.length() - 1; j++) {
            Point p(edgeB[i].x + Boffsetx, edgeB[i].y + Boffsety);
            Point s1(edgeA[j].x + Aoffsetx, edgeA[j].y + Aoffsety);
            Point s2(edgeA[j + 1].x + Aoffsetx, edgeA[j + 1].y + Aoffsety);

            if (std::fabs((s2.y - s1.y) * direction.x - (s2.x - s1.x) * direction.y) < TOL) {
                continue;
            }

            auto d = pointDistance(p, s1, s2, direction);
            if (d.has_value() && (!minprojection.has_value() || d.value() < minprojection.value())) {
                minprojection = d;
            }
        }
        if (minprojection.has_value() && (!distance.has_value() || minprojection.value() > distance.value())) {
            distance = minprojection;
        }
    }

    return distance;
}

bool GeometryUtil::intersect(NFP A, NFP B) {
    double Aoffsetx = A.offsetx.value_or(0);
    double Aoffsety = A.offsety.value_or(0);
    double Boffsetx = B.offsetx.value_or(0);
    double Boffsety = B.offsety.value_or(0);

    A = A.slice(0);
    B = B.slice(0);

    for (int i = 0; i < A.length() - 1; i++) {
        for (int j = 0; j < B.length() - 1; j++) {
            Point a1(A[i].x + Aoffsetx, A[i].y + Aoffsety);
            Point a2(A[i + 1].x + Aoffsetx, A[i + 1].y + Aoffsety);
            Point b1(B[j].x + Boffsetx, B[j].y + Boffsety);
            Point b2(B[j + 1].x + Boffsetx, B[j + 1].y + Boffsety);

            int prevbindex = (j == 0) ? B.length() - 1 : j - 1;
            int prevaindex = (i == 0) ? A.length() - 1 : i - 1;
            int nextbindex = (j + 1 == B.length() - 1) ? 0 : j + 2;
            int nextaindex = (i + 1 == A.length() - 1) ? 0 : i + 2;

            // go even further back if we happen to hit on a loop end point
            if (_almostEqual(B[prevbindex].x, B[j].x) && _almostEqual(B[prevbindex].y, B[j].y)) {
                prevbindex = (prevbindex == 0) ? B.length() - 1 : prevbindex - 1;
            }
            if (_almostEqual(A[prevaindex].x, A[i].x) && _almostEqual(A[prevaindex].y, A[i].y)) {
                prevaindex = (prevaindex == 0) ? A.length() - 1 : prevaindex - 1;
            }
            if (_almostEqual(B[nextbindex].x, B[j + 1].x) && _almostEqual(B[nextbindex].y, B[j + 1].y)) {
                nextbindex = (nextbindex == B.length() - 1) ? 0 : nextbindex + 1;
            }
            if (_almostEqual(A[nextaindex].x, A[i + 1].x) && _almostEqual(A[nextaindex].y, A[i + 1].y)) {
                nextaindex = (nextaindex == A.length() - 1) ? 0 : nextaindex + 1;
            }

            Point a0(A[prevaindex].x + Aoffsetx, A[prevaindex].y + Aoffsety);
            Point b0(B[prevbindex].x + Boffsetx, B[prevbindex].y + Boffsety);
            Point a3(A[nextaindex].x + Aoffsetx, A[nextaindex].y + Aoffsety);
            Point b3(B[nextbindex].x + Boffsetx, B[nextbindex].y + Boffsety);

            if (_onSegment(a1, a2, b1) || (_almostEqual(a1.x, b1.x) && _almostEqual(a1.y, b1.y))) {
                auto b0in = pointInPolygon(b0, A);
                auto b2in = pointInPolygon(b2, A);
                if ((b0in.has_value() && b0in.value() && b2in.has_value() && !b2in.value()) ||
                    (b0in.has_value() && !b0in.value() && b2in.has_value() && b2in.value())) {
                    return true;
                }
                continue;
            }

            if (_onSegment(a1, a2, b2) || (_almostEqual(a2.x, b2.x) && _almostEqual(a2.y, b2.y))) {
                auto b1in = pointInPolygon(b1, A);
                auto b3in = pointInPolygon(b3, A);
                if ((b1in.has_value() && b1in.value() && b3in.has_value() && !b3in.value()) ||
                    (b1in.has_value() && !b1in.value() && b3in.has_value() && b3in.value())) {
                    return true;
                }
                continue;
            }

            if (_onSegment(b1, b2, a1) || (_almostEqual(a1.x, b2.x) && _almostEqual(a1.y, b2.y))) {
                auto a0in = pointInPolygon(a0, B);
                auto a2in = pointInPolygon(a2, B);
                if ((a0in.has_value() && a0in.value() && a2in.has_value() && !a2in.value()) ||
                    (a0in.has_value() && !a0in.value() && a2in.has_value() && a2in.value())) {
                    return true;
                }
                continue;
            }

            if (_onSegment(b1, b2, a2) || (_almostEqual(a2.x, b1.x) && _almostEqual(a2.y, b1.y))) {
                auto a1in = pointInPolygon(a1, B);
                auto a3in = pointInPolygon(a3, B);
                if ((a1in.has_value() && a1in.value() && a3in.has_value() && !a3in.value()) ||
                    (a1in.has_value() && !a1in.value() && a3in.has_value() && a3in.value())) {
                    return true;
                }
                continue;
            }

            auto p = _lineIntersect(b1, b2, a1, a2);
            if (p.has_value()) {
                return true;
            }
        }
    }
    return false;
}

bool GeometryUtil::inNfp(const Point& p, const std::vector<NFP>& nfp) {
    if (nfp.empty()) return false;

    for (const auto& n : nfp) {
        for (int j = 0; j < n.length(); j++) {
            if (_almostEqual(p.x, n[j].x) && _almostEqual(p.y, n[j].y)) {
                return true;
            }
        }
    }
    return false;
}

std::vector<NFP> GeometryUtil::noFitPolygonRectangle(const NFP& A, const NFP& B) {
    double minAx = A[0].x, minAy = A[0].y, maxAx = A[0].x, maxAy = A[0].y;
    for (int i = 1; i < A.Length(); i++) {
        if (A[i].x < minAx) minAx = A[i].x;
        if (A[i].y < minAy) minAy = A[i].y;
        if (A[i].x > maxAx) maxAx = A[i].x;
        if (A[i].y > maxAy) maxAy = A[i].y;
    }

    double minBx = B[0].x, minBy = B[0].y, maxBx = B[0].x, maxBy = B[0].y;
    for (int i = 1; i < B.Length(); i++) {
        if (B[i].x < minBx) minBx = B[i].x;
        if (B[i].y < minBy) minBy = B[i].y;
        if (B[i].x > maxBx) maxBx = B[i].x;
        if (B[i].y > maxBy) maxBy = B[i].y;
    }

    if (maxBx - minBx > maxAx - minAx) return {};
    if (maxBy - minBy > maxAy - minAy) return {};

    NFP result;
    result.AddPoint(Point(minAx - minBx + B[0].x, minAy - minBy + B[0].y));
    result.AddPoint(Point(maxAx - maxBx + B[0].x, minAy - minBy + B[0].y));
    result.AddPoint(Point(maxAx - maxBx + B[0].x, maxAy - maxBy + B[0].y));
    result.AddPoint(Point(minAx - minBx + B[0].x, maxAy - maxBy + B[0].y));
    return {result};
}

std::optional<Point> GeometryUtil::searchStartPoint(NFP A, NFP B, bool inside,
                                                        const std::vector<NFP>& NFPlist) {
    A = A.slice(0);
    B = B.slice(0);
    closeLoop(A);
    closeLoop(B);

    // Use local visited flags instead of per-point marked
    if (searchMarkedA.size() < static_cast<size_t>(A.length()))
        searchMarkedA.resize(A.length(), false);

    for (int i = 0; i < A.length() - 1; i++) {
        if (!searchMarkedA[i]) {
            searchMarkedA[i] = true;
            for (int j = 0; j < B.length(); j++) {
                B.offsetx = A[i].x - B[j].x;
                B.offsety = A[i].y - B[j].y;

                std::optional<bool> Binside;
                for (int k = 0; k < B.length(); k++) {
                    auto inpoly = pointInPolygon(
                        Point(B[k].x + B.offsetx.value(), B[k].y + B.offsety.value()), A);
                    if (inpoly.has_value()) {
                        Binside = inpoly;
                        break;
                    }
                }

                if (!Binside.has_value()) {
                    return std::nullopt; // A and B are the same
                }

                Point startPoint(B.offsetx.value(), B.offsety.value());
                if (((Binside.value() && inside) || (!Binside.value() && !inside)) &&
                    !intersect(A, B) && !inNfp(startPoint, NFPlist)) {
                    return startPoint;
                }

                // slide B along vector
                double vx = A[i + 1].x - A[i].x;
                double vy = A[i + 1].y - A[i].y;

                auto d1 = polygonProjectionDistance(A, B, Point(vx, vy));
                auto d2 = polygonProjectionDistance(B, A, Point(-vx, -vy));

                std::optional<double> d;
                if (!d1.has_value() && !d2.has_value()) {
                    // nothing
                } else if (!d1.has_value()) {
                    d = d2;
                } else if (!d2.has_value()) {
                    d = d1;
                } else {
                    d = std::min(d1.value(), d2.value());
                }

                if (!d.has_value() || _almostEqual(d.value(), 0) || d.value() <= 0) {
                    continue;
                }

                double vd2 = vx * vx + vy * vy;
                if (d.value() * d.value() < vd2 && !_almostEqual(d.value() * d.value(), vd2)) {
                    double vd = std::sqrt(vx * vx + vy * vy);
                    vx *= d.value() / vd;
                    vy *= d.value() / vd;
                }

                B.offsetx = B.offsetx.value() + vx;
                B.offsety = B.offsety.value() + vy;

                for (int k = 0; k < B.length(); k++) {
                    auto inpoly = pointInPolygon(
                        Point(B[k].x + B.offsetx.value(), B[k].y + B.offsety.value()), A);
                    if (inpoly.has_value()) {
                        Binside = inpoly;
                        break;
                    }
                }
                startPoint = Point(B.offsetx.value(), B.offsety.value());
                if (((Binside.value() && inside) || (!Binside.value() && !inside)) &&
                    !intersect(A, B) && !inNfp(startPoint, NFPlist)) {
                    return startPoint;
                }
            }
        }
    }

    return std::nullopt;
}

std::vector<NFP> GeometryUtil::noFitPolygon(NFP A, NFP B, bool inside, bool searchEdges) {
    if (A.length() < 3 || B.length() < 3) {
        return {};
    }

    A.offsetx = 0;
    A.offsety = 0;

    // Local visited flags for A and B vertices (replaces per-point marked)
    searchMarkedA.assign(A.length(), false);
    std::vector<bool> markedB(B.length(), false);

    int minAindex = 0;
    double minA = A[0].y;
    for (int i = 1; i < A.length(); i++) {
        if (A[i].y < minA) {
            minA = A[i].y;
            minAindex = i;
        }
    }

    int maxBindex = 0;
    double maxB = B[0].y;
    for (int i = 1; i < B.length(); i++) {
        if (B[i].y > maxB) {
            maxB = B[i].y;
            maxBindex = i;
        }
    }

    std::optional<Point> startpoint;
    if (!inside) {
        startpoint = Point(
            A[minAindex].x - B[maxBindex].x,
            A[minAindex].y - B[maxBindex].y);
    } else {
        startpoint = searchStartPoint(A, B, true);
    }

    std::vector<NFP> NFPlist;

    while (startpoint.has_value()) {
        B.offsetx = startpoint->x;
        B.offsety = startpoint->y;

        nVector* prevvector = nullptr;
        NFP nfp;
        nfp.push(Point(B[0].x + B.offsetx.value(), B[0].y + B.offsety.value()));

        double referencex = B[0].x + B.offsetx.value();
        double referencey = B[0].y + B.offsety.value();
        double startx = referencex;
        double starty = referencey;
        int counter = 0;

        while (counter < 10 * (A.length() + B.length())) {
            std::vector<TouchingItem> touching;

            for (int i = 0; i < A.length(); i++) {
                int nexti = (i == A.length() - 1) ? 0 : i + 1;
                for (int j = 0; j < B.length(); j++) {
                    int nextj = (j == B.length() - 1) ? 0 : j + 1;
                    if (_almostEqual(A[i].x, B[j].x + B.offsetx.value_or(0)) &&
                        _almostEqual(A[i].y, B[j].y + B.offsety.value_or(0))) {
                        touching.push_back(TouchingItem(0, i, j));
                    } else if (_onSegment(A[i], A[nexti],
                               Point(B[j].x + B.offsetx.value(), B[j].y + B.offsety.value()))) {
                        touching.push_back(TouchingItem(1, nexti, j));
                    } else if (_onSegment(
                               Point(B[j].x + B.offsetx.value(), B[j].y + B.offsety.value()),
                               Point(B[nextj].x + B.offsetx.value(), B[nextj].y + B.offsety.value()),
                               A[i])) {
                        touching.push_back(TouchingItem(2, i, nextj));
                    }
                }
            }

            // Generate translation vectors from touching vertices/edges
            std::vector<nVector> vectors;
            for (size_t ti = 0; ti < touching.size(); ti++) {
                auto vertexA = A[touching[ti].A];
                searchMarkedA[touching[ti].A] = true;

                int prevAindex = touching[ti].A - 1;
                int nextAindex = touching[ti].A + 1;
                prevAindex = (prevAindex < 0) ? A.length() - 1 : prevAindex;
                nextAindex = (nextAindex >= A.length()) ? 0 : nextAindex;

                auto prevA = A[prevAindex];
                auto nextA = A[nextAindex];

                auto vertexB = B[touching[ti].B];
                int prevBindex = touching[ti].B - 1;
                int nextBindex = touching[ti].B + 1;
                prevBindex = (prevBindex < 0) ? B.length() - 1 : prevBindex;
                nextBindex = (nextBindex >= B.length()) ? 0 : nextBindex;

                auto prevB = B[prevBindex];
                auto nextB = B[nextBindex];

                if (touching[ti].type == 0) {
                    vectors.push_back(nVector(prevA.x - vertexA.x, prevA.y - vertexA.y, vertexA, prevA));
                    vectors.push_back(nVector(nextA.x - vertexA.x, nextA.y - vertexA.y, vertexA, nextA));
                    vectors.push_back(nVector(vertexB.x - prevB.x, vertexB.y - prevB.y, prevB, vertexB));
                    vectors.push_back(nVector(vertexB.x - nextB.x, vertexB.y - nextB.y, nextB, vertexB));
                } else if (touching[ti].type == 1) {
                    vectors.push_back(nVector(
                        vertexA.x - (vertexB.x + B.offsetx.value()),
                        vertexA.y - (vertexB.y + B.offsety.value()),
                        prevA, vertexA));
                    vectors.push_back(nVector(
                        prevA.x - (vertexB.x + B.offsetx.value()),
                        prevA.y - (vertexB.y + B.offsety.value()),
                        vertexA, prevA));
                } else if (touching[ti].type == 2) {
                    vectors.push_back(nVector(
                        vertexA.x - (vertexB.x + B.offsetx.value()),
                        vertexA.y - (vertexB.y + B.offsety.value()),
                        prevB, vertexB));
                    vectors.push_back(nVector(
                        vertexA.x - (prevB.x + B.offsetx.value()),
                        vertexA.y - (prevB.y + B.offsety.value()),
                        vertexB, prevB));
                }
            }

            // Find best translation vector
            nVector translate;
            bool hasTranslate = false;
            double maxd = 0;

            for (size_t vi = 0; vi < vectors.size(); vi++) {
                if (vectors[vi].x == 0 && vectors[vi].y == 0) {
                    continue;
                }

                if (prevvector != nullptr &&
                    vectors[vi].y * prevvector->y + vectors[vi].x * prevvector->x < 0) {
                    float vectorlength = static_cast<float>(std::sqrt(
                        vectors[vi].x * vectors[vi].x + vectors[vi].y * vectors[vi].y));
                    Point unitv(vectors[vi].x / vectorlength, vectors[vi].y / vectorlength);

                    float prevlength = static_cast<float>(std::sqrt(
                        prevvector->x * prevvector->x + prevvector->y * prevvector->y));
                    Point prevunit(prevvector->x / prevlength, prevvector->y / prevlength);

                    if (std::fabs(unitv.y * prevunit.x - unitv.x * prevunit.y) < 0.0001) {
                        continue;
                    }
                }

                auto d = polygonSlideDistance(A, B, vectors[vi], true);
                double vecd2 = vectors[vi].x * vectors[vi].x + vectors[vi].y * vectors[vi].y;

                if (!d.has_value() || d.value() * d.value() > vecd2) {
                    d = std::sqrt(vecd2);
                }

                if (d.has_value() && d.value() > maxd) {
                    maxd = d.value();
                    translate = vectors[vi];
                    hasTranslate = true;
                }
            }

            if (!hasTranslate || _almostEqual(maxd, 0)) {
                break; // didn't close the loop
            }

            // Store prevvector on stack
            static nVector prevvec_storage;
            prevvec_storage = translate;
            prevvector = &prevvec_storage;

            // Trim
            double vlength2 = translate.x * translate.x + translate.y * translate.y;
            if (maxd * maxd < vlength2 && !_almostEqual(maxd * maxd, vlength2)) {
                double scale = std::sqrt((maxd * maxd) / vlength2);
                translate.x *= scale;
                translate.y *= scale;
            }

            referencex += translate.x;
            referencey += translate.y;

            if (_almostEqual(referencex, startx) && _almostEqual(referencey, starty)) {
                break; // full loop
            }

            bool looped = false;
            if (nfp.length() > 0) {
                for (int i = 0; i < nfp.length() - 1; i++) {
                    if (_almostEqual(referencex, nfp[i].x) && _almostEqual(referencey, nfp[i].y)) {
                        looped = true;
                    }
                }
            }

            if (looped) break;

            nfp.push(Point(referencex, referencey));

            B.offsetx = B.offsetx.value() + translate.x;
            B.offsety = B.offsety.value() + translate.y;

            counter++;
        }

        if (nfp.length() > 0) {
            NFPlist.push_back(nfp);
        }

        if (!searchEdges) {
            break;
        }
        startpoint = searchStartPoint(A, B, inside, NFPlist);
    }

    return NFPlist;
}

void GeometryUtil::closeLoop(NFP& poly) {
    if (poly.length() > 1) {
        const auto& first = poly[0];
        const auto& last = poly[poly.length() - 1];
        if (!_almostEqual(first.x, last.x) || !_almostEqual(first.y, last.y)) {
            poly.push(poly[0]);
        }
    }
}

bool GeometryUtil::isClosed(const NFP& poly) {
    if (poly.length() < 2) return false;
    return _almostEqual(poly[0].x, poly[poly.length() - 1].x) &&
           _almostEqual(poly[0].y, poly[poly.length() - 1].y);
}

} // namespace nest
