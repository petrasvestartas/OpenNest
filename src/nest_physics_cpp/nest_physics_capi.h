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
    double    spacing;             // IGNORED: offset is applied upstream (host offsets parts/sheets before solving). Kept for ABI stability.
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
                                   //   2 = ALL parts, GLOBAL BIN PACK: every sheet is a column of one wide strip
                                   //       (keep-out gaps between columns) optimised jointly, emptying the last
                                   //       column until it can be dropped. Needs uniform sheet sizes and a non-
                                   //       preview budget (iter_budget >= 500); otherwise falls back to mode 0.
                                   //   3 = ALL parts, GROUP-AND-FILL (solver/group_fill.hpp): plate groups tested for
                                   //       single-sheet feasibility per sheet type, exact cover of the plates, every
                                   //       sheet one joint pack (plates mandatory + long + knapsack small parts),
                                   //       insertion, leftover repack, part-in-hole seating, extra sheets, remnant
                                   //       compaction; threaded portfolio + the fit 2 (fit 0 for mixed sizes) run as
                                   //       a baseline member. Mixed sheet sizes, sheet holes and irregular outlines
                                   //       (as keep-outs) are supported. Preview budgets fall back to mode 0.
    int       n_threads;           // fit_mode 3 only: total solver threads of the portfolio (<= 0 -> 4). APPENDED
                                   //   field (2026-10): it sits in the former tail padding, so sizeof(NpParams)
                                   //   stays 80 and a host that zero-fills the struct gets the default.
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
    const int*    part_rotations,           // [part_count] per-part rotation override: 0 = free
                                            //   continuous rotation (default), N>0 = this part may
                                            //   only use N discrete orientations (360/N deg step;
                                            //   1 = fixed at 0 deg). NULL = no overrides.
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

