// Shared nesting driver: part construction, single-strip warm-restart packing, and greedy
// sequential multi-sheet bin fill. Extracted verbatim from nest_physics.cpp so BOTH the CLI exe
// and the C-ABI shared library (nest_physics_capi.cpp) can drive the solver identically.
//
// Two small, behaviour-preserving additions vs the original main_nest helpers:
//   * build_part() takes an optional `input_centroid` out-param (the centroid of the ORIGINAL
//     un-shifted input polygon). The exe passes nullptr (no effect); the C ABI uses it to fold the
//     part-centering pre-transform into the rigid transform it returns to the host (Rhino).
//   * run_strip()/greedy_fill() take optional per-sheet HOLE polygons (forbidden interior regions).
//     When none are supplied the code path is identical to the original (holes default to empty).
#pragma once
#include "optimizer.hpp"
#include <cstdio>
#include <vector>
#include <utility>
#include <optional>
#include <limits>
#include <cmath>
#include <thread>
#include <mutex>

namespace nest {
// Resolve the worker count for best-of-N. g_n_workers if set, else ~75% of hardware cores. Using
// ALL cores oversubscribes (N workers + the main thread + OS) and benchmarked ~2x SLOWER per round
// from contention AND landed a worse basin; ~75% (e.g. 24 of 32) is the sweet spot — fastest, finds
// the best packing, and leaves the machine responsive. See plan Lever 1.
inline unsigned resolve_n_workers() {
    if (g_n_workers > 0) return g_n_workers;
    unsigned hc = std::thread::hardware_concurrency();
    if (hc == 0) return 8u;
    unsigned nw = hc - hc / 4; // ~75%
    return nw > 0 ? nw : 1u;
}
} // namespace nest

