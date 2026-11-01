// Faithful C++ port of solver/src/optimizer/* (LBF builder, separator, explore, compress).
// DEVIATION: rayon parallelism in relax_round is replaced by SEQUENTIAL execution
// of the N workers from the shared master state, picking the best by weighted loss. The
// algorithm (N independent attempts -> best-of) is preserved; only the parallelism is dropped.
#pragma once
#include "config.hpp"
#include "evaluator.hpp"
#include "sampler.hpp"
#include "overlap.hpp"
#include "rng.hpp"
#include "util.hpp"
#include "../geometry/strip_packing.hpp"
#include <algorithm>
#include <thread>
#include <mutex>
#include <condition_variable>
#include <functional>
#include <memory>

namespace nest {
using namespace nest;

inline bool g_verbose = false;
// NP_EXPL_VERBOSE=1 (diagnostic only): per-relax / per-width / per-perturb trace of the exploration and
// compression loops on stderr. Pure printing — no RNG draw, no state change — so ON or OFF the layout is
// byte-identical. (g_verbose above stays the older one-line "[EXPL] feasible width" trace.)
inline bool g_expl_trace = false;
// NP_EXPL_TARGET=1: run_strip publishes the width it must reach (the sheet width) here for the duration of
// its optimize() calls; exploration then clamps a shrink that would overshoot it to exactly that width.
// thread_local (parallel multi-starts each run their own strip); 0 => no clamp (byte-identical).
inline bool g_expl_target_on = false;
inline thread_local f32 g_expl_target = 0.0f;
// NP_EXPL_STRIKES / NP_EXPL_NOIMPR: strike_limit / iter_no_imprv_limit of the strip-exploration separator
// (run_strip, fit 0/1). 0 => the defaults (3 / 200), byte-identical.
inline usize g_expl_strikes = 0, g_expl_noimpr = 0;

// ===================== InitialPlacer (lbf.rs) =====================
struct InitialPlacer {
    StripInstance instance;
    StripProblem prob;
    Rng rng;
    SampleConfig sample_config;

    InitialPlacer(StripInstance inst, Rng rng_, SampleConfig sc)
        : instance(inst), prob(inst), rng(rng_), sample_config(sc) {}

    std::optional<Placement> find_placement(usize part_id) {
        const Part& part = instance.part(part_id);
        InitialEvaluator evaluator(prob.layout, part);
        auto [best, nevals] = search_placement(prob.layout, part, std::nullopt, evaluator, sample_config, rng);
        (void)nevals;
        if (best && best->second.kind == SampleKind::Clear) return Placement{part_id, best->first};
        return std::nullopt;
    }

    void place_item(usize part_id) {
        auto p = find_placement(part_id);
        if (p) { prob.place_item(*p); }
        else {
            prob.change_strip_width(prob.strip_width() * 1.2f);
            place_item(part_id);
        }
    }

    InitialPlacer& construct() {
        usize n_parts = instance.parts.size();
        std::vector<usize> ids;
        for (usize i = 0; i < n_parts; ++i) ids.push_back(i);
        std::stable_sort(ids.begin(), ids.end(), [&](usize a, usize b) {
            const Polygon& sa = *instance.part(a).collision_shape;
            const Polygon& sb = *instance.part(b).collision_shape;
            f32 ka = sa.surrogate_ref().convex_hull_area * sa.diameter;
            f32 kb = sb.surrogate_ref().convex_hull_area * sb.diameter;
            return ka > kb; // descending (Reverse)
        });
        std::vector<usize> order;
        for (usize id : ids)
            for (usize q = 0; q < prob.part_demand_qtys[id]; ++q) order.push_back(id);
        for (usize part_id : order) place_item(part_id);
        prob.fit_strip();
        return *this;
    }
};

// ===================== RelaxWorker (worker.rs) =====================
struct SepStats {
    usize total_moves = 0;
    usize total_evals = 0;
    SepStats& operator+=(const SepStats& o) { total_moves += o.total_moves; total_evals += o.total_evals; return *this; }
};

struct RelaxWorker {
    StripInstance instance;
    StripProblem prob;
    OverlapTracker ct;
    Rng rng;
    SampleConfig sample_config;

    void load(const StripSolution& sol, const OverlapTracker& ct_) {
        prob.restore(sol);
        ct = ct_;
    }

    // Algorithm 5
    SepStats relax_parts() {
        std::vector<PartKey> candidates;
        prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart&) {
            if (ct.has_loss(pk)) candidates.push_back(pk);
        });
        rng.shuffle(candidates);

