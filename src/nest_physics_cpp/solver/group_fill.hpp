// fit_mode 3: GROUP-AND-FILL multi-sheet driver (C++ port of tools/bench3dm/groupfill_nfp.py + sheetcover.py).
//
// Why: on jobs where a few large parts hold most of the material, the sheet count is decided by HOW THE LARGE
// PARTS ARE GROUPED per sheet, not by the filler packing. fit 0 (greedy strip-and-cut) and fit 2 (global
// k-column strip) decide that grouping implicitly. Here it is decided explicitly, then every sheet is packed as
// ONE joint pack of its plates (mandatory) + long parts + a knapsack set of small parts, using the existing
// one-sheet machinery (driver.hpp prio_fill_one = fit_mode 1 with the E1 mandatory / placed-area objective).
//
// Chain (one portfolio member = one partition + one seed):
//   a. classes: PLATES = area >= NP_FM3_PLATE_FRAC (0.05) of the largest usable sheet area (at most
//      NP_FM3_GROUP_MAX = 24, largest first); LONG = not a plate, longest dimension (polygon diameter) >=
//      NP_FM3_LONG_FRAC (0.33) x the largest sheet dimension; SMALL = the rest.
//   b. per sheet TYPE (= distinct size + keep-out set; irregular outlines reach the engine as keep-outs, see
//      component_nest.cs NonRectFrameKeepouts), single-sheet feasibility of plate groups: singles, pairs, triples
//      whose pairs fit, quads whose triples fit (largest area first, area <= 0.98 x usable). Test = LBF into the
//      sheet (keep-outs respected), and LBF failures within 1.25 W re-tested with NP_FM3_TEST_ROUNDS (150)
//      exploration rounds. Capped by NP_FM3_MAX_TESTS (3000) / NP_FM3_MAX_UPGRADES (400) - counts, not wall
//      time, so iteration-mode runs stay deterministic. Tests run in parallel on the portfolio threads.
//   c. assignment: branch-and-bound exact cover of the plates (largest uncovered plate branched first) by
//      feasible (type, group) pairs, respecting how many sheets of each type exist, at the smallest sheet count
//      k whose usable area holds every part x (1 + NP_FM3_SLACK, 0.06); if no cover of every plate exists, the
//      nb largest plates are covered and the rest become fillers. Covers are ranked by the free band left right
//      of each group's test layout (sum of (W - w_g) x H) and the best NP_FM3_CHAINS go to the portfolio.
//   d. per sheet (tightest first): ONE joint pack of plates (mandatory) + the long parts assigned to it (worst-fit
//      by free area; a long part one sheet drops is carried to the next) + small parts by area to
//      NP_FM3_TARGET (0.90) of the usable area; then the remaining small parts are packed into the frozen layout;
//      after all sheets the leftovers are offered to every sheet again in a whole-sheet repack (every current
//      part mandatory, leftovers normal), then seated into the holes of placed parts (part_holes_mode >= 1).
//   e. leftovers go to further (unused) sheets (greedy strip-and-cut, fit 0); finally the least-filled sheet is
//      re-packed for the minimal bounding rectangle of its used region (strip-height sweep implemented as a
//      keep-out band, so sheet keep-outs and irregular outlines stay respected).
//   f. portfolio: NP params n_threads (default 4) threads in total. Group tests are shared; the chains run
//      concurrently (one best-of-N worker each); then, unless a chain already reached the area lower bound with
//      every part placed, the plain fit 2 run (fit 0 for mixed sheet sizes) runs with all n_threads workers as a
//      BASELINE member. The best layout wins: fewest unplaced, fewest sheets, smallest used sheet area, then the
//      least material on the least-filled sheet (the remnant). Anytime: cancel / the time budget stop every pack
//      cooperatively; the best complete-and-verified layout found so far is returned.
// Nothing here is instance-specific: every threshold is a fraction of the sheet size or of the part areas.
#pragma once
#include "bin_driver.hpp"
#include <functional>
#include <thread>
#include <atomic>
#include <mutex>
#include <map>
#include <set>
#include <string>
#include <cstring>
#include <cstdlib>

namespace nest {

inline double gf_env_d(const char* n, double d) {
    if (const char* e = std::getenv(n)) { char* end = nullptr; double v = std::strtod(e, &end); if (end != e) return v; }
    return d;
}
inline int gf_env_i(const char* n, int d) {
    if (const char* e = std::getenv(n)) { char* end = nullptr; long v = std::strtol(e, &end, 10); if (end != e) return (int)v; }
    return d;
}
inline bool gf_verbose() { return gf_env_i("NP_FM3_VERBOSE", 0) != 0; }

struct GfSlot { f32 W = 0.0f, H = 0.0f; std::vector<Polygon> holes; int type = 0; double usable = 0.0; };
struct GfPlaced { usize gid = 0; RigidTransform dt; std::vector<Point> verts; };
struct GfSheet { int slot = -1; std::vector<GfPlaced> parts; };
struct GfLayout {
    std::vector<GfSheet> sheets;
    std::vector<usize> unplaced;
    std::string tag;
    bool valid = false;
};
struct GfGroup { uint32_t mask = 0; int type = 0; double area = 0.0; f32 w = 0.0f; };
struct GfPartition { std::vector<GfGroup> groups; std::vector<int> extra_types; double score = 0.0; int nb = 0; };

struct GfEnv {
    const std::vector<std::pair<Part, usize>>* parts = nullptr;
    const CollisionConfig* engine = nullptr;
    std::vector<GfSlot> slots;
    int n_types = 0;
    std::vector<int> type_rep;                 // representative slot per type
    std::vector<int> type_count;               // slots per type
    std::vector<std::vector<char>> fits;       // [type][gid]: fits the sheet bbox at some allowed orientation
    std::vector<double> area;                  // per gid
    const std::vector<std::vector<Polygon>>* part_holes = nullptr;   // centered part holes per gid (may be null)
    bool iter = true;
    double B = 4000.0;                         // per-pack budget unit (rounds, or seconds in time mode)
    double deadline = 0.0;                     // time mode: absolute wall deadline (0 = none)
    f32 long_thr = 0.0f;
    std::vector<usize> plates;                 // gids, area descending
    std::vector<char> is_plate, is_long;
    double total_area = 0.0;
    int prio_retries = 2;
    double target = 0.90;
    int knap_max = 24;
    // contact-skin shrink of a placed outline (the C ABI's shrink_verified), so the pack re-check below speaks the
    // final verification's "touching is not overlapping"; empty => strict brute_overlap.
    std::function<std::optional<Polygon>(const Polygon&)> shrink;

