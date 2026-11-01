#include "constructive.hpp"
#include "coverage_archive.hpp"
#include "variant_sampling.hpp"
#include "prefix_archive.hpp"
#include "sheet_patterns.hpp"
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <exception>
#include <limits>
#include <mutex>
#include <numeric>
#include <random>
#include <stdexcept>
#include <thread>
#include <tuple>

namespace constructive {
namespace {
using Clock = std::chrono::steady_clock;
struct Score {
    bool complete = false;
    int count = 0, sheets = 0;
    double area = 0, difficulty = 0, last_extent = 0, aggregate_extent = 0;
    double envelope_area = 0, envelope_perimeter = 0;
};
// Deliberate reconstruction choices, not recovered commercial numeric weights:
// completeness/count first, then covered area, stock count and reusable remnant.
bool better(const Score& a, const Score& b) {
    if (a.complete != b.complete) return a.complete;
    if (a.count != b.count) return a.count > b.count;
    if (a.area != b.area) return a.area > b.area;
    if (a.sheets != b.sheets) return a.sheets < b.sheets;
    if (a.last_extent != b.last_extent) return a.last_extent < b.last_extent;
    if(a.aggregate_extent != b.aggregate_extent) return a.aggregate_extent < b.aggregate_extent;
    if(a.envelope_area != b.envelope_area) return a.envelope_area < b.envelope_area;
    return a.envelope_perimeter < b.envelope_perimeter;
}
struct State {
    std::vector<Placement> placed;
    std::vector<int> used;
};
bool same_layout(const State& a, const State& b) {
    if (a.placed.size() != b.placed.size()) return false;
    using Key = std::tuple<int,int,int,double,double>;
    auto keys = [](const State& state) {
        std::vector<Key> result;
        result.reserve(state.placed.size());
        for (const auto& p : state.placed) result.emplace_back(p.part,p.sheet,p.variant,p.x,p.y);
        std::sort(result.begin(), result.end());
        return result;
    };
    // Copy labels and insertion order don't create distinct geometric states.
    // Keep the first representative, preserving its contiguous copy identities.
    return keys(a) == keys(b);
}
struct Search {
    const Problem& problem;
    const Config& config;
    Geometry& geometry;
    const std::vector<int>& quantities;
    const std::vector<double>& difficulty;
    int requested;
    Stop stop;
    std::function<void(const State&)> publish;
    std::mt19937_64 rng;
    int strategy;
    std::vector<double> rank;
    std::function<void(const State&)> publish_prefix;