        SepStats stats;
        for (PartKey pk : candidates) {
            if (ct.has_loss(pk)) {
                usize part_id = prob.layout.placed_parts[pk].part_id;
                const Part& part = instance.part(part_id);
                RelaxEvaluator evaluator(prob.layout, part, pk, ct);
                auto [best, n_evals] = search_placement(prob.layout, part, std::optional<PartKey>(pk),
                                                         evaluator, sample_config, rng);
                RigidTransform new_dt = best->first; // search always returns a sample
                relocate_part(pk, new_dt);
                stats.total_moves += 1;
                stats.total_evals += n_evals;
            }
        }
        return stats;
    }

    PartKey relocate_part(PartKey pk, RigidTransform d_transf) {
        usize part_id = prob.layout.placed_parts[pk].part_id;
        prob.remove_item(pk);
        PartKey new_pk = prob.place_item(Placement{part_id, d_transf});
        ct.register_part_move(prob.layout, pk, new_pk);
        return new_pk;
    }
};

// ===================== WorkerPool (restores rayon parallelism) =====================
// DEVIATION-REVERSAL: the port had replaced rayon with sequential best-of-N. This persistent
// pool runs the N independent workers in parallel again. Workers are independent (each owns its
// StripProblem/CollisionEngine/OverlapTracker/Rng; the master state is read-only during the batch), and
// the best-of-N pick is deterministic, so the RESULT is identical to the sequential version —
// only wall-clock differs. Each thread i runs job(i) once per dispatch generation.
struct WorkerPool {
    int n;
    std::vector<std::thread> threads;
    std::mutex m;
    std::condition_variable cv, cv_done;
    std::function<void(int)> job;
    std::vector<unsigned> seen;
    unsigned gen = 0;
    int done = 0;
    bool stop = false;

    explicit WorkerPool(int n_) : n(n_), seen(n_ > 0 ? n_ : 0, 0u) {
        for (int i = 0; i < n; ++i) threads.emplace_back([this, i] { loop(i); });
    }
    ~WorkerPool() {
        { std::unique_lock<std::mutex> l(m); stop = true; }
        cv.notify_all();
        for (auto& t : threads) if (t.joinable()) t.join();
    }
    WorkerPool(const WorkerPool&) = delete;
    WorkerPool& operator=(const WorkerPool&) = delete;

    void loop(int i) {
        for (;;) {
            std::unique_lock<std::mutex> l(m);
            cv.wait(l, [&] { return stop || seen[i] != gen; });
            if (stop) return;
            seen[i] = gen;
            auto f = job; // copy under lock
            l.unlock();
            f(i);
            l.lock();
            if (++done == n) cv_done.notify_one();
        }
    }
    void run(std::function<void(int)> f) {
        if (n <= 0) return;
        std::unique_lock<std::mutex> l(m);
        job = std::move(f);
        done = 0;
        ++gen;
        cv.notify_all();
        cv_done.wait(l, [&] { return done == n; });
    }
};

// ===================== Relaxer (separator.rs) =====================
struct Relaxer {
    StripInstance instance;
    Rng rng;
    StripProblem prob;
    OverlapTracker ct;
    std::vector<RelaxWorker> workers;
    RelaxConfig config;
    std::unique_ptr<WorkerPool> pool;

    Relaxer(StripInstance inst, StripProblem prob_, Rng rng_, RelaxConfig cfg)
        : instance(inst), rng(rng_), prob(std::move(prob_)), ct(prob.layout), config(cfg) {
        for (usize i = 0; i < config.n_workers; ++i)
            workers.push_back(RelaxWorker{instance, prob, ct, Rng(rng.next_u64()), config.sample_config});
        pool = std::make_unique<WorkerPool>((int)config.n_workers);
    }

