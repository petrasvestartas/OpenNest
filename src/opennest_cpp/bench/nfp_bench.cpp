// nfp_bench — self-measuring research CLI for the NFP nesting engine.
//
// Calls the real C-API entry (nfp_nest_capi.cpp is compiled into this exe) on
// seeded synthetic datasets and reports wall time + packing-quality metrics,
// so every engine change is a measured A/B at a fixed seed.
//
//   nfp_bench --dataset concave --seed 7 --mode 1 --placement 1 --gens 20 --pop 30 \
//             [--rotations 4] [--tryAllRotations] [--edgeSamples N] [--compaction N] \
//             [--spacing X] [--timeBudget S] [--seeds N] [--dumpPlacements] \
//             --tag baseline [--csv out/results.csv] [--svg out/]
//
// Metrics (CSV row):
//   tag,dataset,seed,mode,placement,gens,pop,wall_ms,placed,total,n_sheets,
//   fitness,bbox_width,util_strip,util_sheet,overlap_area
//   util_strip   = sum(|part area|) / sum_per_sheet(layout bbox width * sheet H)
//   overlap_area = pairwise intersection area of placed parts + out-of-sheet
//                  area (must be ~0 — the correctness guard for speed changes).

#include "capi/nfp_nest_capi.h"
#include "clipper2/clipper.h"
#include "datasets.h"
#include "svg_dump.h"

#include "GeometryUtil.h"
#include "NFP.h"
#include "NfpWorker.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

struct Args {
    std::string dataset = "concave";
    int seed = 7, seeds = 1;
    int mode = 1, placement = 1, gens = 20, pop = 30, rotations = 4;
    int tryAllRotations = -1, edgeSamples = -1, compaction = -1; // -1 = engine default
    int useParallel = 1, maxSheets = 0, nSheets = 4;
    double spacing = 0.0, timeBudget = 0.0;
    int stagnation = 0; // >0 => GA early-exit after N stagnant generations (all placed)
    int exactVoids = 0; // 1 => exact Minkowski void exclusion (maps to NfpParams.useHoles)
    std::string sheetVoid; // "" none, "rect" (convex) or "L" (non-convex) hole injected per sheet
    double sheetW = 0, sheetH = 0; // 0 = dataset default
    bool dumpPlacements = false;
    bool dumpDataset = false;
    bool packDemo = false;
    double probeRotA = -1, probeRotB = -1;
    std::string tag = "run", csv, svgDir;
};

Args parseArgs(int argc, char** argv) {
    Args a;
    auto need = [&](int& i) -> const char* {
        if (i + 1 >= argc) { std::fprintf(stderr, "missing value for %s\n", argv[i]); std::exit(2); }
        return argv[++i];
    };
    for (int i = 1; i < argc; i++) {
        std::string k = argv[i];
        if (k == "--dataset") a.dataset = need(i);
        else if (k == "--seed") a.seed = std::atoi(need(i));
        else if (k == "--seeds") a.seeds = std::atoi(need(i));
        else if (k == "--mode") a.mode = std::atoi(need(i));
        else if (k == "--placement") a.placement = std::atoi(need(i));
        else if (k == "--gens") a.gens = std::atoi(need(i));
        else if (k == "--pop") a.pop = std::atoi(need(i));
        else if (k == "--rotations") a.rotations = std::atoi(need(i));
        else if (k == "--tryAllRotations") a.tryAllRotations = 1;
        else if (k == "--edgeSamples") a.edgeSamples = std::atoi(need(i));
        else if (k == "--compaction") a.compaction = std::atoi(need(i));
        else if (k == "--spacing") a.spacing = std::atof(need(i));
        else if (k == "--timeBudget") a.timeBudget = std::atof(need(i));
        else if (k == "--stagnation") a.stagnation = std::atoi(need(i));
        else if (k == "--exactVoids") a.exactVoids = std::atoi(need(i));
        else if (k == "--sheetVoid") a.sheetVoid = need(i);
        else if (k == "--sheets") a.nSheets = std::atoi(need(i));
        else if (k == "--sheetW") a.sheetW = std::atof(need(i));
        else if (k == "--sheetH") a.sheetH = std::atof(need(i));
        else if (k == "--serial") a.useParallel = 0;
        else if (k == "--dumpPlacements") a.dumpPlacements = true;
        else if (k == "--dumpDataset") a.dumpDataset = true;
        else if (k == "--packDemo") a.packDemo = true;   // exercise nfp_pack (grid layout, no nesting)
        else if (k == "--probeNfp") { a.probeRotA = std::atof(need(i)); a.probeRotB = std::atof(need(i)); }
        else if (k == "--tag") a.tag = need(i);
        else if (k == "--csv") a.csv = need(i);
        else if (k == "--svg") a.svgDir = need(i);
        else { std::fprintf(stderr, "unknown arg: %s\n", k.c_str()); std::exit(2); }
    }
    return a;
}

