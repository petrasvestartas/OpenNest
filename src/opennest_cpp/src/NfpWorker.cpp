#include "NfpWorker.h"
#include <iostream>
#include <cmath>
#include <cstring>
#include <chrono>
#include <thread>
#include <atomic>
#include <unordered_set>

namespace nest {

// Fast hash combining for integer cache keys (replaces string concatenation)
namespace {
    inline uint64_t hashCombine(uint64_t seed, uint64_t v) {
        return seed ^ (v + 0x9e3779b97f4a7c15ULL + (seed << 12) + (seed >> 4));
    }

    inline uint64_t makeProcessKey(int asrc, int bsrc, float aRot, float bRot) {
        uint32_t ar, br;
        std::memcpy(&ar, &aRot, sizeof(float));
        std::memcpy(&br, &bRot, sizeof(float));
        uint64_t k = 0;
        k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(asrc + 1)));
        k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(bsrc + 1)));
        k = hashCombine(k, static_cast<uint64_t>(ar));
        k = hashCombine(k, static_cast<uint64_t>(br));
        return k;
    }

    inline uint64_t makeClipKey(int source, float rotation) {
        uint32_t r;
        std::memcpy(&r, &rotation, sizeof(float));
        return hashCombine(static_cast<uint64_t>(static_cast<uint32_t>(source + 1)),
                           static_cast<uint64_t>(r));
    }
    /// Insert N evenly-spaced points along each polygon edge, preserving vertices.
    NFP densifyPath(const NFP& path, int samplesPerEdge) {
        if (samplesPerEdge <= 0 || path.length() < 2) return path;
        NFP result;
        int n = path.length();
        for (int i = 0; i < n; i++) {
            result.AddPoint(path[i]);
            int next = (i + 1) % n;
            double dx = path[next].x - path[i].x;
            double dy = path[next].y - path[i].y;
            for (int s = 1; s <= samplesPerEdge; s++) {
                double t = static_cast<double>(s) / (samplesPerEdge + 1);
                result.AddPoint(Point(path[i].x + dx * t, path[i].y + dy * t));
            }
        }
        return result;
    }
    struct RotationCandidate {
        std::shared_ptr<NFP> rotatedPart;
        std::vector<std::shared_ptr<NFP>> sheetNfp;
    };

    // ---- fast squeeze-mode hull scoring (S3) -------------------------------------
    // Replicates getHull (D3::polygonHull monotone chain) + polygonArea exactly, but
    // merges two PRE-SORTED point sets per candidate instead of re-sorting every call.

    struct HullPt { double x, y; };

    inline bool hullPtLess(const HullPt& a, const HullPt& b) {
        if (a.x != b.x) return a.x < b.x;
        return a.y < b.y;
    }

    /// Monotone-chain upper hull indices over sorted pts (flipY=true => lower hull).
    /// Identical cross-product expression and pop condition as D3::computeUpperHullIndexes.
    void upperHullIndexes(const std::vector<HullPt>& pts, bool flipY, std::vector<int>& out) {
        int n = static_cast<int>(pts.size());
        out.clear();
        out.resize(2);
        out[0] = 0;
        out[1] = 1;
        int size = 2;
        auto Y = [&](int i) { return flipY ? -pts[i].y : pts[i].y; };
        for (int i = 2; i < n; ++i) {
            while (size > 1) {
                int a = out[size - 2], b = out[size - 1];
                double cr = (pts[b].x - pts[a].x) * (Y(i) - Y(a)) -
                            (Y(b) - Y(a)) * (pts[i].x - pts[a].x);
                if (cr <= 0) --size;
                else break;
            }
            if (size >= static_cast<int>(out.size())) out.resize(size + 1);
            out[size++] = i;
        }
        out.resize(size);
    }

    /// |convex hull area| of sortedA ∪ (sortedB + (dx,dy)). Equivalent to
    /// fabs(polygonArea(*getHull(all points))) — same hull vertex sequence (D3 emits
    /// upper hull right-to-left then lower hull left-to-right) and same shoelace order.
    double hullAreaMergedSorted(const std::vector<HullPt>& A, const std::vector<HullPt>& B,
                                double dx, double dy, std::vector<HullPt>& merged,
                                std::vector<int>& upper, std::vector<int>& lower) {
        merged.clear();
        merged.reserve(A.size() + B.size());
        size_t ia = 0, ib = 0;
        while (ia < A.size() && ib < B.size()) {
            HullPt bb{B[ib].x + dx, B[ib].y + dy};
            if (hullPtLess(bb, A[ia])) { merged.push_back(bb); ++ib; }
            else { merged.push_back(A[ia]); ++ia; }
        }
        for (; ia < A.size(); ++ia) merged.push_back(A[ia]);
        for (; ib < B.size(); ++ib) merged.push_back({B[ib].x + dx, B[ib].y + dy});
        if (merged.size() < 3) return 0;

        upperHullIndexes(merged, false, upper);
        upperHullIndexes(merged, true, lower);
        bool skipLeft = lower[0] == upper[0];
        bool skipRight = lower.back() == upper.back();

        // Walk the hull sequence (no materialization) accumulating the same cyclic
        // shoelace sum as GeometryUtil::polygonArea (j = previous point).
        int lstart = skipLeft ? 1 : 0;
        int lend = static_cast<int>(lower.size()) - (skipRight ? 1 : 0);
        int total = static_cast<int>(upper.size()) + (lend - lstart);
        auto hullAt = [&](int k) -> const HullPt& {
            int u = static_cast<int>(upper.size());
            return k < u ? merged[upper[u - 1 - k]] : merged[lower[lstart + (k - u)]];
        };
        double area = 0;
        for (int i = 0, j = total - 1; i < total; j = i++) {
            const HullPt& pj = hullAt(j);
            const HullPt& pi = hullAt(i);
            area += (pj.x + pi.x) * (pj.y - pi.y);
        }
        return std::fabs(0.5 * area);
    }

    // ---- sliding-compaction ray helpers --------------------------------------------
    // The vertex-replace compaction can only land on feasible-region VERTICES; the
    // score optimum along a slide direction is usually mid-edge. These helpers ray-cast
    // inside the feasible region (first boundary hit) so a part can slide continuously
    // toward the origin (Gomes & Oliveira-style compaction, +1-3pp in the literature).

    /// First crossing t > eps of ray R + t*d with any edge of any region loop
    /// (standard crossing test, half-open vertex rule; collinear edges pass through).
    double rayFirstHit(const std::vector<NFP>& region, double Rx, double Ry,
                       double dx, double dy) {
        double tMin = std::numeric_limits<double>::infinity();
        for (const auto& loop : region) {
            const int n = loop.length();
            for (int i = 0, j = n - 1; i < n; j = i++) {
                const double ax = loop[j].x - Rx, ay = loop[j].y - Ry;
                const double bx = loop[i].x - Rx, by = loop[i].y - Ry;
                const double sa = dx * ay - dy * ax;   // cross(d, a-R)
                const double sb = dx * by - dy * bx;   // cross(d, b-R)
                if (!((sa < 0 && sb >= 0) || (sb < 0 && sa >= 0))) continue;
                const double ex = bx - ax, ey = by - ay;
                const double denom = dx * ey - dy * ex;  // cross(d, b-a)
                if (denom == 0) continue;                 // collinear: slide along it
                const double t = (ax * ey - ay * ex) / denom; // cross(a-R, b-a)/denom
                if (t > 1e-7 && t < tMin) tMin = t;
            }
        }
        return std::isfinite(tMin) ? tMin : 0.0;
    }

    /// Even-odd containment over all region loops. On-boundary (pointInPolygon
    /// nullopt) counts as FEASIBLE — contact landings are valid placements.
    bool regionContains(const std::vector<NFP>& region, double px, double py) {
        Point p(px, py);
        bool inside = false;
        for (const auto& loop : region) {
            auto r = GeometryUtil::pointInPolygon(p, loop);
            if (!r.has_value()) return true;   // on a boundary => feasible
            if (*r) inside = !inside;          // even-odd across loops
        }
        return inside;
    }

    // ---- touching-perimeter (contact-length) scoring ------------------------------
    // Length of the candidate part's boundary that lies ON other placed parts' (or the
    // sheet's) boundary. Used to re-rank near-tie candidates: the bbox/gravity score is
    // indifferent among flush positions (and among ALL positions inside a hole island);
    // contact length prefers snug, interlocking placements (Burke et al. touching
    // perimeter — literature reports +4-8% utilization over bbox-only scoring).

    /// Overlap length between segment (p1,p2) and segment (q1,q2) when collinear
    /// within `tol` (perpendicular distance of both endpoints).
    double segmentContact(const Point& p1, const Point& p2,
                          const Point& q1, const Point& q2, double tol) {
        const double dx = q2.x - q1.x, dy = q2.y - q1.y;
        const double len2 = dx * dx + dy * dy;
        if (len2 < tol * tol) return 0;
        const double invLen = 1.0 / std::sqrt(len2);
        const double d1 = std::fabs(((p1.x - q1.x) * dy - (p1.y - q1.y) * dx) * invLen);
        if (d1 > tol) return 0;
        const double d2 = std::fabs(((p2.x - q1.x) * dy - (p2.y - q1.y) * dx) * invLen);
        if (d2 > tol) return 0;
        double t1 = ((p1.x - q1.x) * dx + (p1.y - q1.y) * dy) / len2;
        double t2 = ((p2.x - q1.x) * dx + (p2.y - q1.y) * dy) / len2;
        if (t1 > t2) std::swap(t1, t2);
        const double o = std::min(t2, 1.0) - std::max(t1, 0.0);
        return o > 0 ? o * std::sqrt(len2) : 0;
    }

    /// Contact length of `part` shifted by (sx,sy) against all placed parts + the sheet.
    double contactLength(const NFP& part, double sx, double sy,
                         const std::vector<std::shared_ptr<NFP>>& placed,
                         const std::vector<PlacementItem>& placements,
                         const NFP& sheet, double tol) {
        double contact = 0;
        const int pn = part.length();
        for (int i = 0, ip = pn - 1; i < pn; ip = i++) {
            Point a1(part[ip].x + sx, part[ip].y + sy);
            Point a2(part[i].x + sx, part[i].y + sy);
            const double aMinX = std::min(a1.x, a2.x) - tol, aMaxX = std::max(a1.x, a2.x) + tol;
            const double aMinY = std::min(a1.y, a2.y) - tol, aMaxY = std::max(a1.y, a2.y) + tol;

            for (size_t m = 0; m < placed.size(); m++) {
                const NFP& ob = *placed[m];
                const double ox = placements[m].x, oy = placements[m].y;
                const int on = ob.length();
                for (int j = 0, jp = on - 1; j < on; jp = j++) {
                    Point b1(ob[jp].x + ox, ob[jp].y + oy);
                    Point b2(ob[j].x + ox, ob[j].y + oy);
                    if (std::max(b1.x, b2.x) < aMinX || std::min(b1.x, b2.x) > aMaxX ||
                        std::max(b1.y, b2.y) < aMinY || std::min(b1.y, b2.y) > aMaxY) continue;
                    contact += segmentContact(a1, a2, b1, b2, tol);
                }
            }
            const int sn = sheet.length();
            for (int j = 0, jp = sn - 1; j < sn; jp = j++) {
                contact += segmentContact(a1, a2, sheet[jp], sheet[j], tol);
            }
        }
        return contact;
    }

    /// Find a strictly interior point of a loop (area-weighted centroid, falling back
    /// to points between edge midpoints and the centroid). Returns false if none found.
    bool interiorPointOf(const Clipper2Lib::Path64& loop, Clipper2Lib::Point64& out) {
        if (loop.size() < 3) return false;
        double cx = 0, cy = 0, a2 = 0;
        for (size_t i = 0, j = loop.size() - 1; i < loop.size(); j = i++) {
            double cr = static_cast<double>(loop[j].x) * loop[i].y -
                        static_cast<double>(loop[i].x) * loop[j].y;
            a2 += cr;
            cx += (static_cast<double>(loop[j].x) + loop[i].x) * cr;
            cy += (static_cast<double>(loop[j].y) + loop[i].y) * cr;
        }
        if (a2 != 0) {
            Clipper2Lib::Point64 c(static_cast<int64_t>(cx / (3.0 * a2)),
                                   static_cast<int64_t>(cy / (3.0 * a2)));
            if (Clipper2Lib::PointInPolygon(c, loop) == Clipper2Lib::PointInPolygonResult::IsInside) {
                out = c;
                return true;
            }
            // fallback: probe between each edge midpoint and the centroid
            for (size_t i = 0, j = loop.size() - 1; i < loop.size(); j = i++) {
                Clipper2Lib::Point64 m((loop[i].x + loop[j].x) / 2, (loop[i].y + loop[j].y) / 2);
                Clipper2Lib::Point64 probe((m.x + c.x) / 2, (m.y + c.y) / 2);
                if (Clipper2Lib::PointInPolygon(probe, loop) ==
                    Clipper2Lib::PointInPolygonResult::IsInside) {
                    out = probe;
                    return true;
                }
            }
        }
        return false;
    }

    /// Clipper2's MinkowskiSum is a boundary sweep (union of edge quads): its result is an
    /// annulus whose interior CW loop can be an ARTIFACT (B in deep overlap inside A —
    /// forbidden, not a hole). A CW loop is a genuine NFP hole (pocket where B fits) only
    /// if B placed inside it does NOT overlap A. Ac = scaled rotated A; negBc = scaled
    /// NEGATED rotated B (exactly as fed to MinkowskiSum); refPt = interior point of the
    /// loop in the same (pre-B[0]-shift) frame.
    bool isGenuineNfpHole(const Clipper2Lib::Path64& Ac, const Clipper2Lib::Path64& negBc,
                          const Clipper2Lib::Point64& refPt) {
        Clipper2Lib::Path64 bAt;
        bAt.reserve(negBc.size());
        // original B vertex = -negB; B placed with its reference (origin) at refPt
        for (auto& p : negBc) bAt.emplace_back(refPt.x - p.x, refPt.y - p.y);
        Clipper2Lib::Paths64 inter = Clipper2Lib::Intersect(
            Clipper2Lib::Paths64{Ac}, Clipper2Lib::Paths64{bAt}, Clipper2Lib::FillRule::NonZero);
        double interArea = 0;
        for (auto& p : inter) interArea += std::fabs(Clipper2Lib::Area(p));
        double aArea = std::fabs(Clipper2Lib::Area(Ac));
        double bArea = std::fabs(Clipper2Lib::Area(bAt));
        return interArea <= 1e-4 * std::min(aArea, bArea);
    }
} // anonymous namespace

