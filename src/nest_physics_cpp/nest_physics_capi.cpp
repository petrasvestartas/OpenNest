// Implementation of the nest_physics C ABI (see nest_physics_capi.h). Drives the SAME solver as the
// CLI exe via solver/driver.hpp (penetration-depth overlap proxy, USE_DEPTH_PROXY=1 by default).
#include "solver/driver.hpp"
#include "nest_physics_capi.h"
#include <vector>
#include <cmath>
#include <thread>
#include <algorithm>
#include <cstdlib>

using namespace nest;

// dedup consecutive points + drop the closing duplicate (Polygon::create rejects duplicates).
static std::vector<Point> clean_ring(std::vector<Point> pts) {
    if (pts.size() >= 2 && pts.front() == pts.back()) pts.pop_back();
    std::vector<Point> clean;
    for (auto& p : pts) if (clean.empty() || !(clean.back() == p)) clean.push_back(p);
    if (!clean.empty() && clean.size() >= 2 && clean.front() == clean.back()) clean.pop_back();
    return clean;
}

static std::vector<Point> read_poly(const double* xy, int nverts, int& cursor) {
    std::vector<Point> pts;
    pts.reserve((usize)nverts);
    for (int k = 0; k < nverts; ++k) {
        pts.push_back(Point((f32)xy[2 * cursor], (f32)xy[2 * cursor + 1]));
        ++cursor;
    }
    return pts;
}

// Live-preview support: np_poll_layout (called from the host's UI thread) needs the part-centering
// offsets (craw) + the solver-part -> original-input index mapping that np_nest builds. Stash them
// globally for the duration of a solve, guarded by g_live.mtx.
static std::vector<int>   g_poll_orig_index;
static std::vector<Point> g_poll_craw;

// FILL-HOLES post-pass: relocate smaller placed parts INTO larger parts' cavities wherever they fit
// with no overlap. The width-minimizing relaxation only uses a hole when it shrinks the strip; on a
// roomy sheet it leaves parts in open space. This pass actively pulls them into cavities (material
// efficiency) — deterministic, and it only commits a move that is verified collision-free, so it can
// never create an overlap. Operates on the final placements (sheet-local frame).
static void fill_cavities(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                          const std::vector<std::vector<Polygon>>& part_holes_centered) {
    auto& P = res.placements;
    int N = (int)P.size();
    if (N == 0) return;
    const f32 TWO_PI = 6.28318530718f;
    const int NROT = 16;    // orientations tried when fitting a part into a cavity (handles parts that only fit rotated)
    const int G = 24;       // position-grid resolution inside a cavity

    // Every cavity (a placed hole) across ALL sheets. Hole geometry comes from the side-table (the
    // engine is solid/hole-unaware), placed via the host's transform.
    struct Cavity { int host; Polygon poly; int sheet; std::vector<Polygon> occ; f32 area; };
    std::vector<Cavity> cavities;
    for (int bi = 0; bi < N; ++bi) {
        usize bc = P[bi].color_id; if (bc >= parts.size() || bc >= part_holes_centered.size()) continue;
        const std::vector<Polygon>& bholes = part_holes_centered[bc];
        if (bholes.empty()) continue;
        AffineTransform bT = P[bi].d_transf.compose();
        for (const auto& h : bholes) {
            Cavity cv{bi, h.transform_clone(bT), P[bi].sheet, {}, 0.0f};
            cv.area = cv.poly.area;
            // Seed occupants with any part the RELAXATION already placed (partly) inside this cavity, so
            // a relocated part is checked against them too (otherwise the move could overlap one of them).
            for (int k = 0; k < N; ++k) {
                if (k == bi || P[k].sheet != cv.sheet || (usize)P[k].color_id >= parts.size()) continue;
                Polygon kp = parts[P[k].color_id].first.collision_shape->transform_clone(P[k].d_transf.compose());
                if (brute_overlap(kp, cv.poly)) cv.occ.push_back(std::move(kp));
            }
            cavities.push_back(std::move(cv));
        }
    }
    if (cavities.empty()) return;

    // Seat parts LONGEST-dimension-first (then area): long/thin parts need a contiguous slot, so claim
    // them before the grid fragments the cavity; bulky parts and small fillers follow.
    std::vector<int> order(N);
    for (int i = 0; i < N; ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) {
        const Polygon* pa = ((usize)P[a].color_id < parts.size()) ? parts[P[a].color_id].first.collision_shape.get() : nullptr;
        const Polygon* pb = ((usize)P[b].color_id < parts.size()) ? parts[P[b].color_id].first.collision_shape.get() : nullptr;
        f32 da = pa ? pa->diameter : 0.0f, db = pb ? pb->diameter : 0.0f;
        if (da != db) return da > db;
        return (pa ? pa->area : 0.0f) > (pb ? pb->area : 0.0f); });

    std::vector<char> relocated(N, 0);
    for (int oi = 0; oi < N; ++oi) {
        int si = order[oi];
        if (relocated[si]) continue;
        usize sc = P[si].color_id; if (sc >= parts.size()) continue;
        const Polygon& sshape = *parts[sc].first.collision_shape;
        bool done = false;
        for (auto& cav : cavities) {
            if (done) break;
            if (cav.host == si || sshape.area >= cav.area) continue;     // not its own hole; must be smaller than the cavity
            for (int r = 0; r < NROT && !done; ++r) {
                f32 ang = TWO_PI * (f32)r / (f32)NROT;
                Polygon srot = sshape.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());   // rotate about origin
                if (srot.bbox.width() > cav.poly.bbox.width() + 1e-3f ||
                    srot.bbox.height() > cav.poly.bbox.height() + 1e-3f) continue;
                f32 spanx = cav.poly.bbox.width() - srot.bbox.width(), spany = cav.poly.bbox.height() - srot.bbox.height();
                for (int iy = 0; iy <= G && !done; ++iy)
                    for (int ix = 0; ix <= G && !done; ++ix) {
                        f32 tx = cav.poly.bbox.x_min - srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                        f32 ty = cav.poly.bbox.y_min - srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                        Polygon cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        bool inside = true;
                        for (const auto& v : cand.vertices) if (!collides(cav.poly, v)) { inside = false; break; }
                        if (!inside) continue;
                        // No poking through the hole wall into the host's material: with every cand vertex
                        // inside the cavity ring, additionally require no cand edge to cross a ring edge.
                        // Together these certify cand lies WHOLLY within the cavity. (The engine is now
                        // solid / hole-unaware, so a donut-overlap test would wrongly reject an in-cavity part.)
                        bool crosses = false;
                        for (usize ei = 0; ei < cand.n_vertices() && !crosses; ++ei)
                            for (usize ej = 0; ej < cav.poly.n_vertices(); ++ej)
                                if (collides(cand.edge(ei), cav.poly.edge(ej))) { crosses = true; break; }
                        if (crosses) continue;
                        bool clash = false;
                        for (const auto& o : cav.occ) if (brute_overlap(cand, o)) { clash = true; break; }
                        if (clash) continue;
                        P[si].d_transf = RigidTransform(ang, tx, ty);            // commit: new pose of the centered shape
                        P[si].verts = cand.vertices;
                        P[si].sheet = cav.sheet;
                        cav.occ.push_back(std::move(cand));
                        relocated[si] = 1; done = true;
                    }
            }
        }
    }

    // Compact sheet indices (a sheet emptied by cross-sheet relocation must not be emitted) + recount.
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    std::vector<int> used(ns, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns) used[p.sheet] = 1;
    std::vector<int> remap(ns, -1); int next = 0;
    for (int s = 0; s < ns; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next > 0 ? next : res.n_sheets;
}