    // Algorithm 9
    std::pair<StripSolution, OverlapSnapshot> relax(const Terminator& term, SolutionListener& listener) {
        std::pair<StripSolution, OverlapSnapshot> min_loss_sol{prob.save(), ct.save()};
        f32 min_loss = ct.get_total_loss();

        usize n_strikes = 0;
        bool relaxed = false;

        while (n_strikes < config.strike_limit && !term.kill() && !relaxed) {
            usize n_iter_no_improvement = 0;
            f32 initial_strike_loss = ct.get_total_loss();

            while (n_iter_no_improvement < config.iter_no_imprv_limit) {
                relax_round();
                f32 loss = ct.get_total_loss();

                if (loss == 0.0f) {
                    min_loss_sol = {prob.save(), ct.save()};
                    relaxed = true;
                    break;
                } else if (loss < min_loss) {
                    listener.report(ReportType::ExplImproving, prob.save(), instance);
                    if (loss < min_loss * 0.98f) n_iter_no_improvement = 0;
                    min_loss_sol = {prob.save(), ct.save()};
                    min_loss = loss;
                } else {
                    n_iter_no_improvement += 1;
                }
                ct.update_gls_weights();
                if (g_iter_mode && term.kill()) break; // iteration mode: stop promptly at the budget
                // (Time mode keeps Rust fidelity: kill() is checked only in the OUTER loop; the inner
                // batch runs to iter_no_imprv_limit. The guarded break above is iteration-mode only.)
            }
            if (relaxed) break;

            if (initial_strike_loss * 0.98f <= min_loss) n_strikes += 1;
            else n_strikes = 0;
            rollback(min_loss_sol.first, &min_loss_sol.second);
            if (g_expl_trace)
                std::fprintf(stderr, "[TRACE] strike end W=%.2f strikes=%zu strike_start_loss=%.6g min_loss=%.6g round=%llu\n",
                             prob.strip_width(), n_strikes, initial_strike_loss, min_loss, (unsigned long long)g_iter_count);
        }
        return min_loss_sol;
    }

    // Algorithm 10 (sequential best-of-N; see file header)
    SepStats relax_round() {
        ++g_iter_count; // one relaxation round (the unit of iteration-based termination); thread_local => per-start budget
        g_iter_pub.fetch_add(1, std::memory_order_relaxed); // global sum, host progress readout only
        // workers==1 IN-PLACE fast path: best-of-1 needs no master snapshot and no best-pick — run
        // the single worker directly ON the master state by move-stealing it in and out. The baseline
        // path costs 2 full layout snapshots + 2 tracker copies per round (prob.save -> load-restore
        // -> save -> restore), all of which reproduce the identical state at best-of-1. The worker's
        // own Rng advances exactly as before, so this is state- and result-identical, copy-free.
        if (workers.size() == 1) {
            RelaxWorker& w = workers[0];
            w.prob = std::move(prob);
            w.ct = std::move(ct);
            SepStats report = w.relax_parts();
            prob = std::move(w.prob);
            ct = std::move(w.ct);
            return report;
        }
        StripSolution master_sol = prob.save();
        // Run the N independent workers in parallel (result-identical to serial best-of-N).
        std::vector<SepStats> per_worker(workers.size());
        pool->run([&](int i) {
            workers[i].load(master_sol, ct);
            per_worker[i] = workers[i].relax_parts();
        });
        SepStats report;
        for (auto& s : per_worker) report += s;
        f32 best_wl = std::numeric_limits<f32>::infinity();
        int best_i = 0;
        for (usize i = 0; i < workers.size(); ++i) {
            f32 wl = workers[i].ct.get_total_weighted_loss();
            if (wl < best_wl) { best_wl = wl; best_i = (int)i; }
        }
        StripSolution best_sol = workers[best_i].prob.save();
        prob.restore(best_sol);
        ct = workers[best_i].ct;
        return report;
    }

    void rollback(const StripSolution& sol, const OverlapSnapshot* ots) {
        prob.restore(sol);
        if (ots) ct.restore_but_keep_weights(*ots, prob.layout);
        else ct = OverlapTracker(prob.layout);
    }

    PartKey relocate_part(PartKey pk, RigidTransform d_transf) {
        usize part_id = prob.layout.placed_parts[pk].part_id;
        prob.remove_item(pk);
        PartKey new_pk = prob.place_item(Placement{part_id, d_transf});
        ct.register_part_move(prob.layout, pk, new_pk);
        return new_pk;
    }

