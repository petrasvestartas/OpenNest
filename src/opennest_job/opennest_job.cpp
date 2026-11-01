// opennest_job: run one OpenNest nesting job from a JSON file and write the result as JSON.
//
//     opennest_job <job.json|-> <result.json|-> [--events <events.ndjson>] [--events-layout]
//                  [--interval <s>] [--max-wall <s>] [--quiet]
//
// This is the server-side worker entry point: one process per job, no GUI, no Rhino, standard library only.
// It links the nest_physics solver directly (nest_physics_capi.cpp is compiled into this executable), so the
// same C ABI the Grasshopper plugin P/Invokes is exercised here. Schemas: see README.md next to this file.
//
// Exit codes: 0 ok or partial (some parts unplaced; see "status"), 1 error (invalid job or solver failure),
// 2 usage, 124 cancelled (wall cap or signal; the best-so-far layout is still written).
#include "json_mini.hpp"
#include "nest_physics_capi.h"
#include "clipper2/clipper.h"   // vendored in src/opennest_cpp/src (spacing / margin offsets)

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <csignal>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <limits>
#include <memory>
#include <mutex>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#ifndef OPENNEST_JOB_VERSION
#define OPENNEST_JOB_VERSION "0.1.0"
#endif

namespace {

using opennest::json::Value;
namespace js = opennest::json;

constexpr const char* kJobSchema = "opennest.job/1";
constexpr const char* kResultSchema = "opennest.result/1";
constexpr const char* kFrame =
    "sheet coordinates: rotate the input part about the origin by angle (radians, counter-clockwise), then "
    "translate by (tx, ty); the result lies in the coordinates the sheet's outer ring was given in";

std::atomic<bool> g_signal_stop{false};

extern "C" void on_signal(int) {
    g_signal_stop.store(true);
    np_cancel();   // lock-free atomic store inside the solver: async-signal-safe
}

std::string compiler_id() {
#if defined(__clang__)
    return std::string("clang ") + __clang_version__;
#elif defined(__GNUC__)
    return std::string("gcc ") + __VERSION__;
#elif defined(_MSC_VER)
    return "msvc " + std::to_string(_MSC_FULL_VER);
#else
    return "unknown";
#endif
}

std::string platform_id() {
#if defined(_WIN32)
    return "windows";
#elif defined(__APPLE__)
    return "macos";
#elif defined(__linux__)
    return "linux";
#else
    return "unknown";
#endif
}

// ---------------------------------------------------------------------------------------------------------
// Job model
// ---------------------------------------------------------------------------------------------------------
using Ring = std::vector<double>;   // interleaved x,y, open (no closing duplicate)

double ring_area(const Ring& r) {
    double a = 0.0;
    size_t n = r.size() / 2;
    for (size_t i = 0, j = n - 1; i < n; j = i++) a += r[2 * j] * r[2 * i + 1] - r[2 * i] * r[2 * j + 1];
    return 0.5 * a;
}

struct BBox { double x0, y0, x1, y1; };
BBox bbox_of(const Ring& r) {
    BBox b{r[0], r[1], r[0], r[1]};
    for (size_t k = 0; k + 1 < r.size(); k += 2) {
        b.x0 = std::min(b.x0, r[k]); b.x1 = std::max(b.x1, r[k]);
        b.y0 = std::min(b.y0, r[k + 1]); b.y1 = std::max(b.y1, r[k + 1]);
    }
    return b;
}

// False for a ring that encloses (almost) nothing relative to its own extent.
bool ring_has_area(const Ring& r) {
    if (r.size() < 6) return false;
    BBox b = bbox_of(r);
    double ext2 = (b.x1 - b.x0) * (b.x1 - b.x0) + (b.y1 - b.y0) * (b.y1 - b.y0);
    return std::fabs(ring_area(r)) > 1e-9 * ext2 + 1e-12;
}

struct PartSpec {
    Value id;
    int qty = 1;
    int rotations = 0;
    Ring outer;
    std::vector<Ring> holes;
    double shift_x = 0.0, shift_y = 0.0;   // input bbox min: the rings above are relative to it (see prepare_geometry)
};

struct SheetSpec {
    Value id;
    int count = 1;
    Ring outer;                           // after prepare_geometry: the usable area's bounding rectangle
    std::vector<Ring> holes;              // after prepare_geometry: input holes + keep-outs outside the outline
    double shift_x = 0.0, shift_y = 0.0;
};

struct Job {
    std::vector<PartSpec> parts;
    std::vector<SheetSpec> sheets;
    NpParams params{};
    double wall_seconds = 0.0;   // 0 = no cap from the job
    double spacing = 0.0;        // minimum distance between parts (applied here as offsets)
    double margin = 0.0;         // minimum distance from a part to a sheet edge or sheet hole
};

int as_int(const Value& v, const std::string& what, long long lo, long long hi) {
    double d = v.as_number(what.c_str());
    if (!std::isfinite(d) || d != std::floor(d)) throw std::runtime_error(what + " must be an integer");
    if (d < double(lo) || d > double(hi))
        throw std::runtime_error(what + " must be in [" + std::to_string(lo) + ", " + std::to_string(hi) + "]");
    return int(d);
}

Ring read_ring(const Value& v, const std::string& what) {
    const auto& pts = v.as_array(what.c_str());
    Ring r;
    r.reserve(pts.size() * 2);
    for (size_t k = 0; k < pts.size(); ++k) {
        const auto& p = pts[k].as_array((what + "[" + std::to_string(k) + "]").c_str());
        if (p.size() != 2) throw std::runtime_error(what + "[" + std::to_string(k) + "] must be [x, y]");
        double x = p[0].as_number("x"), y = p[1].as_number("y");
        if (!std::isfinite(x) || !std::isfinite(y)) throw std::runtime_error(what + " has a non-finite coordinate");
        r.push_back(x);
        r.push_back(y);
    }
    size_t n = r.size() / 2;
    if (n > 1 && r[0] == r[2 * (n - 1)] && r[1] == r[2 * (n - 1) + 1]) r.resize(r.size() - 2);   // closing duplicate
    if (r.size() / 2 < 3) throw std::runtime_error(what + " needs at least 3 distinct vertices");
    return r;
}

std::vector<Ring> read_holes(const Value* v, const std::string& what) {
    std::vector<Ring> out;
    if (!v || v->is_null()) return out;
    const auto& arr = v->as_array(what.c_str());
    for (size_t k = 0; k < arr.size(); ++k) {
        // A hole that encloses nothing (fewer than 3 distinct vertices, or collinear) is ignored rather
        // than rejecting the job: CAD exports and sampled outlines produce them. Everything else that is
        // wrong with a hole (not numbers, not a ring) is still an error.
        Ring r;
        try {
            r = read_ring(arr[k], what + "[" + std::to_string(k) + "]");
        } catch (const std::runtime_error& e) {
            if (std::strstr(e.what(), "at least 3 distinct")) continue;
            throw;
        }
        if (!ring_has_area(r)) continue;
        out.push_back(std::move(r));
    }
    return out;
}

void check_keys(const Value& obj, const std::string& what, std::initializer_list<const char*> allowed) {
    if (!obj.is_object()) throw std::runtime_error(what + " must be an object");
    for (const auto& kv : *obj.obj) {
        bool ok = false;
        for (const char* a : allowed) ok |= (kv.first == a);
        if (!ok) throw std::runtime_error(what + ": unknown key '" + kv.first + "'");
    }
}

void read_params(const Value* pv, Job& job, int total_sheets) {
    NpParams& p = job.params;
    std::memset(&p, 0, sizeof p);
    // Defaults = the Grasshopper component's call (tools/bench3dm/configs.json "gh_np").
    p.num_rotations = 3600;
    p.iter_mode = 1;
    p.iter_budget = 4000;
    p.max_sheets = total_sheets;
    p.n_starts = 1;
    p.part_holes_mode = 1;
    p.pole_max = 48;
    p.final_compact = 1;
    p.fit_mode = 0;
    if (!pv || pv->is_null()) return;
    check_keys(*pv, "params", {"num_rotations", "spacing", "simplify_tolerance", "seed", "time_budget_secs",
                               "iter_budget", "iter_mode", "max_sheets", "n_starts", "part_holes_mode",
                               "pole_max", "final_compact", "fit_mode", "n_threads"});
    auto num = [&](const char* k, double& dst) {
        if (const Value* v = pv->find(k)) {
            double d = v->as_number(k);
            if (!std::isfinite(d) || d < 0) throw std::runtime_error(std::string("params.") + k + " must be finite and >= 0");
            dst = d;
        }
    };
    auto integer = [&](const char* k, int& dst, long long lo, long long hi) {
        if (const Value* v = pv->find(k)) dst = as_int(*v, std::string("params.") + k, lo, hi);
    };
    integer("num_rotations", p.num_rotations, 1, 1000000);
    num("spacing", p.spacing);
    num("simplify_tolerance", p.simplify_tolerance);
    integer("seed", p.seed, 0, 2147483647);
    num("time_budget_secs", p.time_budget_secs);
    if (const Value* v = pv->find("iter_budget")) {
        double d = v->as_number("params.iter_budget");
        if (d != std::floor(d) || d < 1 || d > 1e12) throw std::runtime_error("params.iter_budget must be an integer in [1, 1e12]");
        p.iter_budget = (long long)d;
    }
    integer("iter_mode", p.iter_mode, 0, 1);
    integer("max_sheets", p.max_sheets, 0, 100000);
    integer("n_starts", p.n_starts, 0, 1024);
    integer("part_holes_mode", p.part_holes_mode, 0, 2);
    integer("pole_max", p.pole_max, 0, 4096);
    integer("final_compact", p.final_compact, 0, 1);
    integer("fit_mode", p.fit_mode, 0, 3);
    integer("n_threads", p.n_threads, 0, 1024);
    if (p.iter_mode == 0 && p.time_budget_secs <= 0)
        throw std::runtime_error("params.time_budget_secs must be > 0 when iter_mode is 0 (wall-clock)");
}

Job read_job(const Value& root) {
    check_keys(root, "job", {"schema", "units", "name", "spacing", "margin", "parts", "sheets", "params", "limits"});
    const Value* schema = root.find("schema");
    if (!schema || !schema->is_string() || schema->str != kJobSchema)
        throw std::runtime_error(std::string("job.schema must be \"") + kJobSchema + "\"");
    Job job;
    const Value* parts = root.find("parts");
    if (!parts) throw std::runtime_error("job.parts is required");
    const auto& pa = parts->as_array("job.parts");
    if (pa.empty()) throw std::runtime_error("job.parts is empty");
    for (size_t k = 0; k < pa.size(); ++k) {
        std::string w = "parts[" + std::to_string(k) + "]";
        check_keys(pa[k], w, {"id", "name", "qty", "rotations", "outer", "holes"});
        PartSpec ps;
        if (const Value* id = pa[k].find("id")) ps.id = *id; else { ps.id.kind = Value::Kind::Number; ps.id.num = double(k); }
        if (const Value* q = pa[k].find("qty")) ps.qty = as_int(*q, w + ".qty", 0, 1000000);
        if (const Value* r = pa[k].find("rotations")) ps.rotations = as_int(*r, w + ".rotations", 0, 1000000);
        const Value* outer = pa[k].find("outer");
        if (!outer) throw std::runtime_error(w + ".outer is required");
        ps.outer = read_ring(*outer, w + ".outer");
        ps.holes = read_holes(pa[k].find("holes"), w + ".holes");
        job.parts.push_back(std::move(ps));
    }
    const Value* sheets = root.find("sheets");
    if (!sheets) throw std::runtime_error("job.sheets is required");
    const auto& sa = sheets->as_array("job.sheets");
    if (sa.empty()) throw std::runtime_error("job.sheets is empty");
    int total_sheets = 0;
    for (size_t k = 0; k < sa.size(); ++k) {
        std::string w = "sheets[" + std::to_string(k) + "]";
        check_keys(sa[k], w, {"id", "name", "count", "outer", "holes"});
        SheetSpec ss;
        if (const Value* id = sa[k].find("id")) ss.id = *id; else { ss.id.kind = Value::Kind::Number; ss.id.num = double(k); }
        if (const Value* c = sa[k].find("count")) ss.count = as_int(*c, w + ".count", 0, 100000);
        const Value* outer = sa[k].find("outer");
        if (!outer) throw std::runtime_error(w + ".outer is required");
        ss.outer = read_ring(*outer, w + ".outer");
        ss.holes = read_holes(sa[k].find("holes"), w + ".holes");
        total_sheets += ss.count;
        job.sheets.push_back(std::move(ss));
    }
    if (total_sheets < 1) throw std::runtime_error("job.sheets has no sheet with count >= 1");
    read_params(root.find("params"), job, total_sheets);
    for (const char* k : {"spacing", "margin"}) {
        if (const Value* v = root.find(k)) {
            double d = v->as_number(k);
            if (!std::isfinite(d) || d < 0) throw std::runtime_error(std::string("job.") + k + " must be finite and >= 0");
            (std::string(k) == "spacing" ? job.spacing : job.margin) = d;
        }
    }
    if (const Value* lim = root.find("limits"); lim && !lim->is_null()) {
        check_keys(*lim, "limits", {"wall_seconds"});
        if (const Value* ws = lim->find("wall_seconds")) {
            job.wall_seconds = ws->as_number("limits.wall_seconds");
            if (!std::isfinite(job.wall_seconds) || job.wall_seconds < 0)
                throw std::runtime_error("limits.wall_seconds must be finite and >= 0");
        }
    }
    return job;
}

// ---------------------------------------------------------------------------------------------------------
// Solve geometry. Three things happen to every ring before it reaches the solver.
//
// 1. FRAME. Each part (outer + holes together) and each sheet (outer + holes) is moved to its own
//    bounding-box minimum, in double. The solver casts coordinates to float32 as it reads them
//    (nest_physics_capi.cpp read_poly), so a part drawn 50 m from the origin would be quantised to
//    4 um; near its own origin the grid is ~1000x finer. The shift is folded back into the returned
//    transforms (placed = R*(p - m) + t = R*p + (t - R*m)).
//
// 2. CLEARANCES. nest_physics ignores NpParams.spacing, so round-join offsets are applied here like
//    the Grasshopper intake (nest_geo.offset_nesting_boundary): a part outer grows by spacing/2, a
//    part hole shrinks by spacing/2, a sheet outer shrinks by margin - spacing/2 and a sheet hole grows
//    by it (the other way round when margin < spacing/2). Every offset is pushed a little further on the
//    SAFE side - obstacles bigger, free regions smaller - by the arc-chord sagitta `tol` plus the
//    simplification distance `eps` of step 3, so neither can eat into the requested clearance.
//
// 3. SOLVER-SAFE RINGS. The solver rejects a ring whose non-adjacent edges touch after float32
//    rounding (geometry/polygon.hpp Polygon::create) and silently DROPS a sheet keep-out that fails.
//    Offsets of sampled outlines produce such rings (vertices closer than the float grid, micro-
//    zigzags). Each ring is therefore simplified with Ramer-Douglas-Peucker (deviation <= eps) and
//    checked with a copy of the solver's own float32 edge test in the solver's own frame; a ring that
//    still touches is remade with doubled eps, up to half the offset distance, after which the job is
//    rejected naming the ring. Nothing is dropped silently.
//
// The solver reads a sheet outer only as a bounding box, so a non-rectangular sheet is passed as that
// box plus keep-outs = box minus the (shrunk) outline.
// ---------------------------------------------------------------------------------------------------------
void shift_ring(Ring& r, double dx, double dy) {
    for (size_t k = 0; k + 1 < r.size(); k += 2) { r[k] += dx; r[k + 1] += dy; }
}

// The float32 bbox minimum the solver subtracts from a ring's float32 coordinates (build_part / the
// sheet reader use min_f over the float values, not a double minimum cast afterwards).
std::pair<float, float> f32_frame(const Ring& r) {
    float fx = std::numeric_limits<float>::max(), fy = fx;
    for (size_t k = 0; k + 1 < r.size(); k += 2) {
        float x = float(r[k]), y = float(r[k + 1]);
        fx = x < fx ? x : fx;
        fy = y < fy ? y : fy;
    }
    return {fx, fy};
}

// The solver's edge test, float32 arithmetic included (geometry/primitives.hpp edge_intersection).
bool f32_edges_touch(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4) {
    auto mn = [](float a, float b) { return a < b ? a : b; };
    auto mx = [](float a, float b) { return a > b ? a : b; };
    if (mx(mn(x1, x2), mn(x3, x4)) > mn(mx(x1, x2), mx(x3, x4))) return false;
    if (mx(mn(y1, y2), mn(y3, y4)) > mn(mx(y1, y2), mx(y3, y4))) return false;
    float t_nom = (x2 - x4) * (y4 - y3) - (y2 - y4) * (x4 - x3);
    float t_den = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    float u_nom = (x2 - x4) * (y2 - y1) - (y2 - y4) * (x2 - x1);
    float u_den = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
    if (t_den == 0.0f || u_den == 0.0f) return false;   // parallel: the solver accepts these
    float t = t_nom / t_den, u = u_nom / u_den;
    return t >= 0.0f && t <= 1.0f && u >= 0.0f && u <= 1.0f;
}

// True when the solver would reject the ring: cast to float32 as it reads, subtract the float frame
// origin (fx, fy), drop consecutive duplicates (clean_ring), then test every pair of non-adjacent
// edges with the solver's edge test. A swept pair loop keeps this near-linear for long outlines.
bool solver_rejects(const Ring& r, float fx, float fy) {
    std::vector<float> x, y;
    x.reserve(r.size() / 2);
    y.reserve(r.size() / 2);
    for (size_t k = 0; k + 1 < r.size(); k += 2) {
        float px = float(r[k]) - fx, py = float(r[k + 1]) - fy;
        if (!x.empty() && x.back() == px && y.back() == py) continue;
        x.push_back(px);
        y.push_back(py);
    }
    if (x.size() >= 2 && x.front() == x.back() && y.front() == y.back()) { x.pop_back(); y.pop_back(); }
    const size_t n = x.size();
    if (n < 3) return true;
    std::vector<size_t> order(n);
    for (size_t k = 0; k < n; ++k) order[k] = k;
    auto lo = [&](size_t e) { return std::min(x[e], x[(e + 1) % n]); };
    auto hi = [&](size_t e) { return std::max(x[e], x[(e + 1) % n]); };
    std::sort(order.begin(), order.end(), [&](size_t a, size_t b) { return lo(a) < lo(b); });
    for (size_t ia = 0; ia < n; ++ia) {
        const size_t i = order[ia], i2 = (i + 1) % n;
        for (size_t ib = ia + 1; ib < n && lo(order[ib]) <= hi(i); ++ib) {
            const size_t j = order[ib], j2 = (j + 1) % n;
            if (j == i2 || i == j2) continue;   // neighbours share a vertex (the solver skips them too)
            if (f32_edges_touch(x[i], y[i], x[i2], y[i2], x[j], y[j], x[j2], y[j2])) return true;
        }
    }
    return false;
}

Clipper2Lib::Path64 to_path64(const Ring& r, double scale) {
    Clipper2Lib::Path64 p;
    p.reserve(r.size() / 2);
    for (size_t k = 0; k + 1 < r.size(); k += 2)
        p.emplace_back(int64_t(std::llround(r[k] * scale)), int64_t(std::llround(r[k + 1] * scale)));
    return p;
}

Ring to_ring(const Clipper2Lib::Path64& q, double scale) {
    Ring c;
    c.reserve(q.size() * 2);
    for (const auto& pt : q) { c.push_back(double(pt.x) / scale); c.push_back(double(pt.y) / scale); }
    return c;
}

double grid_scale(double ext) {
    double scale = 1e4;   // integer grid, well inside Clipper's int64 range
    while (scale > 1.0 && ext * scale > 4e15) scale /= 10.0;
    return scale;
}

double ring_extent(const Ring& r) {
    double ext = 0.0;
    for (double v : r) ext = std::max(ext, std::fabs(v));
    return ext;
}

// Offset a closed ring by `delta` with round joins and simplify the pieces with RDP(eps). The extra
// `tol + eps` goes to the safe side: outward for an obstacle, inward for a free region. Returns every
// resulting piece, largest area first (an offset can split a region, or make it vanish).
std::vector<Ring> offset_ring(const Ring& r, double delta, bool obstacle, double tol, double eps) {
    const double scale = grid_scale(ring_extent(r) + std::fabs(delta) + tol + eps + 1.0);
    Clipper2Lib::ClipperOffset co(2.0, std::max(tol * scale, 1.0));   // arc tolerance in grid units
    co.AddPath(to_path64(r, scale), Clipper2Lib::JoinType::Round, Clipper2Lib::EndType::Polygon);
    Clipper2Lib::Paths64 sol;
    co.Execute((delta + (obstacle ? 1.0 : -1.0) * (tol + eps)) * scale, sol);
    std::vector<Ring> out;
    for (const auto& q0 : sol) {
        if (q0.size() < 3) continue;
        Clipper2Lib::Path64 q = Clipper2Lib::TrimCollinear(Clipper2Lib::RamerDouglasPeucker(q0, eps * scale), false);
        if (q.size() < 3) continue;
        Ring c = to_ring(q, scale);
        if (ring_has_area(c)) out.push_back(std::move(c));
    }
    std::sort(out.begin(), out.end(), [](const Ring& a, const Ring& b) { return std::fabs(ring_area(a)) > std::fabs(ring_area(b)); });
    return out;
}

// eps schedule shared by every ring: starts at the sagitta / float floor, doubles on each retry, and
// never exceeds half the offset distance (or 64 floors when there is no offset).
struct EpsPlan {
    double tol, eps, eps_max;
    EpsPlan(const Ring& r, double delta) {
        tol = std::max(0.002 * std::fabs(delta), 1e-6);
        eps = std::max(tol, (ring_extent(r) + 1.0) * 1e-6);
        eps_max = std::max(0.5 * std::fabs(delta), 64.0 * eps);
    }
    bool grow() { if (eps * 2.0 > eps_max) return false; eps *= 2.0; return true; }
};

static bool g_verbose_geometry = std::getenv("OPENNEST_JOB_GEOMETRY_LOG") != nullptr;

// Offset `r` and make every piece solver-safe. `frame` is the ring whose float32 bbox minimum the
// solver subtracts (nullptr: each piece's own). Returns "" and the pieces, or the problem.
std::string make_safe(const Ring& r, double delta, bool obstacle, const Ring* frame, const std::string& what,
                      std::vector<Ring>& out) {
    EpsPlan plan(r, delta);
    for (int attempt = 0;; ++attempt) {
        out = offset_ring(r, delta, obstacle, plan.tol, plan.eps);
        bool ok = true;
        for (const Ring& c : out) {
            auto [fx, fy] = f32_frame(frame ? *frame : c);
            if (solver_rejects(c, fx, fy)) { ok = false; break; }
        }
        if (ok) {
            if (attempt && g_verbose_geometry)
                std::fprintf(stderr, "[opennest_job] %s: solver-safe after %d retr%s (eps %.4g)\n", what.c_str(), attempt, attempt == 1 ? "y" : "ies", plan.eps);
            return "";
        }
        if (!plan.grow())
            return what + ": edges keep touching at float32 precision even after simplifying by " + js::number(plan.eps) +
                   "; clean the outline (remove zigzags / duplicate vertices) or increase the clearance";
    }
}

// Keep-outs of a sheet = bounding rectangle minus the usable pieces. An island of usable sheet enclosed
// by a cut-out cannot be expressed (a hole inside a keep-out), so it is an error.
std::string sheet_keepouts(const std::vector<Ring>& usable, const Ring& rect, const std::string& what, std::vector<Ring>& keepouts) {
    double ext = ring_extent(rect) + 1.0;
    const double scale = grid_scale(ext);
    Clipper2Lib::Clipper64 clip;
    clip.AddSubject({to_path64(rect, scale)});
    Clipper2Lib::Paths64 clips;
    for (const Ring& u : usable) clips.push_back(to_path64(u, scale));
    clip.AddClip(clips);
    Clipper2Lib::PolyTree64 tree;
    clip.Execute(Clipper2Lib::ClipType::Difference, Clipper2Lib::FillRule::NonZero, tree);
    keepouts.clear();
    for (const auto& child : tree) {
        if (child->Count() > 0) return what + ": a piece of usable sheet lies inside a cut-out; split the sheet into separate sheets";
        Clipper2Lib::Path64 q = Clipper2Lib::TrimCollinear(child->Polygon(), false);
        if (q.size() < 3) continue;
        Ring c = to_ring(q, scale);
        if (ring_has_area(c)) keepouts.push_back(std::move(c));
    }
    return "";
}

// Returns "" or a human-readable problem. Always runs (step 1 and 3 apply even without clearances).
std::string prepare_geometry(Job& job) {
    const double half = 0.5 * job.spacing;
    const double sheet_d = job.margin - half;   // > 0: sheets shrink / sheet holes grow; < 0: the other way
    for (size_t k = 0; k < job.parts.size(); ++k) {
        PartSpec& p = job.parts[k];
        const std::string what = "parts[" + std::to_string(k) + "]";
        if (!ring_has_area(p.outer)) return what + ".outer has no area";
        BBox b = bbox_of(p.outer);
        p.shift_x = b.x0;
        p.shift_y = b.y0;
        shift_ring(p.outer, -b.x0, -b.y0);
        for (Ring& h : p.holes) shift_ring(h, -b.x0, -b.y0);
        std::vector<Ring> pieces;
        if (std::string e = make_safe(p.outer, half, true, nullptr, what + ".outer", pieces); !e.empty()) return e;
        if (pieces.empty()) return what + ".outer vanished";
        p.outer = std::move(pieces[0]);   // an outward offset has one outer; a pocket it seals is dropped (solid: safe)
        std::vector<Ring> holes;
        for (size_t h = 0; h < p.holes.size(); ++h) {
            // A hole that cannot be made solver-safe is left out: the solver then treats that area as
            // solid, which only loses a place to nest small parts, never clearance.
            std::vector<Ring> hp;
            std::string e = make_safe(p.holes[h], -half, false, &p.outer, what + ".holes[" + std::to_string(h) + "]", hp);
            if (!e.empty()) { std::fprintf(stderr, "[opennest_job] %s (hole ignored)\n", e.c_str()); continue; }
            for (Ring& piece : hp) holes.push_back(std::move(piece));   // a shrinking hole may split: every piece is a cavity
        }
        p.holes = std::move(holes);
    }
    for (size_t k = 0; k < job.sheets.size(); ++k) {
        SheetSpec& s = job.sheets[k];
        const std::string what = "sheets[" + std::to_string(k) + "]";
        if (!ring_has_area(s.outer)) return what + ".outer has no area";
        BBox b = bbox_of(s.outer);
        s.shift_x = b.x0;
        s.shift_y = b.y0;
        shift_ring(s.outer, -b.x0, -b.y0);
        for (Ring& h : s.holes) shift_ring(h, -b.x0, -b.y0);
        // Usable area: the outline shrunk by the margin (the solver never tests this ring itself, only
        // the keep-outs derived from it, so the retry loop below is driven by those).
        EpsPlan plan(s.outer, -sheet_d);
        Ring rect;
        std::vector<Ring> keepouts;
        for (;;) {
            std::vector<Ring> usable = offset_ring(s.outer, -sheet_d, false, plan.tol, plan.eps);
            if (usable.empty()) return what + ": no usable area is left after the margin";
            BBox u = bbox_of(usable[0]);
            for (const Ring& piece : usable) { BBox c = bbox_of(piece); u.x0 = std::min(u.x0, c.x0); u.y0 = std::min(u.y0, c.y0); u.x1 = std::max(u.x1, c.x1); u.y1 = std::max(u.y1, c.y1); }
            rect = {u.x0, u.y0, u.x1, u.y0, u.x1, u.y1, u.x0, u.y1};
            if (std::string e = sheet_keepouts(usable, rect, what, keepouts); !e.empty()) return e;
            auto [fx, fy] = f32_frame(rect);
            bool ok = true;
            for (const Ring& ko : keepouts) if (solver_rejects(ko, fx, fy)) { ok = false; break; }
            if (ok) break;
            if (!plan.grow()) return what + ".outer: the outline's keep-outs keep touching at float32 precision; simplify the outline";
        }
        std::vector<Ring> holes = std::move(keepouts);
        for (size_t h = 0; h < s.holes.size(); ++h) {
            std::vector<Ring> hp;
            if (std::string e = make_safe(s.holes[h], sheet_d, true, &rect, what + ".holes[" + std::to_string(h) + "]", hp); !e.empty()) return e;
            if (!hp.empty()) holes.push_back(std::move(hp[0]));   // a grown keep-out has one outer; a pocket it seals stays forbidden (safe)
        }
        s.outer = std::move(rect);
        s.holes = std::move(holes);
    }
    return "";
}

// ---------------------------------------------------------------------------------------------------------
// Flat C-ABI arrays (one solver part per copy, part-major; one solver sheet per sheet copy, sheet-major)
// ---------------------------------------------------------------------------------------------------------
struct Flat {
    struct Copy { size_t spec; int copy; };
    std::vector<Copy> part_copies, sheet_copies;
    std::vector<int> pvc, prot, phc, phvc, svc, shc, hvc;
    std::vector<double> pxy, phxy, sxy, hxy;
    std::vector<double> sheet_min_x, sheet_min_y;   // bbox min of each solved sheet ring (solver frame origin)
};

void push_ring(const Ring& r, std::vector<int>& counts, std::vector<double>& xy) {
    counts.push_back(int(r.size() / 2));
    xy.insert(xy.end(), r.begin(), r.end());
}

Flat flatten(const Job& job) {
    Flat f;
    for (size_t s = 0; s < job.parts.size(); ++s) {
        const PartSpec& p = job.parts[s];
        for (int c = 0; c < p.qty; ++c) {
            f.part_copies.push_back({s, c});
            push_ring(p.outer, f.pvc, f.pxy);
            f.prot.push_back(p.rotations);
            f.phc.push_back(int(p.holes.size()));
            for (const Ring& h : p.holes) push_ring(h, f.phvc, f.phxy);
        }
    }
    for (size_t s = 0; s < job.sheets.size(); ++s) {
        const SheetSpec& sh = job.sheets[s];
        for (int c = 0; c < sh.count; ++c) {
            f.sheet_copies.push_back({s, c});
            double mx = sh.outer[0], my = sh.outer[1];
            for (size_t k = 0; k + 1 < sh.outer.size(); k += 2) { mx = std::min(mx, sh.outer[k]); my = std::min(my, sh.outer[k + 1]); }
            f.sheet_min_x.push_back(mx);
            f.sheet_min_y.push_back(my);
            push_ring(sh.outer, f.svc, f.sxy);
            f.shc.push_back(int(sh.holes.size()));
            for (const Ring& h : sh.holes) push_ring(h, f.hvc, f.hxy);
        }
    }
    // Never hand the ABI an empty vector's data() (may be NULL); the counts arrays say how much is read.
    for (auto* v : {&f.phvc, &f.hvc}) if (v->empty()) v->push_back(0);
    for (auto* v : {&f.phxy, &f.hxy}) if (v->empty()) v->push_back(0.0);
    return f;
}

// ---------------------------------------------------------------------------------------------------------
// I/O helpers
// ---------------------------------------------------------------------------------------------------------
std::string read_all(const std::string& path) {
    if (path == "-") return std::string(std::istreambuf_iterator<char>(std::cin), std::istreambuf_iterator<char>());
    std::ifstream in(path, std::ios::binary);
    if (!in) throw std::runtime_error("cannot open " + path);
    std::ostringstream ss;
    ss << in.rdbuf();
    std::string s = ss.str();
    if (s.size() >= 3 && (unsigned char)s[0] == 0xEF && (unsigned char)s[1] == 0xBB && (unsigned char)s[2] == 0xBF)
        s.erase(0, 3);   // UTF-8 BOM (PowerShell's Out-File writes one)
    return s;
}

// Write via a temp file + rename so a reader never sees a half-written result.
void write_atomic(const std::string& path, const std::string& text) {
    if (path == "-") { std::cout << text << std::flush; return; }
    std::string tmp = path + ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out) throw std::runtime_error("cannot write " + tmp);
        out << text;
        out.flush();
        if (!out) throw std::runtime_error("write failed: " + tmp);
    }
    std::error_code ec;
    std::filesystem::rename(tmp, path, ec);
    if (ec) throw std::runtime_error("cannot rename " + tmp + " -> " + path + ": " + ec.message());
}

