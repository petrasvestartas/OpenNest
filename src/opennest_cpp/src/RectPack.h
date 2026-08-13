#pragma once
// MaxRects rectangle packer (Best-Short-Side-Fit) for the axis-aligned-rectangle fast path.
//
// When every part AND every sheet of a nesting job is an axis-aligned rectangle with no holes,
// the general NFP + genetic-algorithm solve is overkill: a deterministic MaxRects pack finds a
// dense guillotine-free layout in well under a millisecond. This header is pure geometry (no
// engine types) so it stays trivially testable; the C-API adapter (nfp_nest_capi.cpp) does the
// rectangle detection and maps the results back onto NFP placement coordinates.
//
// Reference: Jukka Jylanki, "A Thousand Ways to Pack the Bin" (2010), MaxRects-BSSF variant.
//
// PORTFOLIO (see packMaxRects): a single greedy BSSF pass is not good enough. BSSF minimises the
// leftover strip, which is precisely wrong when that leftover lands just BELOW the size of the
// next item — the placement that "fits tightest" is the one that kills the remaining capacity.
// Measured instance (the multisheet regression, 24x 400x300 parts on 1000x1000 sheets, spacing 5,
// rotation enabled): at the third placement the free rect is 1000x695 and
//     upright  405x305 -> leftovers (595, 390), short side 390
//     rotated  305x405 -> leftovers (695, 290), short side 290   <- BSSF prefers this
// 290 is smaller than the item's own 305, so the rotated choice turns the top band into dead
// space and the sheet takes 5 parts instead of 6 — enabling rotation made the pack STRICTLY
// WORSE (5 sheets instead of 4). It is not a tie, so no tie-break can fix it.
//
// Two more appealing fixes were measured and REJECTED on the evidence. Scores below are the total
// bins used over 500 random multi-bin instances (lower is better); the old single BSSF pass = 1539.
//   * Swapping the rule wholesale. Bottom-Left does solve the case above, but scores 1540-1576
//     depending on the item ordering and is worse than plain BSSF on 10-38 of the 500 instances;
//     Best-Long-Side-Fit scores 1593. Contact-point scoring genuinely beats BSSF overall (1535)
//     yet still loses on 6 instances and costs roughly twice the time. Every rule wins somewhere
//     and loses somewhere else.
//   * A "dead strip" term added to BSSF: charge a residual narrower than the smallest remaining
//     item as waste, since nothing can ever fill it. It fixes the case above in a single pass —
//     and scores 1620, worse than plain BSSF on 81 of the 500 instances. Locally smarter, globally
//     worse.
// No single greedy rule dominates, which is Jylanki's own conclusion. So the packer runs a small
// portfolio of complete passes and keeps the best layout: 1529 bins over the same 500 instances,
// worse than the old single pass on NONE of them. It also makes "turning rotation up never places
// fewer parts" an unconditional guarantee rather than a hope — but read the exact statement of
// what is and is not guaranteed, and what the extra passes cost, above packMaxRects.

#include <vector>
#include <array>
#include <cmath>
#include <limits>
#include <algorithm>

namespace nest {

// A free rectangle inside a bin. Coordinates are in whatever frame the caller used for binDims:
// the bin is seeded with its own {originX, originY, W, H}, so free rects — and the blocked rects
// the caller supplies — live in the SAME frame as the bin origin, not a bin-local one. The only
// caller (nfp_nest_capi.cpp rectFastPath) builds both from GeometryUtil::getPolygonBounds of the
// sheet and of its voids, so its frame is sheet-ABSOLUTE, and RectPacked comes back in it too.
struct RectFree { double x, y, w, h; };

// An item to pack. w/h are the FOOTPRINT (caller inflates by the desired gap). canRotate lets the
// packer swap w<->h (a 90-degree turn) when that fits better.
//
// canRotateMin is what canRotate would be if the job's GLOBAL rotation setting were turned all the
// way down — i.e. false for a normal part, but still true for one whose own per-part rotation
// override allows turning regardless of the global setting. The portfolio uses it to reproduce the
// least-rotation configuration exactly; without it, "rotation off" would also strip the per-part
// overrides and the resulting pack would be a configuration the user cannot actually ask for, so
// turning the global setting up could still place fewer parts on jobs mixing locked and free ones.
struct RectToPack { double w, h; int id; bool canRotate; bool canRotateMin = false; };

// Result: item `id` placed in bin `bin` with its footprint bottom-left at (x,y); `rotated` means
// the footprint was laid out as (h x w).
struct RectPacked { int id; int bin; double x, y; bool rotated; };

// One bin's free-rectangle set with MaxRects split/prune.
class MaxRectsBin {
public:
    double W, H;
    std::vector<RectFree> free;

