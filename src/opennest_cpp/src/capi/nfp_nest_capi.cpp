#include "capi/nfp_nest_capi.h"

#include "NestingContext.h"
#include "NestingEngine.h"
#include "NfpWorker.h"
#include "NFP.h"
#include "GeometryUtil.h"
#include "RectPack.h"

#include <vector>
#include <memory>
#include <atomic>
#include <mutex>
#include <chrono>
#include <algorithm>
#include <set>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <functional>

using namespace nest;

namespace {

// ---- cross-thread state for cancel / progress / live preview ----------------
std::atomic<bool>       g_cancel{false};
std::atomic<long long>  g_progress{0};
std::atomic<double>     g_fitness{0.0};

struct Snapshot {
    std::mutex mtx;
    int placed = 0;
    int nSheets = 0;
    std::vector<double> tx, ty, angle;
    std::vector<int>    sheetId, partIndex;
};
Snapshot g_snap;

NestConfig toConfig(const NfpParams* p) {
    NestConfig c;
    c.placementType  = static_cast<PlacementTypeEnum>(p->placementType);
    c.rotations      = p->rotations > 0 ? p->rotations : 1;
    c.mutationRate   = p->mutationRate;
    c.populationSize = p->populationSize > 0 ? p->populationSize : 1;
    c.seed           = p->seed;
    c.curveTolerance = p->curveTolerance;
    c.clipperScale   = p->clipperScale > 0 ? p->clipperScale : 1e7;
    c.spacing        = p->spacing;
    c.sheetSpacing   = p->sheetSpacing;
    c.simplify       = p->simplify != 0;
    // (rotationLimit / exploreConcave / clipByHull / clipByRects remain in the C ABI for
    // compatibility but map to nothing: their engine paths were dead code and removed.)
    c.faithful       = (p->mode == 0);   // mode 0 = faithful parity with the C# engine
    c.exactNfp       = (p->exactNfp != 0);
    // Dedicated opt-in: exact Minkowski void/hole exclusion. Off => fast convex-hull approximation
    // (unchanged default behavior); on => tight packing around non-convex voids (identical for
    // convex voids). Kept separate from useHoles (which remains a no-op) so enabling hole nesting
    // does not silently change existing layouts.
    c.exactVoids     = (p->exactVoids != 0);
    c.parallelPopulation = (p->mode == 1) && (p->useParallel != 0);
    if (c.faithful) {
        // The canonical C# Background.placeParts has NO compaction / edge-sampling — turn the
        // C++ additions off so faithful mode mirrors it.
        c.compactionPasses = 0;
        c.edgeSamples = 0;
        c.tryAllRotations = false;
    } else {
        // Placement-cost knobs: -1 keeps the NestConfig default; >=0 overrides (0 = off, faster).
        if (p->edgeSamples >= 0)      c.edgeSamples = p->edgeSamples;
        if (p->compactionPasses >= 0) c.compactionPasses = p->compactionPasses;
        if (p->tryAllRotations >= 0)  c.tryAllRotations = p->tryAllRotations != 0;
    }
    return c;
}

// Build the part/sheet NFPs into the context from the flat C arrays. Returns the
// instance->part-index map (one entry per emitted instance, expansion order).
std::vector<int> buildInputs(
    NestingContext& ctx,
    int part_count, const int* pvc, const double* pxy, const int* pqty,
    const int* prot,
    const int* phc, const int* phvc, const double* phxy,
    int sheet_count, const int* svc, const double* sxy,
    const int* shc, const int* shvc, const double* shxy)
{
    std::vector<int> instancePart;

    // --- parts ---
    size_t xyCur = 0;     // cursor into pxy
    size_t holeIdx = 0;   // running hole index into phvc
    size_t holeXy = 0;    // cursor into phxy
    for (int i = 0; i < part_count; i++) {
        int nv = pvc[i];
        // capture this part's outer points
        std::vector<Point> outer;
        outer.reserve(nv);
        for (int k = 0; k < nv; k++)
            outer.push_back(Point(pxy[xyCur + 2*k], pxy[xyCur + 2*k + 1]));
        xyCur += static_cast<size_t>(nv) * 2;

        // capture this part's holes
        int nHoles = phc ? phc[i] : 0;
        std::vector<std::vector<Point>> holes;
        for (int h = 0; h < nHoles; h++) {
            int hv = phvc[holeIdx++];
            std::vector<Point> hp;
            hp.reserve(hv);
            for (int k = 0; k < hv; k++)
                hp.push_back(Point(phxy[holeXy + 2*k], phxy[holeXy + 2*k + 1]));
            holeXy += static_cast<size_t>(hv) * 2;
            holes.push_back(std::move(hp));
        }

        int q = (pqty && pqty[i] > 0) ? pqty[i] : 1;
        for (int c = 0; c < q; c++) {
            auto poly = std::make_shared<NFP>();
            // Id = index in ctx.Polygons / ctx.Sheets, the SAME numbering NestingContext::init()
            // assigns. init() only runs on the first NestIterate, so any path that returns before
            // the GA starts (the rectangle fast path) would otherwise read the default Id 0 for
            // EVERY polygon and sheet — and readOutputs reports poly->sheet->Id as out_sheet_id,
            // so an all-rectangle job came back with every part on sheet 0 (multi-sheet layouts
            // collapsed onto one sheet, stacked). Number them here, where they are created.
            poly->Id = static_cast<int>(ctx.Polygons.size());
            poly->source = i;
            poly->rotationCount = (prot && prot[i] > 0) ? prot[i] : 0;   // per-part rotation override
            poly->Points = outer;
            for (auto& hp : holes) {
                auto child = std::make_shared<NFP>();
                child->Points = hp;
                poly->children.push_back(child);
            }
            ctx.Polygons.push_back(poly);
            instancePart.push_back(i);
        }
    }

    // --- sheets ---
    size_t sxyCur = 0, shIdx = 0, shXy = 0;
    for (int i = 0; i < sheet_count; i++) {
        int nv = svc[i];
        auto sheet = std::make_shared<NFP>();
        sheet->Id = i;          // see the part loop above: out_sheet_id IS this Id
        sheet->source = i;
        for (int k = 0; k < nv; k++)
            sheet->AddPoint(Point(sxy[sxyCur + 2*k], sxy[sxyCur + 2*k + 1]));
        sxyCur += static_cast<size_t>(nv) * 2;

        int nHoles = shc ? shc[i] : 0;
        for (int h = 0; h < nHoles; h++) {
            int hv = shvc[shIdx++];
            auto child = std::make_shared<NFP>();
            for (int k = 0; k < hv; k++)
                child->AddPoint(Point(shxy[shXy + 2*k], shxy[shXy + 2*k + 1]));
            shXy += static_cast<size_t>(hv) * 2;
            sheet->children.push_back(child);
        }
        ctx.Sheets.push_back(sheet);
    }
    ctx.ReorderSheets();

    return instancePart;
}

// Read placement from a solved context into caller output arrays.
// Returns the number of placed instances; sets nSheetsOut.
// Positions are SHEET-RELATIVE as documented: `poly->x - sheet->x` removes the internal layout
// shift, and the sheet points' own bbox-min is subtracted too — the engine solves in the frame the
// sheet outline was PASSED in, so a caller providing a non-origin sheet otherwise got positions
// carrying that offset (they read as "parts out of the sheet" to any harness that interprets the
// documented contract; our C# wrapper masked this by normalizing sheets to origin before calling).
int readOutputs(const NestingContext& rc, const std::vector<int>& instancePart,
                double* tx, double* ty, double* angle,
                int* sheetId, int* partIndex, int* nSheetsOut, double* fitnessOut)
{
    int placed = 0;
    std::set<int> usedSheets;
    size_t n = rc.Polygons.size();
    for (size_t k = 0; k < n; k++) {
        const auto& poly = rc.Polygons[k];
        partIndex[k] = (k < instancePart.size()) ? instancePart[k] : (poly->source.value_or(-1));
        if (poly->fitted() && poly->sheet) {
            int sid = poly->sheet->Id;
            auto sb = GeometryUtil::getPolygonBounds(*poly->sheet);
            sheetId[k] = sid;
            tx[k] = poly->x - poly->sheet->x - sb.x;
            ty[k] = poly->y - poly->sheet->y - sb.y;
            angle[k] = poly->Rotation;
            usedSheets.insert(sid);
            placed++;
        } else {
            sheetId[k] = -1;
            tx[k] = 0; ty[k] = 0; angle[k] = 0;
        }
    }
    if (nSheetsOut) *nSheetsOut = static_cast<int>(usedSheets.size());
    if (fitnessOut) *fitnessOut = rc.HasCurrent() ? rc.Current().fitness.value_or(0.0) : 0.0;
    return placed;
}

// Update the live-preview snapshot from the current best layout.
void updateSnapshot(const NestingContext& rc, const std::vector<int>& instancePart) {
    std::lock_guard<std::mutex> lock(g_snap.mtx);
    size_t n = rc.Polygons.size();
    g_snap.tx.assign(n, 0); g_snap.ty.assign(n, 0); g_snap.angle.assign(n, 0);
    g_snap.sheetId.assign(n, -1); g_snap.partIndex.assign(n, -1);
    std::set<int> used;
    int placed = 0;
    for (size_t k = 0; k < n; k++) {
        const auto& poly = rc.Polygons[k];
        g_snap.partIndex[k] = (k < instancePart.size()) ? instancePart[k] : poly->source.value_or(-1);
        if (poly->fitted() && poly->sheet) {
            auto sb = GeometryUtil::getPolygonBounds(*poly->sheet);   // sheet-relative, same as readOutputs
            g_snap.sheetId[k] = poly->sheet->Id;
            g_snap.tx[k] = poly->x - poly->sheet->x - sb.x;
            g_snap.ty[k] = poly->y - poly->sheet->y - sb.y;
            g_snap.angle[k] = poly->Rotation;
            used.insert(poly->sheet->Id);
            placed++;
        }
    }
    g_snap.placed = placed;
    g_snap.nSheets = static_cast<int>(used.size());
}

// Rectangle fast path -------------------------------------------------------
// When every part is an axis-aligned rectangle with no holes and every sheet is an axis-aligned
// rectangle whose voids (if any) are ALSO axis-aligned rectangles, the general NFP+GA solve is
// overkill: place with MaxRects (deterministic, sub-millisecond), treating each rect void as a
// pre-blocked region. Returns the placed-instance count if it handled the job, or -1 to fall
// through to the genetic-algorithm path (non-rect part/sheet/void, or part holes).
int rectFastPath(NestingContext& ctx, const NestConfig& cfg, const NfpParams* params,
                 const std::vector<int>& instancePart,
                 double* out_tx, double* out_ty, double* out_angle,
                 int* out_sheet_id, int* out_part_index, int* out_n_sheets, double* out_fitness)
{
    if (std::getenv("NFP_NO_RECT_FASTPATH") != nullptr) return -1;
    if (ctx.Polygons.empty() || ctx.Sheets.empty()) return -1;

    // Outline-only rectangle test (children checked separately: parts must have NONE, sheets may
    // carry axis-aligned rectangular VOIDS — a defect region is just a pre-blocked area in MaxRects).
    auto isAxisRectOutline = [](const NFP& p) -> bool {
        int n = p.length();
        if (n == 5 && std::fabs(p[0].x - p[4].x) < 1e-9 && std::fabs(p[0].y - p[4].y) < 1e-9) n = 4;
        if (n != 4) return false;
        auto b = GeometryUtil::getPolygonBounds(p);
        if (b.width <= 1e-9 || b.height <= 1e-9) return false;
        // A simple quad whose area equals its bbox area AND whose vertices sit on the bbox corners
        // is exactly the axis-aligned bounding rectangle (a rotated quad has strictly smaller area).
        double area = std::fabs(GeometryUtil::polygonArea(p));
        if (std::fabs(area - b.width * b.height) > 1e-6 * b.width * b.height) return false;
        for (int i = 0; i < 4; i++) {
            bool onX = std::fabs(p[i].x - b.x) < 1e-6 || std::fabs(p[i].x - (b.x + b.width)) < 1e-6;
            bool onY = std::fabs(p[i].y - b.y) < 1e-6 || std::fabs(p[i].y - (b.y + b.height)) < 1e-6;
            if (!onX || !onY) return false;
        }
        return true;
    };
    for (const auto& p : ctx.Polygons)
        if (!p->children.empty() || !isAxisRectOutline(*p)) return -1;   // part holes => GA path
    for (const auto& s : ctx.Sheets) {
        if (!isAxisRectOutline(*s)) return -1;
        for (const auto& c : s->children)
            if (!c || !isAxisRectOutline(*c)) return -1;                 // non-rect void => GA path
    }

    const double spacing = cfg.spacing;
    const double sheetSpacing = cfg.sheetSpacing;
    const int maxSheets = (params->maxSheets > 0)
        ? std::min(params->maxSheets, (int)ctx.Sheets.size()) : (int)ctx.Sheets.size();

    std::vector<std::array<double, 4>> binDims;   // {originX, originY, W, H} per usable sheet
    std::vector<std::vector<RectFree>> blocked;   // per sheet: rect voids (defects), inflated by spacing
    binDims.reserve(maxSheets);
    blocked.resize(maxSheets);
    for (int i = 0; i < maxSheets; i++) {
        auto b = GeometryUtil::getPolygonBounds(*ctx.Sheets[i]);
        // FOOTPRINT FRAME. Every part is packed as (w + spacing) x (h + spacing) — the gap rides
        // on its top and right edge only (see `items` below) — so the usable region must carry the
        // SAME +spacing or the sheet silently loses one whole gap of width and height. With it,
        // k parts in a row need k*(w+spacing) <= usable+spacing, i.e. k*w + (k-1)*spacing <= usable:
        // k parts and the k-1 gaps BETWEEN them, with no phantom gap past the last one.
        //
        // This is exactly the convention the GA path uses — NestingContext::init() offsets each part
        // OUT by spacing/2 (footprint w+spacing) and each sheet IN by (sheetSpacing - spacing/2),
        // which admits real part positions in [sheetSpacing, W - sheetSpacing]. Without the
        // +spacing here the fast path admitted only [sheetSpacing, W - sheetSpacing - spacing] and
        // so under-filled every sheet relative to the GA whenever spacing > 0.
        double usableW = b.width  - 2 * sheetSpacing;
        double usableH = b.height - 2 * sheetSpacing;
        if (usableW <= 0 || usableH <= 0) usableW = usableH = 0;   // sheetSpacing ate the sheet
        else { usableW += spacing; usableH += spacing; }
        binDims.push_back({ b.x + sheetSpacing, b.y + sheetSpacing, usableW, usableH });
        // Each rect void becomes a pre-blocked region, sized in the same footprint frame: a void
        // occupying [vx, vx+vw] blocks exactly [vx, vx+vw+spacing]. A part footprint ending at vx
        // has its real body ending at vx-spacing, and one starting at vx+vw+spacing starts a full
        // `spacing` clear of the void — the gap is kept on BOTH sides with no extra loss.
        for (const auto& c : ctx.Sheets[i]->children) {
            auto vb = GeometryUtil::getPolygonBounds(*c);
            blocked[i].push_back({ vb.x, vb.y, vb.width + spacing, vb.height + spacing });
        }
    }

    // Per-part bbox origin + dims; footprint inflated by `spacing` so neighbours keep the gap.
    struct PInfo { double bx, by, w, h; };
    std::vector<PInfo> pinfo(ctx.Polygons.size());
    std::vector<RectToPack> items;
    items.reserve(ctx.Polygons.size());
    for (int k = 0; k < (int)ctx.Polygons.size(); k++) {
        const auto& p = *ctx.Polygons[k];
        auto b = GeometryUtil::getPolygonBounds(p);
        pinfo[k] = { b.x, b.y, b.width, b.height };
        int rc = p.rotationCount > 0 ? p.rotationCount : cfg.rotations;   // per-part override
        // canRotate = may this part turn under the CURRENT settings; canRotateMin = may it turn
        // even with the global rotation count at its lowest (i.e. its own override says so). The
        // packer needs both so its rotation-floor passes reproduce the exact layout the user would
        // get by turning rotation off, and "more rotation" can never place FEWER parts. (It can
        // still use more sheets when the sheet supply runs out — see the guarantee spelled out
        // above packMaxRects in RectPack.h; the extra sheets always carry extra parts.)
        items.push_back({ b.width + spacing, b.height + spacing, k, rc >= 2, p.rotationCount >= 2 });
    }

    auto placed = packMaxRects(items, binDims, blocked.empty() ? nullptr : &blocked);

    for (auto& p : ctx.Polygons) p->sheet = nullptr;   // reset; anything unplaced stays sheet==null
    for (const auto& pk : placed) {
        const auto& info = pinfo[pk.id];
        NFP* sheet = ctx.Sheets[pk.bin].get();
        // Rotated-bbox min corner in the part's rotate-about-origin frame:
        //   0deg -> (bx, by)     90deg -> (-(by+h), bx)
        double rminx = pk.rotated ? -(info.by + info.h) : info.bx;
        double rminy = pk.rotated ?  info.bx            : info.by;
        // Translate so the rotated rect's min corner lands at the footprint corner (pk.x, pk.y).
        double tx = pk.x - rminx;
        double ty = pk.y - rminy;
        auto& poly = ctx.Polygons[pk.id];
        poly->sheet = sheet;
        poly->x = tx + sheet->x;    // AssignPlacement convention: sheet-local translation + sheet origin
        poly->y = ty + sheet->y;
        poly->Rotation = pk.rotated ? 90.0f : 0.0f;
    }

    return readOutputs(ctx, instancePart, out_tx, out_ty, out_angle,
                       out_sheet_id, out_part_index, out_n_sheets, out_fitness);
}

} // namespace