// --- geometry helpers --------------------------------------------------------

constexpr double CHECK_SCALE = 1e6; // integer scale for the overlap validator

// Optional sheet void (interior hole) to exercise the exact-Minkowski forbidden-zone path.
// "rect" = convex hole; "L" = non-convex L (a concave pocket a part can nest into). Returned in
// sheet-local coordinates, positioned in the lower-left working area near the packing origin.
inline std::vector<double> sheetVoidXY(const std::string& mode, double /*sheetW*/, double /*sheetH*/) {
    const double ox = 450, oy = 250;
    if (mode == "rect") {
        const double w = 420, h = 420;
        return {ox, oy, ox + w, oy, ox + w, oy + h, ox, oy + h};
    }
    if (mode == "L") {
        const double w = 520, h = 520, t = 260;  // outer box minus a (w-t)x(h-t) corner bite
        return {ox, oy, ox + w, oy, ox + w, oy + t, ox + t, oy + t, ox + t, oy + h, ox, oy + h};
    }
    return {};
}

Clipper2Lib::Path64 toPath64(const std::vector<double>& xy, double tx, double ty,
                             double angleDeg) {
    double rad = angleDeg * M_PI / 180.0;
    double c = std::cos(rad), s = std::sin(rad);
    Clipper2Lib::Path64 p;
    p.reserve(xy.size() / 2);
    for (size_t i = 0; i + 1 < xy.size(); i += 2) {
        double x = xy[i] * c - xy[i + 1] * s + tx;
        double y = xy[i] * s + xy[i + 1] * c + ty;
        p.emplace_back(static_cast<int64_t>(std::llround(x * CHECK_SCALE)),
                       static_cast<int64_t>(std::llround(y * CHECK_SCALE)));
    }
    return p;
}

double polyAreaXY(const std::vector<double>& xy) {
    double a = 0;
    size_t n = xy.size() / 2;
    for (size_t i = 0, j = n - 1; i < n; j = i++)
        a += (xy[2 * j] + xy[2 * i]) * (xy[2 * j + 1] - xy[2 * i + 1]);
    return std::fabs(a) / 2;
}

double pathsArea(const Clipper2Lib::Paths64& ps) {
    double a = 0;
    for (auto& p : ps) a += Clipper2Lib::Area(p);
    return std::fabs(a) / (CHECK_SCALE * CHECK_SCALE);
}

// Solid (outer minus holes) of one placed instance as positively-wound paths.
Clipper2Lib::Paths64 instanceSolid(const bench::BenchPart& part, double tx, double ty,
                                   double angle) {
    Clipper2Lib::Paths64 subj{toPath64(part.xy, tx, ty, angle)};
    if (Clipper2Lib::Area(subj[0]) < 0) std::reverse(subj[0].begin(), subj[0].end());
    if (part.holes.empty())
        return Clipper2Lib::Union(subj, Clipper2Lib::FillRule::NonZero);
    Clipper2Lib::Paths64 holes;
    for (auto& h : part.holes) {
        auto hp = toPath64(h, tx, ty, angle);
        if (Clipper2Lib::Area(hp) < 0) std::reverse(hp.begin(), hp.end());
        holes.push_back(hp);
    }
    return Clipper2Lib::Difference(subj, holes, Clipper2Lib::FillRule::NonZero);
}