    // `new_holes` (optional): replace the strip's keep-out hole set in the same step (bin_driver.hpp
    // drops the gap keep-out beyond a shrinking right wall this way). The incremental tracker rebuild
    // below stays exact for that use: unshifted parts keep their hole losses (a removed hole lying wholly
    // beyond the new wall could only touch parts with x_max > new width, which are re-queried). Any OTHER
    // hole change needs a from-scratch tracker (rollback(sol, nullptr)). nullptr => holes unchanged.
    void change_strip_width(f32 new_width, std::optional<f32> split_position,
                            const std::vector<Polygon>* new_holes = nullptr) {
        f32 split = split_position.value_or(prob.strip_width() / 2.0f);
        f32 delta = new_width - prob.strip_width();

        std::vector<std::pair<PartKey, RigidTransform>> to_shift;
        prob.layout.placed_parts.for_each([&](PartKey k, const PlacedPart& pi) {
            if (pi.shape->centroid().x > split) to_shift.emplace_back(k, pi.d_transf);
        });
        // SILENT shift: move the geometry (remove+place updates the engine exactly like
        // relocate_part) but skip the per-part tracker recompute — the engine is rebuilt and the
        // tracker re-derived right below, so relocate_part's O(collisions) query per shifted part
        // was pure waste. Remember (old,new) key pairs for the incremental tracker rebuild.
        std::vector<std::pair<PartKey, PartKey>> remaps;
        remaps.reserve(to_shift.size());
        for (auto& [pik, dtransf] : to_shift) {
            AffineTransform existing = dtransf.compose();
            AffineTransform nt = existing.translate(delta, 0.0f);
            usize part_id = prob.layout.placed_parts[pik].part_id;
            prob.remove_item(pik);
            PartKey new_pk = prob.place_item(Placement{part_id, nt.decompose()});
            remaps.emplace_back(pik, new_pk);
        }
        if (new_holes) prob.strip.set_holes(*new_holes);
        prob.change_strip_width(new_width);
        // Incremental tracker rebuild: only shifted parts + wall-straddlers are re-queried;
        // unshifted pair/hole losses are bit-unchanged (see rebuild_after_width_change).
        ct.rebuild_after_width_change(prob.layout, remaps);
#if defined(VERIFY_INCREMENTAL)
        {
            OverlapTracker fresh(prob.layout);
            std::vector<PartKey> pks;
            prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart&) { pks.push_back(pk); });
            for (usize a = 0; a < pks.size(); ++a) {
                f32 ci = ct.get_container_loss(pks[a]), cf = fresh.get_container_loss(pks[a]);
                if (std::fabs(ci - cf) > 1e-4f * max_f(1.0f, cf))
                    std::fprintf(stderr, "[VERIFY] container loss mismatch part %zu: inc=%g fresh=%g\n", a, ci, cf);
                for (usize b = a + 1; b < pks.size(); ++b) {
                    f32 pi_ = ct.get_pair_loss(pks[a], pks[b]), pf = fresh.get_pair_loss(pks[a], pks[b]);
                    if (std::fabs(pi_ - pf) > 1e-4f * max_f(1.0f, pf))
                        std::fprintf(stderr, "[VERIFY] pair loss mismatch (%zu,%zu): inc=%g fresh=%g\n", a, b, pi_, pf);
                }
            }
        }
#endif
        // Worker refresh. At workers==1 the relax_round fast path move-OVERWRITES the worker's
        // prob/ct before reading them (write-before-read), so the deep copy here (Layout + quadtree
        // + O(N^2) tracker) is dead work — reseed only the RNG, consuming rng.next_u64() exactly as
        // the full refresh would (keeps the deterministic seed stream byte-identical).
        for (auto& w : workers) {
            if (workers.size() == 1) w.rng = Rng(rng.next_u64());
            else w = RelaxWorker{instance, prob, ct, Rng(rng.next_u64()), config.sample_config};
        }
    }
};

// Diagnostic dump (g_expl_trace only): every part's (id, rotation deg, bbox) + every non-zero pair /
// container loss with its GLS weight. Read-only.
inline void trace_state(const char* tag, const Relaxer& r) {
    const Layout& l = r.prob.layout;
    std::vector<PartKey> pks;
    l.placed_parts.for_each([&](PartKey pk, const PlacedPart&) { pks.push_back(pk); });
    std::fprintf(stderr, "[TRACE] %s W=%.2f round=%llu loss=%.6g wloss=%.6g |", tag, r.prob.strip_width(),
                 (unsigned long long)g_iter_count, r.ct.get_total_loss(), r.ct.get_total_weighted_loss());
    for (PartKey pk : pks) {
        const PlacedPart& pi = l.placed_parts[pk];
        f32 deg = normalize_rotation(pi.d_transf.rotation) * 180.0f / PI_F;
        std::fprintf(stderr, " p%zu r=%.2f x[%.1f,%.1f] y[%.1f,%.1f]", pi.part_id, deg, pi.shape->bbox.x_min,
                     pi.shape->bbox.x_max, pi.shape->bbox.y_min, pi.shape->bbox.y_max);
        f32 cl = r.ct.get_container_loss(pk);
        if (cl > 0.0f) std::fprintf(stderr, " C(%.4g w%.3g)", cl, r.ct.get_container_weight(pk));
    }
    for (usize a = 0; a < pks.size(); ++a)
        for (usize b = a + 1; b < pks.size(); ++b) {
            f32 pl = r.ct.get_pair_loss(pks[a], pks[b]);
            if (pl > 0.0f)
                std::fprintf(stderr, " P(%zu,%zu)=%.4g w%.3g", l.placed_parts[pks[a]].part_id,
                             l.placed_parts[pks[b]].part_id, pl, r.ct.get_pair_weight(pks[a], pks[b]));
        }
    std::fprintf(stderr, "\n");
}