// ============================================================================

NFP_API int nfp_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_quantities,
    const int*    part_rotations,
    const int*    part_hole_counts,
    const int*    part_hole_vertex_counts,
    const double* part_hole_xy,
    int           sheet_count,
    const int*    sheet_vertex_counts,
    const double* sheet_xy,
    const int*    sheet_hole_counts,
    const int*    sheet_hole_vertex_counts,
    const double* sheet_hole_xy,
    const NfpParams* params,
    double*       out_tx,
    double*       out_ty,
    double*       out_angle,
    int*          out_sheet_id,
    int*          out_part_index,
    int*          out_n_sheets,
    double*       out_fitness)
{
    if (!params || part_count <= 0 || sheet_count <= 0) return -1;

    g_cancel = false;
    g_progress = 0;
    g_fitness = 0.0;
    { std::lock_guard<std::mutex> lock(g_snap.mtx);
      g_snap.placed = 0; g_snap.nSheets = 0;
      g_snap.tx.clear(); g_snap.ty.clear(); g_snap.angle.clear();
      g_snap.sheetId.clear(); g_snap.partIndex.clear(); }

    // In-generation budget/cancel enforcement: arm NfpWorker's deadline so the heavy loops
    // (NFP pair precompute, placeParts, compaction) can stop MID-generation. Previously the
    // budget/cancel was only checked between generations, so a heavy first generation
    // (high-vertex exact NFP) overran a 5 s budget by 10x+ and nfp_cancel() couldn't stop it.
    // The guard clears the deadline + abort flag on every return path.
    NfpWorker::Abort = false;
    if (params->timeBudgetSecs > 0) {
        long long now = std::chrono::duration_cast<std::chrono::milliseconds>(
                            std::chrono::steady_clock::now().time_since_epoch()).count();
        NfpWorker::DeadlineSteadyMs = now + static_cast<long long>(params->timeBudgetSecs * 1000.0);
    } else {
        NfpWorker::DeadlineSteadyMs = 0;
    }
    struct DeadlineGuard {
        ~DeadlineGuard() { NfpWorker::DeadlineSteadyMs = 0; NfpWorker::Abort = false; }
    } deadlineGuard;

    NestConfig cfg = toConfig(params);
    const int mode = params->mode;

    // stagnationGens semantics: >0 explicit; -1 never early-exit; 0 = AUTO — defaults to 30
    // when a time budget is set, so "converged = stop" is the default and the reported solve
    // time is the real convergence time (external harnesses that zero the struct otherwise
    // burn the whole budget and benchmark the timer, not the solver). Fixed-generation mode
    // (timeBudgetSecs == 0, stagnationGens == 0) is unchanged.
    int effStagnation = params->stagnationGens;
    if (effStagnation == 0 && params->timeBudgetSecs > 0) effStagnation = 30;
    if (effStagnation < 0) effStagnation = 0;

    // Build inputs into a context.
    NestingContext ctx;
    ctx.config = cfg;
    auto instancePart = buildInputs(
        ctx, part_count, part_vertex_counts, part_xy, part_quantities,
        part_rotations,
        part_hole_counts, part_hole_vertex_counts, part_hole_xy,
        sheet_count, sheet_vertex_counts, sheet_xy,
        sheet_hole_counts, sheet_hole_vertex_counts, sheet_hole_xy);

    const long pop  = std::max(1, cfg.populationSize);
    const long gens = std::max(1, params->generations);
    const long totalSteps = gens * pop;

    NfpWorker::UseParallel = (mode == 0) ? false : (params->useParallel != 0);

    // Rectangle fast path: an all-axis-aligned-rectangle job (parts + sheets, no holes) is packed
    // deterministically with MaxRects, skipping the NFP/GA entirely. Any non-rectangle input makes
    // this return -1 and fall through to the genetic algorithm unchanged.
    {
        int fastRc = rectFastPath(ctx, cfg, params, instancePart,
                                  out_tx, out_ty, out_angle, out_sheet_id, out_part_index,
                                  out_n_sheets, out_fitness);
        if (fastRc >= 0) return fastRc;
    }

    // Trivial short-circuit: a single instance has no placement order to optimize, and one
    // placement pass already evaluates every rotation (tryAllRotations), so the full GA / turbo
    // just re-does identical work for the whole budget. Place it once and return. (Skipped in
    // faithful mode, where tryAllRotations is off and rotation is searched across generations.)
    if (static_cast<int>(ctx.Polygons.size()) <= 1 && cfg.tryAllRotations) {
        ctx.StartNest();
        ctx.NestIterate(1);
        updateSnapshot(ctx, instancePart);
        return readOutputs(ctx, instancePart, out_tx, out_ty, out_angle,
                           out_sheet_id, out_part_index, out_n_sheets, out_fitness);
    }

    if (mode == 2) {
        // Turbo: independent parallel seeds, keep best. Honor the time budget, cooperative cancel,
        // and stagnation early-exit inside the seed loops (previously it ran a hard iteration count
        // that ignored --timeBudget and nfp_cancel()).
        int numSeeds = params->numSeeds > 0 ? params->numSeeds : 4;
        auto tStart = std::chrono::steady_clock::now();
        const double budget = params->timeBudgetSecs;
        auto shouldStop = [tStart, budget]() -> bool {
            if (g_cancel.load()) return true;
            if (budget > 0) {
                double el = std::chrono::duration<double>(
                    std::chrono::steady_clock::now() - tStart).count();
                if (el >= budget) return true;
            }
            return false;
        };
        auto best = NestingContext::RunParallelSeeds(
            ctx.Polygons, ctx.Sheets, cfg, numSeeds, static_cast<int>(totalSteps),
            [&](int /*seed*/, int iter, const NestingContext& c) {
                if (iter > g_progress) g_progress = iter;
                if (c.HasCurrent()) g_fitness = c.Current().fitness.value_or(0.0);
            },
            shouldStop, effStagnation);
        updateSnapshot(best, instancePart);
        return readOutputs(best, instancePart, out_tx, out_ty, out_angle,
                           out_sheet_id, out_part_index, out_n_sheets, out_fitness);
    }

    // Faithful (0) / default (1): sequential GA loop with a SHARED NFP cache. One candidate per step;
    // candidate 0 warms the cache (its NFP precompute is internally parallel via pmapDeepNest) and the
    // rest reuse it. (Evaluating candidates on isolated per-thread workers was measurably SLOWER — it
    // duplicates NFP work 1-per-candidate and oversubscribes threads — so we keep the shared-cache path.)
    ctx.StartNest();
    const bool timed = params->timeBudgetSecs > 0;
    const bool profile = std::getenv("NFP_PROFILE") != nullptr;
    // Generation-parallel path: evaluate each GA generation's candidates concurrently on a shared
    // NFP cache (mode 1 / default + useParallel). One iteration = one whole generation. This is the
    // big first-run speedup for exact mode; it is result-equivalent to the serial loop.
    const bool genParallel = (mode == 1) && (params->useParallel != 0)
                             && (std::getenv("NFP_FORCE_SERIAL") == nullptr);
    long long placeMsTotal = 0;
    auto t0 = std::chrono::steady_clock::now();

    // Stagnation early-exit: once the best fitness has not improved for `stagnationGens`
    // generations AND every instance is placed, further generations only re-spend the budget.
    // This makes the reported solve time the real convergence time (see effStagnation above:
    // 0 = auto-30 for time-budgeted runs, -1 = never, >0 explicit).
    const int stagnationGens = effStagnation;
    const int totalInstances = static_cast<int>(ctx.Polygons.size());
    double bestFit = std::numeric_limits<double>::infinity();
    int    stagnant = 0;
    auto placedCount = [&ctx]() {
        int n = 0;
        for (const auto& poly : ctx.Polygons)
            if (poly->fitted() && poly->sheet) ++n;
        return n;
    };
    // Returns true when the solve should stop. `f` is the best fitness this generation
    // (infinity if no placement exists yet). Updates bestFit/stagnant as a side effect.
    auto stagnationBreak = [&](double f) -> bool {
        if (stagnationGens <= 0) return false;
        if (f + 1e-9 < bestFit) { bestFit = f; stagnant = 0; }
        else ++stagnant;
        return stagnant >= stagnationGens && placedCount() == totalInstances;
    };

    if (genParallel) {
        for (long gen = 1; ; gen++) {
            if (g_cancel.load()) break;
            if (timed) {
                double el = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
                if (el >= params->timeBudgetSecs) break;
            } else if (gen > gens) {
                break;
            }
            ctx.NestIterateGeneration();
            g_progress = gen;
            double f = bestFit;
            if (ctx.HasCurrent()) { f = ctx.Current().fitness.value_or(bestFit); g_fitness = f; }
            updateSnapshot(ctx, instancePart);
            if (stagnationBreak(f)) break;
        }
    } else {
        long lastGen = 0;
        for (long step = 1; ; step++) {
            if (g_cancel.load()) break;
            if (timed) {
                double el = std::chrono::duration<double>(std::chrono::steady_clock::now() - t0).count();
                if (el >= params->timeBudgetSecs) break;
            } else if (step > totalSteps) {
                break;
            }
            ctx.NestIterate(1);
            if (profile) placeMsTotal += ctx.Nest.background.LastPlacePartTime;
            long genNow = (step - 1) / pop + 1;
            g_progress = genNow;
            double f = bestFit;
            if (ctx.HasCurrent()) { f = ctx.Current().fitness.value_or(bestFit); g_fitness = f; }
            updateSnapshot(ctx, instancePart);
            // Evaluate stagnation once per generation so `stagnationGens` counts generations
            // (not candidate steps) consistently with the gen-parallel path.
            if (genNow != lastGen) { lastGen = genNow; if (stagnationBreak(f)) break; }
        }
    }
    if (profile) {
        double wallMs = std::chrono::duration<double, std::milli>(
            std::chrono::steady_clock::now() - t0).count();
        std::fprintf(stderr,
            "[NFP_PROFILE] wall=%.0fms  placeParts_total=%lldms (%.0f%%)  nfp_computes=%d  mode=%s\n",
            wallMs, placeMsTotal, 100.0 * placeMsTotal / (wallMs > 0 ? wallMs : 1),
            ctx.Nest.background.callCounter.load(), genParallel ? "gen-parallel" : "serial");
    }

    return readOutputs(ctx, instancePart, out_tx, out_ty, out_angle,
                       out_sheet_id, out_part_index, out_n_sheets, out_fitness);
}

