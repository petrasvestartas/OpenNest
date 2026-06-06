#include "MinkowskiConvolution.h"

#include <boost/polygon/polygon.hpp>
#include <vector>
#include <cmath>
#include <limits>

// Boost.Polygon pulls in <windows.h>-style min/max on some toolchains; defend
// against the macros so (std::min)/(std::max) below are well-formed.
#undef min
#undef max

namespace nest {

namespace {

using bpoint      = boost::polygon::point_data<int>;
using polygon_set = boost::polygon::polygon_set_data<int>;
using bpolygon    = boost::polygon::polygon_with_holes_data<int>;
using bedge       = std::pair<bpoint, bpoint>;
using namespace boost::polygon::operators;

// --- Edge-pair convolution kernel (direct port of minkowski.cc) ---------------

void convolve_two_segments(std::vector<bpoint>& figure, const bedge& a, const bedge& b) {
    using namespace boost::polygon;
    figure.clear();
    figure.push_back(bpoint(a.first));
    figure.push_back(bpoint(a.first));
    figure.push_back(bpoint(a.second));
    figure.push_back(bpoint(a.second));
    convolve(figure[0], b.second);
    convolve(figure[1], b.first);
    convolve(figure[2], b.first);
    convolve(figure[3], b.second);
}

template <typename itrT1, typename itrT2>
void convolve_two_point_sequences(polygon_set& result, itrT1 ab, itrT1 ae, itrT2 bb, itrT2 be) {
    using namespace boost::polygon;
    if (ab == ae || bb == be) return;
    bpoint prev_a = *ab;
    std::vector<bpoint> vec;
    bpolygon poly;
    ++ab;
    for (; ab != ae; ++ab) {
        bpoint prev_b = *bb;
        itrT2 tmpb = bb;
        ++tmpb;
        for (; tmpb != be; ++tmpb) {
            convolve_two_segments(vec, std::make_pair(prev_b, *tmpb), std::make_pair(prev_a, *ab));
            set_points(poly, vec.begin(), vec.end());
            result.insert(poly);
            prev_b = *tmpb;
        }
        prev_a = *ab;
    }
}

template <typename itrT>
void convolve_point_sequence_with_polygons(polygon_set& result, itrT b, itrT e,
                                           const std::vector<bpolygon>& polygons) {
    using namespace boost::polygon;
    for (std::size_t i = 0; i < polygons.size(); ++i) {
        convolve_two_point_sequences(result, b, e, begin_points(polygons[i]), end_points(polygons[i]));
        for (polygon_with_holes_traits<bpolygon>::iterator_holes_type itrh = begin_holes(polygons[i]);
             itrh != end_holes(polygons[i]); ++itrh) {
            convolve_two_point_sequences(result, b, e, begin_points(*itrh), end_points(*itrh));
        }
    }
}

void convolve_two_polygon_sets(polygon_set& result, const polygon_set& a, const polygon_set& b) {
    using namespace boost::polygon;
    result.clear();
    std::vector<bpolygon> a_polygons;
    std::vector<bpolygon> b_polygons;
    a.get(a_polygons);
    b.get(b_polygons);
    for (std::size_t ai = 0; ai < a_polygons.size(); ++ai) {
        convolve_point_sequence_with_polygons(result, begin_points(a_polygons[ai]),
                                              end_points(a_polygons[ai]), b_polygons);
        for (polygon_with_holes_traits<bpolygon>::iterator_holes_type itrh = begin_holes(a_polygons[ai]);
             itrh != end_holes(a_polygons[ai]); ++itrh) {
            convolve_point_sequence_with_polygons(result, begin_points(*itrh),
                                                  end_points(*itrh), b_polygons);
        }
        for (std::size_t bi = 0; bi < b_polygons.size(); ++bi) {
            bpolygon tmp_poly = a_polygons[ai];
            result.insert(convolve(tmp_poly, *(begin_points(b_polygons[bi]))));
            tmp_poly = b_polygons[bi];
            result.insert(convolve(tmp_poly, *(begin_points(a_polygons[ai]))));
        }
    }
}

// Drop consecutive-duplicate and strictly-collinear vertices from a closed
// ring (flat x,y,...). Boost.Polygon leaves redundant collinear points where
// the convolution of two parallel edges meets; removing them yields the minimal
// polygon (e.g. a true hexagon for tri+tri) and speeds up downstream clipping.
// This is the same "clean polygon" step deepnest applies to NFP output.
std::vector<double> cleanRing(const std::vector<double>& flat) {
    size_t n = flat.size() / 2;
    if (n < 3) return flat;
    std::vector<double> xs(n), ys(n);
    for (size_t i = 0; i < n; i++) { xs[i] = flat[2*i]; ys[i] = flat[2*i+1]; }
    // Collapse the explicit closing vertex if present.
    if (n >= 2 && std::fabs(xs[0]-xs[n-1]) < 1e-7 && std::fabs(ys[0]-ys[n-1]) < 1e-7) {
        xs.pop_back(); ys.pop_back(); n--;
    }
    // Scale-relative tolerance for the collinearity (triangle-area) test.
    double ext = 0;
    for (size_t i = 0; i < n; i++) ext = (std::max)(ext, (std::max)(std::fabs(xs[i]), std::fabs(ys[i])));
    double areaEps = (ext > 0 ? ext : 1.0) * 1e-7;
    double dupEps  = (ext > 0 ? ext : 1.0) * 1e-9;

    std::vector<double> ox, oy;
    ox.reserve(n); oy.reserve(n);
    for (size_t i = 0; i < n; i++) {
        // skip exact duplicate of previous kept vertex
        if (!ox.empty() && std::fabs(xs[i]-ox.back()) < dupEps && std::fabs(ys[i]-oy.back()) < dupEps)
            continue;
        ox.push_back(xs[i]); oy.push_back(ys[i]);
    }
    // wrap-around duplicate
    if (ox.size() >= 2 && std::fabs(ox.front()-ox.back()) < dupEps && std::fabs(oy.front()-oy.back()) < dupEps) {
        ox.pop_back(); oy.pop_back();
    }
    // collinearity removal (iterate until stable, considering wrap-around)
    bool changed = true;
    while (changed && ox.size() > 3) {
        changed = false;
        size_t m = ox.size();
        std::vector<double> nx, ny;
        nx.reserve(m); ny.reserve(m);
        for (size_t i = 0; i < m; i++) {
            size_t p = (i + m - 1) % m, q = (i + 1) % m;
            double cross = (ox[i]-ox[p]) * (oy[q]-oy[p]) - (oy[i]-oy[p]) * (ox[q]-ox[p]);
            if (std::fabs(cross) <= areaEps) { changed = true; continue; } // drop collinear i
            nx.push_back(ox[i]); ny.push_back(oy[i]);
        }
        if (nx.size() < 3) break; // never collapse below a triangle
        ox.swap(nx); oy.swap(ny);
    }
    std::vector<double> out;
    out.reserve(ox.size() * 2);
    for (size_t i = 0; i < ox.size(); i++) { out.push_back(ox[i]); out.push_back(oy[i]); }
    return out;
}

} // namespace

MinkowskiConvolution::Result MinkowskiConvolution::compute(
    const std::vector<double>& Avec,
    const std::vector<std::vector<double>>& Aholes,
    const std::vector<double>& Bvec)
{
    using namespace boost::polygon;
    Result result;
    if (Avec.size() < 6 || Bvec.size() < 4) return result;

    polygon_set a, b, c;
    std::vector<bpolygon> polys;
    std::vector<bpoint> pts;

    // --- dynamic scaling (matches minkowski.cc to keep parity with minkowski.dll) ---
    // NOTE: bounds are seeded at 0 like the original, so they always include the
    // origin. This only affects the chosen integer scale (precision), not the
    // unscaled result, and reproduces minkowski.dll's behaviour exactly.
    unsigned int len = static_cast<unsigned int>(Avec.size() / 2);
    double Amaxx = 0, Aminx = 0, Amaxy = 0, Aminy = 0;
    for (unsigned int i = 0; i < len; i++) {
        double x1 = Avec[i * 2], y1 = Avec[i * 2 + 1];
        Amaxx = (std::max)(Amaxx, x1); Aminx = (std::min)(Aminx, x1);
        Amaxy = (std::max)(Amaxy, y1); Aminy = (std::min)(Aminy, y1);
    }
    len = static_cast<unsigned int>(Bvec.size() / 2);
    double Bmaxx = 0, Bminx = 0, Bmaxy = 0, Bminy = 0;
    for (unsigned int i = 0; i < len * 2; i += 2) {
        double x1 = Bvec[i], y1 = Bvec[i + 1];
        Bmaxx = (std::max)(Bmaxx, x1); Bminx = (std::min)(Bminx, x1);
        Bmaxy = (std::max)(Bmaxy, y1); Bminy = (std::min)(Bminy, y1);
    }
    double Cmaxx = Amaxx + Bmaxx, Cminx = Aminx + Bminx;
    double Cmaxy = Amaxy + Bmaxy, Cminy = Aminy + Bminy;
    double maxxAbs = (std::max)(Cmaxx, std::fabs(Cminx));
    double maxyAbs = (std::max)(Cmaxy, std::fabs(Cminy));
    double maxda = (std::max)(maxxAbs, maxyAbs);
    int maxi = std::numeric_limits<int>::max();
    if (maxda < 1) maxda = 1;
    double inputscale = (0.1 * static_cast<double>(maxi)) / maxda;

    // --- polygon A (outer ring minus holes) ---
    len = static_cast<unsigned int>(Avec.size() / 2);
    pts.clear();
    for (unsigned int i = 0; i < len; i++) {
        int x = static_cast<int>(inputscale * Avec[i * 2]);
        int y = static_cast<int>(inputscale * Avec[i * 2 + 1]);
        pts.push_back(bpoint(x, y));
    }
    bpolygon poly;
    set_points(poly, pts.begin(), pts.end());
    a += poly;

    for (const auto& hole : Aholes) {
        if (hole.size() < 6) continue; // need >=3 vertices
        pts.clear();
        unsigned int hlen = static_cast<unsigned int>(hole.size() / 2);
        for (unsigned int j = 0; j < hlen; j++) {
            int x = static_cast<int>(inputscale * hole[j * 2]);
            int y = static_cast<int>(inputscale * hole[j * 2 + 1]);
            pts.push_back(bpoint(x, y));
        }
        set_points(poly, pts.begin(), pts.end());
        a -= poly;
    }

    // --- polygon B (negated), referenced to its first vertex ---
    pts.clear();
    len = static_cast<unsigned int>(Bvec.size() / 2);
    double xshift = (Bvec.size() >= 2) ? Bvec[0] : 0.0;
    double yshift = (Bvec.size() >= 2) ? Bvec[1] : 0.0;
    for (unsigned int i = 0; i < len; i++) {
        int x = -static_cast<int>(inputscale * Bvec[i * 2]);
        int y = -static_cast<int>(inputscale * Bvec[i * 2 + 1]);
        pts.push_back(bpoint(x, y));
    }
    set_points(poly, pts.begin(), pts.end());
    b += poly;

    // --- convolve and read back ---
    convolve_two_polygon_sets(c, a, b);
    c.get(polys);

    for (std::size_t i = 0; i < polys.size(); ++i) {
        std::vector<double> pointlist;
        for (polygon_traits<bpolygon>::iterator_type itr = polys[i].begin(); itr != polys[i].end(); ++itr) {
            double x1 = static_cast<double>((*itr).get(HORIZONTAL)) / inputscale + xshift;
            double y1 = static_cast<double>((*itr).get(VERTICAL)) / inputscale + yshift;
            pointlist.push_back(x1);
            pointlist.push_back(y1);
        }
        result.outerPaths.push_back(cleanRing(pointlist));

        for (polygon_with_holes_traits<bpolygon>::iterator_holes_type itrh = begin_holes(polys[i]);
             itrh != end_holes(polys[i]); ++itrh) {
            std::vector<double> child;
            for (polygon_traits<bpolygon>::iterator_type itr2 = (*itrh).begin(); itr2 != (*itrh).end(); ++itr2) {
                double x1 = static_cast<double>((*itr2).get(HORIZONTAL)) / inputscale + xshift;
                double y1 = static_cast<double>((*itr2).get(VERTICAL)) / inputscale + yshift;
                child.push_back(x1);
                child.push_back(y1);
            }
            result.holes.push_back(cleanRing(child));
        }
    }

    return result;
}

} // namespace nest
