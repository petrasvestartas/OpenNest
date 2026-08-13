// Rhino-free reproduction harness for OpenNestCollision (nest_physics.dll / .dylib).
//
// Drives np_nest through the SAME C ABI + the SAME argument marshalling that
// src/opennest_2/nest_lib/NestPhysicsWrapper/NestPhysicsWrapper.cs uses, and reassembles the world
// placement exactly like NpRun.PartTransform in src/opennest_2/grasshopper/component_nest.cs:
//
//     world = Translation(sheetOrigin[sid]) * Translation(tx,ty) * Rotation(angle) * to_xy
//
// (to_xy is applied by the host BEFORE the solve, so the vertices we hand the solver are already the
// part in its own local frame; here the harness works directly in that frame.) Because it measures the
// HOST-side reassembly, it catches the whole class of bug where the solver's own numbers are fine but
// the sheet the part is reported on is wrong — which is exactly what --sw2/--sh2 exercises.
//
// Metrics mirror src/opennest_cpp/bench/nfp_bench.cpp: placed count, per-sheet distribution,
// pairwise overlap area (exact, convex clip), and out-of-bounds area.
//
// Build (opt-in; never part of the shipped DLL build):
//     cmake -S src/nest_physics_cpp -B build -A x64 -DNEST_PHYSICS_BUILD_BENCH=ON
//     cmake --build build --config Release --target np_bench
//
// Regression cases that matter:
//     np_bench --parts=24 --sheets=6 --iters=1000                     # baseline multi-sheet spread
//     np_bench --parts=24 --sheets=6 --sw2=600  --sh2=600             # MIXED sizes, small sheets later
//     np_bench --parts=24 --sheets=6 --sw=600 --sh=600 --sw2=1000 --sh2=1000   # big sheets later
//     np_bench --parts=40 --sheets=6 --iters=4000 --cancel_ms=2500    # ESC snapshot
// All of these must end "VERDICT: CLEAN" or "UNPLACED-SPILL"; OUT-OF-BOUNDS or OVERLAPPING is a bug.
//
// QUALITY-GATE regression (np_last_quality). The gate is the thing under test here, so every run also
// prints GATE CHECK: the engine's self-report next to this harness's INDEPENDENT measurement (exact
// convex-clip areas on the host-side world geometry). They must agree — a gate that cries wolf on a good
// layout is worse than no gate, and one that stays quiet on a bad one is worse still.
//     np_bench --frames=2 --parts=8 --holes=1     # legitimate hole-nesting, post-pass  -> must be CLEAN
//     np_bench --frames=2 --parts=8 --holes=2     # legitimate hole-nesting, holes-first-> must be CLEAN
//     np_bench --frames=2 --parts=8 --holes=1 --poke=1.0    # child shoved out of its hole -> must be DIRTY
//     np_bench --parts=12 --sheets=4 --shift=0.5           # a part shoved off its sheet -> must be caught
//     np_bench --parts=12 --sheets=4 --shift=0.5 --scale=0.001   # ...and identically at metre scale
//     np_bench --parts=40 --sheets=6 --sw2=600 --sh2=600 --cancel_ms=2500  # ESC on mixed sizes
// --frames=N makes the first N parts a FRAME (outer ring + one big hole, sent through the part_hole_*
// ABI arrays). The frame's convex decomposition is its MATERIAL bands, not its outer rectangle, so a part
// correctly nested in the frame's hole measures ZERO overlap area here — giving this harness ground truth
// about hole-nesting that is completely independent of the engine.
// --scale=<f> multiplies every length (parts and sheets). Every verdict must be scale-invariant: the
// same layout at --scale=1 (mm) and --scale=0.001 (m) must produce the same GATE CHECK and VERDICT.
//
// CONCAVE / NON-CONVEX coverage (added after the area-centroid erosion defect). Every earlier shape here
// was convex or an L, i.e. its area centroid sits INSIDE the material — which is precisely the family the
// old "scale toward the centroid" shrink happens to work on. A U / C part's centroid is outside the
// material, and a U-shaped CAVITY is the same problem for the hole-containment exemption:
//     # concave parts, part_holes_mode 0 (no holes ANYWHERE, so the hole exemption is not involved).
//     # The false positive needs parts actually pressed into each other's channels, so pack one sheet to
//     # max fill with slide-to-contact; a roomy multi-sheet run leaves the gaps too wide to trip it.
//     np_bench --shape=u --pw=250 --ph=250 --t=80 --parts=20 --sheets=1 --fit=1 --compact=2 --holes=0 \
//              --iters=2000 --seed=102        # sweep --seed over 100..111 for u AND c: measured 7 of those
//                                            # 24 runs DISAGREE with the old shrink, 0 with the fixed one
//     np_bench --parts=2 --frames=1 --fshape=u --cl=0.1     # U child in a U cavity, 0.1 units clearance:
//     np_bench --parts=2 --frames=1 --fshape=u --cl=5       # ...must be CLEAN at every cl from 0.1 to 5,
//     np_bench --parts=2 --frames=1 --fshape=u --cl=0.1 --holes=2      # ...in both hole modes,
//     np_bench --parts=2 --frames=1 --fshape=u --cl=0.1 --poke=0.0012  # ...and DIRTY once it crosses out
//     np_bench --parts=10 --sheets=3 --svoid=0.98    # sheet 0 is a 98% keep-out and the other two are
//                                                    # clean: no part may end up on sheet 0's keep-out
//     np_bench --parts=10 --sheets=3 --svoid=0.5 --svoidsheet=-1   # ...identical keep-outs on every sheet
//                                                    # stay interchangeable: layout must not change
//
// Flags added for the quality-gate work:
//   --shape=S         rect | mixed | ell | u | c   (u/c are CONCAVE: centroid outside the material)
//   --t=T             wall thickness for --shape=u/c and for a --fshape=u cavity (model units, scaled)
//   --frames=N        first N parts are frames (outer ring + one hole)
//   --fshape=S        rect (default) | u — shape of the frame's CAVITY. With `u`, every non-frame part
//                     becomes the matching U child: the cavity inset by --cl on every wall, so "wholly
//                     inside with uniform clearance cl" is exact by construction
//   --cl=C            that clearance in model units (scaled). 0.1 .. 5 must all report CLEAN
//   --fhole=F         frame hole size as a multiple of the nominal part (default 1.15; ~1.005 = snug,
//                     the case where the containment certificate is knife-edge against f32 round-off)
//   --svoid=F         give a sheet a centred rectangular KEEP-OUT covering fraction F of its area
//   --svoidsheet=I    which sheet carries it (default 0; -1 = every sheet)
//   --sizes=WxH,...   explicit per-sheet sizes, cycled. Three or more DISTINCT widths is what actually
//                     exercises per-sheet column geometry; --sw/--sw2 only ever gives two
//   --scale=F         multiply every length by F
//   --poke=F          inject NP_TEST_POKE: shove hole-nested children out through the cavity wall
//   --shift=F         inject NP_TEST_SHIFT: shove the right-most placement F x sheet width off its sheet
//   --dropinvalid=0|1 force NP_DROP_INVALID (0 = keep invalid placements so the gate has to catch them)
//   --dump=PATH       write the raw ABI placements at full precision, for a byte-exact A/B between builds
// NOTE --poke / --shift only do anything against a DLL built with -DNEST_PHYSICS_BUILD_BENCH=ON; a
// release DLL has those defect-injection seams compiled out entirely (NP_TEST_SEAMS undefined). Nothing
// is injected, so there is nothing for the gate to disagree ABOUT and the run is indistinguishable from a
// clean one: all three GATE CHECK counters print [agree], the verdict is CLEAN and the exit code is 0.
// Measured, against this tree's own release build:
//     np_bench --dll=<release nest_physics.dll> --frames=2 --parts=8 --holes=1 --poke=1.0
//         -> overlap [agree] | off-sheet-or-on-keepout [agree] | unplaced [agree] | demoted=0
//            VERDICT: CLEAN, exit 0
// The ONLY signal is the "NOTE: --poke/--shift produced NO measurable defect" line printed just above the
// verdict, which names both reasons (poke smaller than the clearance it has to cross, or seams compiled
// out). So a green exit code does not by itself prove an injection case injected anything — when you run
// one, read that NOTE line, and if it appears, point --dll at a BUILD_BENCH=ON build before believing the
// CLEAN. Exit code is 0 only when the engine's self-report agrees with this harness's measurement.
#if defined(_WIN32) || defined(_WIN64)
  #define WIN32_LEAN_AND_MEAN
  #define NOMINMAX
  #include <windows.h>