NFP_API int nfp_pack(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_quantities,
    int           columns,
    double        gap_x,
    double        gap_y,
    double        max_width,
    double*       out_tx,
    double*       out_ty,
    double*       out_angle,
    int*          out_sheet_id)
{
    if (part_count <= 0 || !part_vertex_counts || !part_xy ||
        !out_tx || !out_ty || !out_angle || !out_sheet_id) return -1;
    if (columns < 1) columns = 1;
    const bool distanceMode = max_width > 0;

    double x = 0, y = 0, rowH = 0;
    int col = 0, k = 0;
    size_t cur = 0;
    for (int i = 0; i < part_count; i++) {
        const int nv = part_vertex_counts[i];
        double minx = 0, miny = 0, maxx = 0, maxy = 0;
        for (int v = 0; v < nv; v++) {
            const double px = part_xy[cur + 2 * v], py = part_xy[cur + 2 * v + 1];
            if (v == 0) { minx = maxx = px; miny = maxy = py; }
            else {
                minx = std::min(minx, px); maxx = std::max(maxx, px);
                miny = std::min(miny, py); maxy = std::max(maxy, py);
            }
        }
        cur += static_cast<size_t>(nv) * 2;
        const double w = maxx - minx, h = maxy - miny;

        const int q = (part_quantities && part_quantities[i] > 0) ? part_quantities[i] : 1;
        for (int c = 0; c < q; c++) {
            // distance mode: wrap BEFORE placing if this part would overflow the row
            if (distanceMode && col > 0 && (x + w) > max_width) {
                col = 0; x = 0; y += rowH + gap_y; rowH = 0;
            }
            out_tx[k] = x - minx;   // land the part's min corner at (x, y)
            out_ty[k] = y - miny;
            out_angle[k] = 0.0;
            out_sheet_id[k] = 0;
            k++;
            x += w + gap_x;
            rowH = std::max(rowH, h);
            col++;
            // array mode: wrap AFTER a fixed number of columns
            if (!distanceMode && col >= columns) {
                col = 0; x = 0; y += rowH + gap_y; rowH = 0;
            }
        }
    }
    return k;
}