// ===================== parts_contained_in / disrupt (explore.rs) =====================
inline std::vector<PartKey> parts_contained_in(const Layout& layout, PartKey pk_c) {
    const PlacedPart& pi_c = layout.placed_parts[pk_c];
    ObstacleCollector collector;
    layout.engine_ref().collect_poly_collisions(*pi_c.shape, collector);
    std::vector<PartKey> out;
    collector.for_each([&](ObstacleKey, const ObstacleRef& he) {
        if (he.kind == ObstacleKind::PlacedPart && !(he.pk == pk_c)) {
            const Circle& poi = layout.placed_parts[he.pk].shape->poi;
            if (collides(*pi_c.shape, poi.center)) out.push_back(he.pk);
        }
    });
    return out;
}

inline void perturb_layout(Relaxer& relaxer, const ExplorationConfig& config) {
    if (relaxer.prob.layout.placed_parts.size() < 2) return;

    f32 total_ch = 0.0f;
    for (auto& [part, qty] : relaxer.prob.instance.parts)
        total_ch += part.collision_shape->surrogate_ref().convex_hull_area * (f32)qty;
    f32 cutoff_threshold_area = total_ch * config.large_item_ch_area_cutoff_percentile;

    std::vector<std::pair<const Part*, usize>> sorted;
    for (auto& [part, qty] : relaxer.prob.instance.parts) sorted.emplace_back(&part, qty);
    std::stable_sort(sorted.begin(), sorted.end(), [](const auto& a, const auto& b) {
        return a.first->collision_shape->surrogate_ref().convex_hull_area > b.first->collision_shape->surrogate_ref().convex_hull_area;
    });
    f32 cumulative = 0.0f, ch_area_cutoff = 0.0f;
    for (auto& [part, qty] : sorted) {
        f32 a = part->collision_shape->surrogate_ref().convex_hull_area;
        cumulative += a * (f32)qty;
        if (cumulative > cutoff_threshold_area) { ch_area_cutoff = a; break; }
    }

    std::vector<PartKey> large;
    relaxer.prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart& pi) {
        if (pi.shape->surrogate_ref().convex_hull_area >= ch_area_cutoff) large.push_back(pk);
    });
    if (large.empty()) return;

    PartKey pk1 = relaxer.rng.choose(large);
    const PlacedPart* pi1 = &relaxer.prob.layout.placed_parts[pk1];

    // second part: large, different enough; fallback to any other placed part
    std::vector<PartKey> candidates2;
    for (PartKey pk : large) {
        const PlacedPart& pi = relaxer.prob.layout.placed_parts[pk];
        bool diff_area = std::fabs(pi.shape->area - pi1->shape->area) > pi1->shape->area * 0.01f;
        bool diff_diam = std::fabs(pi.shape->diameter - pi1->shape->diameter) > pi1->shape->diameter * 0.01f;
        if (diff_area && diff_diam) candidates2.push_back(pk);
    }
    PartKey pk2;
    if (!candidates2.empty()) pk2 = relaxer.rng.choose(candidates2);
    else {
        std::vector<PartKey> others;
        relaxer.prob.layout.placed_parts.for_each([&](PartKey pk, const PlacedPart&) { if (!(pk == pk1)) others.push_back(pk); });
        if (others.empty()) return;
        pk2 = relaxer.rng.choose(others);
    }
    const PlacedPart* pi2 = &relaxer.prob.layout.placed_parts[pk2];
    {   // NP_BIG_PIN: never swap a pinned part into another column (pin_pitch 0 => no-op)
        f32 p1 = relaxer.prob.instance.part(pi1->part_id).pin_pitch, p2 = relaxer.prob.instance.part(pi2->part_id).pin_pitch;
        f32 pitch = p1 > 0.0f ? p1 : p2;
        if (pitch > 0.0f && std::floor(pi1->shape->centroid().x / pitch) != std::floor(pi2->shape->centroid().x / pitch))
            return;
    }

    RigidTransform dt1_old = pi1->d_transf, dt2_old = pi2->d_transf;
    usize id1 = pi1->part_id, id2 = pi2->part_id;
    if (g_expl_trace) std::fprintf(stderr, "[TRACE] perturb swap p%zu <-> p%zu (n_large=%zu)\n", id1, id2, large.size());
    RigidTransform dt1_new = convert_sample_to_closest_feasible(dt2_old, relaxer.prob.instance.part(id1));
    RigidTransform dt2_new = convert_sample_to_closest_feasible(dt1_old, relaxer.prob.instance.part(id2));

    PartKey npk1 = relaxer.relocate_part(pk1, dt1_new);
    PartKey npk2 = relaxer.relocate_part(pk2, dt2_new);

    {
        AffineTransform conv = dt1_new.compose().inverse().transform(dt1_old.compose());
        for (PartKey c1 : parts_contained_in(relaxer.prob.layout, npk1)) {
            if (c1 == npk2) continue;
            const PlacedPart& c1_pi = relaxer.prob.layout.placed_parts[c1];
            RigidTransform nd = c1_pi.d_transf.compose().transform(conv).decompose();
            relaxer.relocate_part(c1, convert_sample_to_closest_feasible(nd, relaxer.prob.instance.part(c1_pi.part_id)));
        }
    }
    {
        AffineTransform conv = dt2_new.compose().inverse().transform(dt2_old.compose());
        for (PartKey c2 : parts_contained_in(relaxer.prob.layout, npk2)) {
            if (c2 == npk1) continue;
            const PlacedPart& c2_pi = relaxer.prob.layout.placed_parts[c2];
            RigidTransform nd = c2_pi.d_transf.compose().transform(conv).decompose();
            relaxer.relocate_part(c2, convert_sample_to_closest_feasible(nd, relaxer.prob.instance.part(c2_pi.part_id)));
        }
    }
}