// DROP INVALID placements (g_drop_invalid): a part the solver could not actually fit can be left reported
// on a sheet while it hangs OUT OF BOUNDS (x/y outside [0,sheet_w]x[0,sheet_h]) or overlaps a sheet hole —
// i.e. it consumed a sheet for nothing. Demote every such placement to UNPLACED (remove it from the result)
// so the host lays it out OUTSIDE the sheets. Sheets are recounted (a sheet emptied this way is dropped).
static void drop_invalid_placements(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                                    const std::vector<std::vector<Polygon>>& per_sheet_holes,
                                    f32 sheet_w, f32 sheet_h) {
    if (!g_drop_invalid) return;
    auto& P = res.placements;
    const f32 TOL = 1.0f;
    auto holes_for = [&](int s) -> const std::vector<Polygon>* {
        if (per_sheet_holes.empty()) return nullptr;
        usize hi = (s >= 0 && s < (int)per_sheet_holes.size()) ? (usize)s : per_sheet_holes.size() - 1;
        return &per_sheet_holes[hi];
    };
    std::vector<SheetPlacement> keep; keep.reserve(P.size());
    for (auto& pl : P) {
        usize c = pl.color_id;
        if (c >= parts.size()) { keep.push_back(pl); continue; }
        Polygon poly = parts[c].first.collision_shape->transform_clone(pl.d_transf.compose());
        bool valid = poly.bbox.x_min >= -TOL && poly.bbox.y_min >= -TOL &&
                     poly.bbox.x_max <= sheet_w + TOL && poly.bbox.y_max <= sheet_h + TOL;
        if (valid) {
            if (const std::vector<Polygon>* H = holes_for(pl.sheet))
                for (const auto& h : *H)
                    if (bbox_overlap(poly.bbox, h.bbox) && brute_overlap(poly, h)) { valid = false; break; }
        }
        if (valid) keep.push_back(pl);   // else: dropped -> the part stays unplaced (out_sheet_id == -1)
    }
    if (keep.size() == P.size()) return;
    P.swap(keep);
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    std::vector<int> used(ns, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns) used[p.sheet] = 1;
    std::vector<int> remap(ns, -1); int next = 0;
    for (int s = 0; s < ns; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next;
}

// Minimum bounding height of a polygon over all rotations (= its minimum width): rotate the verts and take
// the smallest (max_y - min_y) across `samples` orientations spanning [0,pi). A part whose min width exceeds
// the strip height can't fit the sheet at ANY orientation, so it must be set aside (else it poisons the nest).
static f32 min_bounding_height(const Polygon& p, int samples) {
    const auto& V = p.vertices;
    if (V.empty()) return 0.0f;
    const f32 PI = 3.14159265358979f;
    f32 best = std::numeric_limits<f32>::max();
    for (int k = 0; k < samples; ++k) {
        f32 a = PI * (f32)k / (f32)samples;
        f32 c = std::cos(a), s = std::sin(a);
        f32 mn = std::numeric_limits<f32>::max(), mx = -mn;
        for (const auto& v : V) { f32 y = s * v.x + c * v.y; if (y < mn) mn = y; if (y > mx) mx = y; }
        f32 h = mx - mn; if (h < best) best = h;
    }
    return best;
}

// HOLES-FIRST pre-pairing (g_holes_first): BEFORE the sheet nest, assign smaller parts INTO bigger parts'
// holes (in the host's CENTERED frame, where the holes live in part_holes_centered). Each assigned small is
// removed from the main nest and rides inside its host; after the host is placed, the small's sheet pose =
// host_transform o rel. Mirrors fill_cavities' fit test (vertices inside the hole ring + no edge crossing +
// no clash with siblings already placed in that hole) but in centered (un-placed) coordinates. Returns the
// pairs (small,host,rel); `deferred[i]` marks the smalls removed from the main nest.
struct HolePair { int small; int host; RigidTransform rel; };
static std::vector<HolePair> precompute_hole_pairs(
        const std::vector<std::pair<Part, usize>>& parts,
        const std::vector<std::vector<Polygon>>& part_holes_centered,
        std::vector<char>& deferred) {
    int N = (int)parts.size();
    deferred.assign((size_t)N, 0);
    std::vector<HolePair> pairs;
    const f32 TWO_PI = 6.28318530718f;
    const int NROT = 16, G = 24;
    for (int h = 0; h < N; ++h) {
        if ((usize)h >= part_holes_centered.size()) continue;
        const std::vector<Polygon>& holes = part_holes_centered[(usize)h];
        for (const auto& hole : holes) {
            std::vector<Polygon> occ;                  // smalls already seated in THIS hole
            f32 harea = hole.area;
            std::vector<int> cand;                     // unpaired parts smaller than the hole, biggest first
            for (int s = 0; s < N; ++s)
                if (s != h && !deferred[(size_t)s] && parts[(size_t)s].first.collision_shape &&
                    parts[(size_t)s].first.collision_shape->area < harea)
                    cand.push_back(s);
            std::stable_sort(cand.begin(), cand.end(), [&](int a, int b) {
                return parts[(size_t)a].first.collision_shape->area > parts[(size_t)b].first.collision_shape->area; });
            for (int s : cand) {
                if (deferred[(size_t)s]) continue;
                const Polygon& ss = *parts[(size_t)s].first.collision_shape;
                bool done = false;
                for (int r = 0; r < NROT && !done; ++r) {
                    f32 ang = TWO_PI * (f32)r / (f32)NROT;
                    Polygon srot = ss.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());
                    if (srot.bbox.width() > hole.bbox.width() + 1e-3f || srot.bbox.height() > hole.bbox.height() + 1e-3f) continue;
                    f32 spanx = hole.bbox.width() - srot.bbox.width(), spany = hole.bbox.height() - srot.bbox.height();
                    for (int iy = 0; iy <= G && !done; ++iy)
                        for (int ix = 0; ix <= G && !done; ++ix) {
                            f32 tx = hole.bbox.x_min - srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                            f32 ty = hole.bbox.y_min - srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                            Polygon cand_poly = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                            bool inside = true;
                            for (const auto& v : cand_poly.vertices) if (!collides(hole, v)) { inside = false; break; }
                            if (!inside) continue;
                            bool cross = false;
                            for (usize ei = 0; ei < cand_poly.n_vertices() && !cross; ++ei)
                                for (usize ej = 0; ej < hole.n_vertices(); ++ej)
                                    if (collides(cand_poly.edge(ei), hole.edge(ej))) { cross = true; break; }
                            if (cross) continue;
                            bool clash = false;
                            for (const auto& o : occ) if (brute_overlap(cand_poly, o)) { clash = true; break; }
                            if (clash) continue;
                            pairs.push_back({s, h, RigidTransform(ang, tx, ty)});
                            occ.push_back(std::move(cand_poly));
                            deferred[(size_t)s] = 1; done = true;
                        }
                }
            }
        }
    }
    return pairs;
}

