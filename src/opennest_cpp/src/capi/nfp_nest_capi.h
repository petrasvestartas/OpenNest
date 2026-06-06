#pragma once
// C ABI for the OpenNest NFP/SvgNest genetic-algorithm nester (nest_lib).
// Mirrors the array/return conventions of nest_physics_capi (see the C# wrapper
// NestPhysicsWrapper.cs) so the Grasshopper plugin can P/Invoke it identically.
//
// Coordinate / transform contract (per placed instance):
//   final_point = Rotate(part_point, angle_degrees, about (0,0)) + (tx, ty)
//   (tx, ty) are SHEET-RELATIVE; the C# side adds the sheet's own placement.
//   out_sheet_id == -1 means the instance was not placed.

#include <stddef.h>

#if defined(_WIN32)
  #define NFP_API extern "C" __declspec(dllexport)
#else
  #define NFP_API extern "C" __attribute__((visibility("default")))
#endif

// Blittable parameter block. Mirrors SvgNestConfig + run-control. Natural
// alignment (x64) matches C# [StructLayout(LayoutKind.Sequential)].
typedef struct NfpParams {
    int    placementType;   // 0=box, 1=gravity, 2=squeeze
    int    rotations;       // discrete rotation count (e.g. 4)
    int    mutationRate;    // GA mutation rate (applied as 0.01*rate)
    int    populationSize;  // GA pool size (one generation = this many candidates)
    int    seed;            // RNG seed (-1 = time-based, non-deterministic)
    double curveTolerance;  // simplification tolerance
    double clipperScale;    // Clipper integer scale (e.g. 1e7)
    double spacing;         // gap between parts
    double sheetSpacing;    // gap inside sheet edge
    double rotationLimit;   // max continuous rotation jitter (degrees), default 360
    int    useHoles;        // bool: nest into holes
    int    exploreConcave;  // bool
    int    clipByHull;      // bool
    int    clipByRects;     // bool
    int    simplify;        // bool: convex-hull simplify
    int    mode;            // 0=faithful (parity, single-thread), 1=default, 2=turbo (multi-seed)
    int    generations;     // GA generations (== component "Iterations")
    int    numSeeds;        // turbo: parallel independent seeds
    int    useParallel;     // bool: parallel NFP / population evaluation
    double timeBudgetSecs;  // >0 => run until elapsed (overrides generations loop length)
    int    maxSheets;       // 0 = use all provided sheets
    int    edgeSamples;     // feasible-region edge samples per part (0=off; lower=faster)
    int    compactionPasses;// post-placement compaction passes (0=off; lower=faster)
    int    tryAllRotations; // bool: evaluate every rotation per placement (slower, tighter)
    int    exactNfp;        // bool: full-resolution exact NFP (no simplify/dilate -> no gap, slower)
} NfpParams;

// Main solve. Output arrays are caller-allocated, length = sum(part_quantities)
// ("instance count"), emitted in expansion order (part0 x q0, part1 x q1, ...).
// out_n_sheets / out_fitness are single values. Returns the number of placed
// instances (>=0), or a negative error code.
NFP_API int nfp_nest(
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]
    const double* part_xy,                   // [sum(part_vertex_counts)*2]
    const int*    part_quantities,           // [part_count] (>=1 each)
    const int*    part_hole_counts,          // [part_count]
    const int*    part_hole_vertex_counts,   // [sum(part_hole_counts)]
    const double* part_hole_xy,              // [sum(part_hole_vertex_counts)*2]
    int           sheet_count,
    const int*    sheet_vertex_counts,       // [sheet_count]
    const double* sheet_xy,                  // [sum(sheet_vertex_counts)*2]
    const int*    sheet_hole_counts,         // [sheet_count]
    const int*    sheet_hole_vertex_counts,  // [sum(sheet_hole_counts)]
    const double* sheet_hole_xy,             // [sum(sheet_hole_vertex_counts)*2]
    const NfpParams* params,
    double*       out_tx,                     // [instance_count]
    double*       out_ty,                     // [instance_count]
    double*       out_angle,                  // [instance_count] degrees
    int*          out_sheet_id,              // [instance_count] (-1 = unplaced)
    int*          out_part_index,            // [instance_count] source part index
    int*          out_n_sheets,             // single
    double*       out_fitness);             // single

// Cooperative cancel + live-preview support for running nfp_nest on a background
// thread (same role as np_cancel/np_progress/np_poll_layout).
NFP_API void      nfp_cancel(void);
NFP_API void      nfp_cancel_reset(void);
NFP_API long long nfp_progress(void);   // GA generation reached so far
NFP_API double    nfp_fitness(void);    // best fitness so far

// Snapshot the current best layout mid-solve. Caller preallocates buffers of
// instance_count. Returns number of placed instances (0 if idle). UI-thread safe.
NFP_API int nfp_poll_layout(
    int     instance_count,
    double* out_tx,
    double* out_ty,
    double* out_angle,
    int*    out_sheet_id,
    int*    out_part_index,
    int*    out_n_sheets);