// ===================== conservative minimum strip width (sparrow PR #162) =====================
// NP_MIN_WIDTH_STOP (default 0 = off, byte-identical): a width below which NO layout of this instance can
// exist. Exploration stops shrinking (hands over to compression) and compression returns its best feasible
// as soon as the next width would be below it or would not reduce the current width, so no relax rounds are
// spent on provably impossible widths.
//   1 = upstream: max(total collision-shape area / strip height, max over DISCRETE-rotation parts of the
//       narrowest width among the orientations whose height fits the strip); continuous parts feed the
//       area term only.
//   2 = 1 + continuous-rotation parts too: min width over 3600 sampled angles whose height fits, made
//       conservative by the Lipschitz bound of the rotated extent (|d width / d angle| <= diameter): the
//       sampled minimum minus diameter * step / 2, heights admitted up to H + diameter * step / 2.
// Holes / keep-outs are ignored (they only make the true minimum larger), and the result is shrunk by a
// relative 1e-4 slack, so the bound never exceeds the true minimum.
inline int g_min_width_stop = 0;
inline f32 strip_min_width_bound(const StripInstance& instance, f32 height) {
    if (!g_min_width_stop || !(height > 0.0f)) return 0.0f;
    double area = 0.0;
    f32 part_lb = 0.0f;
    auto extent = [](const Polygon& p, f32 a, f32& w, f32& h) {
        f32 c = std::cos(a), s = std::sin(a);
        f32 x0 = F32_MAX, x1 = -F32_MAX, y0 = F32_MAX, y1 = -F32_MAX;
        for (const auto& v : p.vertices) {
            f32 x = c * v.x - s * v.y, y = s * v.x + c * v.y;
            x0 = min_f(x0, x); x1 = max_f(x1, x); y0 = min_f(y0, y); y1 = max_f(y1, y);
        }
        w = x1 - x0; h = y1 - y0;
    };
    for (const auto& [part, qty] : instance.parts) {
        const Polygon& cs = *part.collision_shape;
        area += (double)cs.area * (double)qty;
        if (qty == 0) continue;
        f32 best = F32_MAX;
        if (part.allowed_rotation.kind == RotationKind::Discrete || part.allowed_rotation.kind == RotationKind::None) {
            std::vector<f32> angs = part.allowed_rotation.kind == RotationKind::None ? std::vector<f32>{0.0f}
                                                                                    : part.allowed_rotation.discrete;
            for (f32 a : angs) {
                f32 w, h; extent(cs, a, w, h);
                if (h <= height * (1.0f + 1e-4f)) best = min_f(best, w);
            }
        } else if (g_min_width_stop >= 2) {
            const int N = 3600;
            const f32 step = 2.0f * PI_F / (f32)N;
            const f32 slack = cs.diameter * step * 0.5f;
            for (int k = 0; k < N; ++k) {
                f32 w, h; extent(cs, step * (f32)k, w, h);
                if (h <= height * (1.0f + 1e-4f) + slack) best = min_f(best, w - slack);
            }
        } else continue;
        if (best == F32_MAX) return F32_MAX;           // no admissible orientation fits the strip height
        part_lb = max_f(part_lb, best);
    }
    f32 lb = max_f((f32)(area / (double)height), part_lb);
    return lb * (1.0f - 1e-4f);
}
// true => do not shrink to `next` (below the bound, or no actual reduction of `cur`)
inline bool min_width_stop(f32 next, f32 cur, f32 lb) {
    return g_min_width_stop && (next < lb || !(next < cur));
}

