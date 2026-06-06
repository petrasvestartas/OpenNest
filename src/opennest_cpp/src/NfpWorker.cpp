#include "NfpWorker.h"
#include <iostream>
#include <cmath>
#include <cstring>
#include <chrono>
#include <thread>
#include <atomic>

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
} // anonymous namespace

// --- Static member initialization ---
bool NfpWorker::EnableCaches = true;
bool NfpWorker::UseParallel = false;

void NfpWorker::DisplayProgress(float p) {
    if (displayProgress) {
        displayProgress(p);
    }
}

// =============================================================================
// Simple utility methods
// =============================================================================

NFP NfpWorker::shiftPolygon(const NFP& p, const PlacementItem& shift) {
    NFP shifted;
    shifted.Points.reserve(p.length());
    for (int i = 0; i < p.length(); i++) {
        shifted.AddPoint(Point(p[i].x + shift.x, p[i].y + shift.y));
    }
    if (!p.children.empty()) {
        for (size_t i = 0; i < p.children.size(); i++) {
            shifted.children.push_back(std::make_shared<NFP>(shiftPolygon(*p.children[i], shift)));
        }
    }
    return shifted;
}

std::shared_ptr<NFP> NfpWorker::clone(const NFP& nfp) {
    auto newnfp = std::make_shared<NFP>();
    newnfp->source = nfp.source;
    newnfp->Points.reserve(nfp.length());
    for (int i = 0; i < nfp.length(); i++) {
        newnfp->AddPoint(Point(nfp[i].x, nfp[i].y));
    }
    if (!nfp.children.empty()) {
        newnfp->children.reserve(nfp.children.size());
        for (size_t i = 0; i < nfp.children.size(); i++) {
            auto& child = nfp.children[i];
            auto newchild = std::make_shared<NFP>();
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

    // children first
    if (!nfp.children.empty()) {
        for (size_t j = 0; j < nfp.children.size(); j++) {
            if (GeometryUtil::polygonArea(*nfp.children[j]) < 0) {
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

    // children first (holes)
    if (!nfp.children.empty()) {
        for (size_t j = 0; j < nfp.children.size(); j++) {
            auto& child = *nfp.children[j];
            if (GeometryUtil::polygonArea(child) < 0) {
                // Need positive winding — reverse during conversion
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

    // Convert result to NFP (concatenate all outer paths, matching original behavior)
    auto ret = std::make_shared<NFP>();
    for (const auto& outerPath : convResult.outerPaths) {
        for (size_t i = 0; i + 1 < outerPath.size(); i += 2) {
            ret->AddPoint(Point(outerPath[i], outerPath[i + 1]));
        }
    }

    // Parse holes into NFP children
    ret->children.reserve(convResult.holes.size());
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
// NFP computation — NewMinkowskiSum (via Clipper2)
// =============================================================================

std::vector<std::shared_ptr<NFP>> NfpWorker::NewMinkowskiSum(NFP& pattern, NFP& path, int type, bool useChilds, bool takeOnlyBiggestArea) {
    uint64_t key = makeProcessKey(pattern.source.value_or(-1), path.source.value_or(-1), pattern.Rotation, path.Rotation);

    bool cacheAllow = type != 1;
    {
        std::lock_guard<std::mutex> lock(*cacheMutex_);
        if (cacheAllow && cacheProcess.count(key)) {
            return cacheProcess[key];
        }
    }

    auto ac = ClipperUtil::ScaleUpPaths(pattern, 10000000);
    Clipper2Lib::Paths64 solution;

    if (useChilds) {
        auto bc = nfpToClipperCoordinates(path, 10000000);
        for (auto& pathPts : bc) {
            for (auto& pt : pathPts) {
                pt.x *= -1;
                pt.y *= -1;
            }
        }
        // MinkowskiSum with multiple paths
        for (auto& bpath : bc) {
            auto partial = Clipper2Lib::MinkowskiSum(ac, bpath, true);
            solution.insert(solution.end(), partial.begin(), partial.end());
        }
    } else {
        auto bc = ClipperUtil::ScaleUpPaths(path, 10000000);
        for (auto& pt : bc) {
            pt.x *= -1;
            pt.y *= -1;
        }
        solution = Clipper2Lib::MinkowskiSum(ac, bc, true);
    }

    std::shared_ptr<NFP> clipperNfp;
    std::optional<double> largestArea;
    int largestIndex = -1;

    for (size_t i = 0; i < solution.size(); i++) {
        NFP n = toNestCoordinates(solution[i], 10000000);
        double sarea = std::fabs(GeometryUtil::polygonArea(n));
        if (!largestArea.has_value() || *largestArea < sarea) {
            clipperNfp = std::make_shared<NFP>(n);
            largestArea = sarea;
            largestIndex = static_cast<int>(i);
        }
    }

    if (!takeOnlyBiggestArea) {
        for (size_t j = 0; j < solution.size(); j++) {
            if (static_cast<int>(j) == largestIndex) continue;
            auto n = std::make_shared<NFP>(toNestCoordinates(solution[j], 10000000));
            clipperNfp->children.push_back(n);
        }
    }

    for (int i = 0; i < clipperNfp->Length(); i++) {
        clipperNfp->Points[i].x *= -1;
        clipperNfp->Points[i].y *= -1;
        clipperNfp->Points[i].x += pattern[0].x;
        clipperNfp->Points[i].y += pattern[0].y;
    }
    if (!clipperNfp->children.empty()) {
        for (auto& child : clipperNfp->children) {
            for (int j = 0; j < child->Length(); j++) {
                child->Points[j].x *= -1;
                child->Points[j].y *= -1;
                child->Points[j].x += pattern[0].x;
                child->Points[j].y += pattern[0].y;
            }
        }
    }

    std::vector<std::shared_ptr<NFP>> res = {clipperNfp};
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

        std::shared_ptr<NFP> clipperNfpResult;
        std::optional<double> largestAreaVal;
        for (size_t i = 0; i < solution.size(); i++) {
            NFP n = toNestCoordinates(solution[i], 10000000);
            double sarea = GeometryUtil::polygonArea(n);
            if (!largestAreaVal.has_value() || *largestAreaVal > sarea) {
                clipperNfpResult = std::make_shared<NFP>(n);
                largestAreaVal = sarea;
            }
        }
        if (!clipperNfpResult) return nullptr;

        for (int i = 0; i < clipperNfpResult->length(); i++) {
            (*clipperNfpResult)[i].x += B[0].x;
            (*clipperNfpResult)[i].y += B[0].y;
        }

        auto wrapper = std::make_shared<NFP>();
        wrapper->Points = clipperNfpResult->Points;
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
    if (A.source.has_value() && B.source.has_value()) {
        DbCacheKey doc;
        doc.A = A.source.value();
        doc.B = B.source.value();
        doc.ARotation = 0;
        doc.BRotation = B.Rotation;
        doc.nfp = nfpResult->children;
        window.db->insert(doc, true);
    }
    return nfpResult->children;
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
    double totalMerged = 0;

    // rotate parts by given rotation
    std::vector<std::shared_ptr<NFP>> rotated;
    for (i = 0; i < static_cast<int>(parts.size()); i++) {
        auto r = std::make_shared<NFP>(rotatePolygon(*parts[i], parts[i]->Rotation));
        r->Rotation = parts[i]->Rotation;
        r->source = parts[i]->source;
        r->Id = parts[i]->Id;
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
            float prog = 0.66f + 0.34f * (totalPlaced / static_cast<float>(totalParts));
            DisplayProgress(prog);

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
                int effRotations = config.rotations;
                if (config.tryAllRotations && effRotations > 8) effRotations = 8;
                const float rotStep = 360.0f / effRotations;
                for (j = 0; j < effRotations; j++) {
                    auto innerNfp = getInnerNfp(*sheet, *trialPart, 0, config);

                    if (!innerNfp.empty()) {
                        if (innerNfp[0]->length() == 0) {
                            throw std::runtime_error("sheetNfp[0] has 0 points");
                        }
                        rotationCandidates.push_back({trialPart, innerNfp});
                        if (!config.tryAllRotations) break;
                    }

                    // Rotate to next step
                    auto r = std::make_shared<NFP>(rotatePolygon(*trialPart, rotStep));
                    r->Rotation = trialPart->Rotation + rotStep;
                    r->source = trialPart->source;
                    r->Id = trialPart->Id;
                    if (r->Rotation > 360.0f) {
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
            if (config.placementType == PlacementTypeEnum::gravity || config.placementType == PlacementTypeEnum::box) {
                allbounds = GeometryUtil::getPolygonBounds(allpoints);
            } else {
                auto hullResult = getHull(allpoints);
                allpoints = *hullResult;
                useHull = true;
            }

            double gravityCx = allbounds.x + allbounds.width / 2.0;
            double gravityCy = allbounds.y + allbounds.height / 2.0;

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

                // Convert sheet NFP to clipper coordinates
                auto candClipperSheetNfp = innerNfpToClipperCoordinates(sheetNfp, config);

                // Build outer NFP union
                Clipper2Lib::Clipper64 clipperOp;
                bool localError = false;

                for (int jj = 0; jj < static_cast<int>(placed.size()); jj++) {
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

                for (int jj = 0; jj < static_cast<int>(localFinalNfpList.size()); jj++) {
                    auto& nf = localFinalNfpList[jj];
                    for (int kk = 0; kk < nf.length(); kk++) {
                        PlacementItem shiftvector;
                        shiftvector.id = candPart->Id;
                        shiftvector.x = nf[kk].x - (*candPart)[0].x;
                        shiftvector.y = nf[kk].y - (*candPart)[0].y;
                        shiftvector.source = candPart->source.value();
                        shiftvector.rotation = candPart->Rotation;

                        double area = 0;
                        PolygonBounds rectbounds;
                        bool hasRectbounds = false;

                        if (config.placementType == PlacementTypeEnum::gravity || config.placementType == PlacementTypeEnum::box) {
                            NFP poly;
                            poly.AddPoint(Point(allbounds.x, allbounds.y));
                            poly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y));
                            poly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y + allbounds.height));
                            poly.AddPoint(Point(allbounds.x, allbounds.y + allbounds.height));

                            poly.AddPoint(Point(partbounds.x + shiftvector.x, partbounds.y + shiftvector.y));
                            poly.AddPoint(Point(partbounds.x + partbounds.width + shiftvector.x, partbounds.y + shiftvector.y));
                            poly.AddPoint(Point(partbounds.x + partbounds.width + shiftvector.x, partbounds.y + partbounds.height + shiftvector.y));
                            poly.AddPoint(Point(partbounds.x + shiftvector.x, partbounds.y + partbounds.height + shiftvector.y));

                            rectbounds = GeometryUtil::getPolygonBounds(poly);
                            hasRectbounds = true;

                            if (config.placementType == PlacementTypeEnum::gravity) {
                                area = 2.0 * rectbounds.width + 1.0 * rectbounds.height;
                            } else {
                                area = rectbounds.width * rectbounds.height;
                            }

                            if (config.gravityWeight > 0) {
                                double partCx = partbounds.x + partbounds.width / 2.0 + shiftvector.x;
                                double partCy = partbounds.y + partbounds.height / 2.0 + shiftvector.y;
                                double dist = std::sqrt((partCx - gravityCx) * (partCx - gravityCx) +
                                                        (partCy - gravityCy) * (partCy - gravityCy));
                                area += config.gravityWeight * dist;
                            }
                        } else {
                            auto localpoints = clone(allpoints);
                            for (int mm = 0; mm < candPart->length(); mm++) {
                                localpoints->AddPoint(Point((*candPart)[mm].x + shiftvector.x, (*candPart)[mm].y + shiftvector.y));
                            }
                            auto hullLocal = getHull(*localpoints);
                            area = std::fabs(GeometryUtil::polygonArea(*hullLocal));
                            shiftvector.hull = hullLocal;
                            shiftvector.hullsheet = getHull(*sheet);
                        }

                        if (!localMinArea.has_value() ||
                            area < *localMinArea ||
                            (GeometryUtil::_almostEqual(*localMinArea, area) && (!localMinX.has_value() || shiftvector.x < *localMinX)) ||
                            (GeometryUtil::_almostEqual(*localMinArea, area) && localMinX.has_value() && GeometryUtil::_almostEqual(shiftvector.x, *localMinX) && shiftvector.y < localMinY)) {
                            localMinArea = area;
                            result.bestArea = area;
                            result.bestWidth = hasRectbounds ? rectbounds.width : 0;
                            result.bestX = shiftvector.x;
                            result.bestY = shiftvector.y;
                            result.position = shiftvector;
                            result.part = candPart;
                            result.combinedNfp = unionResult;
                            result.valid = true;
                            if (!localMinX.has_value() || shiftvector.x < *localMinX) {
                                localMinX = shiftvector.x;
                            }
                            if (!localMinY.has_value() || shiftvector.y < *localMinY) {
                                localMinY = shiftvector.y;
                            }
                        }
                    }
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
                    winnerCombinedNfp = r.combinedNfp;
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
                // Update clipCache with winning rotation's union data
                if (EnableCaches) {
                    uint64_t winKey = makeClipKey(winnerPart->source.value_or(-1), winnerPart->Rotation);
                    ClipCacheItem cacheItem;
                    cacheItem.index = static_cast<int>(placed.size()) - 1;
                    cacheItem.nfpp = winnerCombinedNfp;
                    clipCache[winKey] = cacheItem;
                }

                part = winnerPart;
                parts[i] = winnerPart;
                placed.push_back(part);
                totalPlaced++;
                placements.push_back(position);
                if (position.mergedLength.has_value()) {
                    totalMerged += position.mergedLength.value();
                }
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
    result.area = sheetarea;
    result.mergedLength = totalMerged;

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

            double gravityCx = allbounds.x + allbounds.width / 2.0;
            double gravityCy = allbounds.y + allbounds.height / 2.0;

            std::optional<double> bestArea;
            std::optional<double> bestX, bestY;
            PlacementItem bestPos;
            bool foundBetter = false;

            for (int fi = 0; fi < static_cast<int>(feasibleList.size()); fi++) {
                auto& nf = feasibleList[fi];
                for (int k = 0; k < nf.length(); k++) {
                    PlacementItem sv;
                    sv.id = part->Id;
                    sv.x = nf[k].x - (*part)[0].x;
                    sv.y = nf[k].y - (*part)[0].y;
                    sv.source = part->source.value();
                    sv.rotation = part->Rotation;

                    // Score candidate position
                    double area;
                    if (config.placementType == PlacementTypeEnum::gravity || config.placementType == PlacementTypeEnum::box) {
                        // Bounding-box scoring
                        NFP poly;
                        poly.AddPoint(Point(allbounds.x, allbounds.y));
                        poly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y));
                        poly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y + allbounds.height));
                        poly.AddPoint(Point(allbounds.x, allbounds.y + allbounds.height));

                        poly.AddPoint(Point(partbounds.x + sv.x, partbounds.y + sv.y));
                        poly.AddPoint(Point(partbounds.x + partbounds.width + sv.x, partbounds.y + sv.y));
                        poly.AddPoint(Point(partbounds.x + partbounds.width + sv.x, partbounds.y + partbounds.height + sv.y));
                        poly.AddPoint(Point(partbounds.x + sv.x, partbounds.y + partbounds.height + sv.y));

                        auto rectbounds = GeometryUtil::getPolygonBounds(poly);
                        if (config.placementType == PlacementTypeEnum::gravity) {
                            area = 2.0 * rectbounds.width + 1.0 * rectbounds.height;
                        } else {
                            area = rectbounds.width * rectbounds.height;
                        }

                        if (config.gravityWeight > 0) {
                            double partCx = partbounds.x + partbounds.width / 2.0 + sv.x;
                            double partCy = partbounds.y + partbounds.height / 2.0 + sv.y;
                            double dist = std::sqrt((partCx - gravityCx) * (partCx - gravityCx) +
                                                    (partCy - gravityCy) * (partCy - gravityCy));
                            area += config.gravityWeight * dist;
                        }
                    } else {
                        // Squeeze mode: hull-area scoring (rewards interlocking)
                        auto localpoints = clone(allpoints);
                        for (int m = 0; m < part->length(); m++) {
                            localpoints->AddPoint(Point((*part)[m].x + sv.x, (*part)[m].y + sv.y));
                        }
                        auto hull = getHull(*localpoints);
                        area = std::fabs(GeometryUtil::polygonArea(*hull));
                    }

                    if (!bestArea.has_value() ||
                        area < *bestArea ||
                        (GeometryUtil::_almostEqual(*bestArea, area) && (!bestX.has_value() || sv.x < *bestX)) ||
                        (GeometryUtil::_almostEqual(*bestArea, area) && bestX.has_value() && GeometryUtil::_almostEqual(sv.x, *bestX) && sv.y < bestY)) {
                        bestArea = area;
                        bestPos = sv;
                        foundBetter = true;
                        if (!bestX.has_value() || sv.x < *bestX) bestX = sv.x;
                        if (!bestY.has_value() || sv.y < *bestY) bestY = sv.y;
                    }
                }
            }

            // 5. Update if better position found
            if (foundBetter) {
                // Compute current score for comparison
                double curArea;
                if (config.placementType == PlacementTypeEnum::gravity || config.placementType == PlacementTypeEnum::box) {
                    NFP curPoly;
                    curPoly.AddPoint(Point(allbounds.x, allbounds.y));
                    curPoly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y));
                    curPoly.AddPoint(Point(allbounds.x + allbounds.width, allbounds.y + allbounds.height));
                    curPoly.AddPoint(Point(allbounds.x, allbounds.y + allbounds.height));

                    curPoly.AddPoint(Point(partbounds.x + placements[ci].x, partbounds.y + placements[ci].y));
                    curPoly.AddPoint(Point(partbounds.x + partbounds.width + placements[ci].x, partbounds.y + placements[ci].y));
                    curPoly.AddPoint(Point(partbounds.x + partbounds.width + placements[ci].x, partbounds.y + partbounds.height + placements[ci].y));
                    curPoly.AddPoint(Point(partbounds.x + placements[ci].x, partbounds.y + partbounds.height + placements[ci].y));

                    auto curRectBounds = GeometryUtil::getPolygonBounds(curPoly);
                    if (config.placementType == PlacementTypeEnum::gravity) {
                        curArea = 2.0 * curRectBounds.width + 1.0 * curRectBounds.height;
                    } else {
                        curArea = curRectBounds.width * curRectBounds.height;
                    }
                    if (config.gravityWeight > 0) {
                        double partCx = partbounds.x + partbounds.width / 2.0 + placements[ci].x;
                        double partCy = partbounds.y + partbounds.height / 2.0 + placements[ci].y;
                        double dist = std::sqrt((partCx - gravityCx) * (partCx - gravityCx) +
                                                (partCy - gravityCy) * (partCy - gravityCy));
                        curArea += config.gravityWeight * dist;
                    }
                } else {
                    // Squeeze mode: hull-area scoring
                    auto localpoints = clone(allpoints);
                    for (int m = 0; m < part->length(); m++) {
                        localpoints->AddPoint(Point((*part)[m].x + placements[ci].x, (*part)[m].y + placements[ci].y));
                    }
                    auto hull = getHull(*localpoints);
                    curArea = std::fabs(GeometryUtil::polygonArea(*hull));
                }

                if (*bestArea < curArea - 1e-6) {
                    placements[ci] = bestPos;
                    anyImproved = true;
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

    // preprocess
    std::vector<NfpPair> pairs;

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

            if (!inpairs(key, pairs) && !window.db->has(doc)) {
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
    std::vector<NfpPair> pairs;
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

            if (!inpairs(key, pairs) && !window.db->has(doc)) {
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
            processed.nfp->children.clear();
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
        std::atomic<int> cnt{0};
        int n = static_cast<int>(processed.size());
        int numThreads = std::min(static_cast<int>(std::thread::hardware_concurrency()), n);
        if (numThreads < 1) numThreads = 1;

        auto worker = [&](int threadId) {
            for (int i = threadId; i < n; i += numThreads) {
                int done = cnt.fetch_add(1) + 1;
                DisplayProgress(0.33f + 0.33f * (done / static_cast<float>(n)));
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
        int cnt = 0;
        for (size_t i = 0; i < processed.size(); i++) {
            float progress = 0.33f + 0.33f * (cnt / static_cast<float>(processed.size()));
            cnt++;
            DisplayProgress(progress);
            thenIterate(processed[i], parts);
        }
    }
    sync();
}

bool NfpWorker::inpairs(const NfpPair& key, const std::vector<NfpPair>& p) {
    for (size_t i = 0; i < p.size(); i++) {
        if (p[i].Asource == key.Asource && p[i].Bsource == key.Bsource &&
            p[i].ARotation == key.ARotation && p[i].BRotation == key.BRotation) {
            return true;
        }
    }
    return false;
}

std::vector<NfpPair> NfpWorker::pmapDeepNest(std::vector<NfpPair>& pairs) {
    std::vector<NfpPair> ret(pairs.size());

    if (UseParallel && pairs.size() > 1) {
        std::atomic<int> cnt{0};
        int n = static_cast<int>(pairs.size());
        int numThreads = std::min(static_cast<int>(std::thread::hardware_concurrency()), n);
        if (numThreads < 1) numThreads = 1;

        auto worker = [&](int threadId) {
            for (int i = threadId; i < n; i += numThreads) {
                ret[i] = process(pairs[i]);
                int done = cnt.fetch_add(1) + 1;
                DisplayProgress(0.33f * (done / static_cast<float>(n)));
            }
        };

        std::vector<std::thread> threads;
        for (int t = 1; t < numThreads; t++) {
            threads.emplace_back(worker, t);
        }
        worker(0); // main thread does work too
        for (auto& t : threads) t.join();
    } else {
        int cnt = 0;
        for (size_t i = 0; i < pairs.size(); i++) {
            ret[i] = process(pairs[i]);
            float progress = 0.33f * (cnt / static_cast<float>(pairs.size()));
            cnt++;
            DisplayProgress(progress);
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

    for (size_t i = 0; i < solution.size(); i++) {
        NFP n = toNestCoordinates(solution[i], 10000000);
        double sarea = -GeometryUtil::polygonArea(n);
        if (!largestArea.has_value() || *largestArea < sarea) {
            clipperNfp = std::make_shared<NFP>(n);
            largestArea = sarea;
        }
    }

    for (int i = 0; i < clipperNfp->length(); i++) {
        (*clipperNfp)[i].x += B[0].x;
        (*clipperNfp)[i].y += B[0].y;
    }

    pair.A = nullptr;
    pair.B = nullptr;
    pair.nfp = clipperNfp;

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