class EventLog {
public:
    explicit EventLog(const std::string& path) {
        if (!path.empty()) {
            f_ = std::fopen(path.c_str(), "wb");
            if (!f_) throw std::runtime_error("cannot open events file " + path);
        }
    }
    ~EventLog() { if (f_) std::fclose(f_); }
    void line(const std::string& json) {
        if (!f_) return;
        std::lock_guard<std::mutex> lk(m_);
        std::fputs(json.c_str(), f_);
        std::fputc('\n', f_);
        std::fflush(f_);
    }
private:
    std::FILE* f_ = nullptr;
    std::mutex m_;
};

struct Options {
    std::string in, out, events, cancel_file, dump_geometry;
    bool events_layout = false;
    bool quiet = false;
    double interval = 2.0;
    double max_wall = 0.0;   // operator cap; the job's limits.wall_seconds can only lower it
    double grace = 120.0;    // after a cancel, how long the solver may take to return before the best
                             // polled layout is written and the process exits anyway
};

[[noreturn]] void usage(const char* msg) {
    if (msg) std::fprintf(stderr, "opennest_job: %s\n", msg);
    std::fprintf(stderr,
        "usage: opennest_job <job.json|-> <result.json|-> [--events <file>] [--events-layout]\n"
        "                    [--interval <s>] [--max-wall <s>] [--grace <s>] [--cancel-file <path>] [--quiet] | --version\n");
    std::exit(2);
}

