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
    c.simplify       = p->simplify != 0;
    // (rotationLimit / exploreConcave / clipByHull / clipByRects remain in the C ABI for
    // compatibility but map to nothing: their engine paths were dead code and removed.)
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

    NestConfig cfg = toConfig(params);
    const int mode = params->mode;

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