// ===================== exploration_phase (explore.rs : Algorithm 12) =====================
inline std::vector<StripSolution> exploration_phase(const StripInstance& instance, Relaxer& relaxer,
                                                 SolutionListener& listener, const Terminator& term,
                                                 const ExplorationConfig& config) {
    f32 current_width = relaxer.prob.strip_width();
    f32 best_width = current_width;
    const f32 min_w_lb = strip_min_width_bound(instance, relaxer.prob.strip.fixed_height);   // 0 when off
    std::vector<StripSolution> feasible_sols{relaxer.prob.save()};
    listener.report(ReportType::ExplFeas, feasible_sols[0], instance);

    std::vector<std::pair<StripSolution, f32>> infeas_pool;

    // STEP 3 (g_expl_bisect): adaptive shrink step + feasibility-wall bisection. cur_step grows back toward
    // the configured shrink_step on success and HALVES on failure (retrying a width between the last-failed
    // and the best feasible, relaxing the best feasible layout) for up to BISECT_K halvings before falling
    // through to the perturb path. OFF => cur_step stays == shrink_step and failures perturb (bit-identical).
    f32 cur_step = config.shrink_step;
    int halvings = 0;
    const int BISECT_K = 6;

    while (!term.kill()) {
        auto local_best = relaxer.relax(term, listener);
        f32 total_loss = local_best.second.get_total_loss();
        if (g_expl_trace) trace_state(total_loss == 0.0f ? "EXPL feasible" : "EXPL infeasible", relaxer);

        if (total_loss == 0.0f) {
            if (current_width < best_width) {
                best_width = current_width;
                feasible_sols.push_back(local_best.first);
                listener.report(ReportType::ExplFeas, local_best.first, instance);
                if (g_verbose) std::fprintf(stderr, "[EXPL] feasible width %.3f dens %.2f%%\n",
                                            current_width, relaxer.prob.density() * 100.0);
            }
            if (g_expl_bisect) { cur_step = std::min(config.shrink_step, cur_step * 2.0f); halvings = 0; }
            f32 next_width = current_width * (1.0f - cur_step);
            // NP_EXPL_TARGET: never step PAST the width the caller needs (the sheet width): a flat 0.1%
            // step from just above it overshoots by up to one step and then fights an over-tight wall.
            if (g_expl_target > 0.0f && current_width > g_expl_target && next_width < g_expl_target)
                next_width = g_expl_target;
            if (min_width_stop(next_width, current_width, min_w_lb)) {     // NP_MIN_WIDTH_STOP: provably futile
                if (g_expl_trace) std::fprintf(stderr, "[TRACE] EXPL min-width stop: next %.3f lb %.3f\n", next_width, min_w_lb);
                break;
            }
            relaxer.change_strip_width(next_width, std::nullopt);
            current_width = next_width;
            infeas_pool.clear();
        } else {
            listener.report(ReportType::ExplInfeas, local_best.first, instance);
            // insert sorted by ascending loss
            auto pos = std::lower_bound(infeas_pool.begin(), infeas_pool.end(), total_loss,
                                        [](const std::pair<StripSolution, f32>& e, f32 v) { return e.second < v; });
            infeas_pool.insert(pos, {local_best.first, total_loss});

            usize cap = config.max_conseq_failed_attempts.value_or(std::numeric_limits<usize>::max());
            if (infeas_pool.size() >= cap) break;

            if (g_expl_bisect && halvings < BISECT_K && !feasible_sols.empty()) {
                // bisect the wall: halve the step and relax the BEST feasible layout at a width between the
                // last failure and best_width (closer to best_width => more likely to re-converge). No perturb.
                halvings++;
                cur_step *= 0.5f;
                relaxer.rollback(feasible_sols.back(), nullptr);
                f32 next_width = best_width * (1.0f - cur_step);
                if (min_width_stop(next_width, best_width, min_w_lb)) break;   // NP_MIN_WIDTH_STOP
                relaxer.change_strip_width(next_width, std::nullopt);
                current_width = next_width;
            } else {
                if (g_expl_bisect) { cur_step = config.shrink_step; halvings = 0; }   // wall resolved -> resume normal exploration
                f32 sample = std::min(std::fabs(config.solution_pool_distribution_stddev * relaxer.rng.gen_normal()), 0.999f);
                usize idx = (usize)(sample * (f32)infeas_pool.size());
                const StripSolution& selected = infeas_pool[idx].first;
                relaxer.rollback(selected, nullptr);
                perturb_layout(relaxer, config);
            }
        }
    }
    return feasible_sols;
}