Options parse_args(int argc, char** argv) {
    Options o;
    std::vector<std::string> pos;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        auto next = [&]() -> std::string { if (i + 1 >= argc) usage(("missing value for " + a).c_str()); return argv[++i]; };
        auto next_num = [&]() -> double {
            std::string v = next();
            char* end = nullptr;
            double d = std::strtod(v.c_str(), &end);
            if (end == v.c_str() || *end || !std::isfinite(d) || d < 0) usage(("bad value for " + a).c_str());
            return d;
        };
        if (a == "--events") o.events = next();
        else if (a == "--cancel-file") o.cancel_file = next();
        else if (a == "--dump-geometry") o.dump_geometry = next();
        else if (a == "--events-layout") o.events_layout = true;
        else if (a == "--interval") o.interval = next_num();
        else if (a == "--max-wall") o.max_wall = next_num();
        else if (a == "--grace") o.grace = next_num();
        else if (a == "--quiet") o.quiet = true;
        else if (a == "--version") {
            std::printf("opennest_job %s (%s, %s)\n", OPENNEST_JOB_VERSION, platform_id().c_str(), compiler_id().c_str());
            std::exit(0);
        } else if (a == "-h" || a == "--help") usage(nullptr);
        else if (a.size() > 1 && a[0] == '-' && a != "-") usage(("unknown option " + a).c_str());
        else pos.push_back(a);
    }
    if (pos.size() != 2) usage("expected <job.json> and <result.json>");
    o.in = pos[0];
    o.out = pos[1];
    if (o.interval < 0.05) o.interval = 0.05;
    return o;
}