// SHEET GAP-FILL post-pass (g_fill_gaps): the greedy packs big-first and carries the spill to the next
// sheet, but a SMALLER part can be stranded on a later sheet while it would fit a GAP a bigger part left
// on an earlier sheet. This pass pulls such parts FORWARD: for each sheet (earliest first) it grid-searches
// every part currently on a LATER sheet (smallest-area first, with rotation) into the sheet's open free
// space. A move is committed only if the candidate lies wholly within [0,sheet_w]x[0,sheet_h], clear of
// every part already on that sheet AND every sheet hole (exact brute_overlap) — verified moves only, so it
// can never create an overlap; it can only fill gaps and empty later sheets. Sheets are recounted after.
static void fill_sheet_gaps(Sheets& res, const std::vector<std::pair<Part, usize>>& parts,
                            const std::vector<std::vector<Polygon>>& per_sheet_holes,
                            f32 sheet_w, f32 sheet_h) {
    if (!g_fill_gaps) return;
    auto& P = res.placements;
    int N = (int)P.size();
    int ns = res.n_sheets > 0 ? res.n_sheets : 1;
    if (N < 2 || ns < 2) return;                 // only meaningful when there's a later sheet to pull from
    const f32 TWO_PI = 6.28318530718f;
    int NROT = 24, G = 40;                        // rotations x position-grid resolution per candidate (fine,
                                                  // so fragmented frame gaps are actually found). Env-tunable.
    if (const char* e = std::getenv("NP_GAP_ROT")) { int v = std::atoi(e); if (v > 0) NROT = v; }
    if (const char* e = std::getenv("NP_GAP_GRID")) { int v = std::atoi(e); if (v > 0) G = v; }

    auto holes_for = [&](int s) -> const std::vector<Polygon>* {
        if (per_sheet_holes.empty()) return nullptr;
        usize hi = (s >= 0 && s < (int)per_sheet_holes.size()) ? (usize)s : per_sheet_holes.size() - 1;
        return &per_sheet_holes[hi];
    };

    // smallest-area first: a small part is the one most likely to drop into a leftover gap.
    std::vector<int> order(N);
    for (int i = 0; i < N; ++i) order[i] = i;
    std::stable_sort(order.begin(), order.end(), [&](int a, int b) {
        const Polygon* pa = ((usize)P[a].color_id < parts.size()) ? parts[P[a].color_id].first.collision_shape.get() : nullptr;
        const Polygon* pb = ((usize)P[b].color_id < parts.size()) ? parts[P[b].color_id].first.collision_shape.get() : nullptr;
        f32 aa = pa ? pa->area : 0.0f, ab = pb ? pb->area : 0.0f;
        return aa < ab; });

    for (int ts = 0; ts < ns; ++ts) {
        const std::vector<Polygon>* holes = holes_for(ts);
        std::vector<Polygon> occ;                // parts already on the target sheet (grows as we add)
        for (int k = 0; k < N; ++k)
            if (P[k].sheet == ts && (usize)P[k].color_id < parts.size())
                occ.push_back(parts[P[k].color_id].first.collision_shape->transform_clone(P[k].d_transf.compose()));

        for (int oi = 0; oi < N; ++oi) {
            int si = order[oi];
            if (P[si].sheet <= ts) continue;     // only pull a part FORWARD from a later sheet
            usize sc = P[si].color_id; if (sc >= parts.size()) continue;
            const Polygon& sshape = *parts[sc].first.collision_shape;
            bool placed = false;
            for (int r = 0; r < NROT && !placed; ++r) {
                f32 ang = TWO_PI * (f32)r / (f32)NROT;
                Polygon srot = sshape.transform_clone(RigidTransform(ang, 0.0f, 0.0f).compose());
                if (srot.bbox.width() > sheet_w + 1e-3f || srot.bbox.height() > sheet_h + 1e-3f) continue;
                f32 spanx = sheet_w - srot.bbox.width(), spany = sheet_h - srot.bbox.height();
                for (int iy = 0; iy <= G && !placed; ++iy)
                    for (int ix = 0; ix <= G && !placed; ++ix) {
                        f32 tx = -srot.bbox.x_min + (spanx > 0 ? spanx * (f32)ix / (f32)G : 0.0f);
                        f32 ty = -srot.bbox.y_min + (spany > 0 ? spany * (f32)iy / (f32)G : 0.0f);
                        Polygon cand = srot.transform_clone(AffineTransform::from_translation(tx, ty));
                        if (cand.bbox.x_min < -1e-3f || cand.bbox.y_min < -1e-3f ||
                            cand.bbox.x_max > sheet_w + 1e-3f || cand.bbox.y_max > sheet_h + 1e-3f) continue;
                        bool bad = false;
                        if (holes) for (const auto& h : *holes) {
                            if (!bbox_overlap(cand.bbox, h.bbox)) continue;
                            if (brute_overlap(cand, h)) { bad = true; break; }
                        }
                        if (bad) continue;
                        for (const auto& o : occ) {
                            if (!bbox_overlap(cand.bbox, o.bbox)) continue;
                            if (brute_overlap(cand, o)) { bad = true; break; }
                        }
                        if (bad) continue;
                        P[si].d_transf = RigidTransform(ang, tx, ty);   // commit: pull si onto sheet ts
                        P[si].verts = cand.vertices;
                        P[si].sheet = ts;
                        occ.push_back(std::move(cand));
                        placed = true;
                    }
            }
        }
    }

    int ns2 = res.n_sheets > 0 ? res.n_sheets : 1;
    std::vector<int> used(ns2, 0);
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns2) used[p.sheet] = 1;
    std::vector<int> remap(ns2, -1); int next = 0;
    for (int s = 0; s < ns2; ++s) if (used[s]) remap[s] = next++;
    for (auto& p : P) if (p.sheet >= 0 && p.sheet < ns2 && remap[p.sheet] >= 0) p.sheet = remap[p.sheet];
    res.n_sheets = next > 0 ? next : res.n_sheets;
}

