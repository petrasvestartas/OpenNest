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

#include <vector>
#include <array>
#include <cmath>
#include <limits>
#include <algorithm>

namespace nest {

// A free rectangle inside a bin (bin-local coordinates).
struct RectFree { double x, y, w, h; };

// An item to pack. w/h are the FOOTPRINT (caller inflates by the desired gap). canRotate lets the
// packer swap w<->h (a 90-degree turn) when that fits better.
struct RectToPack { double w, h; int id; bool canRotate; };

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
    }

    // Best-Short-Side-Fit scoring across all free rects. On success sets the placement corner
    // (bx,by), whether the item was rotated, and returns true.
    bool score(const RectToPack& it, bool& outRot, double& bx, double& by) const {
        constexpr double E = 1e-7;
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
        for (auto& a : added) free.push_back(a);
        prune();
    }

private:
    static bool contains(const RectFree& a, const RectFree& b) {
        constexpr double E = 1e-7;
        return b.x >= a.x - E && b.y >= a.y - E &&
               b.x + b.w <= a.x + a.w + E && b.y + b.h <= a.y + a.h + E;
    }
    void prune() {
        for (size_t i = 0; i < free.size(); ++i) {
            for (size_t j = i + 1; j < free.size(); ) {
                if (contains(free[i], free[j])) { free.erase(free.begin() + j); }
                else if (contains(free[j], free[i])) { free.erase(free.begin() + i); --i; break; }
                else { ++j; }
            }
        }
    }
};

// Pack items into bins. binDims[i] = {originX, originY, W, H} usable region of sheet i. Items are
// sorted largest-first, then each is placed into the FIRST bin it fits (BSSF within that bin), so
// early sheets fill up before new ones open (minimizing sheet count). Items that fit nowhere are
// omitted from the result.
//
// blockedPerBin (optional): per-bin keep-out rectangles (bin-local coordinates) applied when the
// bin is OPENED — each is `place()`d like a pre-placed item, so the free-rectangle split/prune
// carves the usable region around it. This is how rectangular SHEET VOIDS (defects) ride the same
// fast path: sheet minus voids = the maximal-free-rectangle decomposition MaxRects maintains anyway.
inline std::vector<RectPacked> packMaxRects(
        std::vector<RectToPack> items,
        const std::vector<std::array<double, 4>>& binDims,
        const std::vector<std::vector<RectFree>>* blockedPerBin = nullptr)
{
    std::sort(items.begin(), items.end(), [](const RectToPack& a, const RectToPack& b) {
        double am = std::max(a.w, a.h), bm = std::max(b.w, b.h);
        if (am != bm) return am > bm;
        return a.w * a.h > b.w * b.h;
    });

    std::vector<MaxRectsBin> bins;
    std::vector<RectPacked> out;
    out.reserve(items.size());

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
            if (blockedPerBin && b < blockedPerBin->size())
                for (const auto& v : (*blockedPerBin)[b])
                    bins[b].place(v.x, v.y, v.w, v.h);
            if (bins[b].score(it, rot, px, py)) placedBin = (int)b;
        }
        if (placedBin < 0) continue;
        double fw = rot ? it.h : it.w;
        double fh = rot ? it.w : it.h;
        bins[placedBin].place(px, py, fw, fh);
        out.push_back({it.id, placedBin, px, py, rot});
    }
    return out;
}

} // namespace nest