struct RunResult {
    double wall_ms = 0, fitness = 0, bbox_width = 0;
    double util_strip = 0, util_sheet = 0, overlap_area = 0;
    int placed = 0, total = 0, n_sheets = 0;
};

RunResult runOnce(const Args& a, int seed, const bench::BenchData& d) {
    // ---- flatten to C arrays ----
    std::vector<int> pvc, pqty, prot, phc, phvc;
    std::vector<double> pxy, phxy;
    for (auto& p : d.parts) {
        pvc.push_back(static_cast<int>(p.xy.size() / 2));
        pxy.insert(pxy.end(), p.xy.begin(), p.xy.end());
        pqty.push_back(p.qty);
        prot.push_back(p.rotations);
        phc.push_back(static_cast<int>(p.holes.size()));
        for (auto& h : p.holes) {
            phvc.push_back(static_cast<int>(h.size() / 2));
            phxy.insert(phxy.end(), h.begin(), h.end());
        }
    }
    double sheetW = a.sheetW > 0 ? a.sheetW : d.sheetW;
    double sheetH = a.sheetH > 0 ? a.sheetH : d.sheetH;
    std::vector<int> svc, shc, shvc;
    std::vector<double> sxy, shxy;
    std::vector<double> voidPoly = sheetVoidXY(a.sheetVoid, sheetW, sheetH);
    for (int s = 0; s < a.nSheets; s++) {
        svc.push_back(4);
        double r[] = {0, 0, sheetW, 0, sheetW, sheetH, 0, sheetH};
        sxy.insert(sxy.end(), r, r + 8);
        if (!voidPoly.empty()) {
            shc.push_back(1);
            shvc.push_back(static_cast<int>(voidPoly.size() / 2));
            shxy.insert(shxy.end(), voidPoly.begin(), voidPoly.end());
        } else {
            shc.push_back(0);
        }
    }

    int instCount = 0;
    for (int q : pqty) instCount += q;

    NfpParams params{};
    params.placementType   = a.placement;
    params.rotations       = a.rotations;
    params.mutationRate    = 10;
    params.populationSize  = a.pop;
    params.seed            = seed;
    params.curveTolerance  = 0.72;
    params.clipperScale    = 1e7;
    params.spacing         = a.spacing;
    params.sheetSpacing    = 0;
    params.rotationLimit   = 360;
    params.clipByRects     = 1;
    params.mode            = a.mode;
    params.generations     = a.gens;
    params.numSeeds        = 4;
    params.useParallel     = a.useParallel;
    params.timeBudgetSecs  = a.timeBudget;
    params.maxSheets       = a.maxSheets;
    params.edgeSamples     = a.edgeSamples;
    params.compactionPasses= a.compaction;
    params.tryAllRotations = a.tryAllRotations;
    params.stagnationGens  = a.stagnation;
    params.exactVoids      = a.exactVoids;   // dedicated exact Minkowski void-exclusion flag

    std::vector<double> tx(instCount), ty(instCount), angle(instCount);
    std::vector<int> sheetId(instCount), partIndex(instCount);
    int nSheetsOut = 0;
    double fitness = 0;

    auto t0 = std::chrono::steady_clock::now();
    int placed = nfp_nest(
        static_cast<int>(d.parts.size()), pvc.data(), pxy.data(), pqty.data(),
        prot.data(),
        phc.data(), phvc.data(), phxy.data(),
        a.nSheets, svc.data(), sxy.data(), shc.data(), shvc.data(), shxy.data(),
        &params, tx.data(), ty.data(), angle.data(), sheetId.data(),
        partIndex.data(), &nSheetsOut, &fitness);
    double wall_ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - t0).count();

    if (placed < 0) {
        std::fprintf(stderr, "nfp_nest failed: %d\n", placed);
        std::exit(1);
    }

    // ---- metrics ----
    RunResult r;
    r.wall_ms = wall_ms;
    r.placed = placed;
    r.total = instCount;
    r.n_sheets = nSheetsOut;
    r.fitness = fitness;

    // per-sheet layout bbox + placed solid area
    double placedArea = 0;
    std::vector<double> sheetMinX(a.nSheets, 1e300), sheetMaxX(a.nSheets, -1e300);
    std::vector<Clipper2Lib::Paths64> solids(instCount);
    for (int k = 0; k < instCount; k++) {
        if (sheetId[k] < 0) continue;
        const auto& part = d.parts[partIndex[k]];
        placedArea += polyAreaXY(part.xy);
        for (auto& h : part.holes) placedArea -= polyAreaXY(h);
        solids[k] = instanceSolid(part, tx[k], ty[k], angle[k]);
        int sid = sheetId[k];
        if (sid >= 0 && sid < a.nSheets) {
            for (auto& path : solids[k])
                for (auto& pt : path) {
                    double x = pt.x / CHECK_SCALE;
                    if (x < sheetMinX[sid]) sheetMinX[sid] = x;
                    if (x > sheetMaxX[sid]) sheetMaxX[sid] = x;
                }
        }
    }
    double stripArea = 0, usedSheetArea = 0, maxWidth = 0;
    for (int s = 0; s < a.nSheets; s++) {
        if (sheetMaxX[s] < sheetMinX[s]) continue;
        double w = sheetMaxX[s] - sheetMinX[s];
        stripArea += w * sheetH;
        usedSheetArea += sheetW * sheetH;
        if (w > maxWidth) maxWidth = w;
    }
    r.bbox_width = maxWidth;
    r.util_strip = stripArea > 0 ? placedArea / stripArea : 0;
    r.util_sheet = usedSheetArea > 0 ? placedArea / usedSheetArea : 0;

    // overlap validator: pairwise same-sheet intersections + out-of-(usable)-sheet area.
    // Subtracting any sheet void makes a part that intrudes into the void count as out-of-sheet,
    // so overlap>0 flags an exact/convex void-exclusion bug.
    Clipper2Lib::Paths64 sheetRect{toPath64({0, 0, sheetW, 0, sheetW, sheetH, 0, sheetH}, 0, 0, 0)};
    if (!voidPoly.empty()) {
        Clipper2Lib::Paths64 voidPaths{toPath64(voidPoly, 0, 0, 0)};
        sheetRect = Clipper2Lib::Difference(sheetRect, voidPaths, Clipper2Lib::FillRule::NonZero);
    }
    double overlap = 0;
    for (int i = 0; i < instCount; i++) {
        if (sheetId[i] < 0) continue;
        auto out = Clipper2Lib::Difference(solids[i], sheetRect, Clipper2Lib::FillRule::NonZero);
        overlap += pathsArea(out);
        for (int j = i + 1; j < instCount; j++) {
            if (sheetId[j] != sheetId[i]) continue;
            auto inter = Clipper2Lib::Intersect(solids[i], solids[j], Clipper2Lib::FillRule::NonZero);
            double aInter = pathsArea(inter);
            if (aInter > 1.0)
                std::fprintf(stderr,
                             "  OVERLAP pair inst %d (part %d @%.1f,%.1f rot %.0f) x inst %d "
                             "(part %d @%.1f,%.1f rot %.0f): area=%.1f\n",
                             i, partIndex[i], tx[i], ty[i], angle[i],
                             j, partIndex[j], tx[j], ty[j], angle[j], aInter);
            overlap += aInter;
        }
    }
    r.overlap_area = overlap;

    if (a.dumpPlacements) {
        for (int k = 0; k < instCount; k++)
            std::printf("PLACEMENT %d part=%d sheet=%d tx=%.9f ty=%.9f angle=%.4f\n",
                        k, partIndex[k], sheetId[k], tx[k], ty[k], angle[k]);
    }

    if (!a.svgDir.empty()) {
        std::filesystem::create_directories(a.svgDir);
        std::vector<bench::SvgInstance> svgInsts(instCount);
        for (int k = 0; k < instCount; k++) {
            const auto& part = d.parts[partIndex[k]];
            auto& si = svgInsts[k];
            si.sheetId = sheetId[k];
            double rad = angle[k] * M_PI / 180.0, c = std::cos(rad), s = std::sin(rad);
            double ax = sheetId[k] >= 0 ? tx[k] : 0, ay = sheetId[k] >= 0 ? ty[k] : 0;
            auto xform = [&](const std::vector<double>& in) {
                std::vector<double> out;
                out.reserve(in.size());
                for (size_t i2 = 0; i2 + 1 < in.size(); i2 += 2) {
                    out.push_back(in[i2] * c - in[i2 + 1] * s + ax);
                    out.push_back(in[i2] * s + in[i2 + 1] * c + ay);
                }
                return out;
            };
            si.xy = xform(part.xy);
            for (auto& h : part.holes) si.holes.push_back(xform(h));
        }
        char name[256];
        std::snprintf(name, sizeof(name), "%s/%s_%s_%d.svg", a.svgDir.c_str(),
                      a.tag.c_str(), d.name.c_str(), seed);
        bench::writeSvg(name, svgInsts, std::max(nSheetsOut, 1), sheetW, sheetH);
    }

    return r;
}