    const Part& part(usize g) const { return (*parts)[g].first; }
    bool out_of_time() const {
        if (g_cancel.load(std::memory_order_relaxed)) return true;
        return deadline > 0.0 && now_seconds() > deadline;
    }
    // remaining budget for one pack: in time mode never past the deadline
    double pack_budget(double frac) const {
        double b = B * frac;
        if (!iter && deadline > 0.0) b = std::max(0.05, std::min(b, deadline - now_seconds()));
        return b;
    }
};

inline double gf_area_of(const GfEnv& E, const GfSheet& s) {
    double a = 0.0; for (const auto& p : s.parts) a += E.area[p.gid]; return a;
}

inline std::vector<Polygon> gf_polys(const std::vector<GfPlaced>& ps) {
    std::vector<Polygon> out;
    out.reserve(ps.size());
    for (const auto& p : ps) { try { out.push_back(Polygon::create(p.verts)); } catch (...) {} }
    return out;
}

// Exact re-check of a pack result before it is committed: inside the slot's bbox (bounds_tol), clear of the
// slot's keep-outs (strict brute_overlap, like the final verification), of the frozen parts and of every part kept
// before it (contact skin, like the final verification). A part that fails is dropped (stays in the pool). Packs
// come from loss-0 strip layouts, so this practically never fires.
inline std::vector<GfPlaced> gf_clean(const GfEnv& E, const GfSlot& S, std::vector<GfPlaced> in, const std::vector<Polygon>& frozen) {
    std::vector<GfPlaced> keep;
    std::vector<Polygon> kp;
    std::vector<std::optional<Polygon>> ks;
    std::vector<std::optional<Polygon>> fs;
    for (const auto& f : frozen) fs.push_back(E.shrink ? E.shrink(f) : std::nullopt);
    auto pair_hit = [](const Polygon& a, const std::optional<Polygon>& sa, const Polygon& b, const std::optional<Polygon>& sb) {
        if (!bbox_overlap(a.bbox, b.bbox)) return false;
        if (sa && sb) return brute_overlap(*sa, *sb);
        return brute_overlap(a, b);
    };
    for (auto& p : in) {
        Polygon poly;
        try { poly = Polygon::create(p.verts); } catch (...) { continue; }
        std::optional<Polygon> sp = E.shrink ? E.shrink(poly) : std::nullopt;
        const f32 tol = bounds_tol(S.W, S.H, poly.diameter);
        bool ok = poly.bbox.x_min >= -tol && poly.bbox.y_min >= -tol && poly.bbox.x_max <= S.W + tol && poly.bbox.y_max <= S.H + tol;
        for (const auto& h : S.holes) if (ok && bbox_overlap(poly.bbox, h.bbox) && brute_overlap(poly, h)) ok = false;
        for (usize i = 0; i < frozen.size() && ok; ++i) if (pair_hit(poly, sp, frozen[i], fs[i])) ok = false;
        for (usize i = 0; i < kp.size() && ok; ++i) if (pair_hit(poly, sp, kp[i], ks[i])) ok = false;
        if (!ok) continue;
        kp.push_back(std::move(poly));
        ks.push_back(std::move(sp));
        keep.push_back(std::move(p));
    }
    return keep;
}

// One-sheet pack of `ids` (gids) on `slot`: fit_mode 1 machinery. prio = E1 path (mandatory first, then placed
// AREA, drop policy + insertion); else the plain fit-1 run (count objective). `frozen` = placed parts that stay
// put (sheet keep-outs). Returns the kept placements (gids, sheet-local), exactly re-checked.
inline std::vector<GfPlaced> gf_pack(const GfEnv& E, int slot, const std::vector<usize>& ids_in,
                                     const std::vector<char>* mand, const std::vector<Polygon>& frozen,
                                     double budget, uint64_t seed, bool prio) {
    const GfSlot& S = E.slots[(usize)slot];
    std::vector<usize> ids;
    for (usize g : ids_in) if (E.fits[(usize)S.type][g]) ids.push_back(g);
    if (ids.empty() || E.out_of_time()) return {};
    auto sub = subset_items(*E.parts, ids);
    for (usize i = 0; i < sub.size(); ++i) sub[i].first.priority = (mand && (*mand)[ids[i]]) ? 1 : 0;
    std::vector<std::vector<Polygon>> holes;
    if (!S.holes.empty() || !frozen.empty()) {
        holes.resize(1);
        holes[0] = S.holes;
        holes[0].insert(holes[0].end(), frozen.begin(), frozen.end());
    }
    Sheets r = prio ? prio_fill_one(sub, *E.engine, S.W, S.H, budget, holes, seed, false, nullptr)
                    : greedy_fill(sub, *E.engine, S.W, S.H, budget, 1, holes, seed, false, nullptr);
    std::vector<GfPlaced> out;
    for (auto& pl : r.placements)
        if (pl.sheet == 0 && pl.color_id < ids.size()) out.push_back({ids[pl.color_id], pl.d_transf, pl.verts});
    return gf_clean(E, S, std::move(out), frozen);
}

// Single-sheet feasibility test of a plate group on a sheet TYPE (keep-outs respected): LBF into the sheet, then
// (if wider than W) `rounds` exploration rounds. Feasible iff every part lies left of W.
inline GroupTest gf_test(const GfEnv& E, int type, const std::vector<usize>& gids, usize rounds, uint64_t seed) {
    GroupTest t;
    const GfSlot& S = E.slots[(usize)E.type_rep[(usize)type]];
    for (usize g : gids) if (!E.fits[(usize)type][g]) return t;
    auto gp = subset_items(*E.parts, gids);
    Strip s(S.H, *E.engine, part_modify(), S.W);
    if (!S.holes.empty()) s.set_holes(S.holes);
    StripInstance gi(gp, s);
    Rng rng(seed);
    InitialPlacer gip(gi, Rng(rng.next_u64()), INITIAL_SAMPLE_CONFIG);
    gip.construct();
    auto xmax_of = [](const LayoutSnapshot& ls) {
        f32 m = 0.0f;
        ls.placed_parts.for_each([&](PartKey, const PlacedPart& pi) { m = max_f(m, pi.shape->bbox.x_max); });
        return m;
    };
    StripSolution sol = gip.prob.save();
    f32 w = xmax_of(sol.layout_snapshot);
    if (w > S.W && rounds > 0 && w <= 1.25f * S.W && !g_cancel.load(std::memory_order_relaxed)) {
        SolverConfig sc = default_solver_config();
        ExplorationConfig ec = sc.expl_cfg;
        ec.max_conseq_failed_attempts = std::nullopt;
        ec.separator_config.n_workers = resolve_n_workers();
        IterationTerminator it;
        it.new_timeout((double)rounds);
        Relaxer rr(gi, std::move(gip.prob), Rng(rng.next_u64()), ec.separator_config);
        DummySolListener dl;
        auto sols = exploration_phase(gi, rr, dl, it, ec);
        sol = sols.back();
        w = xmax_of(sol.layout_snapshot);
    }
    t.w = w;
    t.feasible = (w <= S.W) && sol.layout_snapshot.placed_parts.size() == gids.size();
    return t;
}

// Fixed-size thread pool for one batch of independent jobs. Every job runs on a pool thread with ONE best-of-N
// worker (g_n_workers_tl) and the priority driver's lines muted, so the process never exceeds n_threads.
inline void gf_parallel(int n_threads, usize n_jobs, const std::function<void(usize)>& job, unsigned workers = 1) {
    if (n_jobs == 0) return;
    std::atomic<usize> next{0};
    auto run = [&]() {
        g_n_workers_tl = workers; g_quiet_tl = true;
        g_prio_alone_cap_tl = std::max(0, gf_env_i("NP_FM3_ALONE_CAP", 4));
        for (;;) {
            usize i = next.fetch_add(1);
            if (i >= n_jobs) break;
            job(i);
        }
        g_n_workers_tl = 0; g_quiet_tl = false; g_prio_alone_cap_tl = -1; g_prio_retries_tl = -1;
    };
    int nt = std::max(1, std::min<int>(n_threads, (int)n_jobs));
    std::vector<std::thread> th;
    for (int t = 0; t < nt; ++t) th.emplace_back(run);
    for (auto& t : th) t.join();
}

inline uint64_t gf_hash(uint64_t seed, int type, const std::vector<usize>& ids) {
    uint64_t h = 1469598103934665603ull ^ (seed * 0x9E3779B97F4A7C15ull) ^ (uint64_t)(type + 1) * 0xBF58476D1CE4E5B9ull;
    for (usize v : ids) { h ^= (uint64_t)v + 0x9E3779B97F4A7C15ull + (h << 6) + (h >> 2); h *= 1099511628211ull; }
    return h;
}

// ---------------------------------------------------------------------------------------------- group tests
struct GfTests {
    std::map<std::pair<int, uint32_t>, GroupTest> cache;   // (type, plate-index mask) -> result
    int n_tests = 0, n_upgrades = 0, n_feasible = 0;
    bool capped = false;
};

inline GfTests gf_group_tests(const GfEnv& E, int n_threads, uint64_t seed) {
    GfTests R;
    const usize m = E.plates.size();
    if (m == 0) return R;
    const int max_tests = std::max(1, gf_env_i("NP_FM3_MAX_TESTS", 3000));
    const int max_up = std::max(0, gf_env_i("NP_FM3_MAX_UPGRADES", 400));
    const usize rounds = (usize)std::max(0, gf_env_i("NP_FM3_TEST_ROUNDS", 150));
    const int gmax = std::max(1, std::min(4, gf_env_i("NP_FM3_GROUP_SIZE", 4)));
    auto mask_area = [&](uint32_t mk) { double a = 0; for (usize j = 0; j < m; ++j) if (mk >> j & 1u) a += E.area[E.plates[j]]; return a; };
    auto feasible = [&](int t, uint32_t mk) { auto it = R.cache.find({t, mk}); return it != R.cache.end() && it->second.feasible; };
    auto run_batch = [&](std::vector<std::pair<int, uint32_t>>& todo, usize rnds) {
        // largest area first; deterministic order; cap
        std::stable_sort(todo.begin(), todo.end(), [&](const auto& a, const auto& b) {
            double xa = mask_area(a.second), xb = mask_area(b.second);
            if (xa != xb) return xa > xb;
            return a < b; });
        if (rnds == 0) {
            if ((int)todo.size() > max_tests - R.n_tests) { todo.resize((usize)std::max(0, max_tests - R.n_tests)); R.capped = true; }
            R.n_tests += (int)todo.size();
        } else {
            if ((int)todo.size() > max_up - R.n_upgrades) { todo.resize((usize)std::max(0, max_up - R.n_upgrades)); R.capped = true; }
            R.n_upgrades += (int)todo.size();
        }
        std::vector<GroupTest> res(todo.size());
        gf_parallel(n_threads, todo.size(), [&](usize i) {
            if (E.out_of_time()) return;
            std::vector<usize> ids;
            for (usize j = 0; j < m; ++j) if (todo[i].second >> j & 1u) ids.push_back(E.plates[j]);
            res[i] = gf_test(E, todo[i].first, ids, rnds, gf_hash(seed, todo[i].first, ids));
        });
        for (usize i = 0; i < todo.size(); ++i) {
            auto& slot = R.cache[todo[i]];
            if (rnds == 0 || res[i].feasible) slot = res[i];
        }
    };
    auto upgrade = [&](const std::vector<std::pair<int, uint32_t>>& level) {
        if (rounds == 0) return;
        std::vector<std::pair<int, uint32_t>> todo;
        for (const auto& key : level) {
            auto it = R.cache.find(key);
            if (it == R.cache.end() || it->second.feasible) continue;
            const GfSlot& S = E.slots[(usize)E.type_rep[(usize)key.first]];
            if (it->second.w > 0.0f && it->second.w <= 1.25f * S.W) todo.push_back(key);
        }
        run_batch(todo, rounds);
    };
    for (int t = 0; t < E.n_types; ++t) {
        const GfSlot& S = E.slots[(usize)E.type_rep[(usize)t]];
        const double cap = 0.98 * S.usable;
        std::vector<std::pair<int, uint32_t>> lvl;
        for (usize i = 0; i < m; ++i) if (E.fits[(usize)t][E.plates[i]] && E.area[E.plates[i]] <= cap) lvl.push_back({t, 1u << i});
        run_batch(lvl, 0); upgrade(lvl);
        for (int size = 2; size <= gmax; ++size) {
            lvl.clear();
            std::vector<usize> g((usize)size);
            std::function<void(usize, int)> gen = [&](usize from, int d) {
                if (d == size) {
                    uint32_t mk = 0; for (usize j : g) mk |= 1u << j;
                    if (mask_area(mk) > cap) return;
                    for (int drop = 0; drop < size; ++drop)
                        if (!feasible(t, mk & ~(1u << g[(usize)drop]))) return;
                    lvl.push_back({t, mk});
                    return;
                }
                for (usize i = from; i < m; ++i) { g[(usize)d] = i; gen(i + 1, d + 1); }
            };
            gen(0, 0);
            if (lvl.empty()) break;
            run_batch(lvl, 0); upgrade(lvl);
        }
    }
    for (const auto& [k, v] : R.cache) if (v.feasible) R.n_feasible++;
    return R;
}

// ---------------------------------------------------------------------------------------------- cover (B&B)
inline std::vector<GfPartition> gf_covers(const GfEnv& E, const GfTests& T, int max_bins, int pool_n, double slack) {
    const usize m = E.plates.size();
    // filler-only sheets take the largest remaining types first
    std::vector<int> types_by_area(E.n_types);
    for (int t = 0; t < E.n_types; ++t) types_by_area[(usize)t] = t;
    std::stable_sort(types_by_area.begin(), types_by_area.end(), [&](int a, int b) {
        return E.slots[(usize)E.type_rep[(usize)a]].usable > E.slots[(usize)E.type_rep[(usize)b]].usable; });
    const std::vector<int>& avail = E.type_count;
    double max_usable = E.slots[(usize)E.type_rep[(usize)types_by_area[0]]].usable;
    const int k_lb = std::max(1, (int)std::ceil(E.total_area * (1.0 + slack) / max_usable - 1e-9));
    std::vector<GfGroup> groups;
    int gsz = 1;
    for (const auto& [key, t] : T.cache) if (t.feasible) {
        GfGroup g; g.type = key.first; g.mask = key.second; g.w = t.w;
        for (usize j = 0; j < m; ++j) if (g.mask >> j & 1u) g.area += E.area[E.plates[j]];
        gsz = std::max(gsz, bin_popcount(g.mask));
        groups.push_back(g);
    }
    std::vector<std::vector<int>> by_low(m);
    for (int gi = 0; gi < (int)groups.size(); ++gi) {
        uint32_t mk = groups[(usize)gi].mask; int low = 0; while (!((mk >> low) & 1u)) ++low;
        by_low[(usize)low].push_back(gi);
    }
    for (auto& v : by_low) std::stable_sort(v.begin(), v.end(), [&](int x, int y) {
        if (groups[(usize)x].area != groups[(usize)y].area) return groups[(usize)x].area > groups[(usize)y].area;
        return groups[(usize)x].w < groups[(usize)y].w; });
    auto band = [&](const GfGroup& g) {
        const GfSlot& S = E.slots[(usize)E.type_rep[(usize)g.type]];
        return (double)std::max(0.0f, S.W - g.w) * (double)S.H;
    };
    // all covers of the nb largest plates by <= k feasible groups whose sheets (plus filler-only sheets) hold every
    // part x (1 + slack); best `pool_n` by the summed free band
    // plates with a feasible single-sheet test on some type; a plate that fits no sheet alone cannot be covered and
    // is left to the fillers (it would otherwise block every cover)
    std::vector<usize> coverable;
    for (usize j = 0; j < m; ++j) {
        bool ok = false;
        for (const auto& g : groups) if (g.mask == (1u << j)) { ok = true; break; }
        if (ok) coverable.push_back(j);
    }
    const usize mc = coverable.size();
    auto search = [&](int k, usize nb) {
        std::vector<GfPartition> pool;
        uint32_t full = 0;
        for (usize i = 0; i < nb && i < mc; ++i) full |= 1u << coverable[i];
        std::vector<int> chosen;
        std::vector<int> used(E.n_types, 0);
        long long nodes = 0;
        std::function<void(uint32_t, double)> dfs = [&](uint32_t cov, double score) {
            if (++nodes > 300000) return;
            if (cov == full) {
                std::vector<int> u = used;
                double cap = 0.0;
                for (int gi : chosen) cap += E.slots[(usize)E.type_rep[(usize)groups[(usize)gi].type]].usable;
                std::vector<int> extra;
                int n_sh = (int)chosen.size();
                const double need = E.total_area * (1.0 + slack);
                for (int t : types_by_area)
                    while (cap < need && n_sh < k && u[(usize)t] < avail[(usize)t]) {
                        u[(usize)t]++; n_sh++; extra.push_back(t);
                        cap += E.slots[(usize)E.type_rep[(usize)t]].usable;
                    }
                if (cap < need) return;
                GfPartition p; p.nb = (int)nb; p.extra_types = extra;
                for (int gi : chosen) p.groups.push_back(groups[(usize)gi]);
                p.score = score;
                for (int t : extra) p.score += (double)E.slots[(usize)E.type_rep[(usize)t]].usable;
                pool.push_back(std::move(p));
                std::stable_sort(pool.begin(), pool.end(), [](const GfPartition& a, const GfPartition& b) { return a.score > b.score; });
                if ((int)pool.size() > pool_n) pool.pop_back();
                return;
            }
            const uint32_t open = full & ~cov;
            int rem = bin_popcount(open);
            if ((int)chosen.size() + (rem + gsz - 1) / gsz > k) return;
            int low = 0; while (!((open >> low) & 1u)) ++low;
            for (int gi : by_low[(usize)low]) {
                const GfGroup& g = groups[(usize)gi];
                if (g.mask & (cov | ~full)) continue;
                if (used[(usize)g.type] >= avail[(usize)g.type]) continue;
                used[(usize)g.type]++;
                chosen.push_back(gi);
                dfs(cov | g.mask, score + band(g));
                chosen.pop_back();
                used[(usize)g.type]--;
            }
        };
        dfs(0u, 0.0);
        return pool;
    };
    std::vector<GfPartition> A, B;
    int kA = -1; usize nbA = 0;
    const int k_max = std::max(k_lb, max_bins);
    // A: the fewest sheets first; at that k the most (largest-first) plates covered
    for (int k = k_lb; k <= k_max && A.empty(); ++k)
        for (usize nb = mc; nb >= 1; --nb) {
            A = search(k, nb);
            if (!A.empty()) { kA = k; nbA = nb; break; }
        }
    // B: when A leaves plates uncovered, also a FULL cover with at most one more sheet (portfolio diversity)
    if (!A.empty() && nbA < mc)
        for (int k = kA; k <= std::min(k_max, kA + 1) && B.empty(); ++k) B = search(k, mc);
    std::vector<GfPartition> out;
    for (usize i = 0; i < std::max(A.size(), B.size()) && (int)out.size() < pool_n; ++i) {
        if (i < A.size()) out.push_back(A[i]);
        if (i < B.size() && (int)out.size() < pool_n) out.push_back(B[i]);
    }
    return out;
}

// ---------------------------------------------------------------------------------------------- hole seating
// Seat leftover parts into the holes of placed parts (part_holes_mode >= 1): fill_cavities' certificate (every
// vertex inside the hole ring, no edge crossing it, no clash with what already sits in it), 36 orientations x a
// 24 x 24 position grid, largest leftover first.
inline std::vector<usize> gf_seat_in_holes(const GfEnv& E, std::vector<GfSheet>& sheets, const std::vector<usize>& left) {
    if (!E.part_holes || left.empty()) return left;
    struct Cav { usize sheet; Polygon poly; std::vector<Polygon> occ; };
    std::vector<Cav> cavs;
    for (usize si = 0; si < sheets.size(); ++si) {
        std::vector<Polygon> placed = gf_polys(sheets[si].parts);
        for (const auto& p : sheets[si].parts) {
            if (p.gid >= E.part_holes->size()) continue;
            for (const auto& h : (*E.part_holes)[p.gid]) {
                Cav c{si, h.transform_clone(p.dt.compose()), {}};
                for (const auto& q : placed) if (bbox_overlap(q.bbox, c.poly.bbox) && brute_overlap(q, c.poly)) c.occ.push_back(q);
                cavs.push_back(std::move(c));
            }
        }
    }
    if (cavs.empty()) return left;
    std::stable_sort(cavs.begin(), cavs.end(), [](const Cav& a, const Cav& b) { return a.poly.area > b.poly.area; });
    std::vector<usize> order = left;
    std::stable_sort(order.begin(), order.end(), [&](usize a, usize b) { return E.area[a] > E.area[b]; });
    std::vector<usize> rest;
    const f32 TWO_PI = 6.28318530718f;
    const int NROT = 36, G = 24;
    for (usize g : order) {
        const Polygon& shp = *E.part(g).collision_shape;
        bool done = false;
        for (auto& cv : cavs) {
            if (done) break;
            if (shp.area >= cv.poly.area) continue;
            for (int r = 0; r < NROT && !done; ++r) {
                f32 ang = TWO_PI * (f32)r / (f32)NROT;
                if (E.part(g).allowed_rotation.kind != RotationKind::Continuous) {
                    const auto& d = E.part(g).allowed_rotation.discrete;
                    if (E.part(g).allowed_rotation.kind == RotationKind::None) { if (r) break; ang = 0.0f; }
                    else { if ((usize)r >= d.size()) break; ang = d[(usize)r]; }
                }
                Polygon srot = shp.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());
                if (srot.bbox.width() > cv.poly.bbox.width() || srot.bbox.height() > cv.poly.bbox.height()) continue;
                f32 sx = cv.poly.bbox.width() - srot.bbox.width(), sy = cv.poly.bbox.height() - srot.bbox.height();
                for (int iy = 0; iy <= G && !done; ++iy)
                    for (int ix = 0; ix <= G && !done; ++ix) {
                        f32 tx = cv.poly.bbox.x_min - srot.bbox.x_min + sx * (f32)ix / (f32)G;
                        f32 ty = cv.poly.bbox.y_min - srot.bbox.y_min + sy * (f32)iy / (f32)G;
                        Polygon cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        bool ok = true;
                        for (const auto& v : cand.vertices) if (!collides(cv.poly, v)) { ok = false; break; }
                        if (!ok) continue;
                        for (usize a = 0; a < cand.n_vertices() && ok; ++a)
                            for (usize b = 0; b < cv.poly.n_vertices(); ++b)
                                if (collides(cand.edge(a), cv.poly.edge(b))) { ok = false; break; }
                        if (!ok) continue;
                        for (const auto& o : cv.occ) if (bbox_overlap(o.bbox, cand.bbox) && brute_overlap(cand, o)) { ok = false; break; }
                        if (!ok) continue;
                        sheets[cv.sheet].parts.push_back({g, RigidTransform(ang, tx, ty), cand.vertices});
                        cv.occ.push_back(std::move(cand));
                        done = true;
                    }
            }
        }
        if (!done) rest.push_back(g);
    }
    return rest;
}