    Score score(const State& state) const {
        Score s;
        s.count = static_cast<int>(state.placed.size());
        s.complete = s.count == requested;
        for (const auto& p : state.placed) s.sheets = std::max(s.sheets, p.sheet + 1);
        std::vector<double> xmax(s.sheets, 0), ymax(s.sheets, 0);
        std::vector<double> xmin(s.sheets,std::numeric_limits<double>::infinity()), ymin=xmin;
        for (const auto& p : state.placed) {
            const auto& v = geometry.variants()[p.variant];
            xmax[p.sheet] = std::max(xmax[p.sheet], p.x + v.bounds.max_x);
            ymax[p.sheet] = std::max(ymax[p.sheet], p.y + v.bounds.max_y);
            xmin[p.sheet] = std::min(xmin[p.sheet],p.x+v.bounds.min_x);
            ymin[p.sheet] = std::min(ymin[p.sheet],p.y+v.bounds.min_y);
        }
        for(std::size_t p=0;p<state.used.size();++p) if(state.used[p]>0) {
            s.area += state.used[p]*geometry.variants()[geometry.variants_for(static_cast<int>(p)).front()].area;
            s.difficulty += state.used[p]*difficulty[p];
        }
        for (int i = 0; i < s.sheets; ++i) {
            // A rectangle spanning the unused right or top edge is reusable stock.
            const double extent = std::min(xmax[i] / problem.width, ymax[i] / problem.height);
            s.aggregate_extent += extent;
            if (i + 1 == s.sheets) s.last_extent = extent;
            if(std::isfinite(xmin[i])) {
                const double width=xmax[i]-xmin[i],height=ymax[i]-ymin[i];
                s.envelope_area+=width*height/(problem.width*problem.height);
                s.envelope_perimeter+=2*(width+height)/(problem.width+problem.height);
            }
        }
        return s;
    }
    void initialize_rank() {
        rank.assign(problem.parts.size(), 0);
        std::uniform_real_distribution<double> jitter(.8, 1.2);
        for (std::size_t p = 0; p < rank.size(); ++p) {
            const auto& variants = geometry.variants_for(static_cast<int>(p));
            if (variants.empty()) continue;
            const auto& v = geometry.variants()[variants.front()];
            double shortest_long = std::numeric_limits<double>::infinity();
            double shortest_short = std::numeric_limits<double>::infinity();
            for (int vi : variants) {
                const auto& b = geometry.variants()[vi].bounds;
                const double w = b.max_x - b.min_x, h = b.max_y - b.min_y;
                shortest_long = std::min(shortest_long, std::max(w, h));
                shortest_short = std::min(shortest_short, std::min(w, h));
            }
            const double area = v.area / (problem.width * problem.height);
            const double length = shortest_long / std::max(problem.width, problem.height);
            const double aspect = shortest_long / std::max(shortest_short, 1e-12);
            switch (strategy % 5) {
                case 0: rank[p] = area; break;
                case 1: rank[p] = length * (1 + std::log1p(aspect)); break;
                case 2: rank[p] = area * (1 + length); break;
                case 3: rank[p] = 1 / std::max(area, 1e-12); break;
                default: rank[p] = (area + length) * jitter(rng); break;
            }
            if (strategy >= 5) rank[p] *= jitter(rng);
        }
    }
    bool fits_bounds(int vi) const {
        const auto& b = geometry.variants()[vi].bounds;
        return b.max_x - b.min_x <= problem.width + 1e-9 &&
               b.max_y - b.min_y <= problem.height + 1e-9;
    }
    bool prefer_branch(const State& a,const State& b) const {
        // Public incumbent comparison stays count/area based. Within a worker,
        // equal-depth branches must honor its construction policy; otherwise
        // covered area silently defeats the long-part and small-part strategies.
        if(a.placed.size()!=b.placed.size()) return a.placed.size()>b.placed.size();
        double ar=0,br=0;
        for(std::size_t p=0;p<rank.size();++p) {
            ar+=a.used[p]*rank[p]; br+=b.used[p]*rank[p];
        }
        if(ar!=br) return ar>br;
        return better(score(a),score(b));
    }
    State fill_sheet(State initial, int sheet) {
        std::vector<State> beam{initial};
        State best = initial;
        Score best_score = score(best);
        while (!stop()) {
            std::vector<State> next;
            // Retain only a bounded beam while expanding; don't materialize all branches.
            const auto retain = [&](State candidate) {
                const Score cs = score(candidate);
                if (prefer_branch(candidate,best)) {
                    best = candidate;
                    best_score = cs;
                }
                for (const auto& existing : next) if (same_layout(existing, candidate)) return;
                publish(candidate); // retain diverse valid partials, not only count-rich states
                next.push_back(std::move(candidate));
                std::stable_sort(next.begin(), next.end(), [&](const State& a, const State& b) {
                    return prefer_branch(a,b);
                });
                if (next.size() > static_cast<std::size_t>(config.beam_width)) next.resize(config.beam_width);
            };
            for (const auto& state : beam) {
                if (stop()) break;
                std::vector<int> order;
                for (std::size_t p = 0; p < quantities.size(); ++p)
                    if (state.used[p] < quantities[p]) order.push_back(static_cast<int>(p));
                std::stable_sort(order.begin(), order.end(), [&](int a, int b) { return rank[a] > rank[b]; });
                int eligible = 0;
                double occupied_area=0;
                for(const auto& placed:state.placed) if(placed.sheet==sheet)
                    occupied_area+=geometry.variants()[placed.variant].area;
                const double free_area=problem.width*problem.height-occupied_area;
                const double area_tolerance=std::max(1.,problem.width*problem.height)*1e-10;
                for (int part : order) {
                    if (stop()) break;
                    const auto& allowed=geometry.variants_for(part);
                    if(allowed.empty()) continue;
                    // Necessary material-area bound only. Do not subtract defect
                    // areas (they can overlap), or infer impossibility from a
                    // heuristic positions() query returning empty.
                    if(geometry.variants()[allowed.front()].area>free_area+area_tolerance) continue;
                    bool found = false;
                    std::vector<int> fitting;
                    for(int vi:geometry.variants_for(part)) if(fits_bounds(vi)) fitting.push_back(vi);
                    const auto variants=detail::variant_order(fitting,config.variant_limit,strategy,
                                                              static_cast<std::uint64_t>(config.seed));
                    const std::size_t sampled=std::min(variants.size(),static_cast<std::size_t>(config.variant_limit));
                    for (std::size_t vindex=0;vindex<variants.size();++vindex) {
                        if (stop()) break;
                        // A successful sampled set bounds expensive geometry queries.
                        // If all sampled orientations fail, exhaust the allowed tail.
                        if(vindex==sampled && found) break;
                        const int vi=variants[vindex];
                        auto positions = geometry.positions(vi, state.placed, sheet, strategy % 4,
                                                            config.position_limit, stop);
                        for (Point pos : positions) {
                            if (stop()) break;
                            if (!geometry.valid(vi, pos, state.placed, sheet)) continue;
                            State candidate = state;
                            candidate.placed.push_back({part, candidate.used[part]++, sheet, vi, pos.x, pos.y});
                            retain(std::move(candidate));
                            found = true;
                        }
                    }
                    // Unplaceable/oversized parts don't consume branching slots. Every
                    // remaining part is eventually tried when higher-ranked parts cannot fit.
                    if (found && ++eligible >= config.branch_parts) break;
                }
            }
            if (next.empty()) break;
            beam = std::move(next);
            if (best_score.complete) break;
        }
        return best;
    }
    State construct(State state, int first_sheet) {
        const auto construction_start=Clock::now();
        const char* trace_env=std::getenv("NC_TRACE");
        const bool trace=trace_env && std::atoi(trace_env)!=0;
        int sheet = first_sheet;
        for (; sheet < config.max_sheets && !stop(); ++sheet) {
            const std::size_t before = state.placed.size();
            const bool had_items = std::any_of(state.placed.begin(), state.placed.end(),
                                               [sheet](const Placement& p) { return p.sheet == sheet; });
            state = fill_sheet(std::move(state), sheet);
            if(!stop() && state.placed.size()>before && publish_prefix)publish_prefix(state);
            if(trace) {
                const auto value=score(state);
                std::fprintf(stderr,"[nc-sheet] strategy=%d sheet=%d added=%zu cumulative=%zu/%d area_sheets=%.6f construction_elapsed=%.3f stopped=%d\n",
                    strategy,sheet,state.placed.size()-before,state.placed.size(),requested,
                    value.area/(problem.width*problem.height),
                    std::chrono::duration<double>(Clock::now()-construction_start).count(),stop()?1:0);
            }
            if (state.placed.size() == static_cast<std::size_t>(requested)) break;
            // Identical rectangular stocks/defects: failing on an empty sheet cannot
            // be cured by opening another identical empty sheet.
            if (state.placed.size() == before && !had_items) break;
        }
        publish(state);
        return state;
    }
    void compact(State& state, int immutable_sheets) {
        if (state.placed.empty() || stop()) return;
        int last = 0;
        for (const auto& p : state.placed) last = std::max(last, p.sheet);
        if (last < immutable_sheets) return;
        // Two bounded coordinate-descent passes; other sheets stay untouched.
        for (int pass = 0; pass < 2 && !stop(); ++pass) {
            bool improved = false;
            for (std::size_t i = 0; i < state.placed.size() && !stop(); ++i) {
                const Placement old = state.placed[i];
                if (old.sheet != last) continue;
                std::vector<Placement> others = state.placed;
                others.erase(others.begin() + i);
                Score current = score(state);
                Placement chosen = old;
                for (int vi : geometry.variants_for(old.part)) {
                    if (stop()) break;
                    if (!fits_bounds(vi)) continue;
                    for (Point pos : geometry.positions(vi, others, last, 0, config.position_limit, stop)) {
                        if (stop()) break;
                        if (!geometry.valid(vi, pos, others, last)) continue;
                        state.placed[i] = {old.part, old.copy, last, vi, pos.x, pos.y};
                        const Score candidate = score(state);
                        if (better(candidate, current)) { chosen = state.placed[i]; current = candidate; improved = true; }
                    }
                }
                state.placed[i] = chosen;
            }
            publish(state);
            if (!improved) break;
        }
    }
};
}

Result solve(const Problem& problem, const Config& config, std::atomic<bool>& cancelled) {
    const auto start = Clock::now();
    if (!(problem.width > 0) || !(problem.height > 0) || config.beam_width < 1 ||
        config.branch_parts < 1 || config.position_limit < 1 || config.variant_limit < 1 || config.max_sheets < 0 ||
        config.restarts < 0 || config.workers < 1 || config.time_limit < 0)
        throw std::invalid_argument("invalid constructive search configuration");
    std::vector<int> quantities(problem.parts.size(), 1);
    if (!problem.quantities.empty()) {
        if (problem.quantities.size() != quantities.size()) throw std::invalid_argument("quantity count mismatch");
        quantities = problem.quantities;
    }
    long long total = 0;
    for (int q : quantities) { if (q < 0) throw std::invalid_argument("negative quantity"); total += q; }
    if (total > std::numeric_limits<int>::max()) throw std::invalid_argument("too many copies");
    const int requested = static_cast<int>(total);
    Geometry geometry(problem, config);
    std::vector<double> difficulty(problem.parts.size(),0);
    for (std::size_t p=0;p<difficulty.size();++p) {
        double longest=std::numeric_limits<double>::infinity(), shortest=longest;
        for(int vi:geometry.variants_for(static_cast<int>(p))) {
            const auto& b=geometry.variants()[vi].bounds;
            longest=std::min(longest,std::max(b.max_x-b.min_x,b.max_y-b.min_y));
            shortest=std::min(shortest,std::min(b.max_x-b.min_x,b.max_y-b.min_y));
        }
        if(std::isfinite(longest)) difficulty[p]=longest/std::max(problem.width,problem.height)*
            (1+std::log1p(longest/std::max(shortest,1e-12)));
    }
    std::atomic<bool> abort{false};
    const Stop stop = [&] {
        return cancelled.load(std::memory_order_relaxed) || abort.load(std::memory_order_relaxed) ||
            (config.time_limit > 0 && std::chrono::duration<double>(Clock::now() - start).count() >= config.time_limit);
    };
    State incumbent; incumbent.used.assign(quantities.size(), 0);
    Score incumbent_score;
    incumbent_score.complete = requested == 0;
    std::mutex best_mutex, error_mutex;
    detail::CoverageArchive<State> archive(8);
    detail::PrefixArchive<State> prefixes(config.max_sheets);
    std::vector<double> part_areas(problem.parts.size(),0);
    for(std::size_t p=0;p<part_areas.size();++p)
        if(!geometry.variants_for(static_cast<int>(p)).empty())
            part_areas[p]=geometry.variants()[geometry.variants_for(static_cast<int>(p)).front()].area;
    detail::SheetPatterns sheet_patterns(quantities,part_areas,difficulty);
    std::size_t new_patterns=0;
    std::atomic<bool> combining{false};
    std::exception_ptr error;
    std::atomic<int> next{0}, iterations{0};
    std::atomic<int> tail_rebuilds{0}, retained_sheets{0};
    std::atomic<int> refill_added{0};
    const char* trace_env = std::getenv("NC_TRACE");
    const bool trace = trace_env && std::atoi(trace_env) != 0;
    auto last_trace = start - std::chrono::seconds(2);
    auto publish = [&](const State& state) {
        Search measure{problem, config, geometry, quantities, difficulty, requested, stop, {}, {}, 0, {}};
        const Score value = measure.score(state);
        std::lock_guard<std::mutex> lock(best_mutex);
        archive.insert({value.complete,value.count,value.sheets,value.area,value.difficulty,value.last_extent},state);
        if (better(value, incumbent_score)) {
            incumbent = state; incumbent_score = value;
            const auto now = Clock::now();
            if (trace && now - last_trace >= std::chrono::seconds(1)) {
                last_trace = now;
                const auto cache = geometry.stats();
                std::fprintf(stderr,"[nc] iter=%d best=%d/%d sheets=%d elapsed=%.3f cache=%llu/%llu\n",
                    iterations.load(),value.count,requested,value.sheets,
                    std::chrono::duration<double>(now-start).count(),
                    static_cast<unsigned long long>(cache.hits),static_cast<unsigned long long>(cache.misses));
            }
        }
    };
    const auto recombine = [&](bool force) {
        if(stop()||combining.exchange(true))return;
        struct Reset {std::atomic<bool>& flag;~Reset(){flag.store(false);}} reset{combining};
        std::unique_ptr<detail::SheetPatterns> snapshot;
        {
            std::lock_guard<std::mutex> lock(best_mutex);
            if(new_patterns==0 || (!force && new_patterns<8))return;
            snapshot=std::make_unique<detail::SheetPatterns>(sheet_patterns);
            new_patterns=0;
        }
        // Combinatorial search runs outside the publication lock and consumes
        // the same global cancellation/time budget as geometric construction.
        const auto combinations=snapshot->combine(config.max_sheets,stop);
        for(const auto& combination:combinations.candidates) {
            State composed;composed.used=combination.used;composed.placed=snapshot->compose(combination);
            publish(composed);
            if(combination.patterns.size()<static_cast<std::size_t>(config.max_sheets)) {
                Search measure{problem,config,geometry,quantities,difficulty,requested,stop,{},{},0,{}};
                const Score value=measure.score(composed);
                std::lock_guard<std::mutex> lock(best_mutex);
                prefixes.insert(true,{value.complete,value.count,value.sheets,value.area,value.difficulty,value.last_extent},
                                composed.used,composed);
            }
        }
        if(trace)std::fprintf(stderr,"[nc-patterns] pool=%zu attempts=%zu offers=%zu stopped=%d\n",
            snapshot->patterns().size(),combinations.attempts,combinations.candidates.size(),combinations.stopped?1:0);
    };
    auto publish_prefix = [&](const State& state) {
        Search measure{problem, config, geometry, quantities, difficulty, requested, stop, {}, {}, 0, {}};
        const Score value=measure.score(state);
        {
            std::lock_guard<std::mutex> lock(best_mutex);
            prefixes.insert(true,{value.complete,value.count,value.sheets,value.area,value.difficulty,value.last_extent},state.used,state);
            if(sheet_patterns.insert(true,state.placed,value.sheets-1))++new_patterns;
        }
        recombine(false);
    };
    auto worker = [&] {
        try {
            while (!stop()) {
                const int iteration = next.fetch_add(1);
                if (iteration >= config.restarts || requested == 0) break;
                iterations.fetch_add(1);
                Search search{problem, config, geometry, quantities, difficulty, requested, stop, publish,
                    std::mt19937_64(static_cast<std::uint64_t>(config.seed) + 0x9e3779b97f4a7c15ULL * iteration), iteration, {}};
                search.initialize_rank();
                search.publish_prefix=publish_prefix;
                State seed; seed.used.assign(quantities.size(), 0);
                int first_sheet = 0;
                if (iteration % 2 == 1) {
                    std::lock_guard<std::mutex> lock(best_mutex);
                    std::vector<std::size_t> available;
                    for(std::size_t level=0;level<prefixes.levels().size();++level)
                        if(!prefixes.levels()[level].entries().empty())available.push_back(level);
                    if(!available.empty()) {
                        const auto level=available[(static_cast<std::uint64_t>(config.seed)+iteration/2)%available.size()];
                        const auto& entries=prefixes.levels()[level].entries();
                        std::size_t chosen=search.rng()%entries.size();
                        if((iteration/2)%2==0)
                            chosen=static_cast<std::size_t>(std::max_element(entries.begin(),entries.end(),
                                [](const auto& a,const auto& b){return a.metric.area<b.metric.area;})-entries.begin());
                        seed=entries[chosen].payload.state;
                        first_sheet=static_cast<int>(level)+1;
                        tail_rebuilds.fetch_add(1);retained_sheets.fetch_add(first_sheet);
                    } else {
                    seed = archive.entries().empty() ? incumbent :
                        archive.entries()[search.rng()%archive.entries().size()].payload;
                    int sheet_count = 0;
                    for (const auto& p : seed.placed) sheet_count = std::max(sheet_count, p.sheet + 1);
                    if (sheet_count > 1) {
                        // CNS ReducedSolution retains complete nesting/sheet records.
                        // Keep a nonempty proper prefix; never reopen its last sheet.
                        first_sheet = 1 + static_cast<int>(search.rng() % (sheet_count - 1));
                        tail_rebuilds.fetch_add(1);
                        retained_sheets.fetch_add(first_sheet);
                        seed.placed.erase(std::remove_if(seed.placed.begin(), seed.placed.end(),
                            [first_sheet](const Placement& p) { return p.sheet >= first_sheet; }), seed.placed.end());
                        std::fill(seed.used.begin(), seed.used.end(), 0);
                        for (const auto& p : seed.placed) ++seed.used[p.part];
                    } else {
                        seed.placed.clear();
                        std::fill(seed.used.begin(), seed.used.end(), 0);
                    }
                    }
                }
                const auto preserved = seed.placed;
                State result = search.construct(std::move(seed), first_sheet);
                search.compact(result, first_sheet);
                if(!stop() && result.placed.size()<static_cast<std::size_t>(requested) && !result.placed.empty()) {
                    int last=0;for(const auto& p:result.placed)last=std::max(last,p.sheet);
                    if(last>=first_sheet) {
                        const auto before=result.placed.size();
                        result=search.fill_sheet(std::move(result),last);
                        refill_added.fetch_add(static_cast<int>(result.placed.size()-before));
                        publish(result);
                        if(!stop())publish_prefix(result);
                    }
                }
                if (result.placed.size() < preserved.size()) throw std::logic_error("lost retained sheet prefix");
                for (std::size_t i=0; i<preserved.size(); ++i) {
                    const auto& a=preserved[i]; const auto& b=result.placed[i];
                    if (a.part!=b.part || a.copy!=b.copy || a.sheet!=b.sheet || a.variant!=b.variant || a.x!=b.x || a.y!=b.y)
                        throw std::logic_error("modified retained sheet prefix");
                }
            }
        } catch (...) {
            std::lock_guard<std::mutex> lock(error_mutex);
            if (!error) error = std::current_exception();
            abort.store(true);
        }
    };
    const unsigned hardware = std::max(1u, std::thread::hardware_concurrency());
    const int workers = std::max(1, std::min({config.workers, 32, static_cast<int>(hardware), std::max(1, config.restarts)}));
    std::vector<std::thread> threads;
    for (int i = 1; i < workers; ++i) threads.emplace_back(worker);
    worker();
    for (auto& thread : threads) thread.join();
    if (error) std::rethrow_exception(error);
    recombine(true);
    // Recheck the final immutable snapshot independently of beam bookkeeping.
    std::vector<Placement> verified;
    std::vector<std::vector<bool>> copies;
    for (int q : quantities) copies.emplace_back(q, false);
    for (const auto& p : incumbent.placed) {
        if (p.part < 0 || p.part >= static_cast<int>(quantities.size()) || p.copy < 0 ||
            p.copy >= quantities[p.part] || copies[p.part][p.copy] || p.sheet < 0 || p.sheet >= config.max_sheets ||
            p.variant < 0 || p.variant >= static_cast<int>(geometry.variants().size()) ||
            geometry.variants()[p.variant].part != p.part || !geometry.valid(p.variant, {p.x,p.y}, verified, p.sheet))
            throw std::runtime_error("constructive final snapshot verification failed");
        copies[p.part][p.copy] = true;
        verified.push_back(p);
    }
    Result result;
    result.placements = std::move(verified);
    result.requested = requested;
    result.sheets = incumbent_score.sheets;
    result.iterations = iterations.load();
    result.tail_rebuilds = tail_rebuilds.load();
    result.retained_sheets = retained_sheets.load();
    result.refill_added=refill_added.load();
    result.cancelled = stop();
    result.elapsed = std::chrono::duration<double>(Clock::now() - start).count();
    result.geometry = geometry.stats();
    if (trace) std::fprintf(stderr,"[nc] final best=%zu/%d sheets=%d iterations=%d tail_rebuilds=%d retained_sheets=%d refill_added=%d elapsed=%.3f\n",
        result.placements.size(),requested,result.sheets,result.iterations,result.tail_rebuilds,
        result.retained_sheets,result.refill_added,result.elapsed);
    return result;
}
}