namespace nest {

inline SurrogateConfig surrogate_config() {
    // Denser poles than the Rust default {{64,0},{16,0.8},{8,0.9}}: thin ribbons need more
    // inscribed circles to cover concave crescents -> sharper separation gradient. (Only affects
    // the heuristic gradient; collision correctness uses exact polygon edges.)
    // Leaner poles than before: the overlap proxy is O(poles^2), so fewer poles = much faster
    // per-eval (benchmarked ~2.5x vs 96) for negligible packing-quality loss on this instance.
    // cap=48 reproduces the tuned default {48,16,8} exactly; a smaller cap shrinks the O(poles^2)
    // overlap kernel (benchmarkable via g_pole_max).
    unsigned cap = g_pole_max ? g_pole_max : 48u;
    unsigned mid = (cap / 3 > 0) ? cap / 3 : 1u;
    unsigned lo  = (cap / 6 > 0) ? cap / 6 : 1u;
    SurrogateConfig c; c.n_pole_limits = {{{(usize)cap, 0.0f}, {(usize)mid, 0.85f}, {(usize)lo, 0.92f}}};
    c.n_ff_poles = 1; c.n_ff_piers = 0; return c;
}
inline ShapeModifyConfig part_modify() {
    ShapeModifyConfig m; m.simplify_tolerance = 0.001f; m.narrow_concavity_cutoff = std::make_pair(0.01f, 0.01f); return m;
}

// Build a Part from a raw polyline. Default = FREE continuous rotation; rotation_count > 0
// restricts the part to N DISCRETE orientations (2π/N step; 1 = fixed at 0 rad — e.g. grain
// direction). nullopt if degenerate.
// If `input_centroid` is non-null it receives the centroid of the ORIGINAL (un-shifted) input;
// translate(-input_centroid) is exactly the part->internal pre-transform (used by the C ABI to
// report a single rigid transform mapping the host's original geometry to its placed pose).
inline std::optional<Part> build_part(usize id, std::vector<Point> pts, Point* input_centroid = nullptr,
                                      const ShapeModifyConfig* mc = nullptr, int rotation_count = 0) {
    f32 mnx = F32_MAX, mny = F32_MAX;
    for (auto& p : pts) { mnx = min_f(mnx, p.x); mny = min_f(mny, p.y); }
    for (auto& p : pts) { p.x -= mnx; p.y -= mny; }
    if (pts.size() >= 2 && pts.front() == pts.back()) pts.pop_back();
    std::vector<Point> clean;
    for (auto& p : pts) if (clean.empty() || !(clean.back() == p)) clean.push_back(p);
    if (!clean.empty() && clean.size() >= 2 && clean.front() == clean.back()) clean.pop_back();
    if (clean.size() < 3) return std::nullopt;
    try {
        Polygon raw = Polygon::create(clean);
        Point c = raw.centroid();
        // centroid of the original input = min-corner offset + centroid of the shifted shape.
        if (input_centroid) *input_centroid = Point(mnx + c.x, mny + c.y);
        SourceShape os;
        os.shape = raw;
        os.pre_transform = RigidTransform(0.0f, -c.x, -c.y); // center at origin (matches nest import)
        os.modify_mode = ShapeModifyMode::Inflate;
        os.modify_config = mc ? *mc : part_modify(); // exe passes nullptr -> identical default
        RotationRange rr = RotationRange::continuous();
        if (rotation_count > 0) {
            std::vector<f32> angles;
            angles.reserve(static_cast<usize>(rotation_count));
            const f32 step = (2.0f * PI_F) / static_cast<f32>(rotation_count);
            for (int k = 0; k < rotation_count; ++k) angles.push_back(static_cast<f32>(k) * step);
            rr = RotationRange::discrete_of(std::move(angles));
        }
        return Part::make(id, std::move(os), std::move(rr), std::nullopt, surrogate_config());
    } catch (const std::exception& e) {
        std::fprintf(stderr, "  skip part %zu: %s\n", id, e.what());
        return std::nullopt;
    }
}

inline bool brute_overlap(const Polygon& a, const Polygon& b) {
    for (usize i = 0; i < a.n_vertices(); ++i)
        for (usize j = 0; j < b.n_vertices(); ++j)
            if (collides(a.edge(i), b.edge(j))) return true;
    if (collides(a, b.vertices[0]) || collides(b, a.vertices[0])) return true;
    return false;
}

// Minimum bounding height of a polygon over all rotations (= its minimum width): rotate the verts and take
// the smallest (max_y - min_y) across `samples` orientations spanning [0,pi). A part whose min width exceeds
// a strip's height can't fit that strip at ANY orientation. (Lives here rather than in the C ABI because
// greedy_fill needs it too, to defer a part that doesn't fit THIS sheet to a taller later one.)
inline f32 min_bounding_height(const Polygon& p, int samples) {
    const auto& V = p.vertices;
    if (V.empty()) return 0.0f;
    f32 best = std::numeric_limits<f32>::max();
    for (int k = 0; k < samples; ++k) {
        f32 a = PI_F * (f32)k / (f32)samples;
        f32 c = std::cos(a), s = std::sin(a);
        f32 mn = std::numeric_limits<f32>::max(), mx = -mn;
        for (const auto& v : V) { f32 y = s * v.x + c * v.y; if (y < mn) mn = y; if (y > mx) mx = y; }
        f32 h = mx - mn; if (h < best) best = h;
    }
    return best;
}

// Re-id a subset of master parts to consecutive ids 0..k-1 (StripInstance indexes by position).
inline std::vector<std::pair<Part, usize>> subset_items(
        const std::vector<std::pair<Part, usize>>& master, const std::vector<usize>& idxs) {
    std::vector<std::pair<Part, usize>> out;
    for (usize newid = 0; newid < idxs.size(); ++newid) {
        Part it = master[idxs[newid]].first; // copy (shared_ptr shapes => cheap)
        it.id = newid;
        out.emplace_back(std::move(it), 1);
    }
    return out;
}

struct StripResult {
    StripSolution sol;
    StripInstance inst;
    f32 fixed_height = 0.0f;
    f32 width = 0.0f;
    f32 used_area = 0.0f;
    f32 density = 0.0f;
    bool fits = false; // achieved width fits within perp_limit (=> all parts fit one sheet)
};

// Multi-pass WARM-RESTART strip pack: pass 0 is a cold optimize(); each later pass feeds the best
// layout back as optimize()'s initial_solution, resuming the width shrink from the plateau instead
// of cold-rebuilding. Different seed per pass for basin diversity. Stops early once width<=limit.
// `holes` (optional) are forbidden interior regions in container coords [0,perp_limit]x[0,height].
inline StripResult run_strip(const std::vector<std::pair<Part, usize>>& items_master,
                             const CollisionConfig& collision_cfg, f32 fixed_height, f32 perp_limit,
                             double budget, uint64_t seed, int n_passes = 1,
                             const std::vector<Polygon>* holes = nullptr,
                             SolutionListener* ext_listener = nullptr) {
    std::vector<std::pair<Part, usize>> parts = items_master;
    f32 total_area = 0.0f;
    for (auto& pr : parts) total_area += pr.first.area() * (f32)pr.second;
    f32 init_width = total_area / fixed_height; // 100%-density width

    Strip strip(fixed_height, collision_cfg, part_modify(), init_width);
    if (holes && !holes->empty()) strip.set_holes(*holes);
    StripInstance inst(parts, strip);

    if (n_passes < 1) n_passes = 1;
    double per = budget / (double)n_passes;
    DummySolListener dummy;
    SolutionListener& listener = ext_listener ? *ext_listener : static_cast<SolutionListener&>(dummy);
    BasicTerminator bterm;
    IterationTerminator iterm;
    Terminator& term = g_iter_mode ? static_cast<Terminator&>(iterm) : static_cast<Terminator&>(bterm);

    StripSolution best;
    bool have = false;
    for (int pass = 0; pass < n_passes; ++pass) {
        SolverConfig cfg = default_solver_config();
        cfg.expl_cfg.time_limit_secs = per * 0.8;
        cfg.cmpr_cfg.time_limit_secs = per * 0.2;
        cfg.expl_cfg.max_conseq_failed_attempts = std::nullopt; // run to the clock
        // Use more of the 24-core machine: best-of-N workers run in parallel (WorkerPool), so a
        // larger N gives better moves per iteration at ~the same wall-time => faster convergence.
        unsigned nw = resolve_n_workers();
        cfg.expl_cfg.separator_config.n_workers = nw;
        cfg.cmpr_cfg.separator_config.n_workers = nw;
        Rng rng(seed + (uint64_t)pass);
        const StripSolution* init = have ? &best : nullptr; // warm-restart: deepen the best basin
        StripSolution sol = optimize(inst, rng, listener, term, cfg.expl_cfg, cfg.cmpr_cfg, init);
        if (!have || sol.strip_width() < best.strip_width()) { best = sol; have = true; }
        if (best.strip_width() <= perp_limit) break; // already fits one sheet -> stop early
    }

    StripResult r;
    r.fixed_height = fixed_height;
    r.width = best.strip_width();
    r.used_area = r.width * fixed_height;
    r.density = best.density(inst);
    r.fits = (r.width <= perp_limit);
    r.sol = std::move(best);
    r.inst = std::move(inst);
    return r;
}

struct SheetPlacement {
    std::vector<Point> verts;   // placed collision polygon, sheet-local coords
    usize color_id;             // GLOBAL part id (index into the master parts list)
    int sheet;                  // sheet index this part landed on
    RigidTransform d_transf;    // placement transform of the centered collision shape
};

// Greedy sequential bin fill: pack ALL remaining parts into one strip (h=sheet_h, minimize width),
// then take everything inside the first sheet_w-wide column as the next FINISHED sheet and carry the
// overflow (parts crossing x=sheet_w) to the next iteration. Sheet 1 ends up as full as possible;
// later sheets hold only the spill. A subset of a feasible (overlap-free) strip packing is itself
// overlap-free and in-bounds, so every emitted sheet is valid (re-verified here by brute force).
// If the whole strip fits in one column the result is a single sheet.
// `per_sheet_holes[s]` (optional) are the forbidden interior regions of sheet s (sheet-local coords);
// sheets beyond the supplied set reuse the last sheet's holes.
struct Sheets {
    std::vector<SheetPlacement> placements;
    std::vector<usize> counts;   // parts per sheet
    std::vector<f32> widths;     // used width per sheet
    int n_sheets = 0;
    int inf = 0;                 // bounds violations
    int bf = 0;                  // brute-force overlaps within a sheet
    bool ok = true;
};

// PER-SHEET dimensions (width, height). Sheets beyond the supplied set reuse the LAST entry, matching how
// per_sheet_holes degrades. An EMPTY vector means "every sheet is the size given by the scalar sheet_w /
// sheet_h arguments" — the original single-size behaviour, kept byte-identical for that (common) case.
typedef std::vector<std::pair<f32, f32>> SheetDims;
inline std::pair<f32, f32> dims_for(const SheetDims* dims, int s, f32 fallback_w, f32 fallback_h) {
    if (!dims || dims->empty()) return {fallback_w, fallback_h};
    usize i = (s >= 0 && s < (int)dims->size()) ? (usize)s : dims->size() - 1;
    return (*dims)[i];
}

// The packer's own ABSOLUTE placement slack: the one number every pass uses for "close enough to the
// wall". greedy_fill keeps a part whose x_max <= cur_w + PACKER_SLACK (below); compact_left refuses to
// slide a part past -PACKER_SLACK / above cur_h + PACKER_SLACK; fill_sheet_gaps and fill_cavities accept
// a candidate within PACKER_SLACK of their frame. It is ABSOLUTE — a legacy of the original code — and it
// is genuinely exercised, not merely permitted: at metre scale the bench places parts outside the sheet by
// up to PACKER_SLACK itself, which is the admission limit every pass uses and therefore the true ceiling.
// (Repro: np_bench --scale=0.001 over seeds 1-12; the max edge excursion varies by seed — ~5e-4 to ~1e-3 —
// so quote the ceiling, not a sampled maximum. Orders of magnitude above f32 round-off at these coords.)
inline constexpr f32 PACKER_SLACK = 1e-3f;

// BOUNDS TOLERANCE for "is this placed part inside the sheet it was placed on?", used by BOTH the
// demotion pass (drop_invalid_placements) and the final quality verification, so the two can never drift.
//
// The RELATIVE term is deliberately relative to the geometry. It used to be an absolute 1.0 model units,
// which is 0.1% of a 1000 mm sheet but 100% of a 1 m sheet — so the very same defective layout was caught
// in a millimetre document and completely invisible in a metre one (proven by running one broken layout
// at both scales). Reference length is the SMALLER of the sheet span and the part's own bbox diagonal, so
// the check also stays tight when small parts sit on a very large sheet.
//
// THE FLOOR IS ABSOLUTE, AND THE VERDICT IS THEREFORE ONLY SCALE-INVARIANT ABOVE IT. The floor is
// 2 x PACKER_SLACK = 0.002 MODEL UNITS, so it dominates the relative term whenever the reference length
// is below 20 model units — i.e. in any document whose parts are ~20 units or smaller across, which is
// what a metre-unit Rhino file looks like. What that costs, in units of the geometry: with the bench's
// 400x300 parts (diagonal 500) the tolerance is the relative term, 1e-4 x 500 = 0.05 units = 0.01% of the
// part; scale the identical job by 0.001 and the diagonal is 0.5, the relative term is 5e-5, and the floor
// takes over at 0.002 = 0.4% of the part — 40x looser. So a defect sized between 0.01% and 0.4% of the
// part is caught at millimetre scale and invisible at metre scale. That band IS the whole statement.
//
// DEMONSTRATED with np_bench (seams armed: -DNEST_PHYSICS_BUILD_BENCH=ON). The --shift seam runs AFTER
// every packing pass, so the excursion it produces is exactly linear in the flag; dial --shift until the
// harness line "max edge excursion = ... (X% of the sheet span)" reads the SAME X at both scales, and the
// two runs then carry the identical RELATIVE defect. It needs two DIFFERENT --shift values to get there,
// because f32 rounding makes the two layouts non-identical — which is exactly why the single --shift=0.1
// this note used to quote demonstrated nothing: at --scale=1 it lands the part 0.046 units out, INSIDE
// the 0.05 tolerance, so nothing is demoted at either scale and the two excursions differ ~7x in relative
// terms. The pair that does work (both --parts=12 --sheets=4 --seed=100):
//     --scale=1     --shift=0.1005    -> excursion 0.545971   units = 0.0273% of the sheet span
//                                        -> DEMOTED (demoted=1, unplaced=1, VERDICT: UNPLACED-SPILL)
//     --scale=0.001 --shift=0.100223  -> excursion 0.000545965 units = 0.0273% of the sheet span
//                                        -> KEPT (clean=1, out_of_bounds=0, demoted=0): it SHIPS hanging
//                                           outside the sheet, and only np_bench's own measurement sees
//                                           it ("VERDICT: OUT-OF-BOUNDS *** GATE DISAGREES ***")
// Same relative defect to three significant figures, opposite verdicts, and the floor is the only
// difference between them. (Append --dropinvalid=0 to the first command to read its excursion: the
// demotion removes the part before the harness can measure it.) The analytic width of that blind band is
// the floor itself: 0.002 units, which on a 1x1 m sheet is 0.1% of the span, against 0.0025% at mm scale.
//
// The floor is required, not cosmetic: the PACKER's own slack epsilons are absolute (PACKER_SLACK above
// and the 1e-3f literals listed below), so without a floor of 2 x PACKER_SLACK this exit check would
// demote placements the packer itself deliberately allowed to graze the wall. The honest fix is to make
// THOSE relative — but they set slide fixpoints and admission tests, so changing them moves layouts that
// are required to stay byte-identical, and it is a separate change. Until then this comment, and the
// np_last_quality contract in nest_physics_capi.h, state the floor rather than claiming invariance.
//
// AUDIT of the other absolute epsilons, since this class of bug hides in all of them:
//   * real_overlap's OVERLAP_EPS_FRAC (capi) shrinks each polygon by a fraction of ITS OWN size — that
//     part was always relative, but the SHRINK ITSELF was wrong: it scaled toward the area centroid,
//     which is not an erosion for a concave part (a U/C centroid is outside the material). Replaced with
//     a genuine miter inward offset, verified to be a subset. Scale-relative and now also shape-correct.
//   * the 1e-3f slacks in compact_left (above), greedy_fill's keep test, fill_cavities,
//     precompute_hole_pairs and fill_sheet_gaps are all PERMISSIVE — they only widen what a pass will
//     ADMIT, and every admitted candidate is then exactly re-verified (brute_overlap) before it commits.
//     They can make the packer slightly more generous at small model scale; they cannot ship overlapping
//     or escaped geometry, because this tolerance guards the exit.
//   * greedy_fill's out.inf test (1e-2f) IS scale-blind, but it feeds only out.ok -> the multi-start
//     score, i.e. which candidate layout is preferred. Anything it waves through still meets
//     drop_invalid_placements, which now uses THIS tolerance and is strictly tighter at metre scale. Left
//     absolute deliberately: changing it perturbs multi-start selection and would move layouts that are
//     required to stay byte-identical.
//   * dims_uniform's 1e-4f only decides whether sheets count as interchangeable for renumbering — and
//     size alone was NOT a sufficient test, see sheets_interchangeable() in the capi: keep-out holes are
//     indexed by sheet id, so identically-sized sheets with different holes are not interchangeable.
inline f32 bounds_tol(f32 sheet_w, f32 sheet_h, f32 part_diag) {
    f32 ref = sheet_w + sheet_h;
    if (part_diag > 0.0f && part_diag < ref) ref = part_diag;
    const f32 rel = 1e-4f * ref;
    const f32 floor_abs = 2.0f * PACKER_SLACK;
    return rel > floor_abs ? rel : floor_abs;
}

// ---- Live layout streaming (for an interactive host's animated preview) ----
// While a solve runs on a background thread, the host (Grasshopper) polls the current best layout
// via np_poll_layout to draw a "tightening" preview. greedy_fill publishes the running strip layout
// here (the optimizer's listener fires on every improvement); np_poll_layout reads it under the lock,
// bands the long strip into sheet-width columns, and folds in part-centering. mutex-guarded because
// the solve thread writes while the UI thread reads.
struct LiveLayout {
    std::mutex mtx;
    bool active = false;                 // a solve is in progress
    f32 sheet_w = 0.0f;                  // sheet width (for banding the strip into columns)
    int max_bins = 6;                    // cap on sheets
    int sheet_base = 0;                  // greedy_fill's current sheet index
    std::vector<usize> sub2glob;         // subset part_id -> global color_id (= the `remaining` vector)
    struct Item { usize color_id; RigidTransform dt; f32 xmin; };
    std::vector<Item> items;             // current strip placement (subset/strip coords)
    unsigned long long gen = 0;          // bumps on each update
};
inline LiveLayout g_live;

// Optimizer listener that snapshots the running strip layout into g_live on every report.
struct ProgressListener : SolutionListener {
    void report(ReportType, const StripSolution& sol, const StripInstance&) override {
        Layout live = Layout::from_snapshot(sol.layout_snapshot);
        std::lock_guard<std::mutex> lk(g_live.mtx);
        g_live.items.clear();
        live.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            usize gid = (pi.part_id < g_live.sub2glob.size()) ? g_live.sub2glob[pi.part_id] : pi.part_id;
            g_live.items.push_back({ gid, pi.d_transf, pi.shape->bbox.x_min });
        });
        g_live.gen++;
    }
};