// ---------------------------------------------------------------------------------------------- remnant
// Re-pack one sheet's parts into the smallest bounding rectangle of the used region (anchored at the sheet
// origin): sweep strip heights h < H, each as a keep-out band [0,W] x [h,H] added to the sheet's keep-outs (so
// irregular outlines / cut-outs stay respected), width minimised by the strip packer; keep the least x_max * y_max.
inline GfSheet gf_remnant(const GfEnv& E, const GfSheet& sh, uint64_t seed) {
    if (sh.parts.empty() || E.out_of_time()) return sh;
    const GfSlot& S = E.slots[(usize)sh.slot];
    auto bbox_of = [](const std::vector<GfPlaced>& ps) {
        f32 x = 0.0f, y = 0.0f;
        for (const auto& p : ps) for (const auto& v : p.verts) { x = max_f(x, v.x); y = max_f(y, v.y); }
        return std::make_pair(x, y);
    };
    auto [x0, y0] = bbox_of(sh.parts);
    double best = (double)x0 * (double)y0;
    GfSheet out = sh;
    std::vector<usize> ids; double A = 0.0; f32 hmin = 0.0f;
    for (const auto& p : sh.parts) { ids.push_back(p.gid); A += E.area[p.gid]; hmin = max_f(hmin, min_bounding_height(*E.part(p.gid).collision_shape, 90)); }
    const double fr[] = {0.875, 0.75, 0.625, 0.5, 0.4375, 0.375, 0.3125, 0.25, 0.1875, 0.125};
    int k = 0;
    for (double f : fr) {
        if (E.out_of_time()) break;
        f32 h = (f32)(f * S.H);
        if (h < hmin * 1.001f || (double)h * S.W < A) continue;
        if ((double)h * std::max((double)hmin, A / (double)h) >= best) continue;
        ++k;
        auto sub = subset_items(*E.parts, ids);
        std::vector<Polygon> holes = S.holes;
        try { holes.push_back(Polygon::create({Point(-1.0f, h), Point(S.W + 1.0f, h), Point(S.W + 1.0f, S.H + 1.0f), Point(-1.0f, S.H + 1.0f)})); }
        catch (...) { continue; }
        StripResult R = run_strip(sub, *E.engine, S.H, S.W, E.pack_budget(0.35), seed + 31ull * (uint64_t)k, 2, &holes, nullptr);
        if (!R.fits) break;   // a lower band will not fit either
        Layout live = Layout::from_snapshot(R.sol.layout_snapshot);
        compact_left(live, S.W, S.H, &holes);
        std::vector<GfPlaced> ps;
        live.placed_parts.for_each([&](PartKey, const PlacedPart& pi) { ps.push_back({ids[pi.part_id], pi.d_transf, pi.shape->vertices}); });
        if (ps.size() != ids.size()) continue;
        ps = gf_clean(E, S, std::move(ps), {});
        if (ps.size() != ids.size()) continue;
        auto [x, y] = bbox_of(ps);
        if ((double)x * (double)y < best) { best = (double)x * (double)y; out.parts = std::move(ps); }
    }
    return out;
}