// --- Static member initialization ---
bool NfpWorker::EnableCaches = true;
bool NfpWorker::UseParallel = false;

// =============================================================================
// Simple utility methods
// =============================================================================

std::shared_ptr<NFP> NfpWorker::clone(const NFP& nfp) {
    auto newnfp = std::make_shared<NFP>();
    newnfp->source = nfp.source;
    newnfp->forbiddenLobe = nfp.forbiddenLobe;
    newnfp->Points.reserve(nfp.length());
    for (int i = 0; i < nfp.length(); i++) {
        newnfp->AddPoint(Point(nfp[i].x, nfp[i].y));
    }
    if (!nfp.children.empty()) {
        newnfp->children.reserve(nfp.children.size());
        for (size_t i = 0; i < nfp.children.size(); i++) {
            auto& child = nfp.children[i];
            auto newchild = std::make_shared<NFP>();
            newchild->forbiddenLobe = child->forbiddenLobe;
            newchild->Points.reserve(child->length());
            for (int j = 0; j < child->length(); j++) {
                newchild->AddPoint(Point((*child)[j].x, (*child)[j].y));
            }
            newnfp->children.push_back(newchild);
        }
    }
    return newnfp;
}

std::vector<std::shared_ptr<NFP>> NfpWorker::cloneNfp(const std::vector<std::shared_ptr<NFP>>& nfp, bool inner) {
    if (!inner) {
        return { clone(*nfp.front()) };
    }
    std::vector<std::shared_ptr<NFP>> newnfp;
    newnfp.reserve(nfp.size());
    for (size_t i = 0; i < nfp.size(); i++) {
        newnfp.push_back(clone(*nfp[i]));
    }
    return newnfp;
}

NFP NfpWorker::rotatePolygon(const NFP& polygon, float degrees) {
    NFP rotated;
    double angle = degrees * M_PI / 180.0;
    double cosA = std::cos(angle);
    double sinA = std::sin(angle);

    for (int i = 0; i < polygon.length(); i++) {
        double x = polygon[i].x;
        double y = polygon[i].y;
        double x1 = x * cosA - y * sinA;
        double y1 = x * sinA + y * cosA;
        rotated.AddPoint(Point(x1, y1));
    }

    if (!polygon.children.empty()) {
        for (size_t j = 0; j < polygon.children.size(); j++) {
            auto childRotated = std::make_shared<NFP>(rotatePolygon(*polygon.children[j], degrees));
            rotated.children.push_back(childRotated);
        }
    }
    return rotated;
}

std::shared_ptr<NFP> NfpWorker::getHull(const NFP& polygon) {
    std::vector<std::vector<double>> points(polygon.length());
    for (int i = 0; i < polygon.length(); i++) {
        points[i] = {polygon[i].x, polygon[i].y};
    }

    auto hullpoints = D3::polygonHull(points);
    if (hullpoints.empty()) {
        return std::make_shared<NFP>(polygon);
    }

    auto hull = std::make_shared<NFP>();
    for (size_t i = 0; i < hullpoints.size(); i++) {
        hull->AddPoint(Point(hullpoints[i][0], hullpoints[i][1]));
    }
    return hull;
}

NFP NfpWorker::getFrame(const NFP& A) {
    auto bounds = GeometryUtil::getPolygonBounds(A);

    bounds.width *= 1.1;
    bounds.height *= 1.1;
    bounds.x -= 0.5 * (bounds.width - (bounds.width / 1.1));
    bounds.y -= 0.5 * (bounds.height - (bounds.height / 1.1));

    NFP frame;
    frame.push(Point(bounds.x, bounds.y));
    frame.push(Point(bounds.x + bounds.width, bounds.y));
    frame.push(Point(bounds.x + bounds.width, bounds.y + bounds.height));
    frame.push(Point(bounds.x, bounds.y + bounds.height));

    // C# does: frame.children = new List<NFP>() { (NFP)A };
    frame.children.push_back(std::make_shared<NFP>(A));
    frame.source = A.source;
    frame.Rotation = 0;

    return frame;
}

// =============================================================================
// Coordinate conversion
// =============================================================================

NFP NfpWorker::toNestCoordinates(const Clipper2Lib::Path64& polygon, double scale) {
    NFP result;
    for (size_t i = 0; i < polygon.size(); i++) {
        result.AddPoint(Point(
            static_cast<double>(polygon[i].x) / scale,
            static_cast<double>(polygon[i].y) / scale));
    }
    return result;
}

std::vector<Clipper2Lib::Path64> NfpWorker::nfpToClipperCoordinates(NFP& nfp, double clipperScale) {
    std::vector<Clipper2Lib::Path64> clipperNfp;

    // children first. Holes get hole-winding (positive SVGNest area); forbidden lobes
    // (extra disjoint regions of a multi-region NFP) get OUTER winding (negative), so
    // they survive the downstream NonZero union as forbidden area instead of cancelling.
    if (!nfp.children.empty()) {
        for (size_t j = 0; j < nfp.children.size(); j++) {
            double carea = GeometryUtil::polygonArea(*nfp.children[j]);
            if (nfp.children[j]->forbiddenLobe ? (carea > 0) : (carea < 0)) {
                nfp.children[j]->reverse();
            }
            auto childNfp = ClipperUtil::ScaleUpPaths(*nfp.children[j], clipperScale);
            clipperNfp.push_back(childNfp);
        }
    }

    if (GeometryUtil::polygonArea(nfp) > 0) {
        nfp.reverse();
    }

    auto outerNfp = ClipperUtil::ScaleUpPaths(nfp, clipperScale);
    clipperNfp.push_back(outerNfp);

    return clipperNfp;
}

std::vector<Clipper2Lib::Path64> NfpWorker::nfpToClipperWithShift(const NFP& nfp, double clipperScale, double dx, double dy) {
    std::vector<Clipper2Lib::Path64> clipperNfp;

    // children first: holes need positive winding, forbidden lobes (extra regions of a
    // multi-region NFP) need OUTER (negative) winding so the NonZero union keeps them
    // as forbidden area instead of cancelling them like holes.
    if (!nfp.children.empty()) {
        for (size_t j = 0; j < nfp.children.size(); j++) {
            auto& child = *nfp.children[j];
            double carea = GeometryUtil::polygonArea(child);
            bool needReverse = child.forbiddenLobe ? (carea > 0) : (carea < 0);
            if (needReverse) {
                // Reverse during conversion
                Clipper2Lib::Path64 childPath;
                childPath.reserve(child.Points.size());
                for (int k = static_cast<int>(child.Points.size()) - 1; k >= 0; k--) {
                    childPath.emplace_back(
                        std::llround((child.Points[k].x + dx) * clipperScale),
                        std::llround((child.Points[k].y + dy) * clipperScale));
                }
                clipperNfp.push_back(std::move(childPath));
            } else {
                clipperNfp.push_back(ClipperUtil::ScaleUpPathsShifted(child, clipperScale, dx, dy));
            }
        }
    }

    // outer path (needs negative winding)
    if (GeometryUtil::polygonArea(nfp) > 0) {
        // Need to reverse — do it during conversion
        Clipper2Lib::Path64 outerPath;
        outerPath.reserve(nfp.Points.size());
        for (int i = static_cast<int>(nfp.Points.size()) - 1; i >= 0; i--) {
            outerPath.emplace_back(
                std::llround((nfp.Points[i].x + dx) * clipperScale),
                std::llround((nfp.Points[i].y + dy) * clipperScale));
        }
        clipperNfp.push_back(std::move(outerPath));
    } else {
        clipperNfp.push_back(ClipperUtil::ScaleUpPathsShifted(nfp, clipperScale, dx, dy));
    }

    return clipperNfp;
}

std::vector<Clipper2Lib::Path64> NfpWorker::innerNfpToClipperCoordinates(std::vector<std::shared_ptr<NFP>>& nfp, const NestConfig& config) {
    std::vector<Clipper2Lib::Path64> clipperNfp;
    for (size_t i = 0; i < nfp.size(); i++) {
        auto clip = nfpToClipperCoordinates(*nfp[i], config.clipperScale);
        clipperNfp.insert(clipperNfp.end(), clip.begin(), clip.end());
    }
    return clipperNfp;
}

// =============================================================================
// NFP computation — Process2 (Boost.Polygon Minkowski via direct call)
// =============================================================================