#else
  #include <dlfcn.h>
  #include <unistd.h>
  #include <pthread.h>
  #include <ctime>
  // Minimal shims so the body below stays one implementation on both platforms.
  typedef void* HMODULE;
  typedef unsigned long DWORD;
  static HMODULE LoadLibraryA(const char* p) { return dlopen(p, RTLD_NOW); }
  static void* GetProcAddress(HMODULE h, const char* n) { return dlsym(h, n); }
  static unsigned long GetLastError(void) { return 0; }
  static DWORD GetTickCount(void) {
      struct timespec ts; clock_gettime(CLOCK_MONOTONIC, &ts);
      return (DWORD)(ts.tv_sec * 1000ull + ts.tv_nsec / 1000000ull);
  }
  static void Sleep(DWORD ms) { usleep((useconds_t)ms * 1000); }
#endif
#include <cstring>
#include <cstdio>
#include <cstdlib>
#include <cmath>
#include <vector>
#include <string>
#include <map>
#include <algorithm>

// ---- mirror of NpParams (nest_physics_capi.h) -------------------------------------------------
struct NpParams {
    int       num_rotations;
    double    spacing;
    double    simplify_tolerance;
    int       seed;
    double    time_budget_secs;
    long long iter_budget;
    int       iter_mode;
    int       max_sheets;
    int       n_starts;
    int       part_holes_mode;
    int       pole_max;
    int       final_compact;
    int       fit_mode;
};

typedef int (*np_nest_fn)(int, const int*, const double*, const int*,
                          int, const int*, const double*, const int*, const int*, const double*,
                          const int*, const int*, const double*,
                          const NpParams*, double*, double*, double*, int*, int*);
typedef void (*np_cancel_fn)(void);
typedef void (*np_cancel_reset_fn)(void);
typedef int  (*np_last_quality_fn)(int*, int*, int*, int*, int*);

// Set an environment variable the DLL reads at np_nest entry (the NP_* dev/harness switches), so a
// regression case is one self-contained command line instead of a shell prelude.
static void set_env(const char* k, const char* v) {
#if defined(_WIN32) || defined(_WIN64)
    _putenv_s(k, v);
#else
    setenv(k, v, 1);
#endif
}

// Mirrors what component_nest.cs does on ESC: np_cancel() from the UI thread while np_nest runs on a
// background thread. The solve returns its best-so-far; the component then PUBLISHES that layout.
static np_cancel_fn g_np_cancel = nullptr;
static DWORD g_cancel_after_ms = 0;
#if defined(_WIN32) || defined(_WIN64)
static DWORD WINAPI cancel_thread(LPVOID) { Sleep(g_cancel_after_ms); if (g_np_cancel) g_np_cancel(); return 0; }
static void start_cancel_timer() { CloseHandle(CreateThread(NULL, 0, cancel_thread, NULL, 0, NULL)); }
#else
static void* cancel_thread(void*) { Sleep(g_cancel_after_ms); if (g_np_cancel) g_np_cancel(); return nullptr; }
static void start_cancel_timer() { pthread_t t; pthread_create(&t, nullptr, cancel_thread, nullptr); pthread_detach(t); }
#endif

// ---- tiny 2D polygon toolkit ------------------------------------------------------------------
struct P2 { double x, y; };
typedef std::vector<P2> Poly;

static double poly_area(const Poly& p) {
    double a = 0.0; size_t n = p.size();
    for (size_t i = 0; i < n; ++i) { const P2& u = p[i]; const P2& v = p[(i + 1) % n]; a += u.x * v.y - v.x * u.y; }
    return std::fabs(a) * 0.5;
}
// Sutherland-Hodgman: clip `sub` by the CONVEX polygon `clip` (CCW). Both test shapes are convex
// rectangles, so this yields the EXACT intersection area.
static Poly clip_convex(const Poly& sub, const Poly& clip) {
    Poly out = sub;
    size_t m = clip.size();
    for (size_t i = 0; i < m && !out.empty(); ++i) {
        P2 a = clip[i], b = clip[(i + 1) % m];
        double ex = b.x - a.x, ey = b.y - a.y;
        Poly in; in.swap(out);
        size_t n = in.size();
        for (size_t j = 0; j < n; ++j) {
            P2 c = in[j], d = in[(j + 1) % n];
            double sc = ex * (c.y - a.y) - ey * (c.x - a.x);   // >0 == left of edge == inside (CCW)
            double sd = ex * (d.y - a.y) - ey * (d.x - a.x);
            if (sc >= 0) out.push_back(c);
            if ((sc > 0 && sd < 0) || (sc < 0 && sd > 0)) {
                double t = sc / (sc - sd);
                out.push_back(P2{ c.x + t * (d.x - c.x), c.y + t * (d.y - c.y) });
            }
        }
    }
    return out;
}
static double inter_area(const Poly& a, const Poly& b) { return poly_area(clip_convex(a, b)); }