// ---------------------------------------------------------------------------------------------- one chain
inline GfLayout gf_chain(const GfEnv& E, const GfPartition& P, uint64_t seed, int chain_id) {
    GfLayout L;
    const usize N = E.parts->size();
    const bool verbose = gf_verbose();
    std::vector<char> used_slot(E.slots.size(), 0);
    auto take_slot = [&](int type) -> int {
        for (usize s = 0; s < E.slots.size(); ++s) if (!used_slot[s] && E.slots[s].type == type) { used_slot[s] = 1; return (int)s; }
        return -1;
    };
    struct WS { GfSheet sh; std::vector<usize> plates; std::vector<usize> longs; double plate_area = 0.0; };
    std::vector<WS> ws;
    std::vector<char> in_group(N, 0);
    for (const auto& g : P.groups) {
        WS w; w.sh.slot = take_slot(g.type);
        if (w.sh.slot < 0) continue;
        for (usize j = 0; j < E.plates.size(); ++j) if (g.mask >> j & 1u) { w.plates.push_back(E.plates[j]); in_group[E.plates[j]] = 1; w.plate_area += E.area[E.plates[j]]; }
        ws.push_back(std::move(w));
    }
    for (int t : P.extra_types) { WS w; w.sh.slot = take_slot(t); if (w.sh.slot >= 0) ws.push_back(std::move(w)); }
    // tightest first (plate area / usable), then by slot index
    std::stable_sort(ws.begin(), ws.end(), [&](const WS& a, const WS& b) {
        double fa = a.plate_area / E.slots[(usize)a.sh.slot].usable, fb = b.plate_area / E.slots[(usize)b.sh.slot].usable;
        if (fa != fb) return fa > fb;
        return a.sh.slot < b.sh.slot; });
    // pool = every part not in a group that fits at least one sheet type
    std::vector<char> in_pool(N, 0);
    for (usize g = 0; g < N; ++g) {
        if (in_group[g]) continue;
        bool any = false;
        for (int t = 0; t < E.n_types; ++t) if (E.fits[(usize)t][g]) { any = true; break; }
        if (any) in_pool[g] = 1; else L.unplaced.push_back(g);
    }
    // long parts -> sheets, longest first, worst-fit by free area, area <= 0.6 x free, must fit the sheet
    {
        std::vector<usize> longs;
        for (usize g = 0; g < N; ++g) if (in_pool[g] && E.is_long[g]) longs.push_back(g);
        std::stable_sort(longs.begin(), longs.end(), [&](usize a, usize b) { return E.part(a).collision_shape->diameter > E.part(b).collision_shape->diameter; });
        std::vector<double> fr(ws.size());
        for (usize i = 0; i < ws.size(); ++i) fr[i] = E.slots[(usize)ws[i].sh.slot].usable - ws[i].plate_area;
        for (usize g : longs) {
            int bi = -1;
            for (usize i = 0; i < ws.size(); ++i) {
                if (!E.fits[(usize)E.slots[(usize)ws[i].sh.slot].type][g]) continue;
                if (E.area[g] > 0.6 * fr[i]) continue;
                if (bi < 0 || fr[i] > fr[(usize)bi]) bi = (int)i;
            }
            if (bi >= 0) { ws[(usize)bi].longs.push_back(g); fr[(usize)bi] -= E.area[g]; }
        }
    }
    std::vector<char> mand(N, 0);
    std::vector<usize> carried;
    std::vector<char> released(N, 0);
    auto pool_list = [&]() { std::vector<usize> v; for (usize g = 0; g < N; ++g) if (in_pool[g]) v.push_back(g); return v; };
    auto commit = [&](WS& w, const std::vector<GfPlaced>& ps) {
        for (const auto& p : ps) { in_pool[p.gid] = 0; w.sh.parts.push_back(p); }
    };
    for (usize wi = 0; wi < ws.size(); ++wi) {
        if (E.out_of_time()) break;
        WS& w = ws[wi];
        const GfSlot& S = E.slots[(usize)w.sh.slot];
        uint64_t sd = seed + 7919ull * (uint64_t)(wi + 1);
        // offer: plates (mandatory) + assigned / carried long parts + small parts by area to the target
        std::vector<usize> offer = w.plates;
        double acc = w.plate_area;
        std::fill(mand.begin(), mand.end(), 0);
        for (usize g : w.plates) mand[g] = 1;
        std::set<usize> offered(offer.begin(), offer.end());
        auto add = [&](usize g) {
            if (!in_pool[g] || offered.count(g) || !E.fits[(usize)S.type][g]) return false;
            offer.push_back(g); offered.insert(g); acc += E.area[g]; return true;
        };
        for (usize g : w.longs) add(g);
        for (usize g : carried) if (acc + E.area[g] <= E.target * S.usable) add(g);
        int n_small = 0;
        std::vector<usize> pl = pool_list();
        std::stable_sort(pl.begin(), pl.end(), [&](usize a, usize b) { return E.area[a] > E.area[b]; });
        for (usize g : pl) {
            if (n_small >= E.knap_max) break;
            if (E.is_long[g] || offered.count(g)) continue;
            if (acc + E.area[g] <= E.target * S.usable && add(g)) ++n_small;
        }
        g_prio_retries_tl = E.prio_retries;
        std::vector<GfPlaced> got = gf_pack(E, w.sh.slot, offer, &mand, {}, E.pack_budget(1.0), sd, true);
        int miss = 0; for (usize g : w.plates) { bool f = false; for (auto& p : got) if (p.gid == g) { f = true; break; } if (!f) ++miss; }
        if (miss > 0 && !E.out_of_time()) {
            // plates + long parts only (the robust floor of the prototype's pack_plates)
            std::vector<usize> o2 = w.plates; for (usize g : w.longs) if (in_pool[g] && E.fits[(usize)S.type][g]) o2.push_back(g);
            std::vector<GfPlaced> g2 = gf_pack(E, w.sh.slot, o2, &mand, {}, E.pack_budget(1.0), sd + 1, true);
            int miss2 = 0; for (usize g : w.plates) { bool f = false; for (auto& p : g2) if (p.gid == g) { f = true; break; } if (!f) ++miss2; }
            if (miss2 < miss) { got = std::move(g2); miss = miss2; }
        }
        // plates that did not land go back to the pool as ordinary parts
        for (usize g : w.plates) in_pool[g] = 1;
        commit(w, got);
        // long parts assigned to a finished sheet but not placed: carry them to the next sheet's joint pack
        for (usize g : w.longs) released[g] = 1;
        carried.clear();
        for (usize g = 0; g < N; ++g) if (in_pool[g] && E.is_long[g] && released[g]) carried.push_back(g);
        std::stable_sort(carried.begin(), carried.end(), [&](usize a, usize b) { return E.area[a] > E.area[b]; });
        double area_now = gf_area_of(E, w.sh);
        // insertion of the remaining (non-long) parts into the frozen layout
        if (!E.out_of_time()) {
            double free = S.usable - area_now;
            std::vector<usize> ins;
            for (usize g : pool_list()) if (E.fits[(usize)S.type][g] && E.area[g] <= free) ins.push_back(g);
            std::stable_sort(ins.begin(), ins.end(), [&](usize a, usize b) { return E.area[a] > E.area[b]; });
            if (ins.size() > 40) ins.resize(40);
            if (!ins.empty()) {
                std::vector<Polygon> frozen = gf_polys(w.sh.parts);
                g_prio_retries_tl = 1;
                std::vector<GfPlaced> add2 = gf_pack(E, w.sh.slot, ins, nullptr, frozen, E.pack_budget(0.5), sd + 2, true);
                commit(w, add2);
            }
        }
        if (verbose)
            std::fprintf(stderr, "[fm3] chain %d sheet %zu (slot %d): plates %zu (%.1f%%) + offered %zu -> kept %zu, missing plates %d, util %.1f%%, pool %zu\n",
                         chain_id, wi, w.sh.slot, w.plates.size(), 100.0 * w.plate_area / S.usable, offer.size(), w.sh.parts.size(), miss,
                         100.0 * gf_area_of(E, w.sh) / S.usable, pool_list().size());
    }
    // leftover repack: every current part mandatory + leftovers normal, most free sheet first
    std::vector<usize> left = pool_list();
    if (!left.empty() && !E.out_of_time()) {
        std::vector<usize> order(ws.size());
        for (usize i = 0; i < ws.size(); ++i) order[i] = i;
        std::stable_sort(order.begin(), order.end(), [&](usize a, usize b) {
            return E.slots[(usize)ws[a].sh.slot].usable - gf_area_of(E, ws[a].sh) > E.slots[(usize)ws[b].sh.slot].usable - gf_area_of(E, ws[b].sh); });
        for (usize oi : order) {
            left = pool_list();
            if (left.empty() || E.out_of_time()) break;
            WS& w = ws[oi];
            const GfSlot& S = E.slots[(usize)w.sh.slot];
            double free = S.usable - gf_area_of(E, w.sh);
            std::vector<usize> cand;
            for (usize g : left) if (E.fits[(usize)S.type][g] && E.area[g] <= free) cand.push_back(g);
            if (cand.empty()) continue;
            std::fill(mand.begin(), mand.end(), 0);
            std::vector<usize> offer;
            for (const auto& p : w.sh.parts) { offer.push_back(p.gid); mand[p.gid] = 1; }
            offer.insert(offer.end(), cand.begin(), cand.end());
            g_prio_retries_tl = 1;
            std::vector<GfPlaced> got = gf_pack(E, w.sh.slot, offer, &mand, {}, E.pack_budget(1.0), seed + 104729ull * (oi + 1), true);
            bool all_cur = true;
            for (const auto& p : w.sh.parts) { bool f = false; for (auto& q : got) if (q.gid == p.gid) { f = true; break; } if (!f) { all_cur = false; break; } }
            double a_new = 0.0; for (auto& q : got) a_new += E.area[q.gid];
            if (all_cur && a_new > gf_area_of(E, w.sh) + 1e-9) {
                w.sh.parts = got;
                for (auto& q : got) in_pool[q.gid] = 0;
                if (verbose) std::fprintf(stderr, "[fm3] chain %d repack sheet slot %d: now %zu parts, util %.1f%%\n", chain_id, w.sh.slot, got.size(), 100.0 * a_new / S.usable);
            }
        }
    }
    std::vector<GfSheet> sheets;
    for (auto& w : ws) if (!w.sh.parts.empty()) sheets.push_back(std::move(w.sh)); else used_slot[(usize)w.sh.slot] = 0;
    left = pool_list();
    // part-in-hole seating of the leftovers
    if (!left.empty()) {
        std::vector<usize> rest = gf_seat_in_holes(E, sheets, left);
        for (usize g : left) in_pool[g] = 0;
        for (usize g : rest) in_pool[g] = 1;
        left = rest;
    }
    // leftovers -> further sheets (greedy strip-and-cut over the unused slots, smallest that holds them first)
    if (!left.empty()) {
        std::vector<int> unused;
        for (usize s = 0; s < E.slots.size(); ++s) if (!used_slot[s]) unused.push_back((int)s);
        double la = 0.0; for (usize g : left) la += E.area[g];
        std::stable_sort(unused.begin(), unused.end(), [&](int a, int b) {
            bool ha = E.slots[(usize)a].usable >= 1.15 * la, hb = E.slots[(usize)b].usable >= 1.15 * la;
            if (ha != hb) return ha;
            if (ha) return E.slots[(usize)a].usable < E.slots[(usize)b].usable;
            return E.slots[(usize)a].usable > E.slots[(usize)b].usable; });
        if (!unused.empty() && !E.out_of_time()) {
            SheetDims dims; std::vector<std::vector<Polygon>> hs; bool any_h = false;
            for (int s : unused) { dims.push_back({E.slots[(usize)s].W, E.slots[(usize)s].H}); hs.push_back(E.slots[(usize)s].holes); any_h |= !E.slots[(usize)s].holes.empty(); }
            if (!any_h) hs.clear();
            std::vector<usize> fit_any;
            for (usize g : left) { bool f = false; for (int s : unused) if (E.fits[(usize)E.slots[(usize)s].type][g]) { f = true; break; } if (f) fit_any.push_back(g); }
            auto sub = subset_items(*E.parts, fit_any);
            Sheets r = greedy_fill(sub, *E.engine, dims[0].first, dims[0].second, E.pack_budget(1.0), (int)unused.size(), hs, seed + 17ull, false, &dims);
            std::map<int, usize> at;
            for (auto& pl : r.placements) {
                if (pl.sheet < 0 || pl.sheet >= (int)unused.size() || pl.color_id >= fit_any.size()) continue;
                int slot = unused[(usize)pl.sheet];
                if (!at.count(slot)) { at[slot] = sheets.size(); GfSheet s; s.slot = slot; sheets.push_back(std::move(s)); }
                sheets[at[slot]].parts.push_back({fit_any[pl.color_id], pl.d_transf, pl.verts});
            }
            for (auto& [slot, idx] : at) {
                std::vector<GfPlaced> c = gf_clean(E, E.slots[(usize)slot], std::move(sheets[idx].parts), {});
                sheets[idx].parts = std::move(c);
                for (auto& p : sheets[idx].parts) in_pool[p.gid] = 0;
            }
        }
        left = pool_list();
    }
    // remnant: least-filled sheet -> minimal bounding rectangle of its used region
    if (!sheets.empty() && !E.out_of_time() && gf_env_i("NP_FM3_REMNANT", 1) != 0) {
        usize li = 0; double lf = 2.0;
        for (usize i = 0; i < sheets.size(); ++i) {
            double f = gf_area_of(E, sheets[i]) / E.slots[(usize)sheets[i].slot].usable;
            if (f < lf) { lf = f; li = i; }
        }
        sheets[li] = gf_remnant(E, sheets[li], seed + 3ull);
    }
    // Same-type sheets are interchangeable (same size, same keep-outs): renumber each type's sheets onto its
    // lowest slot indices, fullest first, so the output has no empty sheet in between and the remnant is last.
    for (int t = 0; t < E.n_types; ++t) {
        std::vector<usize> idx;
        std::vector<int> slots_t;
        for (usize i = 0; i < sheets.size(); ++i) if (E.slots[(usize)sheets[i].slot].type == t) idx.push_back(i);
        for (usize sl = 0; sl < E.slots.size() && slots_t.size() < idx.size(); ++sl) if (E.slots[sl].type == t) slots_t.push_back((int)sl);
        std::stable_sort(idx.begin(), idx.end(), [&](usize a, usize b) { return gf_area_of(E, sheets[a]) > gf_area_of(E, sheets[b]); });
        for (usize q = 0; q < idx.size(); ++q) sheets[idx[q]].slot = slots_t[q];
    }
    std::stable_sort(sheets.begin(), sheets.end(), [](const GfSheet& a, const GfSheet& b) { return a.slot < b.slot; });
    L.sheets = std::move(sheets);
    for (usize g : left) L.unplaced.push_back(g);
    std::sort(L.unplaced.begin(), L.unplaced.end());
    L.valid = true;
    return L;
}