    MaxRectsBin(double ox, double oy, double w, double h) : W(w), H(h) {
        free.push_back({ox, oy, w, h});
        refreshCaps();
    }

    // Best-Short-Side-Fit scoring across all free rects. On success sets the placement corner
    // (bx,by), whether the item was rotated, and returns true.
    bool score(const RectToPack& it, bool& outRot, double& bx, double& by) const {
        constexpr double E = 1e-7;
        // O(1) reject for a bin that is already too full. capW/capH are the largest width and
        // height present anywhere in the free set, so an item exceeding one of them in BOTH
        // orientations provably fits in no free rect — skipping the scan cannot change the
        // result, only the time. (Full bins otherwise get rescanned for every later item.)
        if ((it.w > capW + E || it.h > capH + E) &&
            !(it.canRotate && it.h <= capW + E && it.w <= capH + E)) return false;
        double bestShort = std::numeric_limits<double>::max();
        double bestLong  = std::numeric_limits<double>::max();
        bool found = false;
        auto consider = [&](const RectFree& f, double w, double h, bool rot) {
            if (w > f.w + E || h > f.h + E) return;
            double leftH = std::fabs(f.w - w);
            double leftV = std::fabs(f.h - h);
            double shortS = std::min(leftH, leftV);
            double longS  = std::max(leftH, leftV);
            if (shortS < bestShort - E || (shortS < bestShort + E && longS < bestLong - E)) {
                bestShort = shortS; bestLong = longS;
                outRot = rot; bx = f.x; by = f.y; found = true;
            }
        };
        for (const auto& f : free) {
            consider(f, it.w, it.h, false);
            if (it.canRotate) consider(f, it.h, it.w, true);
        }
        return found;
    }

    // Place a footprint (already oriented) at (x,y): split every overlapping free rect, then prune.
    void place(double x, double y, double w, double h) {
        constexpr double E = 1e-7;
        std::vector<RectFree> added;
        for (size_t i = 0; i < free.size(); ) {
            const RectFree& f = free[i];
            bool overlaps = !(x >= f.x + f.w - E || x + w <= f.x + E ||
                              y >= f.y + f.h - E || y + h <= f.y + E);
            if (!overlaps) { ++i; continue; }
            if (x > f.x + E)                 added.push_back({f.x, f.y, x - f.x, f.h});
            if (x + w < f.x + f.w - E)       added.push_back({x + w, f.y, f.x + f.w - (x + w), f.h});
            if (y > f.y + E)                 added.push_back({f.x, f.y, f.w, y - f.y});
            if (y + h < f.y + f.h - E)       added.push_back({f.x, y + h, f.w, f.y + f.h - (y + h)});
            free.erase(free.begin() + i);
        }
        size_t nKept = free.size();
        for (auto& a : added) free.push_back(a);
        prune(nKept);
        refreshCaps();
    }

private:
    // Largest width / largest height anywhere in the free set (they may come from different
    // rects, so this is only an upper bound — sound for rejection, never for acceptance).
    double capW = 0, capH = 0;
    void refreshCaps() {
        capW = capH = 0;
        for (const auto& f : free) { capW = std::max(capW, f.w); capH = std::max(capH, f.h); }
    }