NFP_API int nfp_offset_polygon(
    int           vertex_count,
    const double* xy,
    double        delta,
    double        miter_limit,
    int           max_out_vertices,
    double*       out_xy)
{
    if (vertex_count < 3 || !xy || !out_xy || max_out_vertices < 0) return -1;
    const double SCALE = 1e7;
    Clipper2Lib::Path64 ring;
    ring.reserve(vertex_count);
    for (int i = 0; i < vertex_count; i++) {
        // skip a duplicated closing vertex
        if (i == vertex_count - 1 && vertex_count > 1 &&
            xy[2 * i] == xy[0] && xy[2 * i + 1] == xy[1]) break;
        ring.emplace_back(static_cast<int64_t>(std::llround(xy[2 * i] * SCALE)),
                          static_cast<int64_t>(std::llround(xy[2 * i + 1] * SCALE)));
    }
    if (ring.size() < 3) return -1;

    Clipper2Lib::Paths64 result = Clipper2Lib::InflatePaths(
        Clipper2Lib::Paths64{ring}, delta * SCALE,
        Clipper2Lib::JoinType::Miter, Clipper2Lib::EndType::Polygon,
        miter_limit > 0 ? miter_limit : 2.0);
    if (result.empty()) return 0;   // offset vanished (e.g. over-shrunk)

    // Largest-area loop is the main result.
    size_t best = 0;
    double bestArea = -1;
    for (size_t i = 0; i < result.size(); i++) {
        const double a = std::fabs(Clipper2Lib::Area(result[i]));
        if (a > bestArea) { bestArea = a; best = i; }
    }
    const int n = static_cast<int>(result[best].size());
    if (n > max_out_vertices) return -n;   // caller's buffer too small: required size, negated
    for (int i = 0; i < n; i++) {
        out_xy[2 * i] = result[best][i].x / SCALE;
        out_xy[2 * i + 1] = result[best][i].y / SCALE;
    }
    return n;
}