// ---------------------------------------------------------------------------------------------- portfolio
struct GfRank { int unplaced = 0; int sheets = 0; double used_area = 0.0; double min_fill = 0.0; };
inline bool gf_rank_better(const GfRank& a, const GfRank& b) {
    if (a.unplaced != b.unplaced) return a.unplaced < b.unplaced;
    if (a.sheets != b.sheets) return a.sheets < b.sheets;
    if (std::fabs(a.used_area - b.used_area) > 1e-6 * std::max(1.0, b.used_area)) return a.used_area < b.used_area;
    return a.min_fill < b.min_fill - 1e-9 * std::max(1.0, b.min_fill);
}
inline GfRank gf_rank_sheets(const Sheets& r, usize n_parts, const std::vector<double>& area,
                             const std::function<std::pair<f32, f32>(int)>& dims_of) {
    GfRank k;
    std::map<int, double> a;
    usize placed = 0;
    for (const auto& p : r.placements) if (p.sheet >= 0 && p.color_id < area.size()) { a[p.sheet] += area[p.color_id]; ++placed; }
    k.unplaced = (int)(n_parts - placed);
    k.sheets = (int)a.size();
    k.min_fill = a.empty() ? 0.0 : 1e300;
    for (auto& [s, v] : a) { auto d = dims_of(s); k.used_area += (double)d.first * d.second; k.min_fill = std::min(k.min_fill, v); }
    return k;
}

