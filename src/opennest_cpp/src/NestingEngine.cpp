#include "NestingEngine.h"
#include <iostream>
#include <thread>
#include <atomic>
#include <cstdlib>
#include <cstdio>
#include <chrono>
#include <unordered_set>

namespace nest {

NestConfig NestingEngine::Config;

Point NestingEngine::RotatePoint(const Point& p, double cx, double cy, double angle) {
    return Point(
        std::cos(angle) * (p.x - cx) - std::sin(angle) * (p.y - cy) + cx,
        std::sin(angle) * (p.x - cx) + std::cos(angle) * (p.y - cy) + cy);
}

NFP NestingEngine::GetMinimumBox(const NFP& vv) {
    NFP hullInput;
    for (int i = 0; i < vv.Length(); i++) {
        hullInput.AddPoint(Point(vv[i].x, vv[i].y));
    }
    auto hull = NfpWorker::getHull(hullInput);

    double minArea = std::numeric_limits<double>::max();
    std::vector<Point> rect;

    for (int i = 0; i < hull->Length(); i++) {
        auto p0 = hull->Points[i];
        auto p1 = hull->Points[(i + 1) % hull->Length()];
        double dx = p1.x - p0.x;
        double dy = p1.y - p0.y;
        double atan = std::atan2(dy, dx);

        std::vector<Point> dd;
        for (int j = 0; j < vv.Length(); j++) {
            dd.push_back(RotatePoint(Point(vv[j].x, vv[j].y), 0, 0, -atan));
        }

        double maxx = -std::numeric_limits<double>::max();
        double maxy = -std::numeric_limits<double>::max();
        double minx = std::numeric_limits<double>::max();
        double miny = std::numeric_limits<double>::max();
        for (auto& d : dd) {
            maxx = std::max(maxx, d.x);
            maxy = std::max(maxy, d.y);
            minx = std::min(minx, d.x);
            miny = std::min(miny, d.y);
        }

        double area = (maxx - minx) * (maxy - miny);
        if (area < minArea) {
            minArea = area;
            rect.clear();
            rect.push_back(Point(minx, miny));
            rect.push_back(Point(maxx, miny));
            rect.push_back(Point(maxx, maxy));
            rect.push_back(Point(minx, maxy));
            for (auto& r : rect) {
                r = RotatePoint(Point(r.x, r.y), 0, 0, atan);
            }
        }
    }

    NFP ret;
    for (auto& r : rect) ret.AddPoint(r);
    return ret;
}

NFP NestingEngine::boundingBox(const NFP& offset) {
    double maxx = -std::numeric_limits<double>::max();
    double maxy = -std::numeric_limits<double>::max();
    double minx = std::numeric_limits<double>::max();
    double miny = std::numeric_limits<double>::max();
    for (auto& p : offset.Points) {
        maxx = std::max(maxx, p.x);
        maxy = std::max(maxy, p.y);
        minx = std::min(minx, p.x);
        miny = std::min(miny, p.y);
    }
    NFP ret;
    ret.AddPoint(Point(minx, miny));
    ret.AddPoint(Point(maxx, miny));
    ret.AddPoint(Point(maxx, maxy));
    ret.AddPoint(Point(minx, maxy));
    return ret;
}

NFP NestingEngine::ClipSubject(NFP& subject, NFP& clipBounds, double clipperScale) {
    std::vector<std::shared_ptr<NFP>> subjectArr = {std::make_shared<NFP>(subject)};
    std::vector<std::shared_ptr<NFP>> clipArr = {std::make_shared<NFP>(clipBounds)};
    auto clipperSubject = NfpWorker::innerNfpToClipperCoordinates(subjectArr, Config);
    auto clipperClip = NfpWorker::innerNfpToClipperCoordinates(clipArr, Config);

    Clipper2Lib::Clipper64 clipper;
    clipper.AddClip(clipperClip);
    clipper.AddSubject(clipperSubject);

    Clipper2Lib::Paths64 finalNfp;
    if (clipper.Execute(Clipper2Lib::ClipType::Intersection, Clipper2Lib::FillRule::NonZero, finalNfp) &&
        !finalNfp.empty()) {
        return NfpWorker::toNestCoordinates(finalNfp[0], clipperScale);
    }
    return subject;
}

Clipper2Lib::Path64 NestingEngine::svgToClipper(const NFP& polygon) {
    return ClipperUtil::ScaleUpPaths(polygon, Config.clipperScale);
}

NFP NestingEngine::clipperToSvg(const Clipper2Lib::Path64& polygon) {
    NFP ret;
    for (size_t i = 0; i < polygon.size(); i++) {
        ret.AddPoint(Point(
            static_cast<double>(polygon[i].x) / Config.clipperScale,
            static_cast<double>(polygon[i].y) / Config.clipperScale));
    }
    return ret;
}

bool NestingEngine::pointInPolygon(const Point& point, const NFP& polygon) {
    auto p = ClipperUtil::ScaleUpPaths(polygon, 1000);
    Clipper2Lib::Point64 pt(static_cast<int64_t>(1000 * point.x), static_cast<int64_t>(1000 * point.y));
    auto result = Clipper2Lib::PointInPolygon(pt, p);
    return result == Clipper2Lib::PointInPolygonResult::IsInside;
}

std::optional<int> NestingEngine::find(const Point& v, const NFP& p) {
    for (int i = 0; i < p.length(); i++) {
        if (GeometryUtil::_withinDistance(v, p[i], Config.curveTolerance / 1000)) {
            return i;
        }
    }
    return std::nullopt;
}

Point NestingEngine::getTarget(const Point& o, const NFP& simple, double tol) {
    struct InrangeItem { Point point; int index; double distance; };
    std::vector<InrangeItem> inrange;

    for (int j = 0; j < simple.length(); j++) {
        auto& s = simple[j];
        double d2 = (o.x - s.x) * (o.x - s.x) + (o.y - s.y) * (o.y - s.y);
        if (d2 < tol * tol) {
            inrange.push_back({s, j, d2});
        }
    }

    Point target(0, 0);
    bool hasTarget = false;

    if (!inrange.empty()) {
        // filter to exact points
        std::vector<InrangeItem> filtered;
        for (auto& item : inrange) {
            if (simple.getExact(item.index)) filtered.push_back(item);
        }
        if (!filtered.empty()) inrange = filtered;

        std::sort(inrange.begin(), inrange.end(), [](const InrangeItem& a, const InrangeItem& b) {
            return a.distance < b.distance;
        });
        target = inrange[0].point;
        hasTarget = true;
    } else {
        std::optional<double> mind;
        for (int j = 0; j < simple.length(); j++) {
            auto& s = simple[j];
            double d2 = (o.x - s.x) * (o.x - s.x) + (o.y - s.y) * (o.y - s.y);
            if (!mind.has_value() || d2 < *mind) {
                target = s;
                mind = d2;
                hasTarget = true;
            }
        }
    }
    return target;
}

bool NestingEngine::exterior(const NFP& simple, const NFP& complex, bool inside) {
    for (int i = 0; i < complex.length(); i++) {
        auto v = complex[i];
        if (!inside && !pointInPolygon(v, simple) && !find(v, simple).has_value()) {
            return true;
        }
        if (inside && pointInPolygon(v, simple) && find(v, simple).has_value()) {
            return true;
        }
    }
    return false;
}

NFP NestingEngine::cleanPolygon2(const NFP& polygon) {
    auto p = svgToClipper(polygon);

    // SimplifyPolygon in Clipper2: union path with itself
    Clipper2Lib::Clipper64 simplifier;
    simplifier.AddSubject({p});
    Clipper2Lib::Paths64 simple;
    simplifier.Execute(Clipper2Lib::ClipType::Union, Clipper2Lib::FillRule::NonZero, simple);

    if (simple.empty()) return NFP();

    // Find biggest
    size_t biggestIdx = 0;
    double biggestArea = std::fabs(Clipper2Lib::Area(simple[0]));
    for (size_t i = 1; i < simple.size(); i++) {
        double area = std::fabs(Clipper2Lib::Area(simple[i]));
        if (area > biggestArea) {
            biggestIdx = i;
            biggestArea = area;
        }
    }

    // CleanPolygon equivalent: use SimplifyPath
    // Clipper2's SimplifyPath with epsilon works similarly to CleanPolygon
    double cleanDist = 0.01 * Config.curveTolerance * Config.clipperScale;
    auto clean = Clipper2Lib::SimplifyPath(simple[biggestIdx], cleanDist);

    if (clean.empty()) return NFP();

    auto cleaned = clipperToSvg(clean);

    // Remove duplicate endpoints
    if (cleaned.length() > 1) {
        auto& start = cleaned[0];
        auto& end = cleaned[cleaned.length() - 1];
        if (GeometryUtil::_almostEqual(start.x, end.x) && GeometryUtil::_almostEqual(start.y, end.y)) {
            cleaned.Points.pop_back();
        }
    }

    return cleaned;
}

std::vector<NFP> NestingEngine::polygonOffsetDeepNest(const NFP& polygon, double offset) {
    if (offset == 0 || GeometryUtil::_almostEqual(offset, 0)) {
        return {polygon};
    }

    auto p = svgToClipper(polygon);

    double miterLimit = 4;
    Clipper2Lib::ClipperOffset co(miterLimit, Config.curveTolerance * Config.clipperScale);
    co.AddPath(p, Clipper2Lib::JoinType::Miter, Clipper2Lib::EndType::Polygon);

    Clipper2Lib::Paths64 newpaths;
    co.Execute(offset * Config.clipperScale, newpaths);

    std::vector<NFP> result;
    for (auto& path : newpaths) {
        result.push_back(clipperToSvg(path));
    }
    return result;
}

NFP NestingEngine::simplifyFunction(const NFP& polygon, bool inside) {
    return simplifyFunction(polygon, inside, Config);
}

NFP NestingEngine::simplifyFunction(const NFP& polygon, bool inside, const NestConfig& config) {
    double tolerance = 4 * config.curveTolerance;
    double fixedTolerance = 40 * config.curveTolerance * 40 * config.curveTolerance;

    auto hull = NfpWorker::getHull(polygon);
    if (config.simplify) {
        if (hull && hull->length() > 0) {
            return *hull;
        }
        return polygon;
    }

    // The original aggressive path (Douglas-Peucker + polygonOffset + "selective reversal") corrupted
    // concave parts (chopped chunks off, moved the reference vertex). We keep the shape-preserving part:
    // clean + Douglas-Peucker vertex reduction (needed for speed — raw organic curves make NFPs slow),
    // and SKIP the buggy reversal — but we MUST keep the CONSERVATIVE offset the C# engine relies on.
    // Douglas-Peucker moves the outline INWARD by up to `tolerance`; without compensating, at spacing 0
    // the NFP is non-conservative and parts overlap by up to `tolerance`. So we dilate the simplified
    // outline OUTWARD by `tolerance` (parts) / erode INWARD (sheets) so it ENCLOSES the real part —
    // exactly like C# simplifyFunction (minus the optimization), giving no overlap at spacing 0.
    {
        auto cleanedEarly = cleanPolygon2(polygon);
        if (cleanedEarly.length() < 3) return polygon;

        NFP copy;
        for (int i = 0; i < cleanedEarly.length(); i++)
            copy.AddPoint(Point(cleanedEarly[i].x, cleanedEarly[i].y));
        copy.push(copy[0]);

        std::vector<bool> markedFlags(copy.length(), false);
        for (int i = 0; i < copy.length() - 1; i++) {
            double dx = copy[i + 1].x - copy[i].x, dy = copy[i + 1].y - copy[i].y;
            if (dx * dx + dy * dy > fixedTolerance) { markedFlags[i] = true; markedFlags[i + 1] = true; }
        }
        auto simpleDP = Simplify::simplify(copy, tolerance, true, markedFlags);
        if (simpleDP.length() > 0) simpleDP.Points.pop_back();
        simpleDP = cleanPolygon2(simpleDP);
        if (simpleDP.length() < 3) return cleanedEarly;

        // Exact NFP mode: nest the full-resolution part (no vertex reduction, no dilation). The NFP then
        // guarantees non-collision directly and parts pack touching (no gap, tightest) — at higher cost.
        if (config.exactNfp) return cleanedEarly;

        // Conservative offset so the simplified outline encloses the original (no overlap at spacing 0).
        auto offsets = polygonOffsetDeepNest(simpleDP, inside ? -tolerance : tolerance);
        NFP best; double bestArea = -1; bool hasBest = false;
        for (auto& o : offsets) {
            double a = std::fabs(GeometryUtil::polygonArea(o));
            if (a > bestArea) { bestArea = a; best = o; hasBest = true; }
        }
        if (!hasBest || best.length() < 3) return simpleDP;
        return best;
    }

    auto cleaned = cleanPolygon2(polygon);
    if (cleaned.length() > 1) {
        // use cleaned
    } else {
        return polygon;
    }

    // polygon to polyline
    NFP copy;
    for (int i = 0; i < cleaned.length(); i++) {
        copy.AddPoint(Point(cleaned[i].x, cleaned[i].y));
    }
    copy.push(copy[0]);

    // mark long segments using per-NFP flags
    std::vector<bool> markedFlags(copy.length(), false);
    for (int i = 0; i < copy.length() - 1; i++) {
        auto& p1 = copy[i];
        auto& p2 = copy[i + 1];
        double sqd = (p2.x - p1.x) * (p2.x - p1.x) + (p2.y - p1.y) * (p2.y - p1.y);
        if (sqd > fixedTolerance) {
            markedFlags[i] = true;
            markedFlags[i + 1] = true;
        }
    }

    auto simple = Simplify::simplify(copy, tolerance, true, markedFlags);
    // back to polygon (remove last duplicate point)
    if (simple.length() > 0) {
        simple.Points.pop_back();
    }

    simple = cleanPolygon2(simple);
    if (simple.length() <= 1) {
        simple = cleaned;
    }

    auto offsets = polygonOffsetDeepNest(simple, inside ? -tolerance : tolerance);

    NFP offset;
    bool hasOffset = false;
    double offsetArea = 0;
    std::vector<NFP> holes;

    for (size_t i = 0; i < offsets.size(); i++) {
        double area = GeometryUtil::polygonArea(offsets[i]);
        if (!hasOffset || area < offsetArea) {
            offset = offsets[i];
            offsetArea = area;
            hasOffset = true;
        }
        if (area > 0) {
            holes.push_back(offsets[i]);
        }
    }

    // mark exact points
    for (int i = 0; i < simple.length(); i++) {
        int next = (i + 1 == simple.length()) ? 0 : i + 1;
        auto index1 = find(simple[i], polygon);
        auto index2 = find(simple[next], polygon);

        if (index1.has_value() && index2.has_value()) {
            int i1 = *index1;
            int i2 = *index2;
            if (i1 + 1 == i2 || i2 + 1 == i1 ||
                (i1 == 0 && i2 == polygon.length() - 1) ||
                (i2 == 0 && i1 == polygon.length() - 1)) {
                simple.setExact(i, true);
                simple.setExact(next, true);
            }
        }
    }

    int numshells = 4;
    std::vector<std::optional<NFP>> shells(numshells);
    for (int j = 1; j < numshells; j++) {
        double delta = j * (tolerance / numshells);
        delta = inside ? -delta : delta;
        auto shell = polygonOffsetDeepNest(simple, delta);
        if (!shell.empty()) {
            shells[j] = shell[0];
        }
    }

    if (!hasOffset) return polygon;

    // selective reversal of offset
    for (int i = 0; i < offset.length(); i++) {
        auto o = offset[i];
        auto target = getTarget(o, simple, 2 * tolerance);

        auto test = offset;
        test.Points[i] = Point(target.x, target.y);

        if (!exterior(test, polygon, inside)) {
            offset.Points[i].x = target.x;
            offset.Points[i].y = target.y;
        } else {
            for (int j = 1; j < numshells; j++) {
                if (shells[j].has_value()) {
                    auto& shell = shells[j].value();
                    double delta = j * (tolerance / numshells);
                    target = getTarget(o, shell, 2 * delta);
                    test = offset;
                    test.Points[i] = Point(target.x, target.y);
                    if (!exterior(test, polygon, inside)) {
                        offset.Points[i].x = target.x;
                        offset.Points[i].y = target.y;
                        break;
                    }
                }
            }
        }
    }

    // Straighten long lines
    for (int i = 0; i < offset.length(); i++) {
        auto& p1 = offset.Points[i];
        int next = (i + 1 == offset.length()) ? 0 : i + 1;
        auto& p2 = offset.Points[next];

        double sqd = (p2.x - p1.x) * (p2.x - p1.x) + (p2.y - p1.y) * (p2.y - p1.y);
        if (sqd < fixedTolerance) continue;

        for (int j = 0; j < simple.length(); j++) {
            auto s1 = simple[j];
            int snext = (j + 1 == simple.length()) ? 0 : j + 1;
            auto s2 = simple[snext];

            double sqds = (s2.x - s1.x) * (s2.x - s1.x) + (s2.y - s1.y) * (s2.y - s1.y);
            if (sqds < fixedTolerance) continue;

            if ((GeometryUtil::_almostEqual(s1.x, s2.x) || GeometryUtil::_almostEqual(s1.y, s2.y)) &&
                GeometryUtil::_withinDistance(p1, s1, 2 * tolerance) &&
                GeometryUtil::_withinDistance(p2, s2, 2 * tolerance) &&
                (!GeometryUtil::_withinDistance(p1, s1, config.curveTolerance / 1000) ||
                 !GeometryUtil::_withinDistance(p2, s2, config.curveTolerance / 1000))) {
                p1.x = s1.x;
                p1.y = s1.y;
                p2.x = s2.x;
                p2.y = s2.y;
            }
        }
    }

    // Union offset with polygon
    {
        auto Ac = ClipperUtil::ScaleUpPaths(offset, 10000000);
        auto Bc = ClipperUtil::ScaleUpPaths(polygon, 10000000);

        Clipper2Lib::Clipper64 clipper;
        clipper.AddSubject({Ac, Bc});

        Clipper2Lib::Paths64 combined;
        if (clipper.Execute(Clipper2Lib::ClipType::Union, Clipper2Lib::FillRule::NonZero, combined)) {
            std::optional<double> largestArea;
            for (size_t i = 0; i < combined.size(); i++) {
                auto n = NfpWorker::toNestCoordinates(combined[i], 10000000);
                double sarea = -GeometryUtil::polygonArea(n);
                if (!largestArea.has_value() || *largestArea < sarea) {
                    offset = n;
                    largestArea = sarea;
                }
            }
        }
    }

    cleaned = cleanPolygon2(offset);
    if (cleaned.length() > 1) {
        offset = cleaned;
    }

    // Clip by hull/rects
    if (config.clipByHull) {
        offset = ClipSubject(offset, *hull, config.clipperScale);
    } else if (config.clipByRects) {
        auto rect1 = boundingBox(*hull);
        offset = ClipSubject(offset, rect1, config.clipperScale);
        auto mbox = GetMinimumBox(*hull);
        offset = ClipSubject(offset, mbox, config.clipperScale);
    }

    // Mark exact points on final offset
    for (int i = 0; i < offset.length(); i++) {
        int next = (i + 1 == offset.length()) ? 0 : i + 1;
        auto index1 = find(offset[i], polygon);
        auto index2 = find(offset[next], polygon);

        if (!index1.has_value()) index1 = 0;
        if (!index2.has_value()) index2 = 0;

        int i1 = *index1;
        int i2 = *index2;
        if (i1 + 1 == i2 || i2 + 1 == i1 ||
            (i1 == 0 && i2 == polygon.length() - 1) ||
            (i2 == 0 && i1 == polygon.length() - 1)) {
            offset.setExact(i, true);
            offset.setExact(next, true);
        }
    }

    if (!inside && !holes.empty()) {
        for (auto& h : holes) {
            offset.children.push_back(std::make_shared<NFP>(h));
        }
    }

    return offset;
}

void NestingEngine::offsetTree(NFP& t, double offset, const NestConfig& config, std::optional<bool> inside) {
    auto simple = simplifyFunction(t, inside.has_value() ? inside.value() : false, config);
    std::vector<NFP> offsetpaths = {simple};

    if (std::fabs(offset) > 0) {
        offsetpaths = polygonOffsetDeepNest(simple, offset);
    }

    if (!offsetpaths.empty()) {
        t.Points.clear();
        for (auto& pt : offsetpaths[0].Points) {
            t.Points.push_back(pt);
        }
    }

    if (!simple.children.empty()) {
        for (auto& child : simple.children) {
            t.children.push_back(child);
        }
    }

    if (!t.children.empty()) {
        for (size_t i = 0; i < t.children.size(); i++) {
            std::optional<bool> childInside;
            if (inside.has_value()) {
                childInside = !inside.value();
            } else {
                childInside = true;
            }
            offsetTree(*t.children[i], -offset, config, childInside);
        }
    }
}

NFP NestingEngine::cloneTree(const NFP& tree) {
    NFP newtree;
    newtree.Points = tree.Points;
    newtree.exactFlags = tree.exactFlags;

    if (!tree.children.empty()) {
        for (auto& c : tree.children) {
            newtree.children.push_back(std::make_shared<NFP>(cloneTree(*c)));
        }
    }
    return newtree;
}

void NestingEngine::ResponseProcessor(SheetPlacement payload) {
    if (!ga) return;

    ga->population[payload.index].processing = false;
    ga->population[payload.index].fitness = payload.fitness;

    if (nests.empty() || (nests[0].fitness.has_value() && payload.fitness.has_value() && nests[0].fitness.value() > payload.fitness.value())) {
        nests.insert(nests.begin(), payload);
        if (static_cast<int>(nests.size()) > config.populationSize) {
            nests.pop_back();
        }
    }
}

void NestingEngine::launchWorkers(const std::vector<NestItem>& parts) {
    background.ResponseAction = [this](SheetPlacement sp) { ResponseProcessor(sp); };

    if (!ga) {
        std::vector<std::shared_ptr<NFP>> adam;
        int id = 0;
        for (size_t i = 0; i < parts.size(); i++) {
            if (!parts[i].IsSheet) {
                for (int j = 0; j < parts[i].Quantity; j++) {
                    auto poly = std::make_shared<NFP>(cloneTree(*parts[i].Polygon));
                    poly->Id = id;
                    poly->source = static_cast<int>(i);
                    adam.push_back(poly);
                    id++;
                }
            }
        }

        // Sort by area descending
        std::sort(adam.begin(), adam.end(), [](const std::shared_ptr<NFP>& a, const std::shared_ptr<NFP>& b) {
            return std::fabs(GeometryUtil::polygonArea(*a)) > std::fabs(GeometryUtil::polygonArea(*b));
        });

        ga = std::make_unique<GeneticAlgorithm>(adam, config);
    }

    // Check if current generation is finished
    bool finished = true;
    for (size_t i = 0; i < ga->population.size(); i++) {
        if (!ga->population[i].fitness.has_value()) {
            finished = false;
            break;
        }
    }
    if (finished) {
        ga->generation();
    }

    int running = 0;
    for (auto& p : ga->population) {
        if (p.processing) running++;
    }

    // Build sheets
    std::vector<std::shared_ptr<NFP>> sheets;
    std::vector<int> sheetids;
    std::vector<int> sheetsources;
    std::vector<std::vector<std::shared_ptr<NFP>>> sheetchildren;
    int sid = 0;
    for (size_t i = 0; i < parts.size(); i++) {
        if (parts[i].IsSheet) {
            auto& poly = parts[i].Polygon;
            for (int j = 0; j < parts[i].Quantity; j++) {
                auto cln = std::make_shared<NFP>(cloneTree(*poly));
                cln->Id = sid;
                cln->source = poly->source;
                sheets.push_back(cln);
                sheetids.push_back(sid);
                sheetsources.push_back(static_cast<int>(i));
                sheetchildren.push_back(poly->children);
                sid++;
            }
        }
    }

    for (size_t i = 0; i < ga->population.size(); i++) {
        if (running < 1 && !ga->population[i].processing && !ga->population[i].fitness.has_value()) {
            ga->population[i].processing = true;

            std::vector<int> ids;
            std::vector<int> sources;
            std::vector<std::vector<std::shared_ptr<NFP>>> children;

            for (size_t j = 0; j < ga->population[i].placements.size(); j++) {
                ids.push_back(ga->population[i].placements[j]->Id);
                sources.push_back(ga->population[i].placements[j]->source.value());
                children.push_back(ga->population[i].placements[j]->children);
            }

            DataInfo data;
            data.index = static_cast<int>(i);
            data.sheets = sheets;
            data.sheetids = sheetids;
            data.sheetsources = sheetsources;
            data.sheetchildren = sheetchildren;
            data.individual = ga->population[i];
            data.config = config;
            data.ids = ids;
            data.sources = sources;
            data.children = children;

            background.BackgroundStart(data);
            running++;
        }
    }
}

void NestingEngine::launchWorkersParallel(const std::vector<NestItem>& parts) {
    // ---- GA bootstrap / advance, mirroring launchWorkers' prologue ----
    if (!ga) {
        std::vector<std::shared_ptr<NFP>> adam;
        int id = 0;
        for (size_t i = 0; i < parts.size(); i++) {
            if (!parts[i].IsSheet) {
                for (int j = 0; j < parts[i].Quantity; j++) {
                    auto poly = std::make_shared<NFP>(cloneTree(*parts[i].Polygon));
                    poly->Id = id;
                    poly->source = static_cast<int>(i);
                    adam.push_back(poly);
                    id++;
                }
            }
        }
        std::sort(adam.begin(), adam.end(), [](const std::shared_ptr<NFP>& a, const std::shared_ptr<NFP>& b) {
            return std::fabs(GeometryUtil::polygonArea(*a)) > std::fabs(GeometryUtil::polygonArea(*b));
        });
        ga = std::make_unique<GeneticAlgorithm>(adam, config);
    }

    bool finished = true;
    for (size_t i = 0; i < ga->population.size(); i++) {
        if (!ga->population[i].fitness.has_value()) { finished = false; break; }
    }
    if (finished) ga->generation();

    // ---- Build sheet clones ONCE; assign Id/source/children once (read-only during eval) ----
    std::vector<std::shared_ptr<NFP>> sheets;
    std::vector<int> sheetids;
    std::vector<int> sheetsources;
    std::vector<std::vector<std::shared_ptr<NFP>>> sheetchildren;
    int sid = 0;
    for (size_t i = 0; i < parts.size(); i++) {
        if (parts[i].IsSheet) {
            auto& poly = parts[i].Polygon;
            for (int j = 0; j < parts[i].Quantity; j++) {
                auto cln = std::make_shared<NFP>(cloneTree(*poly));
                cln->Id = sid;
                cln->source = poly->source;
                cln->children = poly->children;   // identical for every candidate -> set once here
                sheets.push_back(cln);
                sheetids.push_back(sid);
                sheetsources.push_back(static_cast<int>(i));
                sheetchildren.push_back(poly->children);
                sid++;
            }
        }
    }

    // ---- Collect pending candidates and build per-candidate DataInfo with DEEP-CLONED placements ----
    // GA individuals share the same shared_ptr<NFP> objects (mutate/mate copy the vector, not the
    // polygons), and evaluateCandidate writes Rotation/Id/source on them, so each parallel candidate
    // MUST own private clones.
    std::vector<int> pending;
    for (size_t i = 0; i < ga->population.size(); i++) {
        if (!ga->population[i].processing && !ga->population[i].fitness.has_value())
            pending.push_back(static_cast<int>(i));
    }
    if (pending.empty()) return;

    std::vector<DataInfo> datas(pending.size());
    for (size_t k = 0; k < pending.size(); k++) {
        int i = pending[k];
        ga->population[i].processing = true;
        auto& srcInd = ga->population[i];

        DataInfo d;
        d.index = i;
        d.sheets = sheets;                  // shared, read-only
        d.sheetids = sheetids;
        d.sheetsources = sheetsources;
        d.sheetchildren = sheetchildren;
        d.config = config;
        d.individual.Rotation = srcInd.Rotation;
        d.individual.placements.reserve(srcInd.placements.size());
        for (size_t j = 0; j < srcInd.placements.size(); j++) {
            auto cl = std::make_shared<NFP>(cloneTree(*srcInd.placements[j]));
            d.ids.push_back(srcInd.placements[j]->Id);
            d.sources.push_back(srcInd.placements[j]->source.value());
            d.children.push_back(cl->children);   // isolated clone children
            d.individual.placements.push_back(cl);
        }
        datas[k] = std::move(d);
    }

    std::vector<SheetPlacement> results(datas.size());
    const bool prof = std::getenv("NFP_PROFILE") != nullptr;
    bool savedParallel = NfpWorker::UseParallel;

    // ---- Pre-warm: the union of ALL candidates' outer part-pair NFPs, computed in ONE 32-core batch.
    // The outer-NFP cache key is order-sensitive (NFP(A,B) != NFP(B,A)); GA permutes part order, so each
    // candidate would otherwise recompute its own permuted-order pairs serially. Computing the union once
    // (in parallel) means every candidate's placeParts then reads a warm cache. process() is the same
    // function the serial path uses, so the cache — and the final result — are identical to serial. ----
    auto ptw = std::chrono::steady_clock::now();
    {
        std::vector<NfpPair> unionPairs;
        std::unordered_set<uint64_t> seen;
        auto mkKey = [](int as, int bs, float ar, float br) -> uint64_t {
            uint64_t k = 1469598103934665603ULL;
            auto mix = [&](uint64_t v) { k ^= v; k *= 1099511628211ULL; };
            mix(static_cast<uint32_t>(as));
            mix(static_cast<uint32_t>(bs));
            mix(static_cast<uint32_t>(static_cast<int32_t>(std::lround(ar * 10000))));
            mix(static_cast<uint32_t>(static_cast<int32_t>(std::lround(br * 10000))));
            return k;
        };
        for (auto& d : datas) {
            int n = static_cast<int>(d.individual.placements.size());
            for (int i = 0; i < n; i++) {
                for (int j = 0; j < i; j++) {
                    int as = d.sources[j], bs = d.sources[i];
                    float ar = d.individual.Rotation[j], br = d.individual.Rotation[i];
                    uint64_t key = mkKey(as, bs, ar, br);
                    if (!seen.insert(key).second) continue;
                    DbCacheKey doc;
                    doc.A = as; doc.B = bs; doc.ARotation = ar; doc.BRotation = br;
                    if (background.window.db->has(doc)) continue;  // already cached from a previous generation
                    NfpPair np;
                    np.A = d.individual.placements[j].get();  // unrotated clone; process() rotates by ARotation
                    np.B = d.individual.placements[i].get();
                    np.Asource = as; np.Bsource = bs;
                    np.ARotation = ar; np.BRotation = br;
                    unionPairs.push_back(np);
                }
            }
        }

        if (!unionPairs.empty()) {
            NfpWorker::UseParallel = true;                 // saturate all cores for the batch
            auto computed = background.pmapDeepNest(unionPairs);
            for (auto& pr : computed) {
                DbCacheKey doc;
                doc.A = pr.Asource; doc.B = pr.Bsource;
                doc.ARotation = pr.ARotation; doc.BRotation = pr.BRotation;
                if (pr.nfp) doc.nfp = { pr.nfp };
                background.window.db->insert(doc);
            }
        }
        if (prof) {
            double ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-ptw).count();
            std::fprintf(stderr, "  [gen] prewarm(%zu union pairs)=%.0fms  pending=%zu\n",
                         unionPairs.size(), ms, datas.size());
        }
    }