void appendCsv(const Args& a, int seed, const RunResult& r) {
    if (a.csv.empty()) return;
    std::filesystem::path p(a.csv);
    if (p.has_parent_path()) std::filesystem::create_directories(p.parent_path());
    bool writeHeader = !std::filesystem::exists(p);
    std::ofstream f(p, std::ios::app);
    if (writeHeader)
        f << "tag,dataset,seed,mode,placement,gens,pop,wall_ms,placed,total,"
             "n_sheets,fitness,bbox_width,util_strip,util_sheet,overlap_area\n";
    char row[512];
    std::snprintf(row, sizeof(row),
                  "%s,%s,%d,%d,%d,%d,%d,%.1f,%d,%d,%d,%.6f,%.3f,%.5f,%.5f,%.6f\n",
                  a.tag.c_str(), a.dataset.c_str(), seed, a.mode, a.placement,
                  a.gens, a.pop, r.wall_ms, r.placed, r.total, r.n_sheets,
                  r.fitness, r.bbox_width, r.util_strip, r.util_sheet,
                  r.overlap_area);
    f << row;
}

double median(std::vector<double> v) {
    std::sort(v.begin(), v.end());
    size_t n = v.size();
    return n == 0 ? 0 : (n % 2 ? v[n / 2] : 0.5 * (v[n / 2 - 1] + v[n / 2]));
}

} // namespace