inline Sheets gf_to_sheets(const GfEnv& E, const GfLayout& L) {
    Sheets out;
    int ns = 0;
    for (const auto& s : L.sheets) ns = std::max(ns, s.slot + 1);
    out.counts.assign((usize)ns, 0);
    out.widths.assign((usize)ns, 0.0f);
    for (const auto& s : L.sheets)
        for (const auto& p : s.parts) {
            SheetPlacement sp; sp.verts = p.verts; sp.color_id = p.gid; sp.sheet = s.slot; sp.d_transf = p.dt;
            out.counts[(usize)s.slot]++;
            for (const auto& v : p.verts) out.widths[(usize)s.slot] = max_f(out.widths[(usize)s.slot], v.x);
            out.placements.push_back(std::move(sp));
        }
    out.n_sheets = ns;
    out.ok = L.unplaced.empty();
    (void)E;
    return out;
}

// Entry point. `slots_in` = every sheet the job may use (size + sheet-local keep-outs). `baseline` runs the plain
// fit 2 / fit 0 solve (called on this thread with all n_threads workers); `defects` = the C ABI's exact final
// verification (overlap pairs + out-of-bounds) of a candidate in this part index space.
inline Sheets group_fill(const std::vector<std::pair<Part, usize>>& parts, const CollisionConfig& engine,
                         const std::vector<std::pair<f32, f32>>& slot_dims,
                         const std::vector<std::vector<Polygon>>& slot_holes,
                         double budget, uint64_t seed, int n_threads, bool iter_mode,
                         const std::vector<std::vector<Polygon>>* part_holes,
                         const std::function<Sheets()>& baseline,
                         const std::function<int(const Sheets&)>& defects,
                         const std::function<std::optional<Polygon>(const Polygon&)>& shrink = {}) {
    const double t0 = now_seconds();
    const bool verbose = gf_verbose();
    n_threads = std::max(1, n_threads);
    GfEnv E;
    E.parts = &parts; E.engine = &engine; E.iter = iter_mode;
    E.part_holes = part_holes;
    E.shrink = shrink;
    const usize N = parts.size();
    // slots and types (identical size + identical keep-outs => same type)
    for (usize s = 0; s < slot_dims.size(); ++s) {
        GfSlot S; S.W = slot_dims[s].first; S.H = slot_dims[s].second;
        if (s < slot_holes.size()) S.holes = slot_holes[s];
        double ha = 0.0; for (const auto& h : S.holes) ha += h.area;
        S.usable = std::max(0.05 * (double)S.W * S.H, (double)S.W * S.H - ha);
        S.type = -1;
        for (usize r = 0; r < E.type_rep.size() && S.type < 0; ++r) {
            const GfSlot& Q = E.slots[(usize)E.type_rep[r]];
            if (std::fabs(Q.W - S.W) > 1e-4f || std::fabs(Q.H - S.H) > 1e-4f || Q.holes.size() != S.holes.size()) continue;
            bool same = true;
            for (usize i = 0; i < S.holes.size() && same; ++i) {
                if (Q.holes[i].n_vertices() != S.holes[i].n_vertices()) { same = false; break; }
                for (usize k = 0; k < S.holes[i].n_vertices(); ++k)
                    if (!(Q.holes[i].vertices[k] == S.holes[i].vertices[k])) { same = false; break; }
            }
            if (same) S.type = (int)r;
        }
        if (S.type < 0) { S.type = (int)E.type_rep.size(); E.type_rep.push_back((int)s); E.type_count.push_back(0); }
        E.type_count[(usize)S.type]++;
        E.slots.push_back(std::move(S));
    }
    E.n_types = (int)E.type_rep.size();
    E.area.resize(N);
    for (usize g = 0; g < N; ++g) { E.area[g] = parts[g].first.area(); E.total_area += E.area[g]; }
    E.fits.assign((usize)E.n_types, std::vector<char>(N, 0));
    for (int t = 0; t < E.n_types; ++t) {
        const GfSlot& S = E.slots[(usize)E.type_rep[(usize)t]];
        for (usize g = 0; g < N; ++g)
            E.fits[(usize)t][g] = (parts[g].first.collision_shape && E.area[g] <= S.usable && fits_column(parts[g].first, S.W, S.H)) ? 1 : 0;
    }
    // budget: in iteration mode every joint pack gets the caller's iteration budget (a fit-1 call's worth); in
    // time mode the caller's seconds are the WHOLE run (deadline), split over the expected number of packs.
    double max_usable = 0.0; f32 max_dim = 0.0f;
    for (const auto& S : E.slots) { max_usable = std::max(max_usable, S.usable); max_dim = max_f(max_dim, max_f(S.W, S.H)); }
    const int k_lb = std::max(1, (int)std::ceil(E.total_area / max_usable - 1e-9));
    if (iter_mode) E.B = budget * gf_env_d("NP_FM3_PACK_FRAC", 1.0);
    else { E.deadline = t0 + budget; E.B = budget / (double)(3 * k_lb + 10); }
    E.prio_retries = std::max(0, gf_env_i("NP_FM3_PRIO_RETRIES", 2));
    E.target = gf_env_d("NP_FM3_TARGET", 0.90);
    E.knap_max = std::max(1, gf_env_i("NP_FM3_KNAP_MAX", 24));
    // classes
    const double plate_frac = gf_env_d("NP_FM3_PLATE_FRAC", 0.05);
    const int group_max = std::max(0, std::min(24, gf_env_i("NP_FM3_GROUP_MAX", 24)));
    E.long_thr = (f32)(gf_env_d("NP_FM3_LONG_FRAC", 0.33) * (double)max_dim);
    std::vector<usize> by_area(N);
    for (usize g = 0; g < N; ++g) by_area[g] = g;
    std::stable_sort(by_area.begin(), by_area.end(), [&](usize a, usize b) { return E.area[a] > E.area[b]; });
    E.is_plate.assign(N, 0); E.is_long.assign(N, 0);
    for (usize g : by_area) if (E.area[g] >= plate_frac * max_usable && (int)E.plates.size() < group_max) { E.plates.push_back(g); E.is_plate[g] = 1; }
    for (usize g = 0; g < N; ++g) if (!E.is_plate[g] && parts[g].first.collision_shape && parts[g].first.collision_shape->diameter >= E.long_thr) E.is_long[g] = 1;
    int n_long = 0; for (usize g = 0; g < N; ++g) n_long += E.is_long[g];

    // a. group tests (shared, parallel)
    GfTests T = gf_group_tests(E, n_threads, seed);
    const double t_tests = now_seconds() - t0;
    // b. covers
    const int n_chains = std::max(1, gf_env_i("NP_FM3_CHAINS", std::max(1, n_threads - 1)));
    std::vector<GfPartition> parts_pool = gf_covers(E, T, (int)E.slots.size(), n_chains, gf_env_d("NP_FM3_SLACK", 0.06));
    if (parts_pool.empty()) {
        // no plate cover (no plates, or none fits): a single partition of filler-only sheets
        GfPartition p;
        std::vector<int> tb(E.n_types); for (int t = 0; t < E.n_types; ++t) tb[(usize)t] = t;
        std::stable_sort(tb.begin(), tb.end(), [&](int a, int b) { return E.slots[(usize)E.type_rep[(usize)a]].usable > E.slots[(usize)E.type_rep[(usize)b]].usable; });
        double cap = 0.0; std::vector<int> u(E.n_types, 0);
        for (int t : tb) while (cap < E.total_area * 1.06 && u[(usize)t] < E.type_count[(usize)t]) { u[(usize)t]++; p.extra_types.push_back(t); cap += E.slots[(usize)E.type_rep[(usize)t]].usable; }
        parts_pool.push_back(p);
    }
    if (verbose) {
        std::fprintf(stderr, "[fm3] %zu parts: plates %zu, long %d, slots %zu, types %d, k_lb %d | tests %d LBF + %d upgraded, %d feasible%s, %.1fs | %zu cover(s):",
                     N, E.plates.size(), n_long, E.slots.size(), E.n_types, k_lb, T.n_tests, T.n_upgrades, T.n_feasible, T.capped ? " [capped]" : "", t_tests, parts_pool.size());
        for (const auto& p : parts_pool) std::fprintf(stderr, " [%zu groups + %zu extra, nb %d, score %.3g]", p.groups.size(), p.extra_types.size(), p.nb, p.score);
        std::fprintf(stderr, "\n");
    }
    // c-e. chains in parallel (one worker each)
    std::vector<GfLayout> chains((usize)n_chains);
    gf_parallel(n_threads, (usize)n_chains, [&](usize c) {
        const GfPartition& P = parts_pool[c % parts_pool.size()];
        uint64_t sd = seed + 1000003ull * (uint64_t)(c / parts_pool.size()) + 7ull * (uint64_t)c;
        chains[c] = gf_chain(E, P, sd, (int)c);
        chains[c].tag = "chain" + std::to_string(c);
    });
    auto dims_of = [&](int s) -> std::pair<f32, f32> {
        if (s >= 0 && s < (int)E.slots.size()) return {E.slots[(usize)s].W, E.slots[(usize)s].H};
        return {E.slots.back().W, E.slots.back().H};
    };
    bool have = false; Sheets best; GfRank best_rank; std::string best_tag;
    for (auto& L : chains) {
        if (!L.valid) continue;
        Sheets s = gf_to_sheets(E, L);
        int d = defects ? defects(s) : 0;
        GfRank rk = gf_rank_sheets(s, N, E.area, dims_of);
        if (verbose) {
            std::fprintf(stderr, "[fm3] %s: %d sheet(s), unplaced %d, defects %d, util", L.tag.c_str(), rk.sheets, rk.unplaced, d);
            for (const auto& sh : L.sheets) std::fprintf(stderr, " %.1f", 100.0 * gf_area_of(E, sh) / E.slots[(usize)sh.slot].usable);
            std::fprintf(stderr, "\n");
        }
        if (d > 0) continue;
        if (!have || gf_rank_better(rk, best_rank)) { best = std::move(s); best_rank = rk; best_tag = L.tag; have = true; }
    }
    const double t_chains = now_seconds() - t0;
    // f. baseline member: the plain fit 2 / fit 0 solve with every thread as workers, unless a chain already sits on
    // the area lower bound with nothing left over (nothing can beat it on the sheet count).
    bool ran_base = false;
    GfRank base_rank;
    const bool want_base = gf_env_i("NP_FM3_BASELINE", 1) != 0 && baseline &&
                           (!have || best_rank.unplaced > 0 || best_rank.sheets > k_lb) && !g_cancel.load();
    if (want_base) {
        unsigned saved = g_n_workers;
        g_n_workers = (unsigned)n_threads;
        Sheets b = baseline();
        g_n_workers = saved;
        ran_base = true;
        int d = defects ? defects(b) : 0;
        base_rank = gf_rank_sheets(b, N, E.area, dims_of);
        if (d == 0 && (!have || gf_rank_better(base_rank, best_rank))) { best = std::move(b); best_rank = base_rank; best_tag = "baseline"; have = true; }
    }
    if (!have) { best = Sheets(); best.ok = false; }
    std::fprintf(stderr, "[np] fit_mode 3: winner %s: %d sheet(s), unplaced %d | plates %zu long %d, tests %d+%d (%d feasible), covers %zu, chains %d, baseline %s | %.1fs (tests %.1fs, chains %.1fs)\n",
                 have ? best_tag.c_str() : "none", best_rank.sheets, best_rank.unplaced, E.plates.size(), n_long, T.n_tests, T.n_upgrades, T.n_feasible,
                 parts_pool.size(), n_chains, ran_base ? (base_rank.sheets ? (std::to_string(base_rank.sheets) + " sheet(s)").c_str() : "ran") : "skipped",
                 now_seconds() - t0, t_tests, t_chains);
    return best;
}

} // namespace nest
