// Faithful C++ port of solver/src/util/{terminator,listener}.rs
#pragma once
#include "../geometry/strip_packing.hpp"
#include <optional>
#include <atomic>

namespace nest {
using namespace nest;

// Cooperative cancel flag (set by the host's np_cancel(); reset at np_nest start). Checked by BOTH
// terminators so a long solve stops at the next relaxation round and returns the best layout so far
// (the Grasshopper component sets this on ESC). Atomic: written from the UI thread, read by workers.
inline std::atomic<bool> g_cancel{false};

// ---- terminator.rs ----
struct Terminator {
    virtual ~Terminator() = default;
    virtual bool kill() const = 0;
    virtual void new_timeout(double timeout_secs) = 0;
    virtual std::optional<double> timeout_at() const = 0;
};

struct BasicTerminator : Terminator {
    std::optional<double> timeout;
    bool kill() const override { return g_cancel.load(std::memory_order_relaxed) || (timeout.has_value() && now_seconds() > *timeout); }
    void new_timeout(double timeout_secs) override { timeout = now_seconds() + timeout_secs; }
    std::optional<double> timeout_at() const override { return timeout; }
};

// Iteration-based termination: deterministic + machine-independent (unlike wall-clock). g_iter_count
// is bumped once per relaxation round (Relaxer::relax_round); new_timeout sets a budget RELATIVE to
// the current count, so each optimize phase (exploration / compression) gets its own allowance.
// PER-START relaxation-round counter. thread_local so concurrent multi-start runs (the parallel
// portfolio in the capi) each get their OWN iteration budget instead of racing one shared counter.
// It is an integer counter bumped once per round, separate from the FP overlap kernel, so making it
// thread_local does NOT perturb the floating-point basin.
inline thread_local unsigned long long g_iter_count = 0;
// Global SUM of rounds across all concurrent starts — for the host's np_progress() readout ONLY
// (never used for termination). Matches the component's TotalBudget = iter_budget * n_starts.
inline std::atomic<unsigned long long> g_iter_pub{0};
inline bool g_iter_mode = false;

// Best-of-N worker count for the relaxer/compressor. 0 = auto (use all hardware cores). Set once
// before a solve (the exe exposes it as a CLI arg; the DLL leaves it 0 => all cores).
inline unsigned g_n_workers = 0;

// Max surrogate poles per part. 0 = default (48). The overlap proxy is O(poles^2), so a smaller cap
// = faster per-eval at the cost of a coarser separation gradient (quality must be re-checked).
inline unsigned g_pole_max = 0;

// Post-relaxation geometric COMPACTION (STEP 1): after the relaxer reaches a feasible layout, slide each
// part as far LEFT as collision-free, to a fixpoint. The GLS/penalty relaxer stops at "no overlap", not
// "in contact", leaving a few-mm slack that becomes the column-cut spill margin; this reclaims it.
// Monotone (never widens) + collision-checked (never creates overlap), so OFF => baseline bit-identical.
inline bool g_final_compact = false;

// COMPACTION variant (CG-SHOP 2024 / Shadoks slide-to-contact). When g_final_compact is on, this picks
// the MULTI-DIRECTIONAL slide (-x, -y, toward-layout-centroid, toward-nearest-neighbour) over the plain
// 2-direction bottom-left slide. Same EXACT brute_overlap feasibility test; the centroid/neighbour passes
// are additionally width-guarded (a move may never push a part past the current strip width) and clamped
// to the strip height, so it is still strictly non-widening + collision-free. OFF => the plain BL slide.
inline bool g_compact_multidir = false;

// Sheet-hole KEEP-OUT guard for the compaction slide: when on (default), a compaction slide may not push a
// part into a sheet hole (the relaxer already keeps parts out; this stops compaction from undoing it). Set
// env NP_COMPACT_HOLE_GUARD=0 to disable for A/B (reproduces the pre-fix "compaction slides into a hole").
inline bool g_compact_hole_guard = true;

// Sheet GAP-FILL post-pass (default on): after the greedy fills sheets big-first, smaller parts can be left
// on a later sheet (or outside) while they would fit in the GAPS a bigger part left on an earlier sheet.
// This pass grid-searches such parts (smallest-first, with rotation) into the open free space of earlier
// sheets — collision- + bound- + sheet-hole-checked, verified moves only — so it never creates overlap and
// can only reduce the sheet count / parts-left-outside. Env NP_FILL_GAPS=0 disables for A/B.
inline bool g_fill_gaps = true;

// IN-LOOP SPILL RE-INSERTION (ruin-and-recreate, default off). greedy_fill cuts the strip at x=sheet_w
// and carries EVERY part whose x_max exceeds it to the NEXT sheet's fresh strip solve — even though the
// current sheet still has a free right-column (sheet_w - max kept x_max) plus interior voids. When on,
// after the cut greedy_fill grid-searches each spilled part (smallest-area first, with rotation) into the
// current sheet's open free space with the SAME exact brute_overlap feasibility used everywhere else, and
// commits the first collision-free, in-bounds pose. Crucially this runs BEFORE the spill is handed to the
// next sheet, so that sheet's strip solve runs on FEWER parts => genuinely narrower (unlike the host's
// post-hoc fill_sheet_gaps, which can only leave a hole in an already-solved sheet). Verified moves only,
// re-checked by the caller, so it can never create overlap. OFF => byte-identical to before.
inline bool g_reinsert_spill = false;

// Best-of-2 on spill sheets (default off): re-solve each spill sheet's fixed carried set at a second
// seed and keep whichever packs tighter AFTER compaction (the slide-to-contact pass closes different
// slack in different basins, so raw strip width mis-ranks them). Monotone-safe: sheet 0 and the part
// set are untouched, so packing can never get worse; the cost is wall-only. OFF => byte-identical.
inline bool g_spill_best_of2 = false;

// ROTATIONAL kick in compact_left (default off): the slide passes are translation-only, so
// orientation slack at contact is never reclaimed. When the translation sweeps reach a fixpoint,
// try small +/-deg rotations per part (about its centroid, exact brute_overlap feasibility,
// strictly non-widening) and keep a rotation iff it enables a strictly deeper -x slide; then the
// sweeps continue. OFF => byte-identical.
inline bool g_compact_rot = false;

// Drop INVALID placements (default on): a part the solver couldn't actually fit can end up reported on a
// sheet while hanging OUT OF BOUNDS or overlapping a sheet hole (it took a fresh sheet for nothing). This
// demotes any such placement to UNPLACED so the host lays it out OUTSIDE the sheets instead of wasting a
// sheet on a part that doesn't fit. Env NP_DROP_INVALID=0 disables for A/B.
inline bool g_drop_invalid = true;

// HOLES-FIRST (component part_holes_mode>=2 / env NP_HOLES_FIRST): pre-pair smaller parts INTO bigger parts'
// holes BEFORE the sheet nest (the small rides inside its host, removed from the main nest), instead of the
// default fill_cavities POST-pass. Off (0) => post-pass: the smalls first compete for sheet space, then are
// relocated into holes. On (1) packs the non-hole parts tighter because the smalls never took sheet space.
inline bool g_holes_first = false;

// STEP 3: feasibility-wall BISECTION in exploration. The flat 0.1% width shrink overshoots the wall and
// then perturbs, leaving the last mm on the table; with this on, a failed shrink HALVES the step and
// retries a width between the last-failed and the best feasible (binary-searching the exact wall), growing
// the step back on success. OFF => the shrink step stays flat and the failure path perturbs => bit-identical.
inline bool g_expl_bisect = false;

struct IterationTerminator : Terminator {
    unsigned long long kill_at = 0;
    bool kill() const override { return g_cancel.load(std::memory_order_relaxed) || g_iter_count >= kill_at; }
    void new_timeout(double n_rounds) override { kill_at = g_iter_count + (unsigned long long)n_rounds; }
    std::optional<double> timeout_at() const override { return static_cast<double>(kill_at); }
};

// ---- listener.rs ----
enum class ReportType { ExplFeas, ExplInfeas, ExplImproving, CmprFeas, Final };

struct SolutionListener {
    virtual ~SolutionListener() = default;
    virtual void report(ReportType report, const StripSolution& solution, const StripInstance& instance) = 0;
};

struct DummySolListener : SolutionListener {
    void report(ReportType, const StripSolution&, const StripInstance&) override {}
};

} // namespace nest