    // ---- Evaluate all candidates' placeParts concurrently. Outer NFPs are warm (cache reads); the few
    //      inner sheet-NFPs are computed lazily but distributed across candidate threads. ----
    auto ptp = std::chrono::steady_clock::now();
    {
        NfpWorker::UseParallel = false;                   // no nested threads inside a candidate
        int total = static_cast<int>(datas.size());
        std::atomic<int> nextIdx{0};
        int hw = static_cast<int>(std::thread::hardware_concurrency());
        int nthreads = std::min(hw > 0 ? hw : 1, total);
        if (const char* tEnv = std::getenv("NFP_THREADS")) {   // experimentation knob
            int t = std::atoi(tEnv);
            if (t > 0) nthreads = std::min(t, total);
        }
        if (nthreads < 1) nthreads = 1;

        auto worker = [&]() {
            for (;;) {
                int k = nextIdx.fetch_add(1);
                if (k >= total) break;
                results[k] = background.evaluateCandidate(datas[k]);
            }
        };
        std::vector<std::thread> threads;
        for (int t = 1; t < nthreads; t++) threads.emplace_back(worker);
        worker();
        for (auto& th : threads) th.join();
    }
    NfpWorker::UseParallel = savedParallel;
    if (prof) {
        double ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-ptp).count();
        std::fprintf(stderr, "  [gen] place(%zu cands parallel)=%.0fms  cacheKeys=%zu\n",
                     datas.size(), ms, background.cacheProcess.size());
    }

    // ---- Apply results serially (ResponseProcessor mutates ga->population + nests) ----
    for (size_t k = 0; k < results.size(); k++) {
        ResponseProcessor(results[k]);
    }
}

} // namespace nest