extern "C" NP_EXPORT int np_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    int           sheet_count,
    const int*    sheet_outer_vertex_counts,
    const double* sheet_outer_xy,
    const int*    sheet_hole_counts,
    const int*    hole_vertex_counts,
    const double* hole_xy,
    const int*    part_hole_counts,
    const int*    part_hole_vertex_counts,
    const double* part_hole_xy,
    const NpParams* params,
    double*       out_tx,
    double*       out_ty,
    double*       out_angle,
    int*          out_sheet_id,
    int*          out_n_sheets)
{
    try {
        if (out_n_sheets) *out_n_sheets = 0;
        for (int i = 0; i < part_count; ++i) {
            if (out_tx) out_tx[i] = 0.0;
            if (out_ty) out_ty[i] = 0.0;
            if (out_angle) out_angle[i] = 0.0;
            if (out_sheet_id) out_sheet_id[i] = -1;
        }
        if (!params || part_count <= 0 || sheet_count <= 0) return 0;

        NpParams P = *params;
        ROT_N_SAMPLES = (usize)(P.num_rotations < 1 ? 1 : P.num_rotations);
        g_iter_mode = (P.iter_mode != 0);
        g_iter_count = 0;
        g_iter_pub.store(0, std::memory_order_relaxed);   // reset the host-facing progress sum
        g_cancel.store(false);   // clear any stale cancel from a previous (cancelled) run

        // Adaptive preview vs quality (the C ABI owns these globals; set them every call). A tiny
        // iteration budget runs a FAST rough look — few best-of-N workers + lean poles, ~sub-second —
        // because the per-round cost is dominated by copying collision state to every worker. A large
        // budget uses the full best-of-N + dense poles for the tight all-on-one-sheet pack.
        // STEP 1 post-relaxation compaction: driven by the component's "compact" option (P.final_compact);
        // NP_COMPACT env overrides for dev A/B. Monotone + collision-checked, so OFF == baseline.
        g_final_compact = (P.final_compact != 0);
        if (const char* e = std::getenv("NP_COMPACT")) g_final_compact = (std::atoi(e) != 0);
        // Multi-directional slide-to-contact variant (CG-SHOP 2024 / Shadoks). Component selects it with
        // final_compact==2 (1 = plain bottom-left baseline). NP_COMPACT_MULTIDIR env overrides for dev A/B.
        g_compact_multidir = (P.final_compact >= 2);
        if (const char* e = std::getenv("NP_COMPACT_MULTIDIR")) g_compact_multidir = (std::atoi(e) != 0);
        // Sheet-hole keep-out guard for compaction (default on). NP_COMPACT_HOLE_GUARD=0 disables it for A/B.
        g_compact_hole_guard = true;
        if (const char* e = std::getenv("NP_COMPACT_HOLE_GUARD")) g_compact_hole_guard = (std::atoi(e) != 0);
        // Sheet gap-fill post-pass (default on). NP_FILL_GAPS=0 disables it for A/B.
        g_fill_gaps = true;
        if (const char* e = std::getenv("NP_FILL_GAPS")) g_fill_gaps = (std::atoi(e) != 0);
        // Drop out-of-bounds / in-hole placements -> unplaced (default on). NP_DROP_INVALID=0 disables for A/B.
        g_drop_invalid = true;
        if (const char* e = std::getenv("NP_DROP_INVALID")) g_drop_invalid = (std::atoi(e) != 0);
        // STEP 3 feasibility-wall bisection: dev-only env (no measured benefit on near-frontier instances;
        // the limiter there is the arrangement/basin, not the width-step — so it is NOT a component option).
        g_expl_bisect = false;
        if (const char* e = std::getenv("NP_BISECT")) g_expl_bisect = (std::atoi(e) != 0);

        bool preview = g_iter_mode && P.iter_budget > 0 && P.iter_budget < 500;
        g_n_workers = preview ? 4u : 0u;   // 0 => resolve to ~75% of cores
        // Surrogate pole cap: preview => 12 (fast rough look); else the component's "poles" option
        // (P.pole_max>0, ~16 is ~2.7x faster per round and not worse), else default 48. NP_POLES env overrides.
        g_pole_max  = preview ? 12u : (P.pole_max > 0 ? (unsigned)P.pole_max : 0u);
        if (const char* e = std::getenv("NP_POLES")) { int p = std::atoi(e); if (p > 0) g_pole_max = (unsigned)p; }

        // spacing -> inflate each part by spacing/2 so two neighbours keep `spacing` apart.
        ShapeModifyConfig mc = part_modify();
        if (P.spacing > 0.0) mc.offset = (f32)(P.spacing * 0.5);
        if (P.simplify_tolerance > 0.0) mc.simplify_tolerance = (f32)P.simplify_tolerance;

        // Build parts. Degenerate parts are skipped and stay unplaced (sheet id -1). `craw`/`orig_index`
        // stay parallel to the solver parts list (whose ids are the greedy_fill color_id).
        std::vector<std::pair<Part, usize>> parts;
        std::vector<Point> craw;
        std::vector<int> orig_index;
        std::vector<std::vector<Polygon>> part_holes_centered;  // per-part interior holes (centered frame) for the cavity-fill post-pass
        int pcur = 0;
        // FILL-HOLES mode: read each part's interior holes so other parts may nest inside them. Cursors
        // advance whenever the arrays are present (mode-independent) so they stay aligned; the holes are
        // only ATTACHED when part_holes_mode==1 (else this is byte-identical to the keep-empty path).
        bool have_part_holes = part_hole_counts && part_hole_vertex_counts && part_hole_xy;
        bool fill_holes = (P.part_holes_mode >= 1) && have_part_holes;   // 1 = post-pass, 2 = holes-first
        g_holes_first = (P.part_holes_mode >= 2) && have_part_holes;
        if (const char* e = std::getenv("NP_HOLES_FIRST")) g_holes_first = (std::atoi(e) != 0) && have_part_holes;
        int phvcur = 0, phcur = 0;
        for (int i = 0; i < part_count; ++i) {
            std::vector<Point> pts = read_poly(part_xy, part_vertex_counts[i], pcur);
            std::vector<std::vector<Point>> phs;
            if (have_part_holes) {
                int nh = part_hole_counts[i];
                for (int h = 0; h < nh; ++h) {
                    int nv = part_hole_vertex_counts[phvcur++];
                    std::vector<Point> hp = read_poly(part_hole_xy, nv, phcur);
                    if (fill_holes) phs.push_back(std::move(hp));
                }
            }
            Point cc;
            auto p = build_part(parts.size(), std::move(pts), &cc, &mc);
            if (p) {
                parts.emplace_back(std::move(*p), 1); craw.push_back(cc); orig_index.push_back(i);
                // Centered part-holes (input hole - input centroid) kept in a SIDE-TABLE parallel to
                // `parts`, NOT attached to the Polygon: the relaxation engine stays solid / hole-unaware
                // (byte-identical no-hole behaviour); only the isolated cavity-fill post-pass reads these
                // to relocate small parts into cavities.
                std::vector<Polygon> chs;
                if (fill_holes) for (auto& hin : phs) {
                    std::vector<Point> hc;
                    hc.reserve(hin.size());
                    for (auto pt : hin) hc.push_back(Point(pt.x - cc.x, pt.y - cc.y));
                    std::vector<Point> cl = clean_ring(std::move(hc));
                    if (cl.size() >= 3) { try { chs.push_back(Polygon::create(std::move(cl))); } catch (...) {} }
                }
                part_holes_centered.push_back(std::move(chs));
            }
        }
        if (parts.empty()) return 0;

        // Publish the centering/index mapping + open the live-preview channel for np_poll_layout.
        {
            std::lock_guard<std::mutex> lk(g_live.mtx);
            g_poll_orig_index = orig_index;
            g_poll_craw = craw;
            g_live.items.clear();
            g_live.gen = 0;
            g_live.active = true;
        }

        // Sheet dimensions = first sheet's bbox (v1). Holes are translated into each sheet's local
        // frame (origin at that sheet's bbox min) so they line up with the strip-packing frame.
        int ocur = 0, hcur = 0, hszcur = 0;
        f32 sheet_w = 0.0f, sheet_h = 0.0f;
        std::vector<std::vector<Polygon>> per_sheet_holes((usize)sheet_count);
        bool any_hole = false;
        for (int s = 0; s < sheet_count; ++s) {
            std::vector<Point> outer = read_poly(sheet_outer_xy, sheet_outer_vertex_counts[s], ocur);
            f32 minx = F32_MAX, miny = F32_MAX, maxx = F32_MIN, maxy = F32_MIN;
            for (auto& pt : outer) {
                minx = min_f(minx, pt.x); miny = min_f(miny, pt.y);
                maxx = max_f(maxx, pt.x); maxy = max_f(maxy, pt.y);
            }
            if (s == 0) { sheet_w = maxx - minx; sheet_h = maxy - miny; }
            int nh = sheet_hole_counts ? sheet_hole_counts[s] : 0;
            for (int h = 0; h < nh; ++h) {
                int nv = hole_vertex_counts[hszcur++];
                std::vector<Point> hp = read_poly(hole_xy, nv, hcur);
                for (auto& pt : hp) { pt.x -= minx; pt.y -= miny; }   // -> sheet-local
                std::vector<Point> cl = clean_ring(std::move(hp));
                if (cl.size() < 3) continue;
                try { per_sheet_holes[(usize)s].push_back(Polygon::create(std::move(cl))); any_hole = true; }
                catch (...) { /* skip degenerate/self-intersecting hole */ }
            }
        }

        CollisionConfig engine;
        engine.quadtree_depth = 4; engine.cd_threshold = 16;
        engine.part_surrogate_config = surrogate_config();

        double budget = g_iter_mode ? (double)P.iter_budget : P.time_budget_secs;
        int max_bins = P.max_sheets > 0 ? P.max_sheets : 6;
        const int fit_mode = P.fit_mode;
        if (fit_mode == 1) max_bins = 1;   // one-sheet max-fill: never open a 2nd sheet; overflow stays unplaced
        uint64_t seed = (uint64_t)(P.seed >= 0 ? P.seed : 0);
        int n_starts = P.n_starts > 0 ? P.n_starts : 1;
        const std::vector<std::vector<Polygon>> empty_holes;
        const std::vector<std::vector<Polygon>>& holes_arg = any_hole ? per_sheet_holes : empty_holes;

        // Build the list the multi-start actually packs (nest_parts), EXCLUDING:
        //  (a) OVERSIZED parts whose minimum width > the strip height -> they can't fit at ANY rotation and
        //      would poison the whole relaxation; set aside as unplaced (the host lays them OUTSIDE).
        //  (b) HOLES-FIRST deferred smalls -> pre-paired inside a bigger part's hole, re-added after placement.
        // hf_keep maps each kept (nested) part back to its full index for the post-nest remap.
        int NP_all = (int)parts.size();
        std::vector<char> oversized((size_t)NP_all, 0);
        for (int i = 0; i < NP_all; ++i)
            if (parts[(size_t)i].first.collision_shape &&
                min_bounding_height(*parts[(size_t)i].first.collision_shape, 180) > sheet_h * 1.001f)
                oversized[(size_t)i] = 1;
        std::vector<char> hf_deferred((size_t)NP_all, 0);
        std::vector<HolePair> hf_pairs;
        if (g_holes_first) hf_pairs = precompute_hole_pairs(parts, part_holes_centered, hf_deferred);
        std::vector<int> hf_keep;
        std::vector<std::pair<Part, usize>> parts_reduced;
        for (int i = 0; i < NP_all; ++i)
            if (!oversized[(size_t)i] && !hf_deferred[(size_t)i]) { hf_keep.push_back(i); parts_reduced.push_back(parts[(size_t)i]); }
        bool reduced = ((int)hf_keep.size() != NP_all);
        const std::vector<std::pair<Part, usize>>& nest_parts = reduced ? parts_reduced : parts;

        // Multi-start: run n_starts independent seeds (each at the given budget) and keep the densest
        // (fewest sheets, then smallest last-sheet width). Robust against this instance's run-to-run
        // variance; n_starts==1 is the original single run.
        const size_t n_to_place = nest_parts.size();
        auto score = [&](const Sheets& r) -> double {
            double w = r.widths.empty() ? 0.0 : r.widths.back();
            if (fit_mode == 1) {
                // one-sheet max-fill: MAXIMISE parts on the single sheet (fewest left off), then tighter.
                long overflow = (long)n_to_place - (long)r.placements.size();
                if (overflow < 0) overflow = 0;
                return (double)overflow * 1e9 + w;     // lower = fewer left off = fuller sheet
            }
            double pen = r.ok ? 0.0 : 1e15;            // unplaced parts => very bad
            return pen + (double)r.n_sheets * 1e9 + w; // lower is better
        };
        Sheets res;
        bool have = false;
        if (n_starts <= 1) {
            res = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins, holes_arg, seed);
            have = true;
        } else {
            // MULTI-START portfolio. DEFAULT = SEQUENTIAL full-worker starts: each start gets the FULL
            // best-of-~75%-cores worker pool (strongest per start; the proven 47/1 recipe), run one at a
            // time, keeping the densest by `score`, with EARLY-EXIT the instant a start lands one sheet
            // (the goal -> no point continuing). On a ~1/6-per-start instance this needs ~6 starts on
            // average to hit one sheet, but stops as soon as it does. Set NP_PARALLEL_STARTS to force the
            // old concurrent portfolio (shorter wall, but each start is weaker -> less reliable here).
            const bool parallel_starts = (std::getenv("NP_PARALLEL_STARTS") != nullptr);
            if (!parallel_starts) {
                g_n_workers = preview ? 4u : 0u;   // full strength per start (0 => ~75% cores)
                if (const char* e = std::getenv("NP_STARTS_WORKERS")) { int w = std::atoi(e); if (w > 0) g_n_workers = (unsigned)w; }
                for (int s = 0; s < n_starts && !g_cancel.load(); ++s) {
                    uint64_t st_seed = seed + (uint64_t)s * 1009ull;
                    Sheets r = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins, holes_arg, st_seed, /*publish_live=*/true);
                    int rn = r.n_sheets; double rw = r.widths.empty() ? 0.0 : r.widths.back();
                    if (!have || score(r) < score(res)) { res = std::move(r); have = true; }
                    std::fprintf(stderr, "[np] multistart %d/%d seed=%llu -> %d sheet(s) w=%.1f (best so far=%d)\n",
                                 s + 1, n_starts, (unsigned long long)st_seed, rn, rw, res.n_sheets);
                    // stop early when "done enough": all parts on one sheet (max-fill) or a single sheet (min-sheets)
                    if (fit_mode == 1 ? (res.placements.size() >= n_to_place) : (res.n_sheets <= 1)) break;
                }
            } else {
                // ---- concurrent portfolio: faster wall, weaker per start. Early-exits per wave. ----
                unsigned hw = std::thread::hardware_concurrency(); if (hw == 0) hw = 4;
                unsigned cores75 = (hw * 3) / 4; if (cores75 < 1) cores75 = 1;
                int conc = (int)std::min<unsigned>((unsigned)n_starts, cores75);
                if (!preview) { g_n_workers = cores75 / (unsigned)conc; if (g_n_workers < 1) g_n_workers = 1; }
                if (const char* e = std::getenv("NP_STARTS_WORKERS")) {
                    int w = std::atoi(e);
                    if (w > 0) { g_n_workers = (unsigned)w; conc = (int)std::min<unsigned>((unsigned)n_starts, std::max(1u, cores75 / g_n_workers)); }
                }
                std::vector<Sheets> results((size_t)n_starts);
                std::vector<char> done((size_t)n_starts, 0);
                for (int base = 0; base < n_starts && !g_cancel.load(); base += conc) {
                    int hi = std::min(n_starts, base + conc);
                    std::vector<std::thread> ths;
                    for (int s = base; s < hi; ++s) {
                        uint64_t st_seed = seed + (uint64_t)s * 1009ull;
                        bool pub = (s == 0);   // only the first start streams the live preview
                        ths.emplace_back([&, s, st_seed, pub]() {
                            results[(size_t)s] = greedy_fill(nest_parts, engine, sheet_w, sheet_h, budget, max_bins, holes_arg, st_seed, pub);
                            done[(size_t)s] = 1;
                        });
                    }
                    for (auto& t : ths) t.join();
                    for (int s = base; s < hi; ++s) {           // fold in this wave, then early-exit on one sheet
                        if (!done[(size_t)s]) continue;
                        if (!have || score(results[(size_t)s]) < score(res)) { res = results[(size_t)s]; have = true; }
                    }
                    if (have && (fit_mode == 1 ? (res.placements.size() >= n_to_place) : (res.n_sheets <= 1))) break;
                }
            }
        }

        // If the nest list was REDUCED (oversized parts set aside and/or holes-first deferrals), res.placements
        // color_ids index parts_reduced -> remap them back to full part indices. (Oversized parts simply stay
        // unplaced; the host lays them outside.)
        if (reduced) {
            for (auto& pl : res.placements)
                if ((usize)pl.color_id < hf_keep.size()) pl.color_id = (usize)hf_keep[(size_t)pl.color_id];
        }
        // HOLES-FIRST: re-add each deferred small INSIDE its now-placed host:
        // small_sheet_pose = host_pose o rel (rel applied first, then the host transform).
        if (g_holes_first) {
            for (const auto& pr : hf_pairs) {
                int hsheet = -1; RigidTransform hT;
                for (const auto& pl : res.placements)
                    if ((int)pl.color_id == pr.host) { hsheet = pl.sheet; hT = pl.d_transf; break; }
                if (hsheet < 0) continue;     // host unplaced -> leave the small unplaced (host shows it outside)
                RigidTransform sd = pr.rel.compose().transform(hT.compose()).decompose();
                SheetPlacement sp; sp.color_id = (usize)pr.small; sp.sheet = hsheet; sp.d_transf = sd;
                sp.verts = parts[(size_t)pr.small].first.collision_shape->transform_clone(sd.compose()).vertices;
                res.placements.push_back(std::move(sp));
            }
        }

        // FILL-HOLES: actively pull smaller parts into larger parts' cavities (the relaxation only does
        // so under width pressure). Verified collision-free moves only — cannot create overlaps.
        // (Skipped under holes-first: the pre-pairing above already seated the smalls in their holes.)
        if (fill_holes && !g_holes_first) fill_cavities(res, parts, part_holes_centered);
        // GAP-FILL: pull smaller parts FORWARD into earlier sheets' open gaps (so a small part isn't left
        // on a later sheet / outside while it fits where a bigger part couldn't). Verified moves only.
        fill_sheet_gaps(res, parts, holes_arg, sheet_w, sheet_h);
        // DROP INVALID: a part that doesn't actually fit (out of bounds / in a hole) is demoted to unplaced
        // (host shows it OUTSIDE the sheets) instead of wasting a fresh sheet on it.
        drop_invalid_placements(res, parts, holes_arg, sheet_w, sheet_h);

        for (const auto& pl : res.placements) {
            usize ci = pl.color_id;            // solver part index
            if (ci >= orig_index.size()) continue;
            int oi = orig_index[ci];           // original input index
            // Fold the part-centering (translate(-input_centroid)) into the placement transform so
            // the host applies ONE rigid transform (rotate about origin, then translate) to its
            // original geometry. M_pre.transform(D) = D . M_pre  => center first, then place.
            AffineTransform mpre = AffineTransform::from_translation(-craw[ci].x, -craw[ci].y);
            AffineTransform finalT = mpre.transform(pl.d_transf.compose());
            RigidTransform rd = finalT.decompose();
            if (out_angle) out_angle[oi] = (double)rd.rotation;
            if (out_tx) out_tx[oi] = (double)rd.tx;
            if (out_ty) out_ty[oi] = (double)rd.ty;
            if (out_sheet_id) out_sheet_id[oi] = pl.sheet;
        }
        if (out_n_sheets) *out_n_sheets = res.n_sheets;
        { std::lock_guard<std::mutex> lk(g_live.mtx); g_live.active = false; }
        return 0;
    } catch (...) {
        { std::lock_guard<std::mutex> lk(g_live.mtx); g_live.active = false; }
        return 1;
    }
}