// Axis-aligned bbox overlap (cheap reject before the O(verts^2) brute_overlap).
inline bool bbox_overlap(const Rect& a, const Rect& b) {
    return a.x_min < b.x_max && b.x_min < a.x_max && a.y_min < b.y_max && b.y_min < a.y_max;
}

// STEP 1 — post-relaxation geometric COMPACTION (flag-gated by g_final_compact). BOTTOM-LEFT settle: slide
// every placed part as far -x (then -y) as collision-free, alternating axes to a fixpoint. The relaxer
// leaves a few-mm slack everywhere (it stops at zero pole-overlap, not at contact); pulling parts into the
// bottom-left corner reclaims it, so fewer parts straddle x = sheet_w and spill. Each slide binary-searches
// the maximal collision-free translation against the EXACT others (brute_overlap, bbox-gated). Monotone in
// the gravity direction + collision-checked (cannot create overlap); a fresh re-verification happens in the
// caller. Operates on the local snapshot `live` only (no collision-engine state).
inline void compact_left(Layout& live, f32 sheet_w, f32 sheet_h, const std::vector<Polygon>* sheet_holes = nullptr) {
    if (!g_final_compact) return;
    (void)sheet_w;  // strip width is flexible (multi-bin); the width cap is derived from current geometry
    // Sheet-hole KEEP-OUT for the compaction slides. `sheet_holes` are this emitted sheet's holes in the
    // SAME sheet-local / column-0 frame the relaxer used (driver.hpp greedy_fill), so a slide that would
    // push a part into one is rejected with the SAME exact brute_overlap primitive used for parts —
    // otherwise compaction would silently undo the relaxer's keep-out. nullptr/empty => no-op, so a
    // run with no sheet holes is byte-identical to before.
    const std::vector<Polygon>* SH = (g_compact_hole_guard && sheet_holes && !sheet_holes->empty()) ? sheet_holes : nullptr;
    struct PP { PartKey key; Polygon poly; RigidTransform dt; };
    std::vector<PP> ps;
    live.placed_parts.for_each([&](PartKey k, const PlacedPart& pi) { ps.push_back({k, *pi.shape, pi.d_transf}); });
    int N = (int)ps.size();
    if (N < 2) return;
    const f32 TOL = 1e-3f;
    const int BISECT = 18;

    if (!g_compact_multidir) {
        // ===== BASELINE 2-direction bottom-left slide (verbatim; this path == prior `compact 1`). =====
        const int SWEEPS = 24;
        // Slide part i maximally along one axis (xaxis => -x, else -y). Returns the distance moved.
        auto slide = [&](int i, bool xaxis) -> f32 {
            f32 maxd = xaxis ? ps[i].poly.bbox.x_min : ps[i].poly.bbox.y_min;  // can't cross the 0 wall
            if (maxd <= TOL) return 0.0f;
            auto feasible = [&](f32 d) -> bool {
                Polygon t = ps[i].poly;
                t.transform(AffineTransform::from_translation(xaxis ? -d : 0.0f, xaxis ? 0.0f : -d));
                if ((xaxis ? t.bbox.x_min : t.bbox.y_min) < -TOL) return false;
                for (int j = 0; j < N; ++j) {
                    if (j == i) continue;
                    if (!bbox_overlap(t.bbox, ps[j].poly.bbox)) continue;
                    if (brute_overlap(t, ps[j].poly)) return false;
                }
                if (SH) for (const auto& h : *SH) {          // never slide into a sheet hole (keep-out)
                    if (!bbox_overlap(t.bbox, h.bbox)) continue;
                    if (brute_overlap(t, h)) return false;
                }
                return true;
            };
            if (!feasible(0.0f)) return 0.0f;                 // current pose already touches another (skip)
            f32 lo = 0.0f, hi = maxd, best = 0.0f;
            for (int it = 0; it < BISECT; ++it) {
                f32 mid = 0.5f * (lo + hi);
                if (feasible(mid)) { best = mid; lo = mid; } else { hi = mid; }
            }
            if (best > TOL) {
                ps[i].poly.transform(AffineTransform::from_translation(xaxis ? -best : 0.0f, xaxis ? 0.0f : -best));
                if (xaxis) ps[i].dt.tx -= best; else ps[i].dt.ty -= best;
                return best;
            }
            return 0.0f;
        };

        for (int sweep = 0; sweep < SWEEPS; ++sweep) {
            bool moved = false;
            // -x pass: leftmost first so each clears the way for the next
            std::sort(ps.begin(), ps.end(), [](const PP& a, const PP& b) { return a.poly.bbox.x_min < b.poly.bbox.x_min; });
            for (int i = 0; i < N; ++i) if (slide(i, true) > TOL) moved = true;
            // -y pass: bottommost first (frees vertical voids the next -x pass can exploit)
            std::sort(ps.begin(), ps.end(), [](const PP& a, const PP& b) { return a.poly.bbox.y_min < b.poly.bbox.y_min; });
            for (int i = 0; i < N; ++i) if (slide(i, false) > TOL) moved = true;
            if (!moved) break;
        }
    } else {
        // ===== MULTI-DIRECTIONAL exact-geometry slide-to-contact (CG-SHOP 2024 / Shadoks). Generalizes the
        // BL slide to 4 directions: -x, -y, toward layout centroid, toward nearest neighbour. The -x/-y
        // passes only ever REDUCE an extent; the inward passes are width-capped (a move may never push a
        // part's x_max past the current strip width) and height-clamped (y within [0, sheet_h]). Same EXACT
        // brute_overlap feasibility => never creates overlap, strictly non-widening; the caller re-verifies.
        const int SWEEPS = 16;
        const f32 INF = std::numeric_limits<f32>::max();
        // Slide part i along unit dir (ux,uy), up to maxd; width_cap forbids pushing x_max past it. Returns dist.
        auto slide_dir = [&](int i, f32 ux, f32 uy, f32 maxd, f32 width_cap) -> f32 {
            auto feasible = [&](f32 d) -> bool {
                Polygon t = ps[i].poly;
                t.transform(AffineTransform::from_translation(ux * d, uy * d));
                if (t.bbox.x_min < -TOL || t.bbox.y_min < -TOL) return false;   // 0 walls
                if (t.bbox.y_max > sheet_h + TOL) return false;                 // strip-height wall
                if (t.bbox.x_max > width_cap + TOL) return false;               // never widen the strip
                for (int j = 0; j < N; ++j) {
                    if (j == i) continue;
                    if (!bbox_overlap(t.bbox, ps[j].poly.bbox)) continue;
                    if (brute_overlap(t, ps[j].poly)) return false;
                }
                if (SH) for (const auto& h : *SH) {          // never slide into a sheet hole (keep-out)
                    if (!bbox_overlap(t.bbox, h.bbox)) continue;
                    if (brute_overlap(t, h)) return false;
                }
                return true;
            };
            if (maxd <= TOL || !feasible(0.0f)) return 0.0f;
            f32 lo = 0.0f, hi = maxd, best = 0.0f;
            for (int it = 0; it < BISECT; ++it) {
                f32 mid = 0.5f * (lo + hi);
                if (feasible(mid)) { best = mid; lo = mid; } else { hi = mid; }
            }
            if (best > TOL) {
                ps[i].poly.transform(AffineTransform::from_translation(ux * best, uy * best));
                ps[i].dt.tx += ux * best; ps[i].dt.ty += uy * best;
                return best;
            }
            return 0.0f;
        };

        for (int sweep = 0; sweep < SWEEPS; ++sweep) {
            bool moved = false;
            // D0: -x (leftmost first); only reduces x_max => no width cap needed
            std::sort(ps.begin(), ps.end(), [](const PP& a, const PP& b) { return a.poly.bbox.x_min < b.poly.bbox.x_min; });
            for (int i = 0; i < N; ++i) if (slide_dir(i, -1.0f, 0.0f, ps[i].poly.bbox.x_min, INF) > TOL) moved = true;
            // D1: -y (bottommost first)
            std::sort(ps.begin(), ps.end(), [](const PP& a, const PP& b) { return a.poly.bbox.y_min < b.poly.bbox.y_min; });
            for (int i = 0; i < N; ++i) if (slide_dir(i, 0.0f, -1.0f, ps[i].poly.bbox.y_min, INF) > TOL) moved = true;
            // current strip width => cap for the inward passes (fill voids, but never widen)
            f32 width_cap = 0.0f; for (int i = 0; i < N; ++i) width_cap = std::max(width_cap, ps[i].poly.bbox.x_max);
            // layout centroid (mean of part centroids)
            f32 cx = 0.0f, cy = 0.0f;
            for (int i = 0; i < N; ++i) { Point c = ps[i].poly.centroid(); cx += c.x; cy += c.y; }
            cx /= (f32)N; cy /= (f32)N;
            // D2: toward layout centroid (pulls peripheral parts inward into voids)
            for (int i = 0; i < N; ++i) {
                Point c = ps[i].poly.centroid();
                f32 dx = cx - c.x, dy = cy - c.y, len = std::sqrt(dx * dx + dy * dy);
                if (len < TOL) continue;
                if (slide_dir(i, dx / len, dy / len, len, width_cap) > TOL) moved = true;
            }
            // D3: toward nearest-neighbour centroid (closes pair gaps; stops at contact)
            for (int i = 0; i < N; ++i) {
                Point ci = ps[i].poly.centroid();
                int nn = -1; f32 bestdd = INF;
                for (int j = 0; j < N; ++j) {
                    if (j == i) continue;
                    Point cj = ps[j].poly.centroid();
                    f32 dd = (cj.x - ci.x) * (cj.x - ci.x) + (cj.y - ci.y) * (cj.y - ci.y);
                    if (dd < bestdd) { bestdd = dd; nn = j; }
                }
                if (nn < 0) continue;
                Point cn = ps[nn].poly.centroid();
                f32 dx = cn.x - ci.x, dy = cn.y - ci.y, len = std::sqrt(dx * dx + dy * dy);
                if (len < TOL) continue;
                if (slide_dir(i, dx / len, dy / len, len, width_cap) > TOL) moved = true;
            }
            if (!moved) break;
        }
    }

    for (auto& p : ps) {
        PlacedPart* P = live.placed_parts.get(p.key);
        if (P) { *P->shape = p.poly; P->d_transf = p.dt; }
    }
}