// ---- engraving font (single-stroke text) -----------------------------------
#include "engraving_font.inc"

namespace {
// Find a glyph index for a Unicode code point, or -1.
int eng_glyph_index(const EngFont& f, int code) {
    for (int i = 0; i < f.n_glyphs; i++) if (f.g_code[i] == code) return i;
    return -1;
}
} // namespace

NFP_API int nfp_text_to_polylines(
    const char* text, double height, int font, double spacing,
    int max_strokes, int* out_stroke_vertex_counts,
    int max_points, double* out_xy, int* out_total_points)
{
    if (out_total_points) *out_total_points = 0;
    if (!text) return 0;
    const EngFont& f = (font == 1) ? eng_bold_font : eng_regular_font;
    const double H_SPACING = (spacing < 0) ? 0.1 : spacing;
    const double V_SPACING = 1.4;
    const int nul = eng_glyph_index(f, 0);   // fallback glyph (NULL box)

    // First pass: lay everything out into temp strokes (so we can size + then fill).
    std::vector<std::vector<std::pair<double, double>>> strokes;
    double x = 0, y = 0;
    for (const char* p = text; ; ++p) {
        const char ch = *p;
        if (ch == '\0') break;
        if (ch == '\n') { x = 0; y -= V_SPACING * height; continue; }
        int gi = eng_glyph_index(f, static_cast<unsigned char>(ch));
        if (gi < 0) gi = nul;
        if (gi < 0) { x += (0.5 + H_SPACING) * height; continue; }
        x -= f.g_start[gi] * height;   // left bearing
        const int s0 = f.g_s0[gi], ns = f.g_ns[gi];
        for (int s = 0; s < ns; ++s) {
            const int off = f.stroke_off[s0 + s], np = f.stroke_n[s0 + s];
            std::vector<std::pair<double, double>> poly;
            poly.reserve(np);
            for (int k = 0; k < np; ++k)
                poly.emplace_back(x + f.pts[2 * (off + k)] * height,
                                  y + f.pts[2 * (off + k) + 1] * height);
            strokes.push_back(std::move(poly));
        }
        x += f.g_end[gi] * height + H_SPACING * height;
    }

    int total_points = 0;
    for (auto& s : strokes) total_points += static_cast<int>(s.size());
    if (out_total_points) *out_total_points = total_points;

    // Fill caller buffers with whatever fits (the example sizes them generously).
    int written_xy = 0;
    for (size_t s = 0; s < strokes.size(); ++s) {
        if (static_cast<int>(s) < max_strokes && out_stroke_vertex_counts)
            out_stroke_vertex_counts[s] = static_cast<int>(strokes[s].size());
        for (auto& pt : strokes[s]) {
            if (written_xy < max_points && out_xy) {
                out_xy[2 * written_xy] = pt.first;
                out_xy[2 * written_xy + 1] = pt.second;
            }
            ++written_xy;
        }
    }
    return static_cast<int>(strokes.size());
}

NFP_API void nfp_cancel(void)        { g_cancel = true; NfpWorker::Abort = true; }
NFP_API void nfp_cancel_reset(void)  { g_cancel = false; NfpWorker::Abort = false; }
NFP_API long long nfp_progress(void) { return g_progress.load(); }
NFP_API double nfp_fitness(void)     { return g_fitness.load(); }

NFP_API int nfp_poll_layout(
    int instance_count,
    double* out_tx, double* out_ty, double* out_angle,
    int* out_sheet_id, int* out_part_index, int* out_n_sheets)
{
    std::lock_guard<std::mutex> lock(g_snap.mtx);
    int n = std::min<int>(instance_count, static_cast<int>(g_snap.tx.size()));
    for (int k = 0; k < n; k++) {
        out_tx[k]        = g_snap.tx[k];
        out_ty[k]        = g_snap.ty[k];
        out_angle[k]     = g_snap.angle[k];
        out_sheet_id[k]  = g_snap.sheetId[k];
        out_part_index[k]= g_snap.partIndex[k];
    }
    if (out_n_sheets) *out_n_sheets = g_snap.nSheets;
    return g_snap.placed;
}