std::vector<std::shared_ptr<NFP>> NfpWorker::Process2(NFP& A, NFP& B, int type) {
    uint64_t key = makeProcessKey(A.source.value_or(-1), B.source.value_or(-1), A.Rotation, B.Rotation);

    bool cacheAllow = type != 1;
    {
        std::lock_guard<std::mutex> lock(*cacheMutex_);
        if (cacheAllow && cacheProcess.count(key)) {
            return cacheProcess[key];
        }
    }

    // Flatten A points
    std::vector<double> aa;
    aa.reserve(A.Points.size() * 2);
    for (auto& pt : A.Points) {
        aa.push_back(pt.x);
        aa.push_back(pt.y);
    }

    // Flatten B points
    std::vector<double> bb;
    bb.reserve(B.Points.size() * 2);
    for (auto& pt : B.Points) {
        bb.push_back(pt.x);
        bb.push_back(pt.y);
    }

    // Flatten hole points
    std::vector<std::vector<double>> holes;
    holes.reserve(A.children.size());
    for (auto& child : A.children) {
        std::vector<double> holePts;
        holePts.reserve(child->Points.size() * 2);
        for (auto& pt : child->Points) {
            holePts.push_back(pt.x);
            holePts.push_back(pt.y);
        }
        holes.push_back(std::move(holePts));
    }

    // Compute Minkowski convolution using Clipper2-based implementation
    auto convResult = MinkowskiConvolution::compute(aa, holes, bb);

    callCounter++;

    // Convert result to NFP. The convolution can return SEVERAL outer loops (multi-region
    // concave NFP). The old code concatenated them into one self-intersecting ring; instead
    // keep the largest as the primary outer and the rest as forbiddenLobe children.
    auto ret = std::make_shared<NFP>();
    {
        std::vector<std::shared_ptr<NFP>> outers;
        outers.reserve(convResult.outerPaths.size());
        for (const auto& outerPath : convResult.outerPaths) {
            auto o = std::make_shared<NFP>();
            o->Points.reserve(outerPath.size() / 2);
            for (size_t i = 0; i + 1 < outerPath.size(); i += 2) {
                o->AddPoint(Point(outerPath[i], outerPath[i + 1]));
            }
            if (o->length() >= 3) outers.push_back(o);
        }
        size_t primary = 0;
        double bestArea = -1;
        for (size_t i = 0; i < outers.size(); i++) {
            double a = std::fabs(GeometryUtil::polygonArea(*outers[i]));
            if (a > bestArea) { bestArea = a; primary = i; }
        }
        if (!outers.empty()) {
            ret->Points = outers[primary]->Points;
            for (size_t i = 0; i < outers.size(); i++) {
                if (i == primary) continue;
                outers[i]->forbiddenLobe = true;
                ret->children.push_back(outers[i]);
            }
        }
    }

    // Parse holes into NFP children
    ret->children.reserve(ret->children.size() + convResult.holes.size());
    for (const auto& holePath : convResult.holes) {
        auto child = std::make_shared<NFP>();
        child->Points.reserve(holePath.size() / 2);
        for (size_t i = 0; i + 1 < holePath.size(); i += 2) {
            child->AddPoint(Point(holePath[i], holePath[i + 1]));
        }
        ret->children.push_back(child);
    }

    std::vector<std::shared_ptr<NFP>> res = {ret};
    if (cacheAllow) {
        std::lock_guard<std::mutex> lock(*cacheMutex_);
        cacheProcess[key] = res;
    }
    return res;
}


// =============================================================================
// getOuterNfp
// =============================================================================

std::shared_ptr<NFP> NfpWorker::getOuterNfp(NFP& A, NFP& B, int type, bool inside) {
    std::vector<std::shared_ptr<NFP>> nfp;

    DbCacheKey cacheKey;
    cacheKey.A = A.source;
    cacheKey.B = B.source;
    cacheKey.ARotation = A.Rotation;
    cacheKey.BRotation = B.Rotation;

    auto doc = window.db->find(cacheKey);
    if (!doc.empty()) {
        return doc.front();
    }

    // not found in cache
    if (inside || !A.children.empty()) {
        nfp = Process2(A, B, type);
    } else {
        // Outer NFP (no holes) via Clipper2 Minkowski sum. This matches the C# engine's split
        // (Clipper for the no-holes path, Boost.Polygon only for the holes/inner path) and is the
        // correct, well-tested boundary selection for concave parts — routing this through the Boost
        // convolution with a largest-area loop pick produced wrong boundaries (overlaps) on curved parts.
        auto Ac = ClipperUtil::ScaleUpPaths(A, 10000000);
        auto Bc = ClipperUtil::ScaleUpPaths(B, 10000000);
        for (auto& pt : Bc) {
            pt.x *= -1;
            pt.y *= -1;
        }
        // MUST be MinkowskiSum(A, -B) (pattern=A, path=-B), matching the C# engine. Swapping the
        // arguments is commutative for convex parts but produces a WRONG loop decomposition for
        // CONCAVE parts (Clipper sweeps `pattern` along `path`), which caused overlapping placements.
        auto solution = Clipper2Lib::MinkowskiSum(Ac, Bc, true);

        // Keep ALL loops (multi-region concave NFP): primary = largest CCW loop, other
        // CCW loops = forbidden lobes, CW loops = holes/pockets. See process() — same fix.
        std::shared_ptr<NFP> clipperNfpResult;
        std::optional<double> largestAreaVal;
        int primaryIdx = -1;
        for (size_t i = 0; i < solution.size(); i++) {
            NFP n = toNestCoordinates(solution[i], 10000000);
            double sarea = GeometryUtil::polygonArea(n);
            if (!largestAreaVal.has_value() || *largestAreaVal > sarea) {
                clipperNfpResult = std::make_shared<NFP>(n);
                largestAreaVal = sarea;
                primaryIdx = static_cast<int>(i);
            }
        }
        if (!clipperNfpResult) return nullptr;
        for (size_t i = 0; i < solution.size(); i++) {
            if (static_cast<int>(i) == primaryIdx) continue;
            auto child = std::make_shared<NFP>(toNestCoordinates(solution[i], 10000000));
            if (child->length() < 3) continue;
            if (GeometryUtil::polygonArea(*child) < 0) {
                child->forbiddenLobe = true;   // extra forbidden region — always keep
            } else {
                // CW loop: keep only genuine pockets, drop deep-overlap artifacts
                // (see process() — identical reasoning).
                Clipper2Lib::Point64 ip;
                if (!interiorPointOf(solution[i], ip) || !isGenuineNfpHole(Ac, Bc, ip)) continue;
            }
            clipperNfpResult->children.push_back(child);
        }

        for (int i = 0; i < clipperNfpResult->length(); i++) {
            (*clipperNfpResult)[i].x += B[0].x;
            (*clipperNfpResult)[i].y += B[0].y;
        }
        for (auto& child : clipperNfpResult->children) {
            for (int j = 0; j < child->length(); j++) {
                (*child)[j].x += B[0].x;
                (*child)[j].y += B[0].y;
            }
        }

        auto wrapper = std::make_shared<NFP>();
        wrapper->Points = clipperNfpResult->Points;
        wrapper->children = clipperNfpResult->children;
        nfp = {wrapper};
    }

    if (nfp.empty()) return nullptr;

    auto nfps = nfp.front();
    if (!nfps || nfps->Length() == 0) return nullptr;

    if (!inside && A.source.has_value() && B.source.has_value()) {
        DbCacheKey doc2;
        doc2.A = A.source.value();
        doc2.B = B.source.value();
        doc2.ARotation = A.Rotation;
        doc2.BRotation = B.Rotation;
        doc2.nfp = nfp;
        window.db->insert(doc2);
    }

    return nfps;
}

// =============================================================================
// getInnerNfp
// =============================================================================