// Fold the local frames back: the solver placed the part ring relative to its bbox minimum m on a sheet
// ring relative to its bbox minimum, so placed = R*(p - m) + t_solver + sheet_min + sheet_shift.
void fold_back(const Job& job, const Flat& f, int k, double& tx, double& ty, double ang, int sheet) {
    if (sheet < 0 || size_t(sheet) >= f.sheet_copies.size()) return;
    const PartSpec& p = job.parts[f.part_copies[size_t(k)].spec];
    const SheetSpec& sh = job.sheets[f.sheet_copies[size_t(sheet)].spec];
    const double c = std::cos(ang), s = std::sin(ang);
    tx = tx - (c * p.shift_x - s * p.shift_y) + f.sheet_min_x[size_t(sheet)] + sh.shift_x;
    ty = ty - (s * p.shift_x + c * p.shift_y) + f.sheet_min_y[size_t(sheet)] + sh.shift_y;
}

std::string layout_json(const Job& job, const Flat& f, const std::vector<double>& tx, const std::vector<double>& ty,
                        const std::vector<double>& ang, const std::vector<int>& sid) {
    std::string o = "[";
    for (size_t k = 0; k < tx.size(); ++k) {
        double x = tx[k], y = ty[k];
        fold_back(job, f, int(k), x, y, ang[k], sid[k]);
        if (k) o += ",";
        o += "[" + std::to_string(sid[k]) + "," + js::number(x) + "," + js::number(y) + "," + js::number(ang[k]) + "]";
    }
    return o + "]";
}

}  // namespace

