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
    const int*    part_rotations,            // [part_count] per-part rotation-count override:
                                             //   0 = use params->rotations (default), N>0 = this part
                                             //   may only use N discrete orientations (360/N step;
                                             //   1 = fixed at 0°). NULL = no overrides.
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

// Row-major grid layout (NO nesting; the compas_nest `pack()` semantics). Parts are
// expanded by quantity and placed left-to-right; each cell advances by the part's own
// bbox width + gap_x, a row's height is its tallest part + gap_y. Two wrapping modes:
//   array    (max_width <= 0): start a new row every `columns` items.
//   distance (max_width  > 0): wrap when the next part would exceed max_width
//                              (`columns` is ignored; an oversized part gets its own row).
// Outputs use the same contract as nfp_nest (rotate-about-origin + translate; here the
// angle is always 0 and sheet id always 0). Output arrays are caller-allocated, length
// sum(part_quantities), expansion order. Returns the number of placed instances (>=0)
// or a negative error code.
NFP_API int nfp_pack(
    int           part_count,
    const int*    part_vertex_counts,        // [part_count]
    const double* part_xy,                   // [sum(part_vertex_counts)*2]
    const int*    part_quantities,           // [part_count] (NULL => 1 each)
    int           columns,                   // array mode: cells per row (clamped to >=1)
    double        gap_x,
    double        gap_y,
    double        max_width,                 // >0 => distance mode
    double*       out_tx,                    // [instance_count]
    double*       out_ty,                    // [instance_count]
    double*       out_angle,                 // [instance_count] (always 0)
    int*          out_sheet_id);             // [instance_count] (always 0)

// Offset (inflate) ONE closed polygon by `delta` model units with Clipper2: positive
// grows, negative shrinks. Miter joins (miter_limit <= 0 => 2.0). The largest-area
// result loop is returned. Returns the vertex count written into out_xy (interleaved
// x,y), 0 if the offset vanished (e.g. over-shrunk), or the NEGATED required vertex
// count if max_out_vertices is too small (call again with a bigger buffer).
NFP_API int nfp_offset_polygon(
    int           vertex_count,
    const double* xy,                        // [vertex_count*2], closed ring (no dup end needed)
    double        delta,
    double        miter_limit,
    int           max_out_vertices,
    double*       out_xy);                   // [max_out_vertices*2]

// Render `text` to single-stroke ENGRAVING polylines using the bundled OpenNest VDA font
// (the same single-line font compas_nest ships). Glyph arcs are pre-sampled to segments.
// Layout: line 0's baseline-left at the origin, glyphs scaled to cap-height `height`, laid
// out left-to-right; '\n' starts a new line 1.4*height lower. `font`: 0 = regular, 1 = bold.
// `spacing` = extra gap between glyphs in em (compas_nest default 0.1; pass <0 for the default).
//
// Output is a list of strokes (polylines): out_stroke_vertex_counts[k] = points in stroke k,
// and out_xy holds every point x,y concatenated stroke-by-stroke. Returns the TOTAL stroke
// count (>=0). *out_total_points (if non-NULL) receives the total point count across all
// strokes — call once with max_strokes=0 / max_points=0 to size the buffers, then again to
// fill them. If the return value > max_strokes OR *out_total_points > max_points, enlarge and
// call again (nothing partial is relied upon).
NFP_API int nfp_text_to_polylines(
    const char*   text,
    double        height,
    int           font,                      // 0 = regular, 1 = bold
    double        spacing,                   // em gap between glyphs (<0 => 0.1 default)
    int           max_strokes,
    int*          out_stroke_vertex_counts,  // [max_strokes]
    int           max_points,
    double*       out_xy,                    // [max_points*2]
    int*          out_total_points);         // single (may be NULL)

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