int main(int argc, char** argv) try {
    Args a = parseArgs(argc, argv);

    std::vector<int> seedList;
    if (a.seeds <= 1) seedList.push_back(a.seed);
    else for (int i = 0; i < a.seeds; i++) seedList.push_back(7 + 10 * i); // 7,17,27,...

    std::vector<double> utils, walls;
    if (a.probeRotA >= 0) {
        // Compute the engine's outer NFP for (part0 @rotA) vs (part0 @rotB) of the
        // dataset and print every loop with its signed area, plus whether the
        // "exactly on top" offset (B at A's position) is inside the NFP.
        bench::BenchData d = bench::makeDataset(a.dataset, 1234);
        nest::NFP base;
        for (size_t i = 0; i + 1 < d.parts[0].xy.size(); i += 2)
            base.AddPoint(nest::Point(d.parts[0].xy[i], d.parts[0].xy[i + 1]));
        base.source = 0;

        auto A = std::make_shared<nest::NFP>(nest::GeometryUtil::rotatePolygon(base, (float)a.probeRotA));
        A->source = 0; A->Rotation = (float)a.probeRotA;
        auto B = std::make_shared<nest::NFP>(nest::GeometryUtil::rotatePolygon(base, (float)a.probeRotB));
        B->source = 0; B->Rotation = (float)a.probeRotB;

        nest::NfpWorker worker;
        auto nfp = worker.getOuterNfp(*A, *B, 0);
        if (!nfp) { std::printf("PROBE: getOuterNfp returned null\n"); return 0; }
        std::printf("PROBE outer loop: %d pts, signed area %.3f\n",
                    nfp->length(), nest::GeometryUtil::polygonArea(*nfp));
        for (int i = 0; i < nfp->length(); i++)
            std::printf("  %.6f %.6f\n", (*nfp)[i].x, (*nfp)[i].y);
        for (size_t c = 0; c < nfp->children.size(); c++)
            std::printf("PROBE child %zu: %d pts, signed area %.3f\n", c,
                        nfp->children[c]->length(),
                        nest::GeometryUtil::polygonArea(*nfp->children[c]));
        // "on top" reference: shift = B-position == A-position (=0 here). The NFP is the
        // locus of B[0]; B exactly on A means B[0] at its own original location.
        nest::Point onTop((*B)[0].x, (*B)[0].y);
        bool inside = nest::GeometryUtil::pointInPolygon(onTop, *nfp).value_or(false);
        std::printf("PROBE on-top point (%.6f, %.6f) inside outer NFP: %s\n",
                    onTop.x, onTop.y, inside ? "YES (correct)" : "NO (BUG!)");

        // --- scenario: replicate the failing 3rd placement of the seed-17 repro ---
        // placed: A0 = part@270 at s0, A1 = part@180 at s1; candidate = part@270.
        // Build union(outer NFPs shifted) and feasible = sheetIFP - union exactly like
        // evalCandidate, then test whether s0 re-appears as a feasible candidate vertex.
        const double s0x = 56.782889000, s0y = 400.062015200;
        const double s1x = 290.503346278, s1y = 203.195170299;
        auto A1 = std::make_shared<nest::NFP>(nest::GeometryUtil::rotatePolygon(base, 180.0f));
        A1->source = 0; A1->Rotation = 180.0f;

        auto nfp0 = worker.getOuterNfp(*A, *B, 0);   // A@270 vs cand@270
        auto nfp1 = worker.getOuterNfp(*A1, *B, 0);  // A@180 vs cand@270
        nest::NestConfig cfg;
        cfg.spacing = 0;

        Clipper2Lib::Clipper64 cu;
        auto p0 = nest::NfpWorker::nfpToClipperWithShift(*nfp0, cfg.clipperScale, s0x, s0y);
        auto p1 = nest::NfpWorker::nfpToClipperWithShift(*nfp1, cfg.clipperScale, s1x, s1y);
        cu.AddSubject(Clipper2Lib::Paths64(p0.begin(), p0.end()));
        cu.AddSubject(Clipper2Lib::Paths64(p1.begin(), p1.end()));
        Clipper2Lib::Paths64 unionRes;
        cu.Execute(Clipper2Lib::ClipType::Union, Clipper2Lib::FillRule::NonZero, unionRes);
        std::printf("SCENARIO union: %zu paths, areas:", unionRes.size());
        for (auto& p : unionRes) std::printf(" %.0f", Clipper2Lib::Area(p) / (cfg.clipperScale * cfg.clipperScale));
        std::printf("\n");

        // on-top candidate point for the 3rd copy = cand[0] + s0
        Clipper2Lib::Point64 probePt(
            (int64_t)std::llround(((*B)[0].x + s0x) * cfg.clipperScale),
            (int64_t)std::llround(((*B)[0].y + s0y) * cfg.clipperScale));
        int winding = 0;
        for (auto& p : unionRes) {
            auto r = Clipper2Lib::PointInPolygon(probePt, p);
            if (r == Clipper2Lib::PointInPolygonResult::IsInside)
                winding += Clipper2Lib::Area(p) > 0 ? 1 : -1;
            else if (r == Clipper2Lib::PointInPolygonResult::IsOn)
                std::printf("SCENARIO: probe point is ON a union path boundary\n");
        }
        std::printf("SCENARIO probe point net winding in union: %d (%s)\n", winding,
                    winding != 0 ? "covered = correctly excluded" : "NOT covered = BUG");

        // feasible = sheet IFP - union
        nest::NFP sheet;
        sheet.AddPoint(nest::Point(0, 0)); sheet.AddPoint(nest::Point(2000, 0));
        sheet.AddPoint(nest::Point(2000, 1000)); sheet.AddPoint(nest::Point(0, 1000));
        sheet.source = 0;
        auto sheetNfp = worker.getInnerNfp(sheet, *B, 0, cfg);
        if (sheetNfp.empty()) { std::printf("SCENARIO: no inner NFP\n"); return 0; }
        auto sheetPaths = nest::NfpWorker::innerNfpToClipperCoordinates(sheetNfp, cfg);
        Clipper2Lib::Clipper64 cd;
        cd.AddClip(unionRes);
        cd.AddSubject(Clipper2Lib::Paths64(sheetPaths.begin(), sheetPaths.end()));
        Clipper2Lib::Paths64 finalNfp;
        cd.Execute(Clipper2Lib::ClipType::Difference, Clipper2Lib::FillRule::EvenOdd, finalNfp);
        std::printf("SCENARIO feasible: %zu paths\n", finalNfp.size());
        double bestD = 1e300;
        for (auto& p : finalNfp)
            for (auto& pt : p) {
                double cx = pt.x / cfg.clipperScale - (*B)[0].x;
                double cy = pt.y / cfg.clipperScale - (*B)[0].y;
                double dd = std::hypot(cx - s0x, cy - s0y);
                if (dd < bestD) bestD = dd;
            }
        std::printf("SCENARIO nearest feasible-vertex shift distance to s0: %.6f %s\n",
                    bestD, bestD < 1e-3 ? "== s0 (BUG REPRODUCED)" : "(s0 excluded, ok)");
        return 0;
    }

    if (a.packDemo) {
        // Exercise nfp_pack: array mode (5 columns) and distance mode (sheet width).
        bench::BenchData d = bench::makeDataset(a.dataset, 1234);
        std::vector<int> pvc, pqty;
        std::vector<double> pxy;
        int inst = 0;
        for (auto& p : d.parts) {
            pvc.push_back(static_cast<int>(p.xy.size() / 2));
            pxy.insert(pxy.end(), p.xy.begin(), p.xy.end());
            pqty.push_back(p.qty);
            inst += p.qty;
        }
        std::vector<double> tx(inst), ty(inst), ang(inst);
        std::vector<int> sid(inst);
        int k1 = nfp_pack(static_cast<int>(d.parts.size()), pvc.data(), pxy.data(), pqty.data(),
                          5, 10.0, 10.0, 0.0, tx.data(), ty.data(), ang.data(), sid.data());
        std::printf("nfp_pack array(cols=5): placed=%d, first=(%.1f,%.1f) last=(%.1f,%.1f)\n",
                    k1, tx[0], ty[0], tx[k1 - 1], ty[k1 - 1]);
        int k2 = nfp_pack(static_cast<int>(d.parts.size()), pvc.data(), pxy.data(), pqty.data(),
                          10, 10.0, 10.0, d.sheetW, tx.data(), ty.data(), ang.data(), sid.data());
        std::printf("nfp_pack distance(maxW=%.0f): placed=%d, last=(%.1f,%.1f)\n",
                    d.sheetW, k2, tx[k2 - 1], ty[k2 - 1]);
        // offset round-trip on part 0: grow by 5 then shrink by 5 — area must come back close.
        std::vector<double> grown(2048), back(2048);
        int g = nfp_offset_polygon(static_cast<int>(d.parts[0].xy.size() / 2), d.parts[0].xy.data(),
                                   5.0, 2.0, 1024, grown.data());
        int b = g > 0 ? nfp_offset_polygon(g, grown.data(), -5.0, 2.0, 1024, back.data()) : 0;
        std::printf("nfp_offset_polygon: grow -> %d verts, shrink-back -> %d verts (orig %zu), "
                    "area %.1f -> %.1f -> %.1f\n",
                    g, b, d.parts[0].xy.size() / 2, polyAreaXY(d.parts[0].xy),
                    g > 0 ? polyAreaXY(std::vector<double>(grown.begin(), grown.begin() + 2 * g)) : 0.0,
                    b > 0 ? polyAreaXY(std::vector<double>(back.begin(), back.begin() + 2 * b)) : 0.0);
        return 0;
    }

    if (a.dumpDataset) {
        bench::BenchData d = bench::makeDataset(a.dataset, 1234);
        std::printf("# %s\nsheet %.6f %.6f\n", d.name.c_str(), d.sheetW, d.sheetH);
        for (size_t pi = 0; pi < d.parts.size(); pi++) {
            const auto& p = d.parts[pi];
            std::printf("# part index %zu\npart %d\n", pi, p.qty);
            for (size_t i = 0; i + 1 < p.xy.size(); i += 2)
                std::printf("%.9f %.9f\n", p.xy[i], p.xy[i + 1]);
            for (auto& h : p.holes) {
                std::printf("\n");
                for (size_t i = 0; i + 1 < h.size(); i += 2)
                    std::printf("%.9f %.9f\n", h[i], h[i + 1]);
            }
        }
        return 0;
    }

    for (int seed : seedList) {
        bench::BenchData d = bench::makeDataset(a.dataset, 1234); // geometry fixed; seed drives the GA
        RunResult r = runOnce(a, seed, d);
        appendCsv(a, seed, r);
        std::printf("%-14s %-8s seed=%-3d wall=%8.1fms placed=%d/%d sheets=%d "
                    "width=%8.2f util_strip=%.4f util_sheet=%.4f overlap=%.6f fitness=%.4f\n",
                    a.tag.c_str(), a.dataset.c_str(), seed, r.wall_ms, r.placed,
                    r.total, r.n_sheets, r.bbox_width, r.util_strip, r.util_sheet,
                    r.overlap_area, r.fitness);
        if (r.overlap_area > 1e-3)
            std::fprintf(stderr, "WARNING: overlap_area=%.6f > 0 — invalid layout!\n",
                         r.overlap_area);
        utils.push_back(r.util_strip);
        walls.push_back(r.wall_ms);
    }
    if (seedList.size() > 1) {
        std::printf("SUMMARY %-10s %-8s util_strip med=%.4f min=%.4f max=%.4f  "
                    "wall_ms med=%.1f min=%.1f max=%.1f\n",
                    a.tag.c_str(), a.dataset.c_str(), median(utils),
                    *std::min_element(utils.begin(), utils.end()),
                    *std::max_element(utils.begin(), utils.end()), median(walls),
                    *std::min_element(walls.begin(), walls.end()),
                    *std::max_element(walls.begin(), walls.end()));
    }
    return 0;
} catch (const std::exception& e) {
    std::fprintf(stderr, "nfp_bench: fatal: %s\n", e.what());
    return 1;
}