    static bool contains(const RectFree& a, const RectFree& b) {
        constexpr double E = 1e-7;
        return b.x >= a.x - E && b.y >= a.y - E &&
               b.x + b.w <= a.x + a.w + E && b.y + b.h <= a.y + a.h + E;
    }

    // Drop every free rect contained in another, restoring the "maximal rectangles" invariant.
    //
    // Incremental: free[0..nKept) are the rects that did NOT overlap the new placement, so they
    // are untouched and were already pairwise non-containing; free[nKept..) are the split pieces.
    // Every possible containment therefore involves at least one NEW rect, and checking the new
    // ones against everything is sufficient. This produces the same result set as the old
    // all-pairs scan — verified placement-for-placement identical on 1200 random packs — at a
    // fraction of the cost: profiling showed the all-pairs prune was 72-88% of total pack time on
    // 500-4000-item jobs, and replacing it cut one pack from 281 ms to 87 ms at 4000 items.
    void prune(size_t nKept) {
        for (size_t j = nKept; j < free.size(); ) {
            bool dropJ = false;
            for (size_t i = 0; i < free.size(); ++i) {
                if (i == j) continue;
                if (contains(free[i], free[j])) { dropJ = true; break; }
            }
            if (dropJ) { free.erase(free.begin() + j); continue; }
            for (size_t i = 0; i < nKept; ) {
                if (contains(free[j], free[i])) { free.erase(free.begin() + i); --j; --nKept; }
                else ++i;
            }
            ++j;
        }
    }
};

namespace detail {

// Item ordering for one pass. Both are "largest first"; they disagree on long-thin vs bulky
// items, which is where the two passes complement each other.
enum class RectOrder { MaxSide, Area };

// One complete deterministic pass, plus the summary the portfolio ranks on.
struct RectPass {
    std::vector<RectPacked> placements;
    int bins = 0;          // sheets that actually carry a part — the number the C API reports
    int placed = 0;
    double bboxArea = 0;   // sum over used bins of the layout bounding-box area (compactness)
};

inline RectPass packOnce(std::vector<RectToPack> items,
                         const std::vector<std::array<double, 4>>& binDims,
                         const std::vector<std::vector<RectFree>>* blockedPerBin,
                         RectOrder order, bool allowRotation)
{
    if (!allowRotation)
        for (auto& it : items) it.canRotate = it.canRotateMin;

    // Largest-first. `id` is the final key so the comparator is a strict total order: equal-sized
    // items then sort identically regardless of std::sort's (unspecified) handling of ties, which
    // keeps the whole fast path reproducible across platforms and standard libraries.
    std::sort(items.begin(), items.end(), [order](const RectToPack& a, const RectToPack& b) {
        if (order == RectOrder::MaxSide) {
            double am = std::max(a.w, a.h), bm = std::max(b.w, b.h);
            if (am != bm) return am > bm;
            if (a.w * a.h != b.w * b.h) return a.w * a.h > b.w * b.h;
        } else {
            if (a.w * a.h != b.w * b.h) return a.w * a.h > b.w * b.h;
            double am = std::max(a.w, a.h), bm = std::max(b.w, b.h);
            if (am != bm) return am > bm;
        }
        return a.id < b.id;
    });

    std::vector<MaxRectsBin> bins;
    std::vector<double> lox, loy, hix, hiy;   // per-bin layout bbox
    RectPass out;
    out.placements.reserve(items.size());

    for (const auto& it : items) {
        int placedBin = -1; bool rot = false; double px = 0, py = 0;
        for (size_t b = 0; b < bins.size(); ++b) {
            if (bins[b].score(it, rot, px, py)) { placedBin = (int)b; break; }
        }
        // Open further bins until the item fits. Don't stop at the first empty bin that can't take
        // it: with per-sheet voids (or differing sheet sizes) a LATER sheet may still fit it.
        while (placedBin < 0 && bins.size() < binDims.size()) {
            size_t b = bins.size();
            bins.emplace_back(binDims[b][0], binDims[b][1], binDims[b][2], binDims[b][3]);
            lox.push_back(0); loy.push_back(0); hix.push_back(0); hiy.push_back(0);
            if (blockedPerBin && b < blockedPerBin->size())
                for (const auto& v : (*blockedPerBin)[b])
                    bins[b].place(v.x, v.y, v.w, v.h);
            if (bins[b].score(it, rot, px, py)) placedBin = (int)b;
        }
        if (placedBin < 0) continue;
        double fw = rot ? it.h : it.w;
        double fh = rot ? it.w : it.h;
        bins[placedBin].place(px, py, fw, fh);
        out.placements.push_back({it.id, placedBin, px, py, rot});
        if (hix[placedBin] <= lox[placedBin]) {          // first item on this bin
            lox[placedBin] = px; loy[placedBin] = py;
            hix[placedBin] = px + fw; hiy[placedBin] = py + fh;
        } else {
            lox[placedBin] = std::min(lox[placedBin], px);
            loy[placedBin] = std::min(loy[placedBin], py);
            hix[placedBin] = std::max(hix[placedBin], px + fw);
            hiy[placedBin] = std::max(hiy[placedBin], py + fh);
        }
    }
    out.placed = (int)out.placements.size();
    // A bin counts as used only if a part actually landed on it (hix>lox is set by the first
    // placement, never by a blocked/void region), matching the C API's distinct-sheet count.
    for (size_t b = 0; b < hix.size(); b++)
        if (hix[b] > lox[b]) { out.bins++; out.bboxArea += (hix[b] - lox[b]) * (hiy[b] - loy[b]); }
    return out;
}

} // namespace detail

// Pack items into bins. binDims[i] = {originX, originY, W, H} usable region of sheet i. Items that
// fit nowhere are omitted from the result.
//
// FRAME NOTE: item w/h are FOOTPRINTS (caller-inflated by the gap), so binDims must be stated in the
// SAME footprint frame — the caller adds one gap to W and H, not zero. That is what makes k items in
// a row cost k*size + (k-1)*gap (the gaps BETWEEN them) rather than k*(size+gap) (a phantom gap past
// the last one, i.e. one gap of the sheet thrown away in each direction). Same for blocked rects
// below. nfp_nest_capi.cpp's rectFastPath spells the arithmetic out at its binDims construction.
//
// Runs a portfolio of complete MaxRects passes (see the header comment for why one pass is not
// enough) and returns the best. A pass is better when it places MORE items; ties break on FEWER
// bins, then on a tighter total layout bounding box, then on portfolio order — so the result is
// deterministic and can never be worse than the plain BSSF pass that used to be the only one.
//
// Portfolio = {largest-max-side-first, largest-area-first} x {rotation as given, rotation floor}.
// The rotation-floor half is what the "more rotation freedom never costs you" property rests on,
// but state it exactly, because the ranking above places (placed) ABOVE (bins):
//   * Guaranteed unconditionally: raising the global rotation count can never place FEWER parts.
//     A rotations>=2 job evaluates the rotations=1 packs too (see canRotateMin) and keeps them
//     unless a rotating pass places strictly more.
//   * Guaranteed only while the sheet supply is not the binding constraint (i.e. everything is
//     placed either way): then it can never use MORE sheets. Once supply binds, a rotating pass
//     that squeezes in parts the locked pass had to drop outranks it even if those parts open a
//     further sheet, so the sheet count CAN rise. Measured with this exact generator (spelled out
//     because the counts below do not reproduce from a looser description of it) — instance k in
//     [0,4000): std::mt19937(k); 1 + (k % 4) bins of 2000x1500; spacing 0; n = uniform_int(3,40)
//     parts, each side uniform_int(200,1900), so a side in (1500,1900] fits in one orientation only;
//     3897 of the 4000 are saturated (something is dropped). Each instance is packed twice, all
//     parts canRotate=false then all canRotate=true:
//        this portfolio — rotation on places FEWER parts:  0 of 4000
//        this portfolio — rotation on uses MORE sheets:  199 of 4000, and in all 199 of them the
//                         rotating pack also placed strictly MORE parts (the trade the ranking asks for)
//        old single pass — rotation on places FEWER parts:  1273 of 4000  <- the regression this replaced
//     The load-bearing claims are the 0 and the 199-of-199; the exact 199 / 1273 move if you change
//     the generator, so change the numbers with it.
// The floor passes are skipped when the two rotation settings coincide — which is every job that
// turns rotation off globally and has no per-part override.
//
// Complexity: each pass is O(n log n) to sort plus, per item, an O(B*F) scan of the open bins and
// an O(F) split with an O(A*F) prune (n items, B open bins, F free rects per bin, A <= 4 new rects
// per split) — unchanged per pass, times at most 4 passes (2 when rotation is off).
//
// COST: the incremental prune() below is a large win and the extra passes are a cost, and which
// one dominates depends on the job — the portfolio is NOT uniformly faster, and with rotation on at
// large n it is meaningfully SLOWER.
//
// This block states a BOUND plus a reproduction protocol, deliberately, instead of a point estimate.
// Three independent measurement sessions of the sweep below have now put the worst-case ratio at 1.73,
// 1.78 and 1.85 — every tight figure previously written here was refuted by the next run, because
// absolute timings are hardware-, OS- and compiler-dependent. The durable content is the MECHANISM and
// the DIRECTION; the numbers are an illustration, and the bounds are rounded pessimistically so that a
// re-measurement confirms them rather than falsifies them. Re-derive with the protocol, don't trust.
//
// MECHANISM (this is the load-bearing part):
//   * prune() is incremental, and what it saves grows with n — it pays for itself outright.
//   * The portfolio multiplies the whole pack: 2 complete passes with rotation off, 4 with rotation on.
//   * The per-item O(B*F) scan over OPEN BINS is the one cost prune() does NOT reduce, and it grows with
//     the bin count. The more sheets a job opens, the more that pass multiplier costs.
//   => rotation OFF (2 passes): prune() dominates at every n measured — the portfolio is FASTER, always.
//   => rotation ON (4 passes): prune() roughly pays for the extra passes through the middle of the range;
//      the open-bin scan takes over once many sheets are open and the portfolio becomes slower. The
//      crossover sits around n ~ 2000 / ~11 bins, and the cost grows with n from there.
//
// BOUNDS (conservative; hold across every session so far):
//   * rotation off: never slower than the single pass, at any n measured.
//   * rotation on, up to ~1000 parts: never worse than ~1.6x, and usually faster.
//   * rotation on, at the largest size measured (8000 parts / ~41 bins): up to ~2x slower. That is the
//     worst anywhere in the sweep, in any session.
//   * Only that last case can be felt: roughly half a second becomes roughly a second (across the three
//     sessions, old 0.49-0.57 s -> new 0.83-0.97 s). The n=100 rotation-on ratio looks just as bad
//     (~1.3-1.5x) but is ~1.4 ms -> ~1.9 ms, i.e. nothing.
//
// PROTOCOL. Take the pre-portfolio single-BSSF-pass packer — the revision of this file immediately
// before the portfolio landed (`git log --follow -- src/opennest_cpp/src/RectPack.h`, then
// `git show <rev>:src/opennest_cpp/src/RectPack.h`) — rename its namespace to nest_old and compile it
// into the SAME translation unit as this header, /O2 /std:c++17. Instances: unique random rects, INTEGER
// sides uniform 60..420 with the spacing of 5 added to each side, 80 bins of 4000x3000, seeds 1/42/777,
// n in {100,200,500,1000,2000,4000,8000}, best of 5 reps, each instance packed twice (all canRotate=false,
// then all canRotate=true). Run BOTH timing orders (old-first, new-first) as SEPARATE PROCESSES so
// cache/allocator warm-up cannot bias one. Ratios old -> this portfolio (>1 = slower), one session on one
// machine (MSVC 19.44 /O2), min..max over 3 seeds x 2 orders x 2 repeats = 12 samples per cell:
//        n:        100         200         500        1000        2000        4000        8000
//   rot off: 0.63-0.87   0.38-0.48   0.42-0.47   0.41-0.48   0.52-0.55   0.59-0.61   0.82-0.90  (2 passes)
//   rot on : 1.27-1.45   0.78-1.11   0.87-0.94   0.71-0.93   0.99-1.02   1.23-1.27   1.53-1.73  (4 passes)
// Expect your own absolute ratios to differ; what must reproduce is the shape — every rot-off cell below
// 1.0, the rot-on crossover near n=2000, and monotone growth of the rot-on ratio past it.
//
// GENERATOR EQUIVALENCE, so a re-run can tell "different timings" from "different instances": the two
// packers must place the SAME number of parts in all 42 cells, and tie on bin count in 41 of them. The
// single exception is the fingerprint — n=4000, seed 1, rotation off, where the portfolio uses 21 bins to
// the single pass's 22. n=8000 runs at 42 bins with rotation off and 41 with it on. If those don't match,
// the generator is not this one and the ratios are not comparable.
//
// That density is what the time buys: the old pass is IN the portfolio (MaxSide + rotation as given), so
// the result is never worse under the ranking above, and it is sometimes a sheet better (that 22 -> 21).
// If the rotation-on cost at 4000+ ever matters, the lever is skipping a portfolio pass that
// provably cannot win for the job at hand, NOT reverting to a single pass.
// The bench's own rectangle datasets (multisheet 24 parts, rects 28) all run in ~0.2 ms and are
// far too small to catch a packer slowdown — use `manyrects` (600) when changing this file.
//
// blockedPerBin (optional): per-bin keep-out rectangles, in the same frame as that bin's binDims
// entry (sheet-absolute for the only caller — see RectFree), applied when the
// bin is OPENED — each is `place()`d like a pre-placed item, so the free-rectangle split/prune
// carves the usable region around it. This is how rectangular SHEET VOIDS (defects) ride the same
// fast path: sheet minus voids = the maximal-free-rectangle decomposition MaxRects maintains anyway.
inline std::vector<RectPacked> packMaxRects(
        std::vector<RectToPack> items,
        const std::vector<std::array<double, 4>>& binDims,
        const std::vector<std::vector<RectFree>>* blockedPerBin = nullptr)
{
    using detail::RectOrder;
    const RectOrder orders[2] = {RectOrder::MaxSide, RectOrder::Area};

    // The rotation-floor passes only differ from the as-given passes when the global setting is
    // actually unlocking something; otherwise they would duplicate work for no gain.
    bool floorDiffers = false;
    for (const auto& it : items) if (it.canRotate != it.canRotateMin) { floorDiffers = true; break; }

    detail::RectPass best;
    bool haveBest = false;
    // rotPass 0 = rotation as the caller asked, 1 = rotation at its floor (skipped when identical).
    for (int rotPass = 0; rotPass < (floorDiffers ? 2 : 1); ++rotPass) {
        for (RectOrder order : orders) {
            detail::RectPass cand =
                detail::packOnce(items, binDims, blockedPerBin, order, rotPass == 0);
            bool better = !haveBest ||
                cand.placed > best.placed ||
                (cand.placed == best.placed && cand.bins < best.bins) ||
                (cand.placed == best.placed && cand.bins == best.bins &&
                 cand.bboxArea < best.bboxArea - 1e-6);
            if (better) { best = std::move(cand); haveBest = true; }
        }
    }
    return best.placements;
}

} // namespace nest