std::vector<std::shared_ptr<NFP>> NfpWorker::getInnerNfp(NFP& A, NFP& B, int type, const NestConfig& config) {
    if (A.source.has_value() && B.source.has_value()) {
        DbCacheKey cacheKey;
        cacheKey.A = A.source.value();
        cacheKey.B = B.source.value();
        cacheKey.ARotation = 0;
        cacheKey.BRotation = B.Rotation;
        // Type=1 marks INNER-fit entries. Sheet and part `source` numbering both start
        // at 0, so without the Type tag the IFP key (sheet0, partJ, 0, rot) COLLIDES
        // with the outer pair-NFP key (part0@0°, partJ@rot) — whichever inserted first
        // poisoned the other (wrong feasible regions / lost placements).
        cacheKey.Type = 1;

        auto res = window.db->find(cacheKey, true);
        if (!res.empty()) {
            // Return CLONES, not the cached objects: callers (nfpToClipperCoordinates) reverse the
            // winding in place. Sharing the cached object would corrupt it under concurrent placeParts
            // (parallel population) and is the contract the cache documents ("callers clone before
            // modifying"). The clone is cheap (a handful of inner NFPs per part).
            return cloneNfp(res, true);
        }
    }

    // Use the frame approach (matching original Boost-based algorithm):
    // 1. Create a bounding rectangle with A as its hole
    // 2. Compute the Minkowski convolution via Process2 → MinkowskiConvolution
    // 3. The children (holes) of the result are the IFP paths
    NFP frame = getFrame(A);

    auto nfpResult = getOuterNfp(frame, B, type, true);
    if (!nfpResult || nfpResult->children.empty()) {
        return {};
    }

    // For each hole in A, compute the forbidden zone and subtract from the IFP.
    // The forbidden zone is the Minkowski sum H ⊕ (-B): the set of reference
    // positions where part B overlaps hole H.
    //
    // We compute this as the convex hull of all pairwise vertex sums
    // {h - b : h ∈ H, b ∈ B}. For convex shapes this is exact; for non-convex
    // shapes it's conservative (slightly larger forbidden zone), which is safe.
    //
    // The forbidden zone is inflated by spacing to compensate for the reference-
    // point displacement between offset-adjusted and original polygons. The
    // placement algorithm computes positions for adjusted (expanded) parts, but
    // the SVG renders original parts, creating a shift of up to spacing/2*sqrt(2)
    // at polygon corners.
    if (!A.children.empty()) {
        double scale = MinkowskiConvolution::kScale;

        // Scale B to int64
        Clipper2Lib::Path64 bPath;
        bPath.reserve(B.length());
        for (int j = 0; j < B.length(); j++) {
            bPath.push_back(Clipper2Lib::Point64(
                static_cast<int64_t>(std::round(scale * B[j].x)),
                static_cast<int64_t>(std::round(scale * B[j].y))));
        }

        // Convert IFP children to int64 paths for kScale-precision operations
        Clipper2Lib::Paths64 ifpPaths;
        for (auto& child : nfpResult->children) {
            if (child->forbiddenLobe) continue;   // extra outer regions are not IFP loops
            Clipper2Lib::Path64 p;
            p.reserve(child->length());
            for (int j = 0; j < child->length(); j++) {
                p.push_back(Clipper2Lib::Point64(
                    static_cast<int64_t>(std::round(scale * ((*child)[j].x - B[0].x))),
                    static_cast<int64_t>(std::round(scale * ((*child)[j].y - B[0].y)))));
            }
            ifpPaths.push_back(std::move(p));
        }

        for (size_t i = 0; i < A.children.size(); i++) {
            auto& holeChild = *A.children[i];

            // Scale hole to int64
            Clipper2Lib::Path64 holePath;
            holePath.reserve(holeChild.length());
            for (int j = 0; j < holeChild.length(); j++) {
                holePath.push_back(Clipper2Lib::Point64(
                    static_cast<int64_t>(std::round(scale * holeChild[j].x)),
                    static_cast<int64_t>(std::round(scale * holeChild[j].y))));
            }

            // Compute all pairwise sums: h + (-b)
            std::vector<Clipper2Lib::Point64> pairwiseSums;
            pairwiseSums.reserve(holePath.size() * bPath.size());
            for (auto& h : holePath) {
                for (auto& b : bPath) {
                    pairwiseSums.push_back(Clipper2Lib::Point64(h.x - b.x, h.y - b.y));
                }
            }

            // Convex hull via Andrew's monotone chain
            auto cross64 = [](const Clipper2Lib::Point64& O,
                              const Clipper2Lib::Point64& A,
                              const Clipper2Lib::Point64& B) -> double {
                return static_cast<double>(A.x - O.x) * static_cast<double>(B.y - O.y)
                     - static_cast<double>(A.y - O.y) * static_cast<double>(B.x - O.x);
            };

            std::sort(pairwiseSums.begin(), pairwiseSums.end(),
                [](const Clipper2Lib::Point64& a, const Clipper2Lib::Point64& b) {
                    return a.x < b.x || (a.x == b.x && a.y < b.y);
                });
            pairwiseSums.erase(
                std::unique(pairwiseSums.begin(), pairwiseSums.end(),
                    [](const Clipper2Lib::Point64& a, const Clipper2Lib::Point64& b) {
                        return a.x == b.x && a.y == b.y;
                    }),
                pairwiseSums.end());

            int n = static_cast<int>(pairwiseSums.size());
            if (n < 3) continue;

            std::vector<Clipper2Lib::Point64> hull(2 * n);
            int k = 0;
            for (int j = 0; j < n; j++) {
                while (k >= 2 && cross64(hull[k-2], hull[k-1], pairwiseSums[j]) <= 0) k--;
                hull[k++] = pairwiseSums[j];
            }
            int lower_size = k + 1;
            for (int j = n - 2; j >= 0; j--) {
                while (k >= lower_size && cross64(hull[k-2], hull[k-1], pairwiseSums[j]) <= 0) k--;
                hull[k++] = pairwiseSums[j];
            }
            hull.resize(k - 1);

            Clipper2Lib::Paths64 forbiddenZone = { Clipper2Lib::Path64(hull.begin(), hull.end()) };

            // Inflate forbidden zone by spacing to compensate for the rendering
            // displacement between adjusted and original polygon reference points
            if (config.spacing > 0 && !forbiddenZone.empty() && !forbiddenZone[0].empty()) {
                double inflateDelta = config.spacing * scale;
                Clipper2Lib::ClipperOffset co;
                co.AddPaths(forbiddenZone, Clipper2Lib::JoinType::Miter, Clipper2Lib::EndType::Polygon);
                Clipper2Lib::Paths64 inflated;
                co.Execute(inflateDelta, inflated);
                if (!inflated.empty()) {
                    forbiddenZone = inflated;
                }
            }

            // Subtract from IFP
            if (!forbiddenZone.empty() && !forbiddenZone[0].empty()) {
                Clipper2Lib::Clipper64 diff;
                diff.AddSubject(ifpPaths);
                diff.AddClip(forbiddenZone);
                Clipper2Lib::Paths64 result;
                diff.Execute(Clipper2Lib::ClipType::Difference, Clipper2Lib::FillRule::NonZero, result);
                ifpPaths = result;
            }
        }

        if (ifpPaths.empty()) {
            return {};
        }

        // Convert back to nest coordinates, shifted by B[0]
        std::vector<std::shared_ptr<NFP>> f;
        for (auto& path : ifpPaths) {
            auto nfp = std::make_shared<NFP>();
            for (auto& pt : path) {
                nfp->AddPoint(Point(
                    static_cast<double>(pt.x) / scale + B[0].x,
                    static_cast<double>(pt.y) / scale + B[0].y));
            }
            if (nfp->length() > 0) {
                f.push_back(nfp);
            }
        }

        if (f.empty()) return {};

        if (A.source.has_value() && B.source.has_value()) {
            DbCacheKey doc;
            doc.A = A.source.value();
            doc.B = B.source.value();
            doc.ARotation = 0;
            doc.BRotation = B.Rotation;
            doc.Type = 1;   // inner-fit entry (see find above — avoids outer-key collision)
            doc.nfp = f;
            window.db->insert(doc, true);
        }
        return f;
    }

    // No holes: IFP is just the children from the frame NFP result
#ifdef NFP_DEBUG
    {
        double mnx=1e18,mny=1e18,mxx=-1e18,mxy=-1e18;
        for (auto& ch : nfpResult->children) for (int q=0;q<ch->length();q++){auto&p=(*ch)[q];
            mnx=std::min(mnx,p.x);mny=std::min(mny,p.y);mxx=std::max(mxx,p.x);mxy=std::max(mxy,p.y);}
        double abx0=1e18,aby0=1e18,abx1=-1e18,aby1=-1e18;
        for(int q=0;q<A.length();q++){mnx=mnx;double x=A[q].x,y=A[q].y;abx0=std::min(abx0,x);aby0=std::min(aby0,y);abx1=std::max(abx1,x);aby1=std::max(aby1,y);}
        double bbx0=1e18,bby0=1e18,bbx1=-1e18,bby1=-1e18;
        for(int q=0;q<B.length();q++){double x=B[q].x,y=B[q].y;bbx0=std::min(bbx0,x);bby0=std::min(bby0,y);bbx1=std::max(bbx1,x);bby1=std::max(bby1,y);}
        fprintf(stderr,"[IFP] B.src=%d rot=%.0f B[0]=(%.1f,%.1f) Bbbox=[%.1f,%.1f]-[%.1f,%.1f] Abbox=[%.1f,%.1f]-[%.1f,%.1f] IFPbbox=[%.1f,%.1f]-[%.1f,%.1f]\n",
            B.source.value_or(-1),B.Rotation,B[0].x,B[0].y,bbx0,bby0,bbx1,bby1,abx0,aby0,abx1,aby1,mnx,mny,mxx,mxy);
    }
#endif
    // Drop forbiddenLobe children — extra outer regions of the frame NFP are not IFP loops.
    std::vector<std::shared_ptr<NFP>> ifpLoops;
    ifpLoops.reserve(nfpResult->children.size());
    for (auto& child : nfpResult->children) {
        if (!child->forbiddenLobe) ifpLoops.push_back(child);
    }
    if (ifpLoops.empty()) return {};

    if (A.source.has_value() && B.source.has_value()) {
        DbCacheKey doc;
        doc.A = A.source.value();
        doc.B = B.source.value();
        doc.ARotation = 0;
        doc.BRotation = B.Rotation;
        doc.Type = 1;   // inner-fit entry (see find above — avoids outer-key collision)
        doc.nfp = ifpLoops;
        window.db->insert(doc, true);
    }
    return ifpLoops;
}

// =============================================================================
// placeParts — the core placement algorithm
// =============================================================================