// `per_sheet_dims` (optional) gives each sheet its OWN width x height. Without it every sheet is sheet_w x
// sheet_h — the original behaviour, and the path a uniform sheet set still takes byte-for-byte. With it,
// each greedy iteration strip-packs against THAT sheet's real size, which is what makes a mixed-size sheet
// set correct: the old code took the size from sheet 0 alone, so every part placed on a SMALLER later sheet
// was reported "placed" while it hung metres outside that sheet's outline (and a LARGER later sheet was
// packed as if it were tiny).
inline Sheets greedy_fill(const std::vector<std::pair<Part, usize>>& parts, const CollisionConfig& engine,
                          f32 sheet_w, f32 sheet_h, double total_budget, int max_bins = 6,
                          const std::vector<std::vector<Polygon>>& per_sheet_holes = {},
                          uint64_t base_seed = 100, bool publish_live = true,
                          const SheetDims* per_sheet_dims = nullptr) {
    Sheets out;
    std::vector<usize> remaining;
    for (usize i = 0; i < parts.size(); ++i) remaining.push_back(i);

    // publish_live: in a PARALLEL multi-start portfolio only ONE start streams its layout into the
    // shared g_live preview; the rest run silently (a no-op listener + skipping the g_live writes) so
    // concurrent starts don't race / flicker the preview.
    ProgressListener prog;   // streams the running strip layout into g_live for the host's live preview
    DummySolListener dummy;
    SolutionListener* listener = publish_live ? (SolutionListener*)&prog : (SolutionListener*)&dummy;

    int sheet = 0;
    while (!remaining.empty() && sheet < max_bins) {
        bool cancel_now = g_cancel.load(std::memory_order_relaxed);
        // THIS sheet's real size (== sheet_w/sheet_h unless a per-sheet set was supplied).
        auto [cur_w, cur_h] = dims_for(per_sheet_dims, sheet, sheet_w, sheet_h);
        double b = (sheet == 0) ? total_budget * 0.65 : total_budget * 0.25;
        int passes = (sheet == 0) ? 3 : 2;

        // MIXED SIZES: a part whose minimum width exceeds THIS sheet's height cannot fit it at any
        // rotation. Defer it to a later (taller) sheet rather than feed it to a strip it can never
        // satisfy — an unsatisfiable part poisons the whole relaxation for the parts that DO fit.
        // With uniform sheets `defer` is always empty (the caller already set aside parts that fit no
        // sheet at all), so this is a no-op there.
        std::vector<usize> defer;
        if (per_sheet_dims && !per_sheet_dims->empty()) {
            std::vector<usize> fit_now;
            for (usize g : remaining) {
                const Polygon* cs = parts[g].first.collision_shape.get();
                if (cs && min_bounding_height(*cs, 90) > cur_h * 1.001f) defer.push_back(g);
                else fit_now.push_back(g);
            }
            remaining.swap(fit_now);
            if (remaining.empty()) {          // nothing fits this sheet -> leave it empty, try the next
                out.counts.push_back(0); out.widths.push_back(0.0f);
                remaining = defer; sheet++; continue;
            }
        }

        auto sub = subset_items(parts, remaining);
        const std::vector<Polygon>* holes = nullptr;
        if (!per_sheet_holes.empty()) {
            usize hidx = (sheet < (int)per_sheet_holes.size()) ? (usize)sheet : per_sheet_holes.size() - 1;
            holes = &per_sheet_holes[hidx];
        }
        if (publish_live)
        {   // publish the subset->global mapping + sheet context so np_poll_layout can band/fold. The
            // preview bands by ONE width (cur_w); with mixed-size sheets the live preview is therefore
            // approximate mid-solve — the FINAL placement below is exact either way.
            std::lock_guard<std::mutex> lk(g_live.mtx);
            g_live.sub2glob = remaining; g_live.sheet_base = sheet;
            g_live.sheet_w = cur_w; g_live.max_bins = max_bins;
        }
        StripResult R = run_strip(sub, engine, cur_h, cur_w, b, base_seed + (uint64_t)sheet * 7, passes, holes, listener);
        Layout live = Layout::from_snapshot(R.sol.layout_snapshot);

        // CANCELLED (host pressed ESC): freeze the current strip as-is. Emit every part THAT WENT INTO
        // THIS STRIP at its current optimized position, wrapping the long strip into columns sized by the
        // sheet each column lands on (clamped to the last sheet), so the user sees exactly where the
        // solver had each of them.
        //
        // NOT emitted: the parts `defer` just set aside because their minimum width exceeds THIS sheet's
        // height. They have no position in this strip — they were never packed into it — so there is
        // nothing to freeze, and inventing one would report a part as placed on a sheet it provably
        // cannot fit at any rotation. They therefore come back UNPLACED (sheet id -1), which is a real,
        // visible outcome: the host lays unplaced parts out in a row beside the sheets and the quality
        // gate counts them (np_last_quality's `unplaced`). Nothing is silently lost; a cancelled solve on
        // a mixed-size sheet set simply nests fewer parts than a completed one, which is the point of
        // cancelling. (With uniform sheets `defer` is always empty and this paragraph is moot.)
        if (cancel_now) {
            std::vector<int> band_count((usize)max_bins, 0);
            std::vector<f32> band_wmax((usize)max_bins, 0.0f);
            // Column boundaries along the strip, using EACH TARGET SHEET'S OWN width. The old code divided
            // x by cur_w for every band, i.e. it cut a 1000-wide strip into 1000-wide columns and then laid
            // them on sheets that are 600 wide — every part past the first column hung out of its sheet and
            // drop_invalid_placements demoted it, costing 29 of 40 parts on a 1000/600 set. col_off[s] is
            // the strip x where the column for sheet `sheet+s` begins. For a uniform sheet set col_off[s]
            // == s*cur_w, so band = floor(x/cur_w) and dx = -band*cur_w exactly as before: no change there.
            const int nb = (max_bins - sheet > 0) ? (max_bins - sheet) : 1;
            std::vector<f32> col_off((usize)nb + 1, 0.0f);
            for (int s = 0; s < nb; ++s) {
                auto [w_s, h_s] = dims_for(per_sheet_dims, sheet + s, sheet_w, sheet_h);
                (void)h_s;
                col_off[(usize)s + 1] = col_off[(usize)s] + (w_s > 0.0f ? w_s : cur_w);
            }
            live.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
                usize gid = remaining[pi.part_id];
                int band = 0;                                    // last column whose start is <= x_min
                while (band + 1 < nb && pi.shape->bbox.x_min >= col_off[(usize)band + 1]) ++band;
                int sh = sheet + band;                           // <= max_bins-1 by construction of nb
                f32 dx = -col_off[(usize)band];
                std::vector<Point> v = pi.shape->vertices;
                for (auto& p : v) p.x += dx;
                RigidTransform dt = pi.d_transf; dt.tx += dx;
                SheetPlacement p; p.color_id = gid; p.sheet = sh; p.verts = v; p.d_transf = dt;
                for (auto& pt : v) band_wmax[(usize)sh] = max_f(band_wmax[(usize)sh], pt.x);
                band_count[(usize)sh]++;
                out.placements.push_back(std::move(p));
            });
            int used = 0;
            for (int s = 0; s < max_bins; ++s) if (band_count[(usize)s] > 0) used = s + 1;
            for (int s = 0; s < used; ++s) { out.counts.push_back((usize)band_count[(usize)s]); out.widths.push_back(band_wmax[(usize)s]); }
            out.n_sheets = used;
            out.ok = false;   // cancelled snapshot: positions are as-is, may still overlap
            return out;
        }

        // STEP 1: geometric left-slide compaction on the feasible layout (no-op unless g_final_compact),
        // reclaiming the relaxer's leftover slack so fewer parts straddle x = cur_w and spill to sheet 2.
        compact_left(live, cur_w, cur_h, holes);

        // partition placed parts: keep those fully within [0, cur_w], carry the rest
        struct Keep { usize gid; std::vector<Point> verts; RigidTransform dt; };
        std::vector<Keep> keep;
        std::vector<usize> carry;
        f32 best_xmax = F32_MAX; usize best_local = 0; bool any = false;
        live.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
            usize gid = remaining[pi.part_id];
            f32 xmax = pi.shape->bbox.x_max;
            if (!any || xmax < best_xmax) { best_xmax = xmax; best_local = pi.part_id; any = true; }
            if (xmax <= cur_w + 1e-3f) keep.push_back({gid, pi.shape->vertices, pi.d_transf});
            else carry.push_back(gid);
        });
        // progress guarantee: if nothing fit a column (shouldn't happen, parts < sheet_w), force leftmost
        if (keep.empty() && any) {
            usize forced = remaining[best_local];
            live.placed_parts.for_each([&](PartKey, const PlacedPart& pi) {
                if (pi.part_id == best_local) keep.push_back({forced, pi.shape->vertices, pi.d_transf});
            });
            std::vector<usize> c2; for (usize g : carry) if (g != forced) c2.push_back(g); carry = c2;
        }

        // emit + verify this sheet
        f32 wmax = 0.0f;
        std::vector<Polygon> shapes;
        for (auto& k : keep) {
            SheetPlacement p; p.color_id = k.gid; p.sheet = sheet; p.verts = k.verts; p.d_transf = k.dt;
            for (auto& pt : k.verts) wmax = max_f(wmax, pt.x);
            out.placements.push_back(std::move(p));
            try { shapes.push_back(Polygon::create(k.verts)); } catch (...) {}
        }
        for (auto& s : shapes)
            if (s.bbox.x_max > cur_w + 1e-2f || s.bbox.x_min < -1e-2f ||
                s.bbox.y_max > cur_h + 1e-2f || s.bbox.y_min < -1e-2f) out.inf++;
        for (usize i = 0; i < shapes.size(); ++i)
            for (usize j = i + 1; j < shapes.size(); ++j)
                if (brute_overlap(shapes[i], shapes[j])) out.bf++;

        out.counts.push_back(keep.size());
        out.widths.push_back(wmax);
        // parts that didn't fit this column, plus the ones deferred above because this sheet was too short
        remaining = carry;
        remaining.insert(remaining.end(), defer.begin(), defer.end());
        sheet++;
    }
    // Count sheets by the LAST one that actually holds a part: with mixed sizes a sheet can be skipped
    // (nothing left fits it). For a uniform sheet set every iteration keeps >= 1 part (progress guarantee),
    // so this equals the old `sheet` counter exactly.
    int last_used = 0;
    for (const auto& p : out.placements) if (p.sheet + 1 > last_used) last_used = p.sheet + 1;
    out.n_sheets = last_used;
    out.ok = (out.inf == 0) && (out.bf == 0) && remaining.empty();
    return out;
}

} // namespace nest
