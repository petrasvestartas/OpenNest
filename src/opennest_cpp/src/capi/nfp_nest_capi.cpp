#include "capi/nfp_nest_capi.h"

#include "NestingContext.h"
#include "NestingEngine.h"
#include "NfpWorker.h"
#include "NFP.h"

#include <vector>
#include <memory>
#include <atomic>
#include <mutex>
#include <chrono>
#include <algorithm>
#include <set>
#include <cstdio>
#include <cstdlib>

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
    c.rotation_limit = static_cast<float>(p->rotationLimit);
    c.exploreConcave = p->exploreConcave != 0;
    c.clipByHull     = p->clipByHull != 0;
    c.clipByRects    = p->clipByRects != 0;
    c.simplify       = p->simplify != 0;
    c.faithful       = (p->mode == 0);   // mode 0 = faithful parity with the C# engine
    c.exactNfp       = (p->exactNfp != 0);
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
        c.tryAllRotations = p->tryAllRotations != 0;
    }
    return c;
}

// Build the part/sheet NFPs into the context from the flat C arrays. Returns the
// instance->part-index map (one entry per emitted instance, expansion order).
std::vector<int> buildInputs(
    NestingContext& ctx,
    int part_count, const int* pvc, const double* pxy, const int* pqty,
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
            poly->source = i;
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
            sheetId[k] = sid;
            tx[k] = poly->x - poly->sheet->x;
            ty[k] = poly->y - poly->sheet->y;
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
            g_snap.sheetId[k] = poly->sheet->Id;
            g_snap.tx[k] = poly->x - poly->sheet->x;
            g_snap.ty[k] = poly->y - poly->sheet->y;
            g_snap.angle[k] = poly->Rotation;
            used.insert(poly->sheet->Id);
            placed++;
        }
    }
    g_snap.placed = placed;
    g_snap.nSheets = static_cast<int>(used.size());
}

} // namespace

// ============================================================================

NFP_API int nfp_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_quantities,
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

    NestConfig cfg = toConfig(params);
    const int mode = params->mode;

    // Build inputs into a context.
    NestingContext ctx;
    ctx.config = cfg;
    auto instancePart = buildInputs(
        ctx, part_count, part_vertex_counts, part_xy, part_quantities,
        part_hole_counts, part_hole_vertex_counts, part_hole_xy,
        sheet_count, sheet_vertex_counts, sheet_xy,
        sheet_hole_counts, sheet_hole_vertex_counts, sheet_hole_xy);

    const long pop  = std::max(1, cfg.populationSize);
    const long gens = std::max(1, params->generations);
    const long totalSteps = gens * pop;

    NfpWorker::UseParallel = (mode == 0) ? false : (params->useParallel != 0);

    if (mode == 2) {
        // Turbo: independent parallel seeds, keep best.
        int numSeeds = params->numSeeds > 0 ? params->numSeeds : 4;
        auto best = NestingContext::RunParallelSeeds(
            ctx.Polygons, ctx.Sheets, cfg, numSeeds, static_cast<int>(totalSteps),
            [&](int /*seed*/, int iter, const NestingContext& c) {
                if (iter > g_progress) g_progress = iter;
                if (c.HasCurrent()) g_fitness = c.Current().fitness.value_or(0.0);
            });
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
            if (ctx.HasCurrent()) g_fitness = ctx.Current().fitness.value_or(0.0);
            updateSnapshot(ctx, instancePart);
        }
    } else {
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
            g_progress = (step - 1) / pop + 1;
            if (ctx.HasCurrent()) g_fitness = ctx.Current().fitness.value_or(0.0);
            updateSnapshot(ctx, instancePart);
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

NFP_API void nfp_cancel(void)        { g_cancel = true; }
NFP_API void nfp_cancel_reset(void)  { g_cancel = false; }
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