SheetPlacement NfpWorker::placeParts(std::vector<std::shared_ptr<NFP>> sheets, std::vector<std::shared_ptr<NFP>> parts, const NestConfig& config, int nestindex) {
    SheetPlacement nullResult;
    nullResult.fitness = std::nullopt;
    if (sheets.empty()) return nullResult;

    int i, j, k, m, n;
    double totalsheetarea = 0;

    // rotate parts by given rotation
    std::vector<std::shared_ptr<NFP>> rotated;
    for (i = 0; i < static_cast<int>(parts.size()); i++) {
        // Per-part rotation override ENFORCEMENT: GA order-mutation swaps placements but
        // not the positional rotation genes, so a constrained part can inherit an angle
        // from another part's set. Snap the gene to this part's own N-orientation grid
        // before rotating — the constraint then holds for every evaluated candidate.
        float geneRot = parts[i]->Rotation;
        if (parts[i]->rotationCount > 0) {
            const float step = 360.0f / parts[i]->rotationCount;
            geneRot = std::floor(geneRot / step + 0.5f) * step;
            geneRot = std::fmod(geneRot, 360.0f);
            if (geneRot < 0) geneRot += 360.0f;
        }
        auto r = std::make_shared<NFP>(rotatePolygon(*parts[i], geneRot));
        r->Rotation = geneRot;
        r->source = parts[i]->source;
        r->Id = parts[i]->Id;
        r->rotationCount = parts[i]->rotationCount;
        rotated.push_back(r);
    }
    parts = rotated;

    std::vector<SheetPlacementItem> allplacements;
    double fitness = 0;
    double sheetarea = -1;
    int totalPlaced = 0;
    int totalParts = static_cast<int>(parts.size());

    while (!parts.empty()) {
        std::vector<std::shared_ptr<NFP>> placed;
        std::vector<PlacementItem> placements;

        // open a new sheet
        auto sheet = sheets.front();
        sheets.erase(sheets.begin());
        sheetarea = std::fabs(GeometryUtil::polygonArea(*sheet));
        totalsheetarea += sheetarea;
        fitness += config.faithful ? 1.0 : sheetarea;   // canonical C#: +1 per opened sheet

        std::unordered_map<uint64_t, ClipCacheItem> clipCache;
        Clipper2Lib::Paths64 combinedNfp;
        bool error = false;
        std::vector<Clipper2Lib::Path64> clipperSheetNfp;
        std::optional<double> minwidth;
        PlacementItem* positionPtr = nullptr;
        PlacementItem position;
        bool hasPosition = false;
        std::optional<double> minarea;

        // Reusable buffers — hoisted outside per-part loop to avoid re-allocation
        NFP allpoints;
        std::vector<NFP> finalNfpList;
        std::unordered_map<uint64_t, std::vector<Clipper2Lib::Path64>> sheetNfpClipperCache;

        for (i = 0; i < static_cast<int>(parts.size()); i++) {
            auto part = parts[i];

            // === Phase A: Collect all valid rotation candidates ===
            std::vector<RotationCandidate> rotationCandidates;
            {
                auto trialPart = part; // don't modify part/parts[i] yet
                // SAFETY CAP (tryAllRotations only): with All-Rotations on we evaluate EVERY
                // orientation of EVERY part at EVERY placement, so cost/cache grow ~ rotations x parts^2.
                // A large "Rotations" value (e.g. 360/3600) then explodes the NFP cache -> OOM, which
                // shows up as "hung then died". Cap the number of orientations actually tried to 8 and
                // derive the angular STEP from the SAME capped count, so the 8 orientations stay evenly
                // spread over 360 (every 45). When tryAllRotations is OFF this is a no-op: effRotations
                // == config.rotations, the step is unchanged, and the loop still breaks at the first
                // valid rotation -> the fast path is byte-identical.
                // Per-part rotation override: a part with rotationCount > 0 is restricted
                // to ITS OWN N discrete orientations (e.g. grain-constrained rectangles
                // at 4 while freeform parts roam the global setting). 0 = global default.
                int effRotations = part->rotationCount > 0 ? part->rotationCount : config.rotations;
                if (config.tryAllRotations && effRotations > 8) effRotations = 8;
                const float rotStep = 360.0f / effRotations;
                for (j = 0; j < effRotations; j++) {
                    auto innerNfp = getInnerNfp(*sheet, *trialPart, 0, config);

                    if (!innerNfp.empty()) {
                        if (innerNfp[0]->length() == 0) {
                            throw std::runtime_error("sheetNfp[0] has 0 points");
                        }
                        // Memoize the clipper conversion of the sheet IFP per (source,
                        // rotation) — it is recomputed identically for every duplicate
                        // part / repeated rotation otherwise. Populated HERE (serial
                        // Phase A) so the candidate threads only read the map.
                        uint64_t ck = makeClipKey(trialPart->source.value_or(-1), trialPart->Rotation);
                        if (!sheetNfpClipperCache.count(ck)) {
                            sheetNfpClipperCache[ck] = innerNfpToClipperCoordinates(innerNfp, config);
                        }
                        rotationCandidates.push_back({trialPart, innerNfp});
                        if (!config.tryAllRotations) break;
                    }

                    // Rotate to next step
                    auto r = std::make_shared<NFP>(rotatePolygon(*trialPart, rotStep));
                    r->Rotation = trialPart->Rotation + rotStep;
                    r->source = trialPart->source;
                    r->Id = trialPart->Id;
                    r->rotationCount = trialPart->rotationCount;
                    // >= so 360 normalizes to 0 — a float key of 360 is a DIFFERENT bit
                    // pattern than 0 and silently misses every rotation-keyed cache
                    // (DbCacheKey, clipCache).
                    if (r->Rotation >= 360.0f) {
                        r->Rotation = std::fmod(r->Rotation, 360.0f);
                    }
                    trialPart = r;
                }
            }

            // part unplaceable at any rotation
            if (rotationCandidates.empty()) continue;

            hasPosition = false;

            if (placed.empty()) {
                // first placement — top left corner across all rotation candidates
                for (auto& cand : rotationCandidates) {
                    auto& candPart = cand.rotatedPart;
                    auto& sheetNfp = cand.sheetNfp;
                    for (j = 0; j < static_cast<int>(sheetNfp.size()); j++) {
                        for (k = 0; k < sheetNfp[j]->length(); k++) {
                            if (!hasPosition ||
                                (((*sheetNfp[j])[k].x - (*candPart)[0].x) < position.x) ||
                                (GeometryUtil::_almostEqual((*sheetNfp[j])[k].x - (*candPart)[0].x, position.x)
                                 && (((*sheetNfp[j])[k].y - (*candPart)[0].y) < position.y))) {
                                position = PlacementItem();
                                position.x = (*sheetNfp[j])[k].x - (*candPart)[0].x;
                                position.y = (*sheetNfp[j])[k].y - (*candPart)[0].y;
                                position.id = candPart->Id;
                                position.rotation = candPart->Rotation;
                                position.source = candPart->source.value();
                                hasPosition = true;
                                part = candPart; // track winner
                            }
                        }
                    }
                }

                if (!hasPosition) {
                    throw std::runtime_error("position null");
                }
                if (std::getenv("NFP_VERIFY_PLACE") != nullptr) {
                    std::cerr << "[NFP_VERIFY_PLACE] first id=" << part->Id
                              << " src=" << part->source.value_or(-1)
                              << " rot=" << part->Rotation
                              << " pos=(" << position.x << "," << position.y << ")\n";
                }
                parts[i] = part;
                placements.push_back(position);
                placed.push_back(part);
                totalPlaced++;
                continue;
            }

            // === Phase B: Score all rotation candidates, pick global best ===
            // Pre-compute allpoints for scoring (same for all rotations)
            allpoints.Points.clear();
            for (m = 0; m < static_cast<int>(placed.size()); m++) {
                for (n = 0; n < placed[m]->length(); n++) {
                    allpoints.AddPoint(Point(
                        (*placed[m])[n].x + placements[m].x,
                        (*placed[m])[n].y + placements[m].y));
                }
            }

            PolygonBounds allbounds;
            bool useHull = false;
            std::vector<HullPt> sortedAllPts;   // squeeze: layout hull, pre-sorted once per part
            if (config.placementType == PlacementTypeEnum::gravity || config.placementType == PlacementTypeEnum::box) {
                allbounds = GeometryUtil::getPolygonBounds(allpoints);
            } else {
                auto hullResult = getHull(allpoints);
                allpoints = *hullResult;
                useHull = true;
                sortedAllPts.reserve(allpoints.length());
                for (n = 0; n < allpoints.length(); n++)
                    sortedAllPts.push_back({allpoints[n].x, allpoints[n].y});
                std::sort(sortedAllPts.begin(), sortedAllPts.end(), hullPtLess);
            }

            // Q4b: anchor the gravity pull at the SHEET ORIGIN, not the layout centroid
            // (non-faithful). Among candidates with equal bbox growth — e.g. every
            // position inside a hole island — the centroid pull scattered parts
            // mid-region and fragmented holes; an origin pull packs bottom-left both
            // globally and inside islands.
            double gravityCx = config.faithful ? (allbounds.x + allbounds.width / 2.0) : 0.0;
            double gravityCy = config.faithful ? (allbounds.y + allbounds.height / 2.0) : 0.0;

            minwidth = std::nullopt;
            minarea = std::nullopt;
            std::optional<double> minx;
            std::optional<double> miny;
            std::shared_ptr<NFP> winnerPart;
            Clipper2Lib::Paths64 winnerCombinedNfp;

            // Per-candidate result for parallel evaluation
            struct CandResult {
                bool valid = false;
                double bestArea = 0;
                double bestWidth = 0;
                double bestX = 0;
                double bestY = 0;
                PlacementItem position;
                std::shared_ptr<NFP> part;
                Clipper2Lib::Paths64 combinedNfp;
            };

            int numCands = static_cast<int>(rotationCandidates.size());
            std::vector<CandResult> candResults(numCands);

            auto evalCandidate = [&](int ci) {
                auto& cand = rotationCandidates[ci];
                auto& candPart = cand.rotatedPart;
                auto& sheetNfp = cand.sheetNfp;
                auto& result = candResults[ci];

                // Sheet IFP in clipper coordinates — memoized in Phase A (read-only here)
                const auto& candClipperSheetNfp =
                    sheetNfpClipperCache.at(makeClipKey(candPart->source.value_or(-1), candPart->Rotation));
                (void)sheetNfp;

                // Build outer NFP union — INCREMENTALLY (deepnest's clipCache scheme):
                // a cached entry for this (source, rotation) key holds the union over
                // placed[0..index]; seed the op with it and union only the parts placed
                // since. Re-unioning placed[index] is idempotent (deepnest exact
                // semantics). Cached unions are Execute outputs (already orientation-
                // normalized) — added as Subjects directly, NEVER re-passed through
                // nfpToClipperWithShift. clipCache is read here concurrently by the
                // rotation-candidate threads and written ONLY on the main thread after
                // join(), so the map is never mutated during the parallel section.
                Clipper2Lib::Clipper64 clipperOp;
                bool localError = false;
                int startIndex = 0;

                if (EnableCaches) {
                    auto itc = clipCache.find(makeClipKey(candPart->source.value_or(-1), candPart->Rotation));
                    if (itc != clipCache.end() && itc->second.index < static_cast<int>(placed.size())) {
                        clipperOp.AddSubject(itc->second.nfpp);
                        startIndex = itc->second.index;
                    }
                }

                for (int jj = startIndex; jj < static_cast<int>(placed.size()); jj++) {
                    auto nfpRaw = getOuterNfp(*placed[jj], *candPart, 0);
                    if (!nfpRaw) {
                        localError = true;
                        break;
                    }
                    auto clipperNfpPaths = nfpToClipperWithShift(*nfpRaw, config.clipperScale, placements[jj].x, placements[jj].y);
                    clipperOp.AddSubject(clipperNfpPaths);
                }

                if (localError) return;

                Clipper2Lib::Paths64 unionResult;
                if (!clipperOp.Execute(Clipper2Lib::ClipType::Union, Clipper2Lib::FillRule::NonZero, unionResult)) {
                    return;
                }

                // Difference with sheet polygon
                Clipper2Lib::Clipper64 clipper2;
                clipper2.AddClip(unionResult);
                Clipper2Lib::Paths64 sheetSubject(candClipperSheetNfp.begin(), candClipperSheetNfp.end());
                clipper2.AddSubject(sheetSubject);

                Clipper2Lib::Paths64 _finalNfp;
                if (!clipper2.Execute(Clipper2Lib::ClipType::Difference, Clipper2Lib::FillRule::EvenOdd, _finalNfp)) {
                    return;
                }

                if (_finalNfp.empty()) return;

                std::vector<NFP> localFinalNfpList;
                for (int jj = 0; jj < static_cast<int>(_finalNfp.size()); jj++) {
                    localFinalNfpList.push_back(toNestCoordinates(_finalNfp[jj], config.clipperScale));
                }

                if (config.edgeSamples > 0) {
                    for (auto& nf : localFinalNfpList)
                        nf = densifyPath(nf, config.edgeSamples);
                }

                // Compute part bounds for this rotation
                PolygonBounds partbounds;
                if (!useHull) {
                    NFP partpoints;
                    for (int mm = 0; mm < candPart->length(); mm++) {
                        partpoints.AddPoint(Point((*candPart)[mm].x, (*candPart)[mm].y));
                    }
                    partbounds = GeometryUtil::getPolygonBounds(partpoints);
                }

                // Score all candidate positions — find local best
                std::optional<double> localMinArea;
                std::optional<double> localMinX;
                std::optional<double> localMinY;

                const bool bboxScoring = (config.placementType == PlacementTypeEnum::gravity ||
                                          config.placementType == PlacementTypeEnum::box);
                // Hoisted scalar bounds of the placed-parts layout (box/gravity scoring is
                // pure min/max arithmetic — no per-candidate polygon allocation).
                const double abMinX = allbounds.x, abMinY = allbounds.y;
                const double abMaxX = allbounds.x + allbounds.width;
                const double abMaxY = allbounds.y + allbounds.height;

                // Squeeze: part points sorted once per rotation candidate; per-position
                // hull = merge of the two pre-sorted sets (thread-local scratch).
                std::vector<HullPt> sortedPartPts, hullScratch;
                std::vector<int> upperScratch, lowerScratch;
                if (!bboxScoring) {
                    sortedPartPts.reserve(candPart->length());
                    for (int mm = 0; mm < candPart->length(); mm++)
                        sortedPartPts.push_back({(*candPart)[mm].x, (*candPart)[mm].y});
                    std::sort(sortedPartPts.begin(), sortedPartPts.end(), hullPtLess);
                }

                // Contact re-ranking (non-faithful, bbox modes): collect every candidate,
                // then among near-ties of the primary score pick the max contact length.
                struct CandPos { double sx, sy, area, width; };
                const bool useContact = bboxScoring && !config.faithful;
                std::vector<CandPos> contactCands;
                if (useContact) contactCands.reserve(256);

                for (int jj = 0; jj < static_cast<int>(localFinalNfpList.size()); jj++) {
                    auto& nf = localFinalNfpList[jj];
                    for (int kk = 0; kk < nf.length(); kk++) {
                        const double sx = nf[kk].x - (*candPart)[0].x;
                        const double sy = nf[kk].y - (*candPart)[0].y;

                        double area = 0;
                        double rectWidth = 0;

                        if (bboxScoring) {
                            const double cminx = std::min(abMinX, partbounds.x + sx);
                            const double cminy = std::min(abMinY, partbounds.y + sy);
                            const double cmaxx = std::max(abMaxX, partbounds.x + partbounds.width + sx);
                            const double cmaxy = std::max(abMaxY, partbounds.y + partbounds.height + sy);
                            rectWidth = cmaxx - cminx;
                            const double rectHeight = cmaxy - cminy;

                            if (config.placementType == PlacementTypeEnum::gravity) {
                                area = 2.0 * rectWidth + 1.0 * rectHeight;
                            } else {
                                area = rectWidth * rectHeight;
                            }

                            if (config.gravityWeight > 0) {
                                double partCx = partbounds.x + partbounds.width / 2.0 + sx;
                                double partCy = partbounds.y + partbounds.height / 2.0 + sy;
                                double dist = std::sqrt((partCx - gravityCx) * (partCx - gravityCx) +
                                                        (partCy - gravityCy) * (partCy - gravityCy));
                                area += config.gravityWeight * dist;
                            }
                        } else {
                            area = hullAreaMergedSorted(sortedAllPts, sortedPartPts, sx, sy,
                                                        hullScratch, upperScratch, lowerScratch);
                        }

                        if (useContact) {
                            contactCands.push_back({sx, sy, area, rectWidth});
                            continue;
                        }

                        if (!localMinArea.has_value() ||
                            area < *localMinArea ||
                            (GeometryUtil::_almostEqual(*localMinArea, area) && (!localMinX.has_value() || sx < *localMinX)) ||
                            (GeometryUtil::_almostEqual(*localMinArea, area) && localMinX.has_value() && GeometryUtil::_almostEqual(sx, *localMinX) && sy < localMinY)) {
                            localMinArea = area;
                            result.bestArea = area;
                            result.bestWidth = bboxScoring ? rectWidth : 0;
                            result.bestX = sx;
                            result.bestY = sy;
                            result.valid = true;
                            if (!localMinX.has_value() || sx < *localMinX) {
                                localMinX = sx;
                            }
                            if (!localMinY.has_value() || sy < *localMinY) {
                                localMinY = sy;
                            }
                        }
                    }
                }

                if (useContact && !contactCands.empty()) {
                    // Primary score: min area. Near-ties (within 0.2%) re-ranked by
                    // touching perimeter; remaining ties by min x, then min y.
                    double minArea = contactCands[0].area;
                    for (auto& c : contactCands) minArea = std::min(minArea, c.area);
                    const double tieTol = 0.01 * std::max(1.0, std::fabs(minArea));

                    const CandPos* best = nullptr;
                    double bestContact = -1;
                    int evals = 0;
                    for (auto& c : contactCands) {
                        if (c.area > minArea + tieTol) continue;
                        double ct = (evals++ < 64)
                                        ? contactLength(*candPart, c.sx, c.sy, placed,
                                                        placements, *sheet, 1e-3)
                                        : 0;   // cap: degenerate flush runs stay bounded
                        if (!best || ct > bestContact + 1e-9 ||
                            (std::fabs(ct - bestContact) <= 1e-9 &&
                             (c.sx < best->sx || (GeometryUtil::_almostEqual(c.sx, best->sx) && c.sy < best->sy)))) {
                            best = &c;
                            bestContact = ct;
                        }
                    }
                    result.bestArea = best->area;
                    result.bestWidth = best->width;
                    result.bestX = best->sx;
                    result.bestY = best->sy;
                    result.valid = true;
                }

                // Fill the winning PlacementItem + union copy ONCE after the scan (identical
                // outcome — previously rebuilt per improving candidate).
                if (result.valid) {
                    PlacementItem shiftvector;
                    shiftvector.id = candPart->Id;
                    shiftvector.x = result.bestX;
                    shiftvector.y = result.bestY;
                    shiftvector.source = candPart->source.value();
                    shiftvector.rotation = candPart->Rotation;
                    result.position = shiftvector;
                    result.part = candPart;
                    result.combinedNfp = std::move(unionResult);
                }
            };

            // Evaluate rotation candidates in parallel
            if (UseParallel && numCands > 1) {
                int numThreads = std::min(static_cast<int>(std::thread::hardware_concurrency()), numCands);
                if (numThreads < 1) numThreads = 1;

                std::vector<std::thread> threads;
                for (int ci = 1; ci < numCands; ci++) {
                    threads.emplace_back(evalCandidate, ci);
                }
                evalCandidate(0);
                for (auto& t : threads) t.join();
            } else {
                for (int ci = 0; ci < numCands; ci++) {
                    evalCandidate(ci);
                }
            }

            // Pick global best from candidate results
            int winnerCi = -1;
            for (int ci = 0; ci < numCands; ci++) {
                auto& r = candResults[ci];
                if (!r.valid) continue;

                if (!minarea.has_value() ||
                    r.bestArea < *minarea ||
                    (GeometryUtil::_almostEqual(*minarea, r.bestArea) && (!minx.has_value() || r.bestX < *minx)) ||
                    (GeometryUtil::_almostEqual(*minarea, r.bestArea) && minx.has_value() && GeometryUtil::_almostEqual(r.bestX, *minx) && r.bestY < miny)) {
                    minarea = r.bestArea;
                    minwidth = r.bestWidth;
                    position = r.position;
                    hasPosition = true;
                    winnerPart = r.part;
                    winnerCi = ci;
                    if (!minx.has_value() || r.bestX < *minx) {
                        minx = r.bestX;
                    }
                    if (!miny.has_value() || r.bestY < *miny) {
                        miny = r.bestY;
                    }
                }
            }
            // end rotation candidates evaluation

            if (hasPosition) {
                // Debug guard (env NFP_VERIFY_PLACE=1): the winning position's reference point
                // must lie ON the combined-NFP boundary (contact) or outside — strictly inside
                // means the part overlaps an already-placed part. Dumps the offending geometry.
                if (std::getenv("NFP_VERIFY_PLACE") != nullptr) {
                    winnerCombinedNfp = candResults[winnerCi].combinedNfp;
                    Clipper2Lib::Point64 refPt(
                        static_cast<int64_t>(std::llround(((*winnerPart)[0].x + position.x) * config.clipperScale)),
                        static_cast<int64_t>(std::llround(((*winnerPart)[0].y + position.y) * config.clipperScale)));
                    int winding = 0;
                    bool onBoundary = false;
                    for (auto& pth : winnerCombinedNfp) {
                        auto rIn = Clipper2Lib::PointInPolygon(refPt, pth);
                        if (rIn == Clipper2Lib::PointInPolygonResult::IsOn) { onBoundary = true; break; }
                        if (rIn == Clipper2Lib::PointInPolygonResult::IsInside)
                            winding += Clipper2Lib::Area(pth) > 0 ? 1 : -1;
                    }
                    std::cerr << "[NFP_VERIFY_PLACE] place id=" << winnerPart->Id
                              << " src=" << winnerPart->source.value_or(-1)
                              << " rot=" << winnerPart->Rotation
                              << " pos=(" << position.x << "," << position.y << ")"
                              << " placed_before=" << placed.size()
                              << " unionPaths=" << winnerCombinedNfp.size()
                              << " refPt=" << (onBoundary ? "ON" : (winding != 0 ? "INSIDE" : "OUT"))
                              << "\n";
                    if (!onBoundary && winding != 0) {
                        std::cerr << "[NFP_VERIFY_PLACE] BUG: part src=" << winnerPart->source.value_or(-1)
                                  << " rot=" << winnerPart->Rotation
                                  << " placed STRICTLY INSIDE combined NFP at (" << position.x
                                  << ", " << position.y << "), placed_count=" << placed.size()
                                  << ", union paths=" << winnerCombinedNfp.size() << "\n";
                        for (auto& pth : winnerCombinedNfp) {
                            std::cerr << "  union path area=" << (Clipper2Lib::Area(pth) /
                                         (config.clipperScale * config.clipperScale)) << ":";
                            for (auto& pt : pth)
                                std::cerr << " " << (pt.x / config.clipperScale) << ","
                                          << (pt.y / config.clipperScale);
                            std::cerr << "\n";
                        }
                        std::cerr << "  ref point: " << ((*winnerPart)[0].x + position.x) << ","
                                  << ((*winnerPart)[0].y + position.y) << "\n";
                    }
                }

                // Cache EVERY valid candidate's union (not just the winner): each union
                // covers placed[0..size-1] for its (source, rotation) key, so duplicate-
                // quantity parts and losing rotations of later parts seed from it and
                // union only the parts placed since (deepnest's incremental clipCache).
                // Written on the main thread strictly after the candidate threads joined.
                if (EnableCaches) {
                    for (auto& r : candResults) {
                        if (!r.valid) continue;
                        ClipCacheItem cacheItem;
                        cacheItem.index = static_cast<int>(placed.size()) - 1;
                        cacheItem.nfpp = std::move(r.combinedNfp);
                        clipCache[makeClipKey(r.part->source.value_or(-1), r.part->Rotation)] =
                            std::move(cacheItem);
                    }
                }

                part = winnerPart;
                parts[i] = winnerPart;
                placed.push_back(part);
                totalPlaced++;
                placements.push_back(position);
            }
        }

        // Compaction: re-place each part into tighter positions
        if (!placed.empty() && config.compactionPasses > 0) {
            compactPlacements(placed, placements, *sheet, config);
        }

        // Recompute minwidth/minarea after compaction
        if (!placed.empty() && config.compactionPasses > 0) {
            // Recompute minwidth/minarea from compacted layout
            NFP allPtsFinal;
            for (i = 0; i < static_cast<int>(placed.size()); i++) {
                for (j = 0; j < placed[i]->length(); j++) {
                    allPtsFinal.AddPoint(Point(
                        (*placed[i])[j].x + placements[i].x,
                        (*placed[i])[j].y + placements[i].y));
                }
            }
            auto bounds = GeometryUtil::getPolygonBounds(allPtsFinal);
            minwidth = bounds.width;
            if (config.placementType == PlacementTypeEnum::gravity) {
                minarea = 2.0 * bounds.width + 1.0 * bounds.height;
            } else {
                minarea = bounds.width * bounds.height;
            }
        }

        if (!minwidth.has_value()) {
            // no placement
        } else {
            // canonical C#: only minwidth/sheetarea (minarea is the local position-selection metric)
            fitness += config.faithful ? (*minwidth / sheetarea) : ((*minwidth / sheetarea) + *minarea);
        }

        // Remove placed parts
        for (i = 0; i < static_cast<int>(placed.size()); i++) {
            for (j = 0; j < static_cast<int>(parts.size()); j++) {
                if (parts[j].get() == placed[i].get()) {
                    parts.erase(parts.begin() + j);
                    break;
                }
            }
        }

        if (!placements.empty()) {
            SheetPlacementItem item;
            item.sheetId = sheet->Id;
            item.sheetSource = sheet->source.value();
            item.sheetplacements = placements;
            allplacements.push_back(item);
        } else {
            break;
        }

        if (sheets.empty()) break;
    }

    // Parts that couldn't be placed — penalty. Canonical C#: +2 per unplaced part.
    for (i = 0; i < static_cast<int>(parts.size()); i++) {
        fitness += config.faithful ? 2.0 : (100000000.0 * (std::fabs(GeometryUtil::polygonArea(*parts[i])) / totalsheetarea));
    }

    SheetPlacement result;
    result.placements = {allplacements};
    result.fitness = fitness;

    return result;
}