int main(int argc, char** argv) {
    Options opt = parse_args(argc, argv);
    std::signal(SIGINT, on_signal);
    std::signal(SIGTERM, on_signal);

    auto t0 = std::chrono::steady_clock::now();
    auto elapsed = [&] { return std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count(); };

    auto fail_result = [&](const std::string& msg) -> int {
        std::fprintf(stderr, "opennest_job: error: %s\n", msg.c_str());
        std::string doc = std::string("{\"schema\":") + js::quote(kResultSchema) + ",\"status\":\"error\",\"error\":" +
                          js::quote(msg) + ",\"placements\":[]}\n";
        try { write_atomic(opt.out, doc); } catch (const std::exception& e) {
            std::fprintf(stderr, "opennest_job: %s\n", e.what());
        }
        return 1;
    };

    Job job;
    try {
        job = read_job(js::parse(read_all(opt.in)));
    } catch (const std::exception& e) {
        return fail_result(std::string("invalid job: ") + e.what());
    }
    if (std::string err = prepare_geometry(job); !err.empty()) return fail_result("invalid job: " + err);
    if (!opt.dump_geometry.empty()) {   // debug: the exact rings the solver receives (after clearances)
        auto ring_json = [](const Ring& r) {
            std::string o = "[";
            for (size_t k = 0; k + 1 < r.size(); k += 2) o += (k ? ",[" : "[") + js::number(r[k]) + "," + js::number(r[k + 1]) + "]";
            return o + "]";
        };
        std::string d = "{\"parts\":[";
        for (size_t k = 0; k < job.parts.size(); ++k) {
            d += (k ? "," : "") + std::string("{\"id\":") + js::dump(job.parts[k].id) + ",\"outer\":" + ring_json(job.parts[k].outer) + ",\"holes\":[";
            for (size_t h = 0; h < job.parts[k].holes.size(); ++h) d += (h ? "," : "") + ring_json(job.parts[k].holes[h]);
            d += "]}";
        }
        d += "],\"sheets\":[";
        for (size_t k = 0; k < job.sheets.size(); ++k) d += (k ? "," : "") + std::string("{\"outer\":") + ring_json(job.sheets[k].outer) + "}";
        d += "]}\n";
        try { write_atomic(opt.dump_geometry, d); } catch (const std::exception& e) { return fail_result(e.what()); }
    }
    Flat f = flatten(job);
    const int n = int(f.part_copies.size());
    const int ns = int(f.sheet_copies.size());
    if (n == 0) return fail_result("invalid job: every part has qty 0");

    double cap = job.wall_seconds;
    if (opt.max_wall > 0 && (cap <= 0 || opt.max_wall < cap)) cap = opt.max_wall;

    std::unique_ptr<EventLog> events;
    try { events = std::make_unique<EventLog>(opt.events); } catch (const std::exception& e) { return fail_result(e.what()); }
    events->line("{\"event\":\"start\",\"t\":0,\"parts\":" + std::to_string(n) + ",\"sheets\":" + std::to_string(ns) +
                 ",\"fit_mode\":" + std::to_string(job.params.fit_mode) + ",\"wall_cap\":" + js::number(cap) + "}");
    if (!opt.quiet)
        std::fprintf(stderr, "[opennest_job] %s %s | parts %d (copies) sheets %d fit_mode %d iter_budget %lld seed %d cap %.0fs\n",
                     platform_id().c_str(), OPENNEST_JOB_VERSION, n, ns, job.params.fit_mode, job.params.iter_budget,
                     job.params.seed, cap);

    std::vector<double> tx(n, 0.0), ty(n, 0.0), ang(n, 0.0);
    std::vector<int> sid(n, -1);
    int n_sheets = 0;

    // The opennest.result/1 document. Placements are expected already folded back to input coordinates.
    auto result_doc = [&](const std::string& status, const std::string& reason, const std::string& error, double wall,
                          long long rounds, int nsh, bool clean, int q_overlap, int q_oob, int q_unplaced, int q_demoted,
                          const std::vector<double>& tx, const std::vector<double>& ty, const std::vector<double>& ang,
                          const std::vector<int>& sid) {
        std::string o;
        o.reserve(256 + size_t(n) * 160);
        o += "{\n \"schema\": " + js::quote(kResultSchema) + ",\n";
        o += " \"status\": " + js::quote(status) + ",\n";
        if (!error.empty()) o += " \"error\": " + js::quote(error) + ",\n";
        if (!reason.empty()) o += " \"cancel_reason\": " + js::quote(reason) + ",\n";
        o += " \"engine\": {\"name\": \"nest_physics\", \"runner\": " + js::quote(std::string("opennest_job ") + OPENNEST_JOB_VERSION) +
             ", \"platform\": " + js::quote(platform_id()) + ", \"compiler\": " + js::quote(compiler_id()) + "},\n";
        o += " \"wall_s\": " + js::number(wall) + ",\n";
        o += " \"rounds\": " + std::to_string(rounds) + ",\n";
        std::vector<int> used;
        for (int k = 0; k < n; ++k)
            if (sid[k] >= 0 && std::find(used.begin(), used.end(), sid[k]) == used.end()) used.push_back(sid[k]);
        std::sort(used.begin(), used.end());
        o += " \"n_sheets\": " + std::to_string(nsh > 0 ? nsh : int(used.size())) + ",\n";
        o += " \"sheets_used\": [";
        for (size_t k = 0; k < used.size(); ++k) o += (k ? "," : "") + std::to_string(used[k]);
        o += "],\n";
        o += " \"quality\": {\"clean\": " + std::string(clean ? "true" : "false") + ", \"overlap_pairs\": " +
             std::to_string(q_overlap) + ", \"out_of_bounds\": " + std::to_string(q_oob) + ", \"unplaced\": " +
             std::to_string(q_unplaced) + ", \"demoted\": " + std::to_string(q_demoted) + "},\n";
        o += " \"frame\": " + js::quote(kFrame) + ",\n";
        o += " \"clearance\": {\"spacing\": " + js::number(job.spacing) + ", \"margin\": " + js::number(job.margin) + "},\n";
        o += " \"placements\": [\n";
        for (int k = 0; k < n; ++k) {
            const auto& pc = f.part_copies[size_t(k)];
            o += "  {\"part\": " + js::dump(job.parts[pc.spec].id) + ", \"copy\": " + std::to_string(pc.copy) +
                 ", \"sheet\": " + std::to_string(sid[k] >= 0 && sid[k] < ns ? sid[k] : -1);
            if (sid[k] >= 0 && sid[k] < ns) {
                const auto& sc = f.sheet_copies[size_t(sid[k])];
                o += ", \"sheet_id\": " + js::dump(job.sheets[sc.spec].id) + ", \"sheet_copy\": " + std::to_string(sc.copy);
            }
            o += ", \"tx\": " + js::number(tx[k]) + ", \"ty\": " + js::number(ty[k]) + ", \"angle\": " + js::number(ang[k]) + "}";
            o += (k + 1 < n ? ",\n" : "\n");
        }
        o += " ]\n}\n";
        return o;
    };

    // Monitor thread: progress events, the wall cap, and re-issuing a signal's cancel (np_nest clears the
    // cancel flag on entry, so a signal that lands just before the solve starts must be repeated).
    std::mutex mu;
    std::condition_variable cv;
    bool done = false;
    std::atomic<bool> cap_hit{false};
    std::atomic<bool> file_stop{false};
    std::thread monitor([&] {
        std::vector<double> ptx(n), pty(n), pang(n);
        std::vector<int> psid(n);
        double next_event = opt.interval;
        unsigned file_checks = 0;
        double cancel_at = -1.0;   // when the first cancel was issued
        std::unique_lock<std::mutex> lk(mu);
        while (!done) {
            cv.wait_for(lk, std::chrono::milliseconds(50), [&] { return done; });
            if (done) break;
            double t = elapsed();
            if (g_signal_stop.load()) np_cancel();
            if (cancel_at < 0 && (g_signal_stop.load() || file_stop.load() || cap_hit.load())) cancel_at = t;
            // A solver phase that does not look at the cancel flag for a long time (group-and-fill on a slow
            // machine) must not turn a budget overrun into a lost result: after `grace`, write the best
            // layout polled so far and leave without waiting for np_nest to return.
            if (cancel_at >= 0 && t - cancel_at >= opt.grace) {
                int pns = 0;
                int placed = np_poll_layout(n, ptx.data(), pty.data(), pang.data(), psid.data(), &pns);
                for (int k = 0; k < n; ++k) fold_back(job, f, k, ptx[k], pty[k], pang[k], psid[k]);
                std::string reason = g_signal_stop.load() ? "signal" : file_stop.load() ? "cancel_file" : "wall_cap";
                std::string o = result_doc("cancelled", reason + " (solver did not stop within the grace period; layout is the last polled snapshot, unverified)",
                                           "", t, np_progress(), pns, false, -1, -1, n - placed, -1, ptx, pty, pang, psid);
                try { write_atomic(opt.out, o); } catch (...) {}
                events->line("{\"event\":\"done\",\"t\":" + js::number(t) + ",\"status\":\"cancelled\",\"n_sheets\":" +
                             std::to_string(pns) + ",\"unplaced\":" + std::to_string(n - placed) + ",\"forced\":true}");
                if (!opt.quiet) std::fprintf(stderr, "[opennest_job] cancelled: solver unresponsive for %.0fs after the cancel; wrote the polled layout (%d placed) and exiting\n", opt.grace, placed);
                std::fflush(stderr);
                std::_Exit(124);
            }
            if (!opt.cancel_file.empty() && !file_stop.load() && ++file_checks % 5 == 0) {
                std::error_code ec;
                if (std::filesystem::exists(opt.cancel_file, ec)) {
                    file_stop.store(true);
                    events->line("{\"event\":\"cancel\",\"t\":" + js::number(t) + "}");
                }
            }
            if (file_stop.load()) np_cancel();
            if (cap > 0 && t >= cap && !cap_hit.exchange(true)) {
                np_cancel();
                events->line("{\"event\":\"cap\",\"t\":" + js::number(t) + "}");
            }
            if (cap_hit.load()) np_cancel();
            if (t >= next_event) {
                next_event = t + opt.interval;
                int pns = 0;
                int placed = np_poll_layout(n, ptx.data(), pty.data(), pang.data(), psid.data(), &pns);
                std::string ev = "{\"event\":\"progress\",\"t\":" + js::number(t) + ",\"rounds\":" +
                                 std::to_string(np_progress()) + ",\"placed\":" + std::to_string(placed) +
                                 ",\"n_sheets\":" + std::to_string(pns);
                if (opt.events_layout) ev += ",\"layout\":" + layout_json(job, f, ptx, pty, pang, psid);
                events->line(ev + "}");
            }
        }
    });

    np_cancel_reset();
    int rc = np_nest(n, f.pvc.data(), f.pxy.data(), f.prot.data(),
                     ns, f.svc.data(), f.sxy.data(), f.shc.data(), f.hvc.data(), f.hxy.data(),
                     f.phc.data(), f.phvc.data(), f.phxy.data(), &job.params,
                     tx.data(), ty.data(), ang.data(), sid.data(), &n_sheets);
    {
        std::lock_guard<std::mutex> lk(mu);
        done = true;
    }
    cv.notify_all();
    monitor.join();
    double wall = elapsed();
    for (int k = 0; k < n; ++k) fold_back(job, f, k, tx[k], ty[k], ang[k], sid[k]);

    int q_overlap = -1, q_oob = -1, q_unplaced = -1, q_cancelled = -1, q_demoted = -1;
    int clean = np_last_quality(&q_overlap, &q_oob, &q_unplaced, &q_cancelled, &q_demoted);
    long long rounds = np_progress();
    bool cancelled = cap_hit.load() || g_signal_stop.load() || file_stop.load() || q_cancelled == 1;
    // "partial" = the solve finished but some parts fit on no sheet (too big, or too few sheets given).
    std::string status = rc != 0 ? "error" : cancelled ? "cancelled" : (q_unplaced > 0 ? "partial" : "ok");

    std::string reason = g_signal_stop.load() ? "signal" : file_stop.load() ? "cancel_file" : cap_hit.load() ? "wall_cap" : "solver";
    std::string o = result_doc(status, cancelled ? reason : "", rc != 0 ? "np_nest returned " + std::to_string(rc) : "",
                               wall, rounds, n_sheets, clean != 0, q_overlap, q_oob, q_unplaced, q_demoted, tx, ty, ang, sid);
    try { write_atomic(opt.out, o); } catch (const std::exception& e) { return fail_result(e.what()); }
    events->line("{\"event\":\"done\",\"t\":" + js::number(wall) + ",\"status\":" + js::quote(status) +
                 ",\"n_sheets\":" + std::to_string(n_sheets) + ",\"unplaced\":" + std::to_string(q_unplaced) + "}");
    if (!opt.quiet)
        std::fprintf(stderr, "[opennest_job] %s rc=%d wall=%.1fs sheets=%d unplaced=%d overlaps=%d clean=%d\n",
                     status.c_str(), rc, wall, n_sheets, q_unplaced, q_overlap, clean);
    if (rc != 0) return 1;
    return cancelled ? 124 : 0;
}