// Explicit one-sheet warm insertion. Seed transforms use the same public frame as outputs.
// Every offered part must be placed for success. Incumbents remain movable during repair.
// Returns 0 only for complete clean repair, 1 for no repair/cancel, 2 for invalid input.
// On 1/2 valid seed entries are returned unchanged; incoming parts remain unplaced.
// Rejected/fallback seed geometry has quality overlap/oob=-1 (unverified), never a clean verdict.
// Exactly one sheet, part_holes_mode 0/1 only. NpParams and old exports are unchanged.
NP_EXPORT int np_nest_warm_one(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_rotations,           // [part_count] per-part rotation override: 0 = free
                                            //   continuous rotation (default), N>0 = this part may
                                            //   only use N discrete orientations (360/N deg step;
                                            //   1 = fixed at 0 deg). NULL = no overrides.
    const int*    warm_present,
    const double* warm_tx,
    const double* warm_ty,
    const double* warm_angle,
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

// E1: np_nest with an optional per-part PRIORITY array. Every argument is np_nest's, in the same order, plus
//   part_priority[part_count]  one int per input part (same order as part_vertex_counts):
//                              0 = normal, >0 = MANDATORY. NULL => exactly np_nest (np_nest calls this with NULL).
// Only fit_mode 1 (ONE sheet, MAX FILL) reads it; fit modes 0 and 2 ignore it (all parts are placed there).
// With an array (even all zeros) fit_mode 1 changes its objective from placed COUNT to:
//   1. every mandatory part on the sheet: a layout missing a mandatory part ranks below any layout that has
//      them all - across the multi-starts and when choosing the strip layout to cut at the sheet width;
//   2. then the most placed AREA (sum of part areas), then the narrowest used width.
// Attempt 0 is exactly the plain fit-1 run (same budget, passes, seed, LBF), so a run that drops nothing
// equals np_nest. If it does not keep every offered part, the NORMAL parts (area ascending) are dropped by
// BISECTION on how many of the smallest go (first half; a retry that keeps everything it was offered re-adds
// half of the dropped, otherwise half of the rest is dropped). Each retry runs a cold LBF pack of the reduced
// set (= the plain call on it) and, if that does not keep everything, a warm start from the best earlier
// layout minus the dropped parts; the better counts. A mandatory part is never dropped. Last, the best layout
// holding every mandatory part is frozen and the normal parts it lacks are packed into its free space
// (insertion run; counted as a retry). The best cut over all runs wins. When the mandatory parts cannot all fit, the best effort is returned and np_last_prio reports
// how many are missing (stderr: "[np] prio attempt/retry/verdict ..." and the per-start "multistart" line).
// Env: NP_PRIO_RETRY_BUDGET=<f> budget of each retry run as a fraction of the plain run's (default 1.0);
//      NP_PRIO_MAX_RETRIES=<n> retries per start (default 6); wall time grows with both.
// Dev env: NP_PRIO_AREA=1 forces the area objective on np_nest (fit 1) without a priority array.
NP_EXPORT int np_nest_prio(
    int           part_count,
    const int*    part_vertex_counts,
    const double* part_xy,
    const int*    part_rotations,
    const int*    part_priority,            // [part_count] 0 = normal, >0 = mandatory; NULL = np_nest
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
    int*          out_n_sheets);

// E1 priority verdict of the LAST completed np_nest / np_nest_prio, on the FINAL placements (after every
// post-pass, like np_last_quality; same process-global, not re-entrant contract):
//   out_mandatory          input parts flagged mandatory (0 for np_nest)
//   out_mandatory_missing  of those, how many got no sheet (should be 0)
//   out_placed_area        sum of the placed parts' areas (model units^2)
//   out_offered_area       sum of every buildable input part's area
//   out_retries            drop-policy retries (drop sets evaluated) summed over the multi-starts; 0 when
//                          attempt 0 kept everything or the priority path did not run
// Any out-param may be NULL. Returns out_mandatory_missing.
NP_EXPORT int np_last_prio(int* out_mandatory, int* out_mandatory_missing,
                           double* out_placed_area, double* out_offered_area, int* out_retries);

// Quality verdict of the LAST completed np_nest, measured by exact brute-force verification of the FINAL
// placements (after every post-pass), so it describes precisely the layout np_nest just returned:
//   out_overlap_pairs  pairs of parts that genuinely interpenetrate on the same sheet   (should be 0).
//                      A part sitting INSIDE another part's hole is legitimate nesting (part_holes_mode
//                      1/2 puts it there on purpose) and is NOT counted; a part that pokes out through
//                      the hole wall it was seated in IS counted. "Genuinely" means deeper than a thin
//                      contact skin (a fraction of each part's own size): touching is the desired outcome
//                      of a tight nest, so a strict edge test would fire on most good layouts.
//   out_out_of_bounds  parts that are not on usable material of the sheet they are reported on — either
//                      their bbox leaves the sheet, or they sit on one of that sheet's keep-out holes.
//                      INTERNAL CONSISTENCY DIAGNOSTIC, not a user-facing condition: with the default
//                      drop-invalid pass on, that pass has already demoted every such part to unplaced
//                      using the same two predicates, so this is 0 by construction. It can only be
//                      non-zero with the dev switch NP_DROP_INVALID=0 — which is exactly what it exists
//                      for (A/B harnessing) — or if a future post-pass moves a part after the demotion,
//                      which is the regression this counter is here to catch.
//                      Hosts should report `out_demoted` / `out_unplaced` to users, never this.
//   out_unplaced       input parts that got no sheet (host lays them outside). Includes out_demoted.
//   out_cancelled      1 if the solve was stopped early (np_cancel) — that snapshot is best-effort
//   out_demoted        of out_unplaced, how many the solver HAD placed and the drop-invalid pass took
//                      back off a sheet because they hung outside it or sat in a sheet hole
// Any out-param may be NULL. Returns 1 when the layout is CLEAN (no overlaps, in bounds, nothing
// unplaced, not cancelled), else 0.
// The relaxation solver optimises against a penetration-depth proxy over inscribed poles, not exact
// polygon geometry, so a returned layout is not overlap-free by construction — this is the check.
//
// SCALE. Bounds are judged with a tolerance that is RELATIVE to the sheet/part size, BUT WITH AN ABSOLUTE
// FLOOR of 0.002 model units (2 x the packer's own absolute placement slack — see bounds_tol in
// solver/driver.hpp for why that floor cannot be removed without moving layouts). The floor dominates
// whenever the reference length is below ~20 model units, so in a METRE-unit document there is a blind
// band: an excursion of up to 0.002 units past the sheet edge is accepted, which on a 1x1 m sheet is 0.1%
// of the sheet span, against 0.0025% at millimetre scale. Measured: the identical relative defect
// (np_bench --shift=0.1) is demoted at --scale=1 and ships at --scale=0.001 hanging 0.0161% of the sheet
// span outside. Above that band the verdict IS the same in mm and in m. The overlap test has no such
// floor — its skin is a pure fraction of each part's own size.
NP_EXPORT int np_last_quality(int* out_overlap_pairs, int* out_out_of_bounds,
                              int* out_unplaced, int* out_cancelled, int* out_demoted);

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