// =============================================================================
// Compaction — re-place parts into tighter positions using cached NFPs
// =============================================================================

void NfpWorker::compactPlacements(
    std::vector<std::shared_ptr<NFP>>& placed,
    std::vector<PlacementItem>& placements,
    NFP& sheet, const NestConfig& config)
{
    int numParts = static_cast<int>(placed.size());
    if (numParts <= 1) return;

    for (int pass = 0; pass < config.compactionPasses; pass++) {
        bool anyImproved = false;

        // Re-place last-placed parts first (they got worst positions)
        for (int ci = numParts - 1; ci >= 0; ci--) {
            auto& part = placed[ci];

            // 1. Get inner NFP (sheet feasible region for this part)
            auto sheetNfp = getInnerNfp(sheet, *part, 0, config);
            if (sheetNfp.empty() || sheetNfp[0]->length() == 0) continue;

            auto clipperSheetNfp = innerNfpToClipperCoordinates(sheetNfp, config);

            // 2. Build union of outer NFPs for all OTHER placed parts
            Clipper2Lib::Clipper64 clipper;
            bool error = false;

            for (int oj = 0; oj < numParts; oj++) {
                if (oj == ci) continue;

                auto nfpResult = getOuterNfp(*placed[oj], *part, 0);
                if (!nfpResult) { error = true; break; }

                auto clipperNfpPaths = nfpToClipperWithShift(*nfpResult, config.clipperScale, placements[oj].x, placements[oj].y);
                clipper.AddSubject(clipperNfpPaths);
            }

            if (error) continue;

            // Union of all outer NFPs
            Clipper2Lib::Paths64 unionResult;
            if (!clipper.Execute(Clipper2Lib::ClipType::Union, Clipper2Lib::FillRule::NonZero, unionResult)) {
                continue;
            }

            // 3. Feasible region = sheetNfp - union(outerNfps)
            Clipper2Lib::Clipper64 clipper2;
            clipper2.AddClip(unionResult);
            Clipper2Lib::Paths64 sheetSubject(clipperSheetNfp.begin(), clipperSheetNfp.end());
            clipper2.AddSubject(sheetSubject);

            Clipper2Lib::Paths64 feasibleClip;
            if (!clipper2.Execute(Clipper2Lib::ClipType::Difference, Clipper2Lib::FillRule::EvenOdd, feasibleClip)) {
                continue;
            }
            if (feasibleClip.empty()) continue;

            // Convert to nest coordinates
            std::vector<NFP> feasibleList;
            for (int fi = 0; fi < static_cast<int>(feasibleClip.size()); fi++) {
                feasibleList.push_back(toNestCoordinates(feasibleClip[fi], config.clipperScale));
            }

            // Densify edges
            if (config.edgeSamples > 0) {
                for (auto& nf : feasibleList)
                    nf = densifyPath(nf, config.edgeSamples);
            }

            // 4. Score all candidates (same logic as main loop)
            // Build allpoints from all OTHER placed parts
            NFP allpoints;
            for (int oj = 0; oj < numParts; oj++) {
                if (oj == ci) continue;
                for (int m = 0; m < placed[oj]->length(); m++) {
                    allpoints.AddPoint(Point(
                        (*placed[oj])[m].x + placements[oj].x,
                        (*placed[oj])[m].y + placements[oj].y));
                }
            }

            PolygonBounds allbounds = GeometryUtil::getPolygonBounds(allpoints);
            NFP partpoints;
            for (int m = 0; m < part->length(); m++) {
                partpoints.AddPoint(Point((*part)[m].x, (*part)[m].y));
            }
            PolygonBounds partbounds = GeometryUtil::getPolygonBounds(partpoints);

            // Q4b: origin-anchored gravity (see placeParts) — same pull during compaction.
            double gravityCx = config.faithful ? (allbounds.x + allbounds.width / 2.0) : 0.0;
            double gravityCy = config.faithful ? (allbounds.y + allbounds.height / 2.0) : 0.0;

            std::optional<double> bestArea;
            std::optional<double> bestX, bestY;
            double bestSx = 0, bestSy = 0;
            bool foundBetter = false;

            const bool bboxScoring = (config.placementType == PlacementTypeEnum::gravity ||
                                      config.placementType == PlacementTypeEnum::box);
            const double abMinX = allbounds.x, abMinY = allbounds.y;
            const double abMaxX = allbounds.x + allbounds.width;
            const double abMaxY = allbounds.y + allbounds.height;

            // Squeeze: pre-sort the other-parts points and this part's points once per ci.
            std::vector<HullPt> sortedAllPts, sortedPartPts, hullScratch;
            std::vector<int> upperScratch, lowerScratch;
            if (!bboxScoring) {
                sortedAllPts.reserve(allpoints.length());
                for (int m = 0; m < allpoints.length(); m++)
                    sortedAllPts.push_back({allpoints[m].x, allpoints[m].y});
                std::sort(sortedAllPts.begin(), sortedAllPts.end(), hullPtLess);
                sortedPartPts.reserve(part->length());
                for (int m = 0; m < part->length(); m++)
                    sortedPartPts.push_back({(*part)[m].x, (*part)[m].y});
                std::sort(sortedPartPts.begin(), sortedPartPts.end(), hullPtLess);
            }

            // Score one candidate shift (scalar min/max for box/gravity — identical math
            // to the old 8-point polygon + getPolygonBounds, no allocation).
            auto scoreShift = [&](double sx, double sy) -> double {
                double area;
                if (bboxScoring) {
                    const double cminx = std::min(abMinX, partbounds.x + sx);
                    const double cminy = std::min(abMinY, partbounds.y + sy);
                    const double cmaxx = std::max(abMaxX, partbounds.x + partbounds.width + sx);
                    const double cmaxy = std::max(abMaxY, partbounds.y + partbounds.height + sy);
                    const double rw = cmaxx - cminx, rh = cmaxy - cminy;
                    area = (config.placementType == PlacementTypeEnum::gravity)
                               ? 2.0 * rw + 1.0 * rh
                               : rw * rh;
                    if (config.gravityWeight > 0) {
                        double partCx = partbounds.x + partbounds.width / 2.0 + sx;
                        double partCy = partbounds.y + partbounds.height / 2.0 + sy;
                        double dist = std::sqrt((partCx - gravityCx) * (partCx - gravityCx) +
                                                (partCy - gravityCy) * (partCy - gravityCy));
                        area += config.gravityWeight * dist;
                    }
                } else {
                    // Squeeze mode: hull-area scoring (rewards interlocking)
                    area = hullAreaMergedSorted(sortedAllPts, sortedPartPts, sx, sy,
                                                hullScratch, upperScratch, lowerScratch);
                }
                return area;
            };

            for (int fi = 0; fi < static_cast<int>(feasibleList.size()); fi++) {
                auto& nf = feasibleList[fi];
                for (int k = 0; k < nf.length(); k++) {
                    const double sx = nf[k].x - (*part)[0].x;
                    const double sy = nf[k].y - (*part)[0].y;
                    double area = scoreShift(sx, sy);

                    if (!bestArea.has_value() ||
                        area < *bestArea ||
                        (GeometryUtil::_almostEqual(*bestArea, area) && (!bestX.has_value() || sx < *bestX)) ||
                        (GeometryUtil::_almostEqual(*bestArea, area) && bestX.has_value() && GeometryUtil::_almostEqual(sx, *bestX) && sy < bestY)) {
                        bestArea = area;
                        bestSx = sx;
                        bestSy = sy;
                        foundBetter = true;
                        if (!bestX.has_value() || sx < *bestX) bestX = sx;
                        if (!bestY.has_value() || sy < *bestY) bestY = sy;
                    }
                }
            }

            // 5. Update if better position found
            if (foundBetter) {
                double curArea = scoreShift(placements[ci].x, placements[ci].y);
                if (*bestArea < curArea - 1e-6) {
                    PlacementItem bestPos;
                    bestPos.id = part->Id;
                    bestPos.x = bestSx;
                    bestPos.y = bestSy;
                    bestPos.source = part->source.value();
                    bestPos.rotation = part->Rotation;
                    placements[ci] = bestPos;
                    anyImproved = true;
                }
            }

            // 6. Sliding refinement (env NFP_SLIDE=1, non-faithful): from the current
            // position, slide continuously toward the origin along {left, down,
            // diagonal} to the first feasible-boundary hit, accepting the best-scoring
            // point along the ray. Vertex-replace can only reach feasible-region
            // VERTICES; the score optimum along a ray is usually mid-edge.
            // feasibleList depends only on the OTHER parts, so it stays valid while
            // this part slides.
            if (config.slideCompaction && !config.faithful) {
                static const double dirs[3][2] = {
                    {-1.0, 0.0}, {0.0, -1.0}, {-0.70710678118654752, -0.70710678118654752}};
                bool slid = true;
                for (int round = 0; slid && round < 4; round++) {
                    slid = false;
                    for (const auto& d : dirs) {
                        const double Rx = (*part)[0].x + placements[ci].x;
                        const double Ry = (*part)[0].y + placements[ci].y;
                        double tExit = rayFirstHit(feasibleList, Rx, Ry, d[0], d[1]);
                        if (tExit <= 1e-6) continue;
                        // the ray may start ON the boundary heading outside — verify
                        if (!regionContains(feasibleList,
                                            Rx + 0.5 * tExit * d[0], Ry + 0.5 * tExit * d[1]))
                            continue;

                        const double curScore = scoreShift(placements[ci].x, placements[ci].y);
                        // presample (handles non-convex box-mode score), then refine
                        double tBest = 0, sBest = curScore;
                        const int NS = 16;
                        for (int s = 1; s <= NS; s++) {
                            const double t = tExit * s / NS;
                            const double sc = scoreShift(placements[ci].x + t * d[0],
                                                         placements[ci].y + t * d[1]);
                            if (sc < sBest) { sBest = sc; tBest = t; }
                        }
                        // local ternary refinement around the best sample (gravity
                        // score is convex along the ray; box benefits too locally)
                        double lo = std::max(0.0, tBest - tExit / NS);
                        double hi = std::min(tExit, tBest + tExit / NS);
                        for (int it = 0; it < 24; it++) {
                            const double m1 = lo + (hi - lo) / 3.0;
                            const double m2 = hi - (hi - lo) / 3.0;
                            const double f1 = scoreShift(placements[ci].x + m1 * d[0],
                                                         placements[ci].y + m1 * d[1]);
                            const double f2 = scoreShift(placements[ci].x + m2 * d[0],
                                                         placements[ci].y + m2 * d[1]);
                            if (f1 <= f2) hi = m2; else lo = m1;
                        }
                        {
                            const double tm = 0.5 * (lo + hi);
                            const double sm = scoreShift(placements[ci].x + tm * d[0],
                                                         placements[ci].y + tm * d[1]);
                            if (sm < sBest) { sBest = sm; tBest = tm; }
                        }

                        if (tBest <= 1e-7 || sBest >= curScore - 1e-6) continue;
                        // landing feasibility insurance: halve on failure (rounding)
                        double tLand = tBest;
                        int halvings = 0;
                        while (halvings < 6 &&
                               !regionContains(feasibleList, Rx + tLand * d[0], Ry + tLand * d[1])) {
                            tLand *= 0.5;
                            halvings++;
                        }
                        if (halvings >= 6) continue;
                        if (scoreShift(placements[ci].x + tLand * d[0],
                                       placements[ci].y + tLand * d[1]) >= curScore - 1e-6)
                            continue;

                        placements[ci].x += tLand * d[0];
                        placements[ci].y += tLand * d[1];
                        anyImproved = true;
                        slid = true;
                    }
                }
            }
        }

        if (!anyImproved) break;
    }
}

