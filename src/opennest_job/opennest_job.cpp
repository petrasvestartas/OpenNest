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
    "sheet-local: rotate the input part about the world origin by angle (radians, counter-clockwise), then "
    "translate by (tx, ty); add the bounding-box minimum of the sheet's outer ring for world coordinates";

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

struct PartSpec {
    Value id;
    int qty = 1;
    int rotations = 0;
    Ring outer;
    std::vector<Ring> holes;
};

struct SheetSpec {
    Value id;
    int count = 1;
    Ring outer;
    std::vector<Ring> holes;
};

struct Job {
    std::vector<PartSpec> parts;
    std::vector<SheetSpec> sheets;
    NpParams params{};
    double wall_seconds = 0.0;   // 0 = no cap from the job
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
    for (size_t k = 0; k < arr.size(); ++k) out.push_back(read_ring(arr[k], what + "[" + std::to_string(k) + "]"));
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
    check_keys(root, "job", {"schema", "units", "name", "parts", "sheets", "params", "limits"});
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
// Flat C-ABI arrays (one solver part per copy, part-major; one solver sheet per sheet copy, sheet-major)
// ---------------------------------------------------------------------------------------------------------
struct Flat {
    struct Copy { size_t spec; int copy; };
    std::vector<Copy> part_copies, sheet_copies;
    std::vector<int> pvc, prot, phc, phvc, svc, shc, hvc;
    std::vector<double> pxy, phxy, sxy, hxy;
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
    std::string in, out, events;
    bool events_layout = false;
    bool quiet = false;
    double interval = 2.0;
    double max_wall = 0.0;   // operator cap; the job's limits.wall_seconds can only lower it
};

[[noreturn]] void usage(const char* msg) {
    if (msg) std::fprintf(stderr, "opennest_job: %s\n", msg);
    std::fprintf(stderr,
        "usage: opennest_job <job.json|-> <result.json|-> [--events <file>] [--events-layout]\n"
        "                    [--interval <s>] [--max-wall <s>] [--quiet] | --version\n");
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
        else if (a == "--events-layout") o.events_layout = true;
        else if (a == "--interval") o.interval = next_num();
        else if (a == "--max-wall") o.max_wall = next_num();
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

std::string layout_json(const std::vector<double>& tx, const std::vector<double>& ty,
                        const std::vector<double>& ang, const std::vector<int>& sid) {
    std::string o = "[";
    for (size_t k = 0; k < tx.size(); ++k) {
        if (k) o += ",";
        o += "[" + std::to_string(sid[k]) + "," + js::number(tx[k]) + "," + js::number(ty[k]) + "," + js::number(ang[k]) + "]";
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

    // Monitor thread: progress events, the wall cap, and re-issuing a signal's cancel (np_nest clears the
    // cancel flag on entry, so a signal that lands just before the solve starts must be repeated).
    std::mutex mu;
    std::condition_variable cv;
    bool done = false;
    std::atomic<bool> cap_hit{false};
    std::thread monitor([&] {
        std::vector<double> ptx(n), pty(n), pang(n);
        std::vector<int> psid(n);
        double next_event = opt.interval;
        std::unique_lock<std::mutex> lk(mu);
        while (!done) {
            cv.wait_for(lk, std::chrono::milliseconds(50), [&] { return done; });
            if (done) break;
            double t = elapsed();
            if (g_signal_stop.load()) np_cancel();
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
                if (opt.events_layout) ev += ",\"layout\":" + layout_json(ptx, pty, pang, psid);
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

    int q_overlap = -1, q_oob = -1, q_unplaced = -1, q_cancelled = -1, q_demoted = -1;
    int clean = np_last_quality(&q_overlap, &q_oob, &q_unplaced, &q_cancelled, &q_demoted);
    long long rounds = np_progress();
    bool cancelled = cap_hit.load() || g_signal_stop.load() || q_cancelled == 1;
    // "partial" = the solve finished but some parts fit on no sheet (too big, or too few sheets given).
    std::string status = rc != 0 ? "error" : cancelled ? "cancelled" : (q_unplaced > 0 ? "partial" : "ok");

    // ---- result document ----
    std::string o;
    o.reserve(256 + size_t(n) * 160);
    o += "{\n \"schema\": " + js::quote(kResultSchema) + ",\n";
    o += " \"status\": " + js::quote(status) + ",\n";
    if (rc != 0) o += " \"error\": " + js::quote("np_nest returned " + std::to_string(rc)) + ",\n";
    if (cancelled) o += std::string(" \"cancel_reason\": ") + (g_signal_stop.load() ? "\"signal\"" : cap_hit.load() ? "\"wall_cap\"" : "\"solver\"") + ",\n";
    o += " \"engine\": {\"name\": \"nest_physics\", \"runner\": " + js::quote(std::string("opennest_job ") + OPENNEST_JOB_VERSION) +
         ", \"platform\": " + js::quote(platform_id()) + ", \"compiler\": " + js::quote(compiler_id()) + "},\n";
    o += " \"wall_s\": " + js::number(wall) + ",\n";
    o += " \"rounds\": " + std::to_string(rounds) + ",\n";
    std::vector<int> used;
    for (int k = 0; k < n; ++k)
        if (sid[k] >= 0 && std::find(used.begin(), used.end(), sid[k]) == used.end()) used.push_back(sid[k]);
    std::sort(used.begin(), used.end());
    o += " \"n_sheets\": " + std::to_string(n_sheets > 0 ? n_sheets : int(used.size())) + ",\n";
    o += " \"sheets_used\": [";
    for (size_t k = 0; k < used.size(); ++k) o += (k ? "," : "") + std::to_string(used[k]);
    o += "],\n";
    o += " \"quality\": {\"clean\": " + std::string(clean ? "true" : "false") + ", \"overlap_pairs\": " +
         std::to_string(q_overlap) + ", \"out_of_bounds\": " + std::to_string(q_oob) + ", \"unplaced\": " +
         std::to_string(q_unplaced) + ", \"demoted\": " + std::to_string(q_demoted) + "},\n";
    o += " \"frame\": " + js::quote(kFrame) + ",\n";
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

    try { write_atomic(opt.out, o); } catch (const std::exception& e) { return fail_result(e.what()); }
    events->line("{\"event\":\"done\",\"t\":" + js::number(wall) + ",\"status\":" + js::quote(status) +
                 ",\"n_sheets\":" + std::to_string(n_sheets) + ",\"unplaced\":" + std::to_string(q_unplaced) + "}");
    if (!opt.quiet)
        std::fprintf(stderr, "[opennest_job] %s rc=%d wall=%.1fs sheets=%d unplaced=%d overlaps=%d clean=%d\n",
                     status.c_str(), rc, wall, n_sheets, q_unplaced, q_overlap, clean);
    if (rc != 0) return 1;
    return cancelled ? 124 : 0;
}