// --- Cooperative cancel + progress (for an interactive/async host like the Grasshopper component) ---
// np_cancel() asks the currently-running np_nest to stop at the next relaxation round and return the
// best layout it has so far (it does NOT abort mid-evaluation, so the returned nest is always valid).
// np_progress() returns the relaxation-round counter, monotonically increasing during a solve, so the
// host can show a live "rounds done" readout while np_nest runs on a background thread.
extern "C" NP_EXPORT void np_cancel()       { g_cancel.store(true); }
extern "C" NP_EXPORT void np_cancel_reset() { g_cancel.store(false); }
extern "C" NP_EXPORT long long np_progress() { return (long long)g_iter_pub.load(std::memory_order_relaxed); }

// Snapshot the CURRENT best layout mid-solve for an animated host preview. Bands the running strip
// into sheet-width columns and folds in part-centering, exactly like np_nest's final output, so the
// preview is in the same (angle, tx, ty, sheet) convention. Host preallocates buffers length
// part_count; unplaced parts come back as sheet_id -1. Returns the number of placed parts (0 if no
// solve is active yet). Thread-safe: reads g_live under its mutex while the solve thread writes it.
extern "C" NP_EXPORT int np_poll_layout(
    int part_count,
    double* out_tx, double* out_ty, double* out_angle, int* out_sheet_id, int* out_n_sheets)
{
    for (int i = 0; i < part_count; ++i) {
        if (out_tx) out_tx[i] = 0.0; if (out_ty) out_ty[i] = 0.0;
        if (out_angle) out_angle[i] = 0.0; if (out_sheet_id) out_sheet_id[i] = -1;
    }
    if (out_n_sheets) *out_n_sheets = 0;
    std::lock_guard<std::mutex> lk(g_live.mtx);
    if (!g_live.active || g_live.items.empty()) return 0;
    f32 sw = g_live.sheet_w > 0.0f ? g_live.sheet_w : 1.0f;
    int base = g_live.sheet_base, maxb = g_live.max_bins;
    int max_sheet = 0, written = 0;
    for (const auto& it : g_live.items) {
        usize ci = it.color_id;
        if (ci >= g_poll_orig_index.size() || ci >= g_poll_craw.size()) continue;
        int oi = g_poll_orig_index[ci];
        if (oi < 0 || oi >= part_count) continue;
        int band = (int)(it.xmin / sw); if (band < 0) band = 0;
        int sh = base + band; if (sh > maxb - 1) sh = maxb - 1;
        f32 dx = -(f32)(sh - base) * sw;
        AffineTransform mpre = AffineTransform::from_translation(-g_poll_craw[ci].x, -g_poll_craw[ci].y);
        RigidTransform dtb = it.dt; dtb.tx += dx;
        AffineTransform finalT = mpre.transform(dtb.compose());
        RigidTransform rd = finalT.decompose();
        if (out_angle) out_angle[oi] = (double)rd.rotation;
        if (out_tx) out_tx[oi] = (double)rd.tx;
        if (out_ty) out_ty[oi] = (double)rd.ty;
        if (out_sheet_id) out_sheet_id[oi] = sh;
        if (sh + 1 > max_sheet) max_sheet = sh + 1;
        ++written;
    }
    if (out_n_sheets) *out_n_sheets = max_sheet;
    return written;
}