// =============================================================================
// Worker coordination
// =============================================================================

void NfpWorker::sync() {
    auto start = std::chrono::high_resolution_clock::now();

    auto placement = placeParts(
        data.sheets,
        std::vector<std::shared_ptr<NFP>>(parts_vec.begin(), parts_vec.end()),
        data.config, index);

    auto end = std::chrono::high_resolution_clock::now();
    LastPlacePartTime = std::chrono::duration_cast<std::chrono::milliseconds>(end - start).count();

    placement.index = data.index;
    if (ResponseAction) {
        ResponseAction(placement);
    }
}

void NfpWorker::BackgroundStart(DataInfo d) {
    this->data = d;
    index = d.index;
    auto& individual = d.individual;
    auto& parts = individual.placements;
    auto& rotations = individual.Rotation;
    auto& ids = d.ids;
    auto& sources = d.sources;
    auto& children = d.children;

    for (size_t i = 0; i < parts.size(); i++) {
        parts[i]->Rotation = rotations[i];
        parts[i]->Id = ids[i];
        parts[i]->source = sources[i];
        if (!d.config.simplify) {
            parts[i]->children = children[i];
        }
    }

    for (size_t i = 0; i < d.sheets.size(); i++) {
        d.sheets[i]->Id = d.sheetids[i];
        d.sheets[i]->source = d.sheetsources[i];
        d.sheets[i]->children = d.sheetchildren[i];
    }

    // preprocess — O(1) dedup via key set (inpairs was a linear scan inside the pair loop)
    std::vector<NfpPair> pairs;
    std::unordered_set<uint64_t> pairKeys;

    for (size_t i = 0; i < parts.size(); i++) {
        auto& B = parts[i];
        for (size_t j = 0; j < i; j++) {
            auto& A = parts[j];
            NfpPair key;
            key.A = A.get();
            key.B = B.get();
            key.ARotation = A->Rotation;
            key.BRotation = B->Rotation;
            key.Asource = A->source.value();
            key.Bsource = B->source.value();

            DbCacheKey doc;
            doc.A = A->source.value();
            doc.B = B->source.value();
            doc.ARotation = A->Rotation;
            doc.BRotation = B->Rotation;

            uint64_t pk = makeProcessKey(key.Asource, key.Bsource, key.ARotation, key.BRotation);
            if (pairKeys.insert(pk).second && !window.db->has(doc)) {
                pairs.push_back(key);
            }
        }
    }

    this->parts_vec = std::vector<std::shared_ptr<NFP>>(parts.begin(), parts.end());

    if (!pairs.empty()) {
        auto ret1 = pmapDeepNest(pairs);
        thenDeepNest(ret1, parts);
    } else {
        sync();
    }
}