// ===================== compression_phase (compress.rs : Algorithm 13) =====================
inline std::optional<StripSolution> attempt_to_compress(Relaxer& relaxer, const StripSolution& init_sol, f32 r_shrink,
                                                     const Terminator& term, SolutionListener& listener) {
    relaxer.change_strip_width(init_sol.strip_width(), std::nullopt);
    relaxer.rollback(init_sol, nullptr);
    f32 new_width = init_sol.strip_width() * (1.0f - r_shrink);
    f32 split_pos = relaxer.rng.random_range_f32(0.0f, relaxer.prob.strip_width());
    relaxer.change_strip_width(new_width, split_pos);
    auto [compacted, ot] = relaxer.relax(term, listener);
    if (g_expl_trace)
        std::fprintf(stderr, "[TRACE] CMPR attempt %.2f -> %.2f split=%.1f loss=%.6g round=%llu\n", init_sol.strip_width(),
                     new_width, split_pos, ot.get_total_loss(), (unsigned long long)g_iter_count);
    if (ot.get_total_loss() == 0.0f) return compacted;
    return std::nullopt;
}

inline StripSolution compression_phase(const StripInstance& instance, Relaxer& relaxer, const StripSolution& init_sol,
                                    SolutionListener& listener, const Terminator& term,
                                    const CompressionConfig& config) {
    StripSolution best_sol = init_sol;
    // Progress clock: wall-seconds in time mode, relaxation-round count in iteration mode (so the
    // time-based shrink schedule is deterministic + reproducible when running by iterations).
    auto progress_now = []() -> double { return g_iter_mode ? (double)g_iter_count : now_seconds(); };
    double start = progress_now();
    int n_failed = 0;

    auto shrink_step_size = [&](int n_failed_attempts) -> f32 {
        if (config.shrink_decay.kind == ShrinkDecayKind::TimeBased) {
            f32 range = config.shrink_range.second - config.shrink_range.first;
            f32 ratio = (f32)((progress_now() - start) / config.time_limit_secs);
            return config.shrink_range.first + ratio * range;
        } else {
            return config.shrink_range.first * std::pow(config.shrink_decay.failure_ratio, (f32)n_failed_attempts);
        }
    };

    const f32 min_w_lb = strip_min_width_bound(instance, relaxer.prob.strip.fixed_height);   // 0 when off
    while (!term.kill()) {
        f32 step = shrink_step_size(n_failed);
        if (step < config.shrink_range.second) break;
        if (min_width_stop(best_sol.strip_width() * (1.0f - step), best_sol.strip_width(), min_w_lb)) {
            if (g_expl_trace) std::fprintf(stderr, "[TRACE] CMPR min-width stop: width %.3f lb %.3f\n", best_sol.strip_width(), min_w_lb);
            break;
        }
        auto compacted = attempt_to_compress(relaxer, best_sol, step, term, listener);
        if (compacted) { listener.report(ReportType::CmprFeas, *compacted, instance); best_sol = *compacted; }
        else n_failed += 1;
    }
    return best_sol;
}

// ===================== optimize (mod.rs : Algorithm 11) =====================
inline StripSolution optimize(StripInstance instance, Rng rng, SolutionListener& listener, Terminator& terminator,
                           const ExplorationConfig& expl_config, const CompressionConfig& cmpr_config,
                           const StripSolution* initial_solution) {
    auto next_rng = [&]() { return Rng(rng.next_u64()); };

    StripProblem start_prob = [&]() {
        if (!initial_solution) {
            InitialPlacer builder(instance, next_rng(), INITIAL_SAMPLE_CONFIG);
            builder.construct();
            return builder.prob;
        } else {
            StripProblem p(instance);
            p.restore(*initial_solution);
            return p;
        }
    }();

    terminator.new_timeout(expl_config.time_limit_secs);
    Relaxer expl_relaxer(instance, std::move(start_prob), next_rng(), expl_config.separator_config);
    std::vector<StripSolution> solutions = exploration_phase(instance, expl_relaxer, listener, terminator, expl_config);
    StripSolution final_explore_sol = solutions.back();

    terminator.new_timeout(cmpr_config.time_limit_secs);
    Relaxer cmpr_relaxer(expl_relaxer.instance, expl_relaxer.prob, next_rng(), cmpr_config.separator_config);
    StripSolution cmpr_sol = compression_phase(instance, cmpr_relaxer, final_explore_sol, listener, terminator, cmpr_config);

    listener.report(ReportType::Final, cmpr_sol, instance);
    return cmpr_sol;
}

} // namespace nest