static Poly make_rect(double x, double y, double w, double h) {
    Poly p; p.push_back(P2{x, y}); p.push_back(P2{x + w, y}); p.push_back(P2{x + w, y + h}); p.push_back(P2{x, y + h});
    return p;   // CCW
}

int main(int argc, char** argv) {
    // ---- scenario knobs (defaults = the reported class of input) --------------------------------
    int    nparts   = 24;
    double pw       = 400, ph = 300;      // part size
    int    nsheets  = 6;
    double sw       = 1000, sh = 1000;    // sheet 0 size
    double sw2      = -1,   sh2 = -1;     // size of sheets 1.. (default: same as sheet 0)
    long long iters = 4000;               // component's "Iterations" default
    int    maxSheetsArg = -1;             // -1 => pass nsheets (what component_nest.cs does)
    int    nstarts  = 1, poles = 48, compact = 1, fitmode = 0, holes = 1, rots = 32, seed = 100;
    const char* shape = "rect";
    int    nframes  = 0;                  // first N parts are frames (outer ring + one hole)
    double fhole    = 1.15;               // frame hole size as a multiple of the nominal part: ~1.0 = a
                                          //   SNUG fit, the case where the containment certificate is
                                          //   knife-edge against f32 round-off
    const char* fshape = "rect";          // frame CAVITY shape: rect | u  (u => non-convex cavity)
    double wallt    = -1;                 // --t: wall thickness for u/c parts and u cavities (<=0 => auto)
    double clear    = 2.0;                // --cl: uniform clearance of the U child inside the U cavity
    double svoid    = 0.0;                // --svoid: sheet keep-out as a fraction of the sheet's area
    int    svoidsheet = 0;                // which sheet carries it (-1 = all)
    double scale    = 1.0;                // multiplies every length: 1 = mm-ish, 0.001 = the same job in m
    double poke = 0.0, shift = 0.0;       // defect injection through the DLL's harness seams
    int    dropinvalid = -1;              // -1 = leave the DLL default (on); 0/1 forces NP_DROP_INVALID
    const char* sizes = 0;                // "WxH,WxH,..." explicit per-sheet sizes, cycled; beats sw/sw2
    const char* dump  = 0;                // if set, write every raw placement here for a byte-exact A/B
#if defined(_WIN32) || defined(_WIN64)
    const char* dll = "./nest_physics.dll";
#else
    const char* dll = "./nest_physics.dylib";
#endif

    for (int i = 1; i < argc; ++i) {
        const char* a = argv[i];
        // Return a pointer INTO argv, which lives for the whole run. (It used to point into a std::string
        // local to this loop iteration, so any value kept past the iteration — --shape, and now --sizes —
        // was read back from freed storage. Short values survived on the small-string buffer by luck.)
        auto val = [&](const char* k) -> const char* {
            size_t kl = strlen(k);
            return (strncmp(a, k, kl) == 0 && a[kl] != '\0') ? a + kl : nullptr; };
        if (const char* v = val("--parts="))    nparts  = atoi(v);
        else if (const char* v = val("--pw="))  pw      = atof(v);
        else if (const char* v = val("--ph="))  ph      = atof(v);
        else if (const char* v = val("--sheets=")) nsheets = atoi(v);
        else if (const char* v = val("--sw="))  sw      = atof(v);
        else if (const char* v = val("--sh="))  sh      = atof(v);
        else if (const char* v = val("--sw2=")) sw2     = atof(v);   // size of sheets 1..n-1 (default = sheet 0)
        else if (const char* v = val("--sh2=")) sh2     = atof(v);
        else if (const char* v = val("--iters="))  iters = atoll(v);
        else if (const char* v = val("--maxsheets=")) maxSheetsArg = atoi(v);
        else if (const char* v = val("--starts=")) nstarts = atoi(v);
        else if (const char* v = val("--poles="))  poles  = atoi(v);
        else if (const char* v = val("--compact=")) compact = atoi(v);
        else if (const char* v = val("--fit="))    fitmode = atoi(v);
        else if (const char* v = val("--holes="))  holes  = atoi(v);
        else if (const char* v = val("--rots="))   rots   = atoi(v);
        else if (const char* v = val("--seed="))   seed   = atoi(v);
        else if (const char* v = val("--cancel_ms=")) g_cancel_after_ms = (DWORD)atoi(v);
        else if (const char* v = val("--shape="))  shape  = v;         // rect | mixed | ell | u | c
        else if (const char* v = val("--frames=")) nframes = atoi(v);
        else if (const char* v = val("--fhole="))  fhole  = atof(v);
        else if (const char* v = val("--fshape=")) fshape = v;         // rect | u  (cavity shape)
        else if (const char* v = val("--t="))      wallt  = atof(v);
        else if (const char* v = val("--cl="))     clear  = atof(v);
        else if (const char* v = val("--svoid="))  svoid  = atof(v);
        else if (const char* v = val("--svoidsheet=")) svoidsheet = atoi(v);
        else if (const char* v = val("--scale="))  scale  = atof(v);
        else if (const char* v = val("--poke="))   poke   = atof(v);
        else if (const char* v = val("--shift="))  shift  = atof(v);
        else if (const char* v = val("--dropinvalid=")) dropinvalid = atoi(v);
        else if (const char* v = val("--sizes="))  sizes  = v;         // WxH,WxH,... per sheet (cycled)
        else if (const char* v = val("--dump="))   dump   = v;         // write raw placements for an exact A/B
        else if (const char* v = val("--dll="))    dll    = v;
    }
    if (scale <= 0) scale = 1.0;
    pw *= scale; ph *= scale; sw *= scale; sh *= scale;
    if (sw2 > 0) sw2 *= scale;
    if (sh2 > 0) sh2 *= scale;
    if (wallt > 0) wallt *= scale;
    clear *= scale;
    // Defect injection + A/B switches go in as env before the first np_nest call (the DLL reads them there).
    { char b[64]; snprintf(b, sizeof b, "%.6f", poke);  set_env("NP_TEST_POKE",  b); }
    { char b[64]; snprintf(b, sizeof b, "%.6f", shift); set_env("NP_TEST_SHIFT", b); }
    if (dropinvalid >= 0) { char b[8]; snprintf(b, sizeof b, "%d", dropinvalid); set_env("NP_DROP_INVALID", b); }

    HMODULE h = LoadLibraryA(dll);
    if (!h) { fprintf(stderr, "LoadLibrary failed (%lu) for %s\n", GetLastError(), dll); return 2; }
    np_nest_fn np_nest = (np_nest_fn)GetProcAddress(h, "np_nest");
    if (!np_nest) { fprintf(stderr, "np_nest not exported\n"); return 2; }
    g_np_cancel = (np_cancel_fn)GetProcAddress(h, "np_cancel");
    { np_cancel_reset_fn r = (np_cancel_reset_fn)GetProcAddress(h, "np_cancel_reset"); if (r) r(); }

    // ---- parts in their own local frame (the host applies to_xy before calling) --------------------
    // `local[i]`  = the outer ring sent to the solver
    // `pieces[i]` = a CONVEX decomposition of the same shape, so the exact-area clipper stays valid
    // `holeRing[i]` = part i's interior hole (empty for a plain part); the frame's `pieces` deliberately
    // EXCLUDE it, so a child correctly nested in the hole scores zero overlap area against its host.
    std::vector<int> pvc(nparts);
    std::vector<double> pxy; pxy.reserve((size_t)nparts * 16);
    std::vector<Poly> local(nparts);
    std::vector<std::vector<Poly> > pieces(nparts);
    std::vector<Poly> holeRing(nparts);
    // Convex decomposition of the hole ring. The hole-nesting ground truth below measures area with a
    // CONVEX clipper, so a non-convex cavity (--fshape=u) has to be handed to it in convex pieces or the
    // "child wholly inside the hole" measurement would silently be wrong — and the whole point of that
    // measurement is to be independent of the engine.
    std::vector<std::vector<Poly> > holePieces(nparts);
    bool is_ell = (strcmp(shape, "ell") == 0), is_mixed = (strcmp(shape, "mixed") == 0);
    bool is_u   = (strcmp(shape, "u") == 0),   is_c     = (strcmp(shape, "c") == 0);
    bool ucav   = (strcmp(fshape, "u") == 0);
    // Geometry of the frame cavity, shared by the frame parts and (for --fshape=u) by their children.
    double FW = 2.4 * pw, FH = 2.4 * ph, chw = fhole * pw, chh = fhole * ph;
    double cx0 = 0.5 * (FW - chw), cy0 = 0.5 * (FH - chh), cx1 = cx0 + chw, cy1 = cy0 + chh;
    double ut  = (wallt > 0) ? wallt : 0.3 * std::min(chw, chh);           // cavity wall thickness
    if (ut > 0.45 * std::min(chw, chh)) ut = 0.45 * std::min(chw, chh);
    if (clear >= 0.45 * ut) clear = 0.45 * ut;                             // legs must survive the inset
    if (nframes > nparts) nframes = nparts;
    for (int i = 0; i < nparts; ++i) {
        double w = pw, hh = ph;
        if (is_mixed) { double f = 0.5 + 0.5 * ((i % 4) / 3.0); w = pw * f; hh = ph * (1.5 - 0.5 * ((i % 3) / 2.0)); }
        if (i < nframes) {
            // FRAME: outer 2.4x the nominal part with a centred hole 1.15x it, so a plain pw x ph part
            // fits inside the hole with real clearance (fill_cavities needs child area < hole area and
            // the child's rotated bbox to fit the hole bbox).
            local[i] = make_rect(0, 0, FW, FH);
            if (ucav) {
                // U-shaped CAVITY (channel opening up). The material is the outer rect minus the U, which
                // includes the block BETWEEN the U's legs — so a child seated in the U still measures zero
                // overlap against the host, exactly like the rectangular case.
                holeRing[i].clear();
                holeRing[i].push_back(P2{cx0, cy0});      holeRing[i].push_back(P2{cx1, cy0});
                holeRing[i].push_back(P2{cx1, cy1});      holeRing[i].push_back(P2{cx1 - ut, cy1});
                holeRing[i].push_back(P2{cx1 - ut, cy0 + ut}); holeRing[i].push_back(P2{cx0 + ut, cy0 + ut});
                holeRing[i].push_back(P2{cx0 + ut, cy1}); holeRing[i].push_back(P2{cx0, cy1});
                holePieces[i].push_back(make_rect(cx0, cy0, chw, ut));                    // base
                holePieces[i].push_back(make_rect(cx0, cy0 + ut, ut, chh - ut));          // left leg
                holePieces[i].push_back(make_rect(cx1 - ut, cy0 + ut, ut, chh - ut));     // right leg
                pieces[i].push_back(make_rect(0, 0, FW, cy0));                            // bottom band
                pieces[i].push_back(make_rect(0, cy1, FW, FH - cy1));                     // top band
                pieces[i].push_back(make_rect(0, cy0, cx0, chh));                         // left band
                pieces[i].push_back(make_rect(cx1, cy0, FW - cx1, chh));                  // right band
                pieces[i].push_back(make_rect(cx0 + ut, cy0 + ut, chw - 2 * ut, chh - ut));// block in the channel
            } else {
                holeRing[i] = make_rect(cx0, cy0, chw, chh);
                holePieces[i].push_back(holeRing[i]);
                // the four MATERIAL bands (outer rect minus the hole), disjoint and convex
                pieces[i].push_back(make_rect(0, 0, FW, cy0));                    // bottom band
                pieces[i].push_back(make_rect(0, cy1, FW, FH - cy1));             // top band
                pieces[i].push_back(make_rect(0, cy0, cx0, chh));                 // left band
                pieces[i].push_back(make_rect(cx1, cy0, FW - cx1, chh));          // right band
            }
            pvc[i] = (int)local[i].size();
            for (size_t k = 0; k < local[i].size(); ++k) { pxy.push_back(local[i][k].x); pxy.push_back(local[i][k].y); }
            continue;
        }
        if (ucav && nframes > 0) {
            // The MATCHING CHILD of a U cavity: the cavity inset by `clear` on every wall (a true inward
            // offset, so the outer walls move in and the channel walls move INTO the legs). "Wholly inside
            // the cavity with uniform clearance cl, zero material overlap" is then true by construction —
            // ground truth the engine's containment exemption has to agree with at every cl.
            double x0 = cx0 + clear, x1 = cx1 - clear, y0 = cy0 + clear, y1 = cy1 - clear;
            double i0 = cx0 + ut - clear, i1 = cx1 - ut + clear, iy = cy0 + ut - clear;
            local[i].clear();
            local[i].push_back(P2{x0, y0}); local[i].push_back(P2{x1, y0}); local[i].push_back(P2{x1, y1});
            local[i].push_back(P2{i1, y1}); local[i].push_back(P2{i1, iy}); local[i].push_back(P2{i0, iy});
            local[i].push_back(P2{i0, y1}); local[i].push_back(P2{x0, y1});
            pieces[i].push_back(make_rect(x0, y0, x1 - x0, iy - y0));      // base
            pieces[i].push_back(make_rect(x0, iy, i0 - x0, y1 - iy));      // left leg
            pieces[i].push_back(make_rect(i1, iy, x1 - i1, y1 - iy));      // right leg
        } else if (is_u || is_c) {
            // CONCAVE profile whose AREA CENTROID LIES OUTSIDE THE MATERIAL — the family every earlier
            // shape here missed, and the one a shrink-toward-the-centroid gets wrong.
            double t = (wallt > 0) ? wallt : 0.3 * std::min(w, hh);
            if (t > 0.45 * std::min(w, hh)) t = 0.45 * std::min(w, hh);
            local[i].clear();
            if (is_u) {                                  // channel opens upward
                local[i].push_back(P2{0, 0});      local[i].push_back(P2{w, 0});
                local[i].push_back(P2{w, hh});     local[i].push_back(P2{w - t, hh});
                local[i].push_back(P2{w - t, t});  local[i].push_back(P2{t, t});
                local[i].push_back(P2{t, hh});     local[i].push_back(P2{0, hh});
                pieces[i].push_back(make_rect(0, 0, w, t));                 // base
                pieces[i].push_back(make_rect(0, t, t, hh - t));            // left leg
                pieces[i].push_back(make_rect(w - t, t, t, hh - t));        // right leg
            } else {                                     // C: opens to the right
                local[i].push_back(P2{0, 0});       local[i].push_back(P2{w, 0});
                local[i].push_back(P2{w, t});       local[i].push_back(P2{t, t});
                local[i].push_back(P2{t, hh - t});  local[i].push_back(P2{w, hh - t});
                local[i].push_back(P2{w, hh});      local[i].push_back(P2{0, hh});
                pieces[i].push_back(make_rect(0, 0, t, hh));                // spine
                pieces[i].push_back(make_rect(t, 0, w - t, t));             // bottom arm
                pieces[i].push_back(make_rect(t, hh - t, w - t, t));        // top arm
            }
        } else if (is_ell) {
            // concave L: the pole surrogate covers concave shapes worst, so this is the stress case
            double a = w * 0.4, b = hh * 0.4;
            local[i].clear();
            local[i].push_back(P2{0, 0});   local[i].push_back(P2{w, 0});
            local[i].push_back(P2{w, b});   local[i].push_back(P2{a, b});
            local[i].push_back(P2{a, hh});  local[i].push_back(P2{0, hh});
            pieces[i].push_back(make_rect(0, 0, w, b));
            pieces[i].push_back(make_rect(0, b, a, hh - b));
        } else {
            local[i] = make_rect(0, 0, w, hh);
            pieces[i].push_back(local[i]);
        }
        pvc[i] = (int)local[i].size();
        for (size_t k = 0; k < local[i].size(); ++k) { pxy.push_back(local[i][k].x); pxy.push_back(local[i][k].y); }
    }
    std::vector<int> prot(nparts, 0);   // 0 = free continuous rotation (the component default)

    // ---- sheets: tiled left-to-right with a gap, like nest_sheets' copy grid -----------------------
    std::vector<int> sovc(nsheets, 4);
    std::vector<double> soxy; soxy.reserve((size_t)nsheets * 8);
    std::vector<P2> sheetOrigin(nsheets);
    std::vector<Poly> sheetPoly(nsheets);
    double gap = 0.3 * sw;
    if (sw2 <= 0) sw2 = sw;
    if (sh2 <= 0) sh2 = sh;
    // --sizes gives every sheet its own W x H (cycled if fewer entries than sheets). Three or more
    // DISTINCT widths is what actually exercises per-sheet column geometry; --sw/--sw2 only ever produces
    // two, and past sheet 0 the tail is uniform again, which hides banding defects.
    std::vector<P2> sizeList;
    if (sizes && *sizes) {
        const char* p = sizes;
        while (*p) {
            double w = atof(p);
            const char* x = strchr(p, 'x');
            double hgt = x ? atof(x + 1) : w;
            sizeList.push_back(P2{w * scale, hgt * scale});
            const char* c = strchr(p, ',');
            if (!c) break;
            p = c + 1;
        }
    }
    for (int s = 0; s < nsheets; ++s) {
        double w = (s == 0) ? sw : sw2, hgt = (s == 0) ? sh : sh2;
        if (!sizeList.empty()) { w = sizeList[(size_t)s % sizeList.size()].x; hgt = sizeList[(size_t)s % sizeList.size()].y; }
        double ox = s * (sw + gap), oy = 0.0;
        sheetOrigin[s] = P2{ox, oy};
        sheetPoly[s] = make_rect(ox, oy, w, hgt);
        for (const P2& v : sheetPoly[s]) { soxy.push_back(v.x); soxy.push_back(v.y); }
    }
    // SHEET KEEP-OUTS (--svoid). A centred rectangle covering the given fraction of the sheet's AREA,
    // handed to the ABI in absolute coordinates (the engine translates it into the sheet's local frame).
    // This is the input the sheet-id renumbering could get wrong: keep-outs are indexed BY SHEET ID, so a
    // renumber that treats same-size sheets as interchangeable can move a part onto a keep-out it was
    // never checked against. Default 0 => no holes, byte-identical to every earlier run.
    std::vector<int> shc(nsheets, 0);
    std::vector<int> hvc; std::vector<double> hxy;
    std::vector<std::vector<Poly> > sheetVoids(nsheets);   // world coords, for the independent measurement
    if (svoid > 0.0) {
        if (svoid > 0.999) svoid = 0.999;
        double f = std::sqrt(svoid);
        for (int s = 0; s < nsheets; ++s) {
            if (svoidsheet >= 0 && s != svoidsheet) continue;
            double W = sheetPoly[s][2].x - sheetPoly[s][0].x, H = sheetPoly[s][2].y - sheetPoly[s][0].y;
            double vw = f * W, vh = f * H;
            Poly v = make_rect(sheetPoly[s][0].x + 0.5 * (W - vw), sheetPoly[s][0].y + 0.5 * (H - vh), vw, vh);
            sheetVoids[s].push_back(v);
            shc[s] = 1;
        }
    }
    for (int s = 0; s < nsheets; ++s)
        for (size_t k = 0; k < sheetVoids[s].size(); ++k) {
            hvc.push_back((int)sheetVoids[s][k].size());
            for (size_t v = 0; v < sheetVoids[s][k].size(); ++v)
                { hxy.push_back(sheetVoids[s][k][v].x); hxy.push_back(sheetVoids[s][k][v].y); }
        }
    if (hvc.empty()) { hvc.push_back(0); hxy.push_back(0.0); }   // never hand the ABI a null data pointer
    // part_hole_* ABI arrays, part-major (same marshalling as NestPhysicsWrapper).
    std::vector<int> phc(nparts, 0), phvc; std::vector<double> phxy;
    for (int i = 0; i < nparts; ++i) {
        if (holeRing[i].empty()) continue;
        phc[i] = 1;
        phvc.push_back((int)holeRing[i].size());
        for (size_t k = 0; k < holeRing[i].size(); ++k) { phxy.push_back(holeRing[i][k].x); phxy.push_back(holeRing[i][k].y); }
    }
    if (phvc.empty()) { phvc.push_back(0); phxy.push_back(0.0); }   // never hand the ABI a null data pointer

    NpParams np{};
    np.num_rotations = rots; np.spacing = 0.0; np.simplify_tolerance = 0.0; np.seed = seed;
    np.time_budget_secs = 0; np.iter_budget = iters; np.iter_mode = 1;
    np.max_sheets = (maxSheetsArg >= 0) ? maxSheetsArg : nsheets;   // component: nsheet>0 ? nsheet : 6
    np.n_starts = nstarts; np.part_holes_mode = holes; np.pole_max = poles;
    np.final_compact = compact; np.fit_mode = fitmode;

    std::vector<double> otx(nparts), oty(nparts), oang(nparts);
    std::vector<int> osid(nparts);
    int nsheets_used = 0;

    printf("== np_nest: %d parts %gx%g shape=%s (%d frame, cavity=%s) | %d sheets %gx%g (later %gx%g) | "
           "scale=%g | iters=%lld max_sheets=%d starts=%d compact=%d fit=%d holes=%d poles=%d rots=%d seed=%d\n",
           nparts, pw, ph, shape, nframes, fshape, nsheets, sw, sh, sw2, sh2, scale, (long long)iters,
           np.max_sheets, nstarts, compact, fitmode, holes, poles, rots, seed);
    if (ucav && nframes > 0)
        printf("   U-cavity: %gx%g, wall %g, child = cavity inset by cl=%g on every wall\n", chw, chh, ut, clear);
    if (svoid > 0.0)
        printf("   sheet keep-out: %.1f%% of the area of sheet %s\n", 100.0 * svoid,
               svoidsheet < 0 ? "EVERY" : (svoidsheet == 0 ? "0" : "N"));
    if (!sizeList.empty()) {
        printf("   per-sheet sizes:");
        for (int s = 0; s < nsheets; ++s) printf(" [%d]=%gx%g", s, sheetPoly[s][2].x - sheetPoly[s][0].x,
                                                 sheetPoly[s][2].y - sheetPoly[s][0].y);
        printf("\n");
    }
    if (poke > 0 || shift > 0 || dropinvalid >= 0)
        printf("   (INJECTED: NP_TEST_POKE=%g NP_TEST_SHIFT=%g NP_DROP_INVALID=%s)\n",
               poke, shift, dropinvalid >= 0 ? (dropinvalid ? "1" : "0") : "default");
    if (g_cancel_after_ms > 0) {
        printf("   (ESC simulation: np_cancel() after %lu ms — the component publishes this snapshot)\n",
               (unsigned long)g_cancel_after_ms);
        start_cancel_timer();
    }
    DWORD t0 = GetTickCount();
    int rc = np_nest(nparts, pvc.data(), pxy.data(), prot.data(),
                     nsheets, sovc.data(), soxy.data(), shc.data(), hvc.data(), hxy.data(),
                     phc.data(), phvc.data(), phxy.data(),
                     &np, otx.data(), oty.data(), oang.data(), osid.data(), &nsheets_used);
    DWORD t1 = GetTickCount();
    printf("   rc=%d  n_sheets_used=%d  wall=%.1fs\n", rc, nsheets_used, (t1 - t0) / 1000.0);
    int q_ovl = -1, q_oob = -1, q_unp = -1, q_can = -1, q_dem = -1, q_clean = -1, q_have = 0;
    if (np_last_quality_fn q = (np_last_quality_fn)GetProcAddress(h, "np_last_quality")) {
        q_clean = q(&q_ovl, &q_oob, &q_unp, &q_can, &q_dem);
        q_have = 1;
        printf("   ENGINE SELF-REPORT (np_last_quality): clean=%d overlap_pairs=%d out_of_bounds=%d unplaced=%d "
               "cancelled=%d demoted=%d\n", q_clean, q_ovl, q_oob, q_unp, q_can, q_dem);
    } else printf("   ENGINE SELF-REPORT: np_last_quality not exported (old DLL)\n");

    // Raw ABI output, full precision — the artefact an A/B against another build diffs. Anything that is
    // supposed to leave uniform-sheet layouts untouched must produce a byte-identical file here.
    if (dump) {
        if (FILE* f = fopen(dump, "w")) {
            fprintf(f, "n_sheets_used %d\n", nsheets_used);
            for (int i = 0; i < nparts; ++i)
                fprintf(f, "%d sheet=%d tx=%.17g ty=%.17g ang=%.17g\n", i, osid[i], otx[i], oty[i], oang[i]);
            fclose(f);
        } else fprintf(stderr, "could not write dump %s\n", dump);
    }

    // ---- reassemble world placements exactly like NpRun.PartTransform ------------------------------
    std::vector<Poly> world(nparts);
    std::vector<std::vector<Poly> > wpieces(nparts);
    std::vector<std::vector<Poly> > wholes(nparts);   // each frame's hole, in world coords, CONVEX pieces
    std::vector<int> placed_sid(nparts, -1);
    int placed = 0, unplaced = 0;
    std::map<int, int> per_sheet;
    for (int i = 0; i < nparts; ++i) {
        int sid = osid[i];
        if (sid < 0 || sid >= nsheets) { ++unplaced; continue; }
        ++placed; per_sheet[sid]++; placed_sid[i] = sid;
        double c = std::cos(oang[i]), s = std::sin(oang[i]);
        double dx = otx[i] + sheetOrigin[sid].x, dy = oty[i] + sheetOrigin[sid].y;
        auto xf = [&](const Poly& p) {
            Poly w; w.reserve(p.size());
            for (size_t k = 0; k < p.size(); ++k)                      // rotate about origin, + tx,ty, + sheet origin
                w.push_back(P2{ c * p[k].x - s * p[k].y + dx, s * p[k].x + c * p[k].y + dy });
            return w; };
        world[i] = xf(local[i]);
        for (size_t k = 0; k < pieces[i].size(); ++k) wpieces[i].push_back(xf(pieces[i][k]));
        for (size_t k = 0; k < holePieces[i].size(); ++k) wholes[i].push_back(xf(holePieces[i][k]));
    }

    printf("   placed=%d  unplaced=%d  distinct sheets=%d\n", placed, unplaced, (int)per_sheet.size());
    printf("   per-sheet:");
    for (int s = 0; s < nsheets; ++s) printf(" [%d]=%d", s, per_sheet.count(s) ? per_sheet[s] : 0);
    printf("\n");

    // Every threshold below is expressed as a FRACTION of one part's area / one sheet's span, never as an
    // absolute number of model units — otherwise the harness would itself be scale-blind and could not
    // check that the engine is not (which is one of the things it is here to check).
    double part_area = 0.0, placed_area = 0.0;
    std::vector<double> area_of(nparts, 0.0);
    for (int i = 0; i < nparts; ++i) {
        for (size_t u = 0; u < pieces[i].size(); ++u) area_of[i] += poly_area(pieces[i][u]);
        part_area = std::max(part_area, area_of[i]);
        if (placed_sid[i] >= 0) placed_area += area_of[i];
    }
    const double NOISE = 1e-4 * part_area;        // 0.01% of the biggest part: f32 round-off lives far below
    // Per-pair floor for "these two REALLY interpenetrate". Set to the engine's DOCUMENTED contract, not
    // to its implementation: nest_physics_capi's real_overlap deliberately erodes each part by
    // OVERLAP_EPS_FRAC = 1e-3 of its own size before testing, because tangential contact is the desired
    // outcome of a tight nest and a strict edge test fires on it. Holding the harness to a far finer
    // threshold than that would make GATE CHECK flag contact the engine is specified to ignore.
    const double OVERLAP_EPS_FRAC = 1e-3;

    // ---- pairwise overlap area (same sheet) --------------------------------------------------------
    // For a frame part the pieces are its MATERIAL bands, so a child correctly seated in the frame's hole
    // contributes exactly 0 here. That makes this measurement independent ground truth for the engine's
    // hole-aware overlap gate: agreement between the two columns of GATE CHECK is the real assertion.
    double ovl = 0.0, ovl_grazing = 0.0; int ovl_pairs = 0; double worst = 0.0; int wi = -1, wj = -1;
    for (int i = 0; i < nparts; ++i) {
        if (placed_sid[i] < 0) continue;
        for (int j = i + 1; j < nparts; ++j) {
            if (placed_sid[j] != placed_sid[i]) continue;
            double a = 0.0;
            for (size_t u = 0; u < wpieces[i].size(); ++u)
                for (size_t v = 0; v < wpieces[j].size(); ++v) a += inter_area(wpieces[i][u], wpieces[j][v]);
            if (a > OVERLAP_EPS_FRAC * std::min(area_of[i], area_of[j]))
                { ovl += a; ++ovl_pairs; if (a > worst) { worst = a; wi = i; wj = j; } }
            else ovl_grazing += a;   // below the engine's contracted tolerance: contact, not interpenetration
        }
    }
    // ---- out-of-bounds area + max linear excursion past the sheet edge -------------------------------
    // `oob_rel` is the excursion as a fraction of the sheet's own span: THAT is the number that must match
    // across --scale, and the number an absolute model-unit tolerance in the engine cannot see.
    double oob = 0.0; int oob_parts = 0; double oob_depth = 0.0, oob_rel = 0.0;
    for (int i = 0; i < nparts; ++i) {
        if (placed_sid[i] < 0) continue;
        const Poly& S = sheetPoly[placed_sid[i]];
        double sx0 = S[0].x, sy0 = S[0].y, sx1 = S[2].x, sy1 = S[2].y;
        double inside = 0.0, a = 0.0;
        for (size_t u = 0; u < wpieces[i].size(); ++u) { inside += inter_area(wpieces[i][u], S); a += poly_area(wpieces[i][u]); }
        double d = a - inside;
        double dep = 0.0;
        for (const P2& v : world[i]) {
            dep = std::max(dep, sx0 - v.x); dep = std::max(dep, v.x - sx1);
            dep = std::max(dep, sy0 - v.y); dep = std::max(dep, v.y - sy1);
        }
        oob_depth = std::max(oob_depth, dep);
        oob_rel = std::max(oob_rel, dep / std::max(1e-30, (sx1 - sx0) + (sy1 - sy0)));
        if (d > NOISE) { oob += d; ++oob_parts; }
    }
    // ---- parts sitting on their sheet's KEEP-OUT ------------------------------------------------------
    // Measured against the void of THE SHEET THE PART WAS REPORTED ON, which is the whole point: the
    // defect this catches is a sheet-id renumber that moves a part onto a different sheet's keep-out.
    // Reported as a fraction of the part's own area so the number means the same thing at every --scale.
    double void_area = 0.0, void_worst_frac = 0.0; int void_parts = 0;
    for (int i = 0; i < nparts; ++i) {
        if (placed_sid[i] < 0 || area_of[i] <= 0) continue;
        double a = 0.0;
        for (size_t k = 0; k < sheetVoids[placed_sid[i]].size(); ++k)
            for (size_t u = 0; u < wpieces[i].size(); ++u) a += inter_area(wpieces[i][u], sheetVoids[placed_sid[i]][k]);
        if (a > OVERLAP_EPS_FRAC * area_of[i]) { void_area += a; ++void_parts; }
        void_worst_frac = std::max(void_worst_frac, a / area_of[i]);
    }
    printf("   OVERLAP  total=%.6g (%.5f%% of one part) pairs=%d worst=%.6g (%d,%d) grazing=%.6g\n",
           ovl, 100.0 * ovl / part_area, ovl_pairs, worst, wi, wj, ovl_grazing);
    printf("   OUTOFBND total=%.6g over %d part(s), max edge excursion=%.6g units (%.3g%% of the sheet span)\n",
           oob, oob_parts, oob_depth, 100.0 * oob_rel);
    if (svoid > 0.0)
        printf("   SHEETVOID %d part(s) sitting on their sheet's keep-out, area=%.6g, worst part %.1f%% inside it\n",
               void_parts, void_area, 100.0 * void_worst_frac);
    // Utilisation over the sheets actually used, summing each part's REAL area (frames are mostly hole)
    // and each used sheet's OWN size — the old "placed x biggest part / sheet-0 area" was meaningless for
    // mixed part or mixed sheet sizes.
    double used_sheet_area = 0.0;
    for (std::map<int,int>::const_iterator it = per_sheet.begin(); it != per_sheet.end(); ++it)
        used_sheet_area += poly_area(sheetPoly[it->first]);
    printf("   utilisation on used sheets = %.1f%%\n", 100.0 * placed_area / std::max(1e-30, used_sheet_area));

    // ---- HOLE-NESTING ground truth ------------------------------------------------------------------
    // Without this, a "no overlaps" verdict on a --frames run is worthless: it is equally consistent with
    // "the exemption works" and with "nothing was ever nested in a hole". Classify every placed part by
    // how much of its area lies inside some OTHER placed part's hole ring: fully in = the legitimate case
    // the gate must stay quiet about; straddling the wall = the defect the gate must still report.
    int nested_in = 0, nested_straddle = 0;
    double worst_straddle = 0.0;
    for (int j = 0; j < nparts; ++j) {
        if (placed_sid[j] < 0 || area_of[j] <= 0) continue;
        double best_in = 0.0;
        for (int i = 0; i < nparts; ++i) {
            if (i == j || placed_sid[i] != placed_sid[j] || wholes[i].empty()) continue;
            double inside = 0.0;
            for (size_t v = 0; v < wholes[i].size(); ++v)                 // hole handed over in CONVEX pieces
                for (size_t u = 0; u < wpieces[j].size(); ++u) inside += inter_area(wpieces[j][u], wholes[i][v]);
            best_in = std::max(best_in, inside);
        }
        double frac = best_in / area_of[j];
        if (frac > 0.999) ++nested_in;
        else if (frac > 1e-6) { ++nested_straddle; worst_straddle = std::max(worst_straddle, frac); }
    }
    if (nframes > 0) {
        printf("   HOLE-NEST: %d part(s) fully inside another part's hole, %d straddling a hole wall%s\n",
               nested_in, nested_straddle,
               nested_straddle ? "" : " (worst straddle n/a)");
        if (nested_straddle) printf("              worst straddling part is %.1f%% inside the hole\n", 100.0 * worst_straddle);
        // The one sentence the U-cavity regression turns on: an independent, engine-free statement that
        // the child really is wholly inside the (non-convex) cavity with no material overlap anywhere.
        printf("   GROUND TRUTH: child wholly inside host hole = %s (material overlap pairs = %d)\n",
               nested_in > 0 ? "YES" : "NO", ovl_pairs);
    }

    // ---- ACCOUNTING: every input part must have exactly one outcome ---------------------------------
    // The ESC/cancel path on mixed-size sheets is the case this guards: parts deferred to a taller sheet
    // must come back visibly UNPLACED, never vanish from both columns.
    printf("   ACCOUNTING: placed=%d + unplaced=%d = %d of %d input part(s) -> %s\n",
           placed, unplaced, placed + unplaced, nparts,
           (placed + unplaced == nparts) ? "ALL ACCOUNTED FOR" : "*** PARTS LOST ***");

    // ---- GATE CHECK: engine self-report vs this harness's independent measurement --------------------
    // Both columns describe the SAME final layout. A disagreement is a defect in the gate, whichever way
    // it points: over-reporting (crying wolf on a legal hole-nest) is as bad as under-reporting.
    int gate_bad = 0;
    if (q_have) {
        bool ovl_agree = ((q_ovl > 0) == (ovl_pairs > 0));
        // The engine only reports parts STILL out of bounds; ones it demoted are gone from the layout, so
        // the harness cannot see them at all. Both views must agree that nothing left on a sheet sticks
        // out — OR sits on that sheet's keep-out, which the engine folds into the same counter because it
        // is the same user-visible condition: the part is not on usable material.
        int harness_unusable = oob_parts + void_parts;
        bool oob_agree = ((q_oob > 0) == (harness_unusable > 0));
        bool unp_agree = (q_unp == unplaced);
        gate_bad = (!ovl_agree || !oob_agree || !unp_agree) ? 1 : 0;
        printf("   GATE CHECK: overlap engine=%d harness=%d [%s] | off-sheet-or-on-keepout engine=%d "
               "harness=%d (oob %d + keepout %d) [%s] | unplaced engine=%d harness=%d [%s] | demoted=%d\n",
               q_ovl, ovl_pairs, ovl_agree ? "agree" : "DISAGREE",
               q_oob, harness_unusable, oob_parts, void_parts, oob_agree ? "agree" : "DISAGREE",
               q_unp, unplaced, unp_agree ? "agree" : "DISAGREE", q_dem);
        // An injection case that injected NOTHING passes trivially, which is worse than no case at all.
        // It is not automatically a failure — a poke smaller than the clearance the child has to cross is
        // legitimately inert. That threshold is NOT a fixed number of units (this note used to claim ~23
        // units at --cl=2, which was the top of the original sweep, not its threshold): it TRACKS --cl, at
        // roughly 2.6-3.1x it. Bisected on --frames=2 --parts=8 --fshape=u over a 460-wide cavity, the gate
        // stays quiet up to ~0.27 units at --cl=0.1, ~1.55 at 0.5, ~2.77 at 1, ~5.32 at 2 and ~13.0 at 5,
        // and reports within ~1% above each. Those absolutes shift with the sweep grid and with the rest
        // of the configuration; the proportionality is the durable part. Also make sure the configuration
        // seats a child at all — --frames=2 --parts=8 seats 2 at every cl from 0.1 to 5, while --frames=1
        // at the default part count seats NONE and --poke is then inert at every value.
        // So say so instead of failing.
        if ((poke > 0 || shift > 0) && ovl_pairs == 0 && oob_parts == 0 && void_parts == 0 && q_dem == 0)
            printf("   NOTE: --poke/--shift produced NO measurable defect. Either the displacement is "
                   "smaller than the clearance it has to cross (try a snugger --cl / --fhole), or this DLL "
                   "was built without -DNEST_PHYSICS_BUILD_BENCH=ON and has the seams compiled out.\n");
    }

    const char* verdict = (ovl > NOISE) ? "OVERLAPPING"
                        : (oob > NOISE) ? "OUT-OF-BOUNDS"
                        : (void_parts > 0) ? "ON-KEEPOUT"
                        : (unplaced > 0) ? "UNPLACED-SPILL" : "CLEAN";
    printf("   VERDICT: %s%s\n", verdict, gate_bad ? "   *** GATE DISAGREES WITH GROUND TRUTH ***" : "");
    return gate_bad;
}