SheetPlacement NfpWorker::evaluateCandidate(DataInfo d) {
    // Same logic as BackgroundStart + sync, but on LOCAL transient state so it is safe to run
    // concurrently for many candidates on one worker (the caches it touches are mutex-protected).
    auto& individual = d.individual;
    auto& parts = individual.placements;   // deep-cloned by caller -> safe to mutate
    auto& rotations = individual.Rotation;
    auto& ids = d.ids;
    auto& sources = d.sources;
    auto& children = d.children;

    for (size_t i = 0; i < parts.size(); i++) {
        parts[i]->Rotation = rotations[i];
        parts[i]->Id = ids[i];
        parts[i]->source = sources[i];
        if (!d.config.simplify) {
            parts[i]->children = children[i];  // caller supplies isolated (cloned) children
        }
    }
    // NOTE: d.sheets already have Id/source/children assigned by the caller (read-only here).

    // Build the set of part-pair NFPs this candidate needs that aren't already cached.
    // O(1) dedup via key set (inpairs was a linear scan inside the pair loop).
    std::vector<NfpPair> pairs;
    std::unordered_set<uint64_t> pairKeys;
    for (size_t i = 0; i < parts.size(); i++) {
        auto& B = parts[i];
        for (size_t j = 0; j < i; j++) {
            auto& A = parts[j];
            NfpPair key;
            key.A = A.get();
            key.B = B.get();
            key.ARotation = A->Rotation;
            key.BRotation = B->Rotation;
            key.Asource = A->source.value();
            key.Bsource = B->source.value();

            DbCacheKey doc;
            doc.A = A->source.value();
            doc.B = B->source.value();
            doc.ARotation = A->Rotation;
            doc.BRotation = B->Rotation;

            uint64_t pk = makeProcessKey(key.Asource, key.Bsource, key.ARotation, key.BRotation);
            if (pairKeys.insert(pk).second && !window.db->has(doc)) {
                pairs.push_back(key);
            }
        }
    }

    std::vector<std::shared_ptr<NFP>> localParts(parts.begin(), parts.end());

    if (!pairs.empty()) {
        auto ret1 = pmapDeepNest(pairs);
        // thenDeepNest, but WITHOUT the trailing sync() (we return the placement instead).
        for (size_t i = 0; i < ret1.size(); i++) {
            thenIterate(ret1[i], localParts);
        }
    }

    auto placement = placeParts(d.sheets, localParts, d.config, d.index);
    placement.index = d.index;
    return placement;
}

std::shared_ptr<NFP> NfpWorker::getPart(int source, const std::vector<std::shared_ptr<NFP>>& parts) {
    for (size_t k = 0; k < parts.size(); k++) {
        if (parts[k]->source.has_value() && parts[k]->source.value() == source) {
            return parts[k];
        }
    }
    return nullptr;
}

void NfpWorker::thenIterate(NfpPair& processed, const std::vector<std::shared_ptr<NFP>>& parts) {
    auto A = getPart(processed.Asource, parts);
    auto B = getPart(processed.Bsource, parts);

    std::vector<std::shared_ptr<NFP>> Achildren;
    if (A && !A->children.empty()) {
        for (size_t j = 0; j < A->children.size(); j++) {
            Achildren.push_back(std::make_shared<NFP>(rotatePolygon(*A->children[j], processed.ARotation)));
        }
    }

    if (!Achildren.empty()) {
        auto Brotated = std::make_shared<NFP>(rotatePolygon(*B, processed.BRotation));
        auto bbounds = GeometryUtil::getPolygonBounds(*Brotated);
        std::vector<std::shared_ptr<NFP>> cnfp;

        for (size_t j = 0; j < Achildren.size(); j++) {
            auto cbounds = GeometryUtil::getPolygonBounds(*Achildren[j]);
            if (cbounds.width > bbounds.width && cbounds.height > bbounds.height) {
                auto innerResult = getInnerNfp(*Achildren[j], *Brotated, 1, data.config);
                if (!innerResult.empty()) {
                    cnfp.insert(cnfp.end(), innerResult.begin(), innerResult.end());
                }
            }
        }

        if (processed.nfp) {
            // Replace pocket/hole children with the precise hole inner-NFPs, but KEEP
            // forbidden lobes — they are real exclusion regions of a multi-region NFP.
            processed.nfp->children.erase(
                std::remove_if(processed.nfp->children.begin(), processed.nfp->children.end(),
                               [](const std::shared_ptr<NFP>& c) { return !c->forbiddenLobe; }),
                processed.nfp->children.end());
            for (auto& c : cnfp) {
                processed.nfp->children.push_back(c);
            }
        }
    }

    DbCacheKey doc;
    doc.A = processed.Asource;
    doc.B = processed.Bsource;
    doc.ARotation = processed.ARotation;
    doc.BRotation = processed.BRotation;
    if (processed.nfp) {
        doc.nfp = {processed.nfp};
    }
    window.db->insert(doc);
}

void NfpWorker::thenDeepNest(std::vector<NfpPair>& processed, const std::vector<std::shared_ptr<NFP>>& parts) {
    if (UseParallel && processed.size() > 1) {
        int n = static_cast<int>(processed.size());
        int numThreads = std::min(static_cast<int>(std::thread::hardware_concurrency()), n);
        if (numThreads < 1) numThreads = 1;

        auto worker = [&](int threadId) {
            for (int i = threadId; i < n; i += numThreads) {
                thenIterate(processed[i], parts);
            }
        };

        std::vector<std::thread> threads;
        for (int t = 1; t < numThreads; t++) {
            threads.emplace_back(worker, t);
        }
        worker(0);
        for (auto& t : threads) t.join();
    } else {
        for (size_t i = 0; i < processed.size(); i++) {
            thenIterate(processed[i], parts);
        }
    }
    sync();
}

std::vector<NfpPair> NfpWorker::pmapDeepNest(std::vector<NfpPair>& pairs) {
    std::vector<NfpPair> ret(pairs.size());

    if (UseParallel && pairs.size() > 1) {
        int n = static_cast<int>(pairs.size());
        int numThreads = std::min(static_cast<int>(std::thread::hardware_concurrency()), n);
        if (numThreads < 1) numThreads = 1;

        auto worker = [&](int threadId) {
            for (int i = threadId; i < n; i += numThreads) {
                ret[i] = process(pairs[i]);
            }
        };

        std::vector<std::thread> threads;
        for (int t = 1; t < numThreads; t++) {
            threads.emplace_back(worker, t);
        }
        worker(0); // main thread does work too
        for (auto& t : threads) t.join();
    } else {
        for (size_t i = 0; i < pairs.size(); i++) {
            ret[i] = process(pairs[i]);
        }
    }
    return ret;
}

NfpPair NfpWorker::process(NfpPair pair) {
    auto A = rotatePolygon(*pair.A, pair.ARotation);
    auto B = rotatePolygon(*pair.B, pair.BRotation);

    auto Ac = ClipperUtil::ScaleUpPaths(A, 10000000);
    auto Bc = ClipperUtil::ScaleUpPaths(B, 10000000);
    for (auto& pt : Bc) {
        pt.x *= -1;
        pt.y *= -1;
    }

    // MinkowskiSum(A, -B) (pattern=A, path=-B), matching the C# engine (Background.process). The
    // swapped order (Bc, Ac) is only equivalent for convex parts; for CONCAVE parts it yields a wrong
    // NFP and overlapping placements. This is the pairwise NFP that pmapDeepNest caches for placeParts.
    auto solution = Clipper2Lib::MinkowskiSum(Ac, Bc, true);
    std::shared_ptr<NFP> clipperNfp;
    std::optional<double> largestArea;
    int primaryIndex = -1;

    // Primary outer = the largest CCW loop (most-negative SVGNest-signed area). A concave
    // NFP is often MULTI-REGION: keep EVERY other loop too — CCW loops are additional
    // forbidden lobes (forbiddenLobe=true), CW loops are holes/pockets where B fits.
    // The old "largest loop only" selection dropped real exclusion zones, letting parts
    // be placed inside a discarded lobe (exactly on top of an identical part).
    for (size_t i = 0; i < solution.size(); i++) {
        NFP n = toNestCoordinates(solution[i], 10000000);
        double sarea = -GeometryUtil::polygonArea(n);
        if (!largestArea.has_value() || *largestArea < sarea) {
            clipperNfp = std::make_shared<NFP>(n);
            largestArea = sarea;
            primaryIndex = static_cast<int>(i);
        }
    }
    if (!clipperNfp) {
        pair.A = nullptr;
        pair.B = nullptr;
        pair.nfp = nullptr;
        return pair;
    }
    for (size_t i = 0; i < solution.size(); i++) {
        if (static_cast<int>(i) == primaryIndex) continue;
        auto child = std::make_shared<NFP>(toNestCoordinates(solution[i], 10000000));
        if (child->length() < 3) continue;
        if (GeometryUtil::polygonArea(*child) < 0) {
            // CCW loop = additional forbidden region (multi-lobe NFP) — always keep.
            child->forbiddenLobe = true;
        } else {
            // CW loop = either a genuine NFP hole (pocket where B fits without overlap)
            // or the deep-overlap interior ARTIFACT of the quad-union sweep. Validate by
            // actually testing overlap; drop artifacts (their area stays covered by the
            // outer loop's fill, so dropping is exactly the old correct convex behavior).
            Clipper2Lib::Point64 ip;
            if (!interiorPointOf(solution[i], ip) || !isGenuineNfpHole(Ac, Bc, ip)) continue;
        }
        clipperNfp->children.push_back(child);
    }

    for (int i = 0; i < clipperNfp->length(); i++) {
        (*clipperNfp)[i].x += B[0].x;
        (*clipperNfp)[i].y += B[0].y;
    }
    for (auto& child : clipperNfp->children) {
        for (int i = 0; i < child->length(); i++) {
            (*child)[i].x += B[0].x;
            (*child)[i].y += B[0].y;
        }
    }

    pair.A = nullptr;
    pair.B = nullptr;
    pair.nfp = clipperNfp;

    if (std::getenv("NFP_DUMP_PAIR") != nullptr && clipperNfp) {
        std::cerr << "[NFP_DUMP_PAIR] (" << pair.Asource << "," << pair.Bsource
                  << "," << pair.ARotation << "," << pair.BRotation << ") "
                  << clipperNfp->length() << " pts, area="
                  << GeometryUtil::polygonArea(*clipperNfp) << ", loops_in_solution="
                  << solution.size() << ":";
        for (int i = 0; i < clipperNfp->length(); i++)
            std::cerr << " " << (*clipperNfp)[i].x << "," << (*clipperNfp)[i].y;
        std::cerr << "\n";
    }

    return pair;
}

// =============================================================================
// NfpCache / dbCache implementation
// =============================================================================

NfpCache::NfpCache() {
    db = std::make_unique<dbCache>(this);
}

NfpCache::NfpCache(NfpCache&& other) noexcept
    : nfpCache(std::move(other.nfpCache)), db(std::move(other.db)) {
    if (db) db->setWindow(this);
}

NfpCache& NfpCache::operator=(NfpCache&& other) noexcept {
    if (this != &other) {
        nfpCache = std::move(other.nfpCache);
        db = std::move(other.db);
        if (db) db->setWindow(this);
    }
    return *this;
}

uint64_t dbCache::getKey(const DbCacheKey& obj) const {
    uint64_t k = 0;
    k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(obj.A.value_or(-1) + 1)));
    k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(obj.B.value_or(-1) + 1)));
    k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(
        static_cast<int32_t>(std::round(obj.ARotation * 10000)))));
    k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(
        static_cast<int32_t>(std::round(obj.BRotation * 10000)))));
    k = hashCombine(k, static_cast<uint64_t>(static_cast<uint32_t>(obj.Type)));
    return k;
}

bool dbCache::has(const DbCacheKey& obj) {
    std::shared_lock<std::shared_mutex> lock(lockobj);
    auto key = getKey(obj);
    return window->nfpCache.count(key) > 0;
}

void dbCache::insert(const DbCacheKey& obj, bool inner) {
    auto key = getKey(obj);
    std::unique_lock<std::shared_mutex> lock(lockobj);
    if (!window->nfpCache.count(key)) {
        window->nfpCache[key] = NfpWorker::cloneNfp(obj.nfp, inner);
    }
}

std::vector<std::shared_ptr<NFP>> dbCache::find(const DbCacheKey& obj, bool inner) {
    std::shared_lock<std::shared_mutex> lock(lockobj);
    auto key = getKey(obj);
    auto it = window->nfpCache.find(key);
    if (it != window->nfpCache.end()) {
        return it->second;  // Return cached shared_ptrs directly — callers clone before modifying
    }
    return {};
}

} // namespace nest
