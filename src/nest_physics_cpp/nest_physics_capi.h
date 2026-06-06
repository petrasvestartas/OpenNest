// C ABI for nest_physics: a single stateless entry point a host (e.g. the OpenNest Grasshopper
// plugin) can P/Invoke. Geometry crosses as flat double[]/int[] arrays; the host preallocates the
// output buffers (it knows part_count). cdecl, no name mangling (extern "C").
#ifndef NEST_PHYSICS_CAPI_H
#define NEST_PHYSICS_CAPI_H

#if defined(_WIN32) || defined(_WIN64)
  #define NP_EXPORT __declspec(dllexport)
#else
  #define NP_EXPORT
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct NpParams {
    int       num_rotations;       // discrete orientations sampled per part (>=1)
    double    spacing;             // min gap between parts / from holes (0 = touching allowed)
    double    simplify_tolerance;  // geometry simplification tolerance (<=0 keeps the lean default)
    int       seed;                // base RNG seed (>=0)
    double    time_budget_secs;    // wall-clock budget, used when iter_mode == 0
    long long iter_budget;         // relaxation-round budget, used when iter_mode != 0 (deterministic)
    int       iter_mode;           // 0 = wall-clock, 1 = deterministic iteration count
    int       max_sheets;          // cap on sheets used (0 -> default 6)
    int       n_starts;            // multi-start: run this many seeds, keep the densest (0/1 -> single)
    int       part_holes_mode;     // 0 = keep-empty (default, part holes ignored); 1 = fill-holes
                                   //     (parts may nest INTO other parts' holes; needs part_hole_* arrays)
    int       pole_max;            // surrogate poles per part (0 = default 48; ~16 ≈ 2.7x faster per round)
    int       final_compact;       // 1 = post-relaxation geometric left/down compaction slide (tighter pack)
    int       fit_mode;            // 0 = place ALL parts on the fewest sheets (default); 1 = ONE sheet, MAX FILL
                                   //     (cap to a single sheet; multi-start keeps the run with the most parts on
                                   //      it; parts that don't fit are reported unplaced for the host to lay outside)
} NpParams;

// Nest `part_count` polygons onto `sheet_count` fixed sheets (sheet 0's bbox sets the W x H used for
// every sheet in v1). Holes are forbidden interior regions parts must avoid (kept empty).
//
// Inputs (all rings given CCW or CW, NOT closed; a trailing duplicate point is tolerated):
//   part_vertex_counts[part_count]          vertex count of each part's outer ring
//   part_xy[2*sum(part_vertex_counts)]      interleaved x,y for all parts, in order
//   sheet_outer_vertex_counts[sheet_count]  vertex count of each sheet outer ring
//   sheet_outer_xy[2*sum(...)]              interleaved x,y for all sheet outers
//   sheet_hole_counts[sheet_count]          number of holes per sheet (may be NULL => no holes)
//   hole_vertex_counts[sum(sheet_hole_counts)]  vertex count per hole, sheet-major (may be NULL)
//   hole_xy[2*sum(hole_vertex_counts)]      interleaved x,y for all holes, sheet-major (may be NULL)
//
// Outputs (host-allocated, length part_count; indexed by ORIGINAL input part order):
//   out_tx, out_ty       translation of the placed part (apply AFTER rotation about the origin)
//   out_angle            rotation in radians (rotate the original geometry about the world origin)
//   out_sheet_id         sheet index the part landed on, or -1 if unplaced
//   out_n_sheets         (length 1) number of sheets actually used
//
// The returned (angle, tx, ty) already fold in the internal part-centering, and are expressed in the
// sheet-LOCAL frame (origin at that sheet's bbox min). The host adds the sheet's world origin.
//
// Returns 0 on success, non-zero on failure (outputs are zeroed / sheet ids set to -1).
NP_EXPORT int np_nest(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    int           sheet_count,
    const int*    sheet_outer_vertex_counts,
    const double* sheet_outer_xy,
    const int*    sheet_hole_counts,
    const int*    hole_vertex_counts,
    const double* hole_xy,
    const int*    part_hole_counts,         // [part_count] holes per part (NULL => no part holes)
    const int*    part_hole_vertex_counts,  // [sum(part_hole_counts)] vertex count per part-hole, part-major (NULL ok)
    const double* part_hole_xy,             // interleaved x,y for all part holes, part-major (NULL ok)
    const NpParams* params,
    double*       out_tx,
    double*       out_ty,
    double*       out_angle,
    int*          out_sheet_id,
    int*          out_n_sheets);

// Cooperative cancel + progress for an interactive host that runs np_nest on a background thread.
// np_cancel(): ask the running solve to stop at the next round and return its best-so-far (valid nest).
// np_cancel_reset(): clear the flag (np_nest also clears it on entry).
// np_progress(): relaxation rounds completed so far (monotonic during a solve) — for a live readout.
NP_EXPORT void      np_cancel(void);
NP_EXPORT void      np_cancel_reset(void);
NP_EXPORT long long np_progress(void);

// Snapshot the current best layout mid-solve (for an animated host preview). Same (angle,tx,ty,sheet)
// convention as np_nest's output. Host preallocates buffers length part_count; unplaced => sheet -1.
// Returns the number of placed parts (0 if no solve active). Safe to call from another thread.
NP_EXPORT int np_poll_layout(
    int     part_count,
    double* out_tx,
    double* out_ty,
    double* out_angle,
    int*    out_sheet_id,
    int*    out_n_sheets);

#ifdef __cplusplus
}
#endif

#endif // NEST_PHYSICS_CAPI_H
