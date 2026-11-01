# nest_physics

2D irregular nesting — packs polygon parts into fixed-size sheets without overlaps.

## Build
    g++ -std=c++20 -O3 -march=native -pthread -fopenmp-simd nest_physics.cpp -o nest_physics.exe

## Run
    nest_physics.exe <input.json> <output.svg> <budget>

budget: `120` = 120 seconds (anytime) | `3000i` = 3000 iterations (deterministic, reproducible)

## Overlap signal
Overlaps are resolved by minimizing a per-pair overlap penalty. By default the penalty is a
**penetration-depth** measure — how far two parts must move to separate — which handles deep, thin
overlaps better than an area measure and packs tighter (≈44/47 vs 42/47 on the test sheet at equal
work). Build with `-DUSE_DEPTH_PROXY=0` for the area-based baseline.

## Multi-sheet modes (`NpParams.fit_mode`)
- `0` all parts, fewest sheets (default): greedy sequential fill (`solver/driver.hpp` `greedy_fill`) -
  strip-pack the remaining parts, cut the first sheet-wide column off as a finished sheet, carry the
  overflow to the next sheet.
- `1` one sheet, max fill: overflow is reported unplaced.
- `2` all parts, GLOBAL bin pack (`solver/bin_driver.hpp` `bin_pack_columns`): k sheets are one strip of
  k sheet-wide columns separated by keep-out gaps (registered as strip holes), so the separator moves and
  swaps parts across sheets freely. LBF on the wide strip, then exploration shrinks only the LAST column
  and drops it (k -> k-1) as soon as a feasible layout leaves it empty; compression then squeezes the
  last column (usable remnant). Each column is finally translated to its own sheet, compacted and
  verified exactly like mode 0, so every post-pass runs unchanged. Needs uniform sheet sizes (per-sheet
  holes are fine) and a non-preview budget (`iter_budget >= 500` in iteration mode); otherwise it prints
  `[np] fit_mode 2 -> greedy fallback (...)` and runs mode 0. Everything scales from the sheet size and
  the part areas; there are no instance-specific constants.

- `3` all parts, GROUP & FILL (`solver/group_fill.hpp` `group_fill`, C++ port of the Python prototype
  `tools/bench3dm/groupfill_nfp.py` + `sheetcover.py`). Decides explicitly which large parts share a sheet, then packs
  every sheet as ONE joint pack with the one-sheet fit-1 machinery (`prio_fill_one`, E1 mandatory / placed-area path):
  1. classes: PLATES = area >= `NP_FM3_PLATE_FRAC` (0.05) of the largest usable sheet area (at most
     `NP_FM3_GROUP_MAX` = 24); LONG = not a plate, polygon diameter >= `NP_FM3_LONG_FRAC` (0.33) x the largest sheet
     dimension; SMALL = rest.
  2. sheet TYPES = distinct (size, keep-out set). Sheet holes and irregular outlines (which reach the engine as
     keep-outs, see component_nest.cs `NonRectFrameKeepouts`) are respected everywhere; usable area = bbox - keep-outs.
  3. per type, single-sheet feasibility tests of plate groups: singles, pairs, triples whose pairs fit, quads whose
     triples fit, largest area first, area <= 0.98 usable. LBF into the sheet (keep-outs on), LBF failures within
     1.25 W re-tested with `NP_FM3_TEST_ROUNDS` (150) exploration rounds; caps `NP_FM3_MAX_TESTS` (3000) /
     `NP_FM3_MAX_UPGRADES` (400) are COUNTS, so iteration-mode runs stay deterministic. Run on the portfolio threads.
  4. assignment = branch-and-bound exact cover (largest uncovered plate branched first) of the plates by feasible
     (type, group) pairs, sheet counts per type respected, at the smallest sheet count k whose usable area holds
     every part x (1 + `NP_FM3_SLACK`, 0.06); at that k as many of the largest plates as possible (uncovered plates
     become fillers), plus, for diversity, a full cover with at most one more sheet. Covers ranked by the free band
     right of each group's test layout; the best `NP_FM3_CHAINS` (n_threads - 1) go to the portfolio.
  5. per sheet, tightest first: joint pack of plates (mandatory) + the long parts assigned to it (worst-fit by free
     area, a long part one sheet drops is carried to the next) + small parts by area to `NP_FM3_TARGET` (0.90) of the
     usable area (`NP_FM3_KNAP_MAX` 24 at most); then the remaining parts are packed into the frozen layout; after all
     sheets the leftovers are offered to every sheet again (whole-sheet repack, current parts mandatory), then seated
     into holes of placed parts (part_holes_mode >= 1), then packed onto further unused sheets (fit 0 greedy; with
     mixed sizes the smallest sheet that holds them first); finally the least-filled sheet is re-packed for the
     smallest bounding rectangle of its used region (strip-height sweep done as a keep-out band, so it also works on
     irregular sheets; `NP_FM3_REMNANT=0` off). Same-type sheets are renumbered fullest first, remnant last.
  6. portfolio over covers / seeds on `NpParams.n_threads` threads (default 4; `NP_FM3_THREADS` overrides), every
     chain with one best-of-N worker; every candidate is vetted by the C ABI's exact final verification. Unless a
     chain already reached the area lower bound with nothing left over, the plain fit 2 run (fit 0 for mixed sheet
     sizes) then runs with all n_threads workers as a BASELINE member (`NP_FM3_BASELINE=0` off). Winner: fewest
     unplaced, fewest sheets, smallest used sheet area, least material on the least-filled sheet. stderr
     `[np] fit_mode 3: winner ...` names it (`NP_FM3_VERBOSE=1` prints per-chain / per-sheet lines).
  Budget: iteration mode gives EVERY joint pack the caller's `iter_budget` (`NP_FM3_PACK_FRAC` scales it), so fit 3
  costs several fit-1 calls per sheet; time mode treats `time_budget_secs` as the whole-run deadline. Drop-policy
  retries per pack `NP_FM3_PRIO_RETRIES` (2), single-part insertions per pack `NP_FM3_ALONE_CAP` (4). Anytime: cancel
  / the deadline stop every pack; the best verified layout so far is returned (parts not reached stay unplaced).
  Preview budgets (`iter_budget < 500`) fall back to fit 0.

## Defaults (2026-10) and `NP_LEGACY`
Measured neutral-or-better on the np_bench gates (see "Tests" below) and therefore ON by default:
`NP_SMART_ROT=1` (smart orientation candidates), `NP_SIMPL_BBOX=1`, `NP_SIMPL_MAXDEV` = 1e-4 x the diagonal of sheet
0's bounding box (0.63 mm on 6000 x 2000, 0.14 on 1000 x 1000; relative, so the same in mm and m documents), and the
cheaper exploration stall `NP_EXPL_STRIKES=2 NP_EXPL_NOIMPR=50`. `NP_SMART_ROT_BIG` stays OFF (harmful). Each env var
still switches its lever off (`=0`) or sets a value; `NP_LEGACY=1` restores every old default at once and is
byte-identical to the pre-change engine on every np_bench `--dump` gate.

## Part priorities (E1, `np_nest_prio`, fit_mode 1 only)
`np_nest_prio(...)` = `np_nest` with one extra input right after `part_rotations`: `const int* part_priority`,
one int per input part (0 = normal, >0 = MANDATORY; NULL = exactly `np_nest`, which now forwards to it with
NULL). Fit modes 0 / 2 ignore it. With an array, fit_mode 1 (`solver/driver.hpp` `prio_fill_one`) changes its
objective from placed COUNT to: every mandatory part on the sheet first (a layout missing one ranks below any
layout that has them all), then the most placed AREA, then the narrowest width. Applied (a) across the
multi-starts, (b) when run_strip picks the layout to cut (`CutRankListener` ranks every feasible layout the
exploration / compression reports, so a wider layout that keeps the mandatory parts beats the narrowest one),
and (c) by a DROP POLICY. Attempt 0 is exactly the plain fit-1 run (same budget, passes, seed, LBF): a run
that drops nothing returns the plain layout. If attempt 0 does not keep every offered part, the normal parts
(area ascending) are dropped by bisection on how many of the smallest go: first the smaller half; a retry that
keeps all it was offered re-adds half of the dropped, otherwise half of the rest is dropped. Every retry runs
a COLD pack of the reduced set (fresh LBF, plain seed, `NP_PRIO_RETRY_BUDGET` x the plain budget, default 1.0
= exactly the plain call on that set) and, if that does not keep everything, a WARM start from the best
earlier layout whose set contains the reduced one, minus the dropped parts (no run at all if those already lie
inside the sheet); the better counts. At most `NP_PRIO_MAX_RETRIES` (default 6) retries per start; a drop count
whose whole offered area cannot beat the best layout already holding every mandatory part is skipped. Last,
an INSERTION run: the best layout holding every mandatory part is frozen as sheet keep-outs and the normal
parts it lacks are strip-packed into the free space; those inside the sheet are added (the joint pack can fail
with parts that fit once the big parts stay put). A
mandatory part is never dropped; if they cannot all fit, the best effort is returned and
`np_last_prio(&mandatory, &missing, &placed_area, &offered_area, &retries)` reports it (stderr:
`[np] prio attempt 0 / retry`, `[np] multistart ... mandatory missing`, `[np] prio verdict ... retries, strip
runs`). GLS / compression / LBF are unchanged; wall time grows with the retries (up to 2 strip runs each).
Mirrors: `NestPhysicsWrapper.np_nest_prio` / `np_last_prio`, `tools/bench3dm/engines.py`
(`flatten_np(instance, cfg, mandatory=...)` or a per-part `"priority"` key -> `inputs.prio` -> `solve_np`
calls `np_nest_prio`).

## Upstream sync (sparrow 0.3.0 / jagua-rs 1.1.0, compared Oct 2026)
This engine is a C++ port of sparrow (strip-packing optimiser) on jagua-rs (collision engine). Items from
sparrow 0.2.0-0.3.0 and jagua-rs 1.1.0 checked against the port; every ported item is behind an env switch
whose default reproduces the pre-sync engine byte for byte (np_bench `--dump` gates).

| upstream item | status | here |
|---|---|---|
| sparrow #156: jagua-rs `cd_threshold` 16 -> 64 | PORTED, switch | `NP_CD_THRESHOLD=<n>` (capi `engine.cd_threshold`, default 16) |
| sparrow #156: early-exit sample evaluation | ALREADY PRESENT | `solver/evaluator.hpp` `SpecializedHazardCollector::early_terminate` |
| sparrow #162 / issue #160: conservative minimum strip width stop | PORTED, switch | `NP_MIN_WIDTH_STOP=1` (upstream bound) / `=2` (+ continuous-rotation parts), `solver/optimizer.hpp` `strip_min_width_bound`, exploration + compression |
| sparrow #161: loss overflow to +inf must not turn into NaN | PORTED | zero-loss x inf-weight guard always on (bit-identical on finite weights): `OverlapEntry::weighted_loss`, `calc_weighted_loss`; optional weight cap `NP_LOSS_GUARD=1` (`NP_LOSS_GUARD_CAP`, 1e20); diagnostics `NP_LOSS_TRACE=1` |
| jagua-rs 1.1.0: simplified shapes stay within the original bbox | PORTED, switch | `NP_SIMPL_BBOX=1`, `geometry/shape_ops.hpp` `simplify_shape` (rejects ConvexConvex steps whose new vertex leaves the input bbox); report `NP_SIMPL_REPORT=1` |
| jagua-rs: generic hazard entities | NOT APPLICABLE | the port keeps a fixed `ObstacleRef` kind set (part / exterior / hole / quality zone); nothing here needs user-defined hazards |
| sparrow: orientation input format (explicit allowed-angle lists) | DEFERRED | the C ABI exposes `part_rotations` (N equal steps or free); arbitrary angle lists would need an ABI extension, no host asks for it yet |
| jagua-rs: `Arc<Item>` sharing | NOT APPLICABLE | `Part` already shares its shapes / caches through `std::shared_ptr`, copies are cheap |

Dev env knobs (read per solve):
- `NP_CD_THRESHOLD=<n>` (1..255, default 16) quadtree nodes with at most n active edges are collision-
  checked directly. Exact either way; only the traversal changes (speed, and FP summation order of losses).
- `NP_MIN_WIDTH_STOP=1|2` stop exploration / compression before a width below a provable lower bound:
  1 = max(total collision area / strip height, widest narrowest-fitting orientation of discrete-rotation
  parts); 2 = also continuous-rotation parts (3600 sampled angles, Lipschitz slack). Relative 1e-4 slack.
- `NP_LOSS_GUARD=1` cap GLS weights at `NP_LOSS_GUARD_CAP` (default 1e20); `NP_LOSS_TRACE=1` prints how
  many weight updates ended at +inf and how many of those had zero loss (NaN without the always-on guard).
- `NP_SIMPL_BBOX=1` collision-outline simplification never leaves the exact outline's bbox;
  `NP_SIMPL_REPORT=1` prints per part vertex counts, max outline deviation, bbox excursion, area change.
- `NP_PRIO_AREA=1` forces the fit-1 priority path (area objective, no mandatory parts) on plain `np_nest`.
  Default off: without it and without a priority array every mode is byte-identical to before E1.
- `NP_PRIO_RETRY_BUDGET=<f>` (priority path) budget of each retry strip run as a fraction of the plain fit-1
  run's budget (default 1.0). `NP_PRIO_MAX_RETRIES=<n>` retries per start (default 6, 0 = attempt 0 only).
- `NP_FIT_MODE=<n>` overrides `NpParams.fit_mode`.
- `NP_BIN_GAP_FRAC=<f>` keep-out gap between columns as a fraction of the sheet width (default 0.10).
- `NP_BIN_QT_DEPTH=<d>` quadtree depth of the wide strip (default: the engine depth, 4 - deeper trees measured slower, with bit-identical layouts).
- `NP_BIN_STEP_CAP=<f>` largest last-column shrink step, as a fraction of the sheet width, the adaptive
  (bisecting) step may grow to (default 0.02; the base step is the separator's 0.001).
- `NP_BIN_VERBOSE=1` progress lines on stderr: one `[bin] SNAP` line per dropped column (round, new k,
  rounds since the previous drop, part count / area of the new last column) and a `[bin] done` summary
  (k0, snaps, rounds per snap, failed relaxations, column rotations, evictions tried/ok, lever flags).
  They are plain C stderr writes, so they also appear when the DLL is loaded from Python via ctypes
  (fd 2 of the host process; a Python-level `sys.stderr` replacement does not capture them).
- `NP_BIN_ROTATE_COL=1` before exploring and after every column drop, swap the least-filled column with
  the last one (rigid +-delta*P moves), so the shrink attacks the emptiest column. Only when all
  per-sheet keep-outs are identical. Default OFF: on the synthetic sets it never saved a sheet and cost
  one (30 C-parts, seed 100: 3 -> 4 sheets).
- `NP_BIN_EVICT=1` targeted eviction: every `NP_BIN_EVICT_EVERY` (default 4) failed relaxations since the
  last column drop, and when the last column sits at its width floor with <= 3 parts, move every
  last-column part to its best weighted-overlap pose inside the other columns, then relax. Default OFF:
  a failed relaxation costs ~1000 rounds, so a 4000-round run sees ~3 failures and the eviction rarely
  fires (0 successes measured).
- `NP_BIN_COLWALLS=1|2` per-column walls: every non-last column gets its own right wall (its keep-out
  band starts at c*P + w_c - guard). When the last-column squeeze stalls (`NP_BIN_SWEEP_AFTER`, default
  1 failed relaxation, or the last column at its floor) a COMPACTION SWEEP shrinks the walls of columns
  0..k-2, densest first, by an adaptive step (short separator budget per attempt; a failing attempt is
  reverted, tracker rebuilt from scratch). Mode 1 then releases the walls so the squeezed-out right-end
  bands become free space (eviction, if on, samples them first); mode 2 keeps the walls. Compression
  starts with one sweep over all columns (walls kept). Off by default: on the synthetic sets it cuts the
  total used width (30 C-parts: -6 %) but moves sheet counts both ways (+1 / -1 over 9 runs); on the
  58-part job (20000 rounds) it left the 6th sheet at 72 % of W instead of stalling at 94 %.
- `NP_BIN_BFD=1` balanced start: instead of the left-to-right LBF, best-fit-decreasing over k_lb+1
  pre-created columns. Parts in LBF order (convex-hull area x diameter, descending); each tries the
  columns with the most free area first and is placed by the LBF evaluator sampled inside that column
  (collision-free only); a part no column accepts opens a new column. Verbose prints the start's
  per-column fill.
- `NP_BIN_STRIKES=<n>` / `NP_BIN_NOIMPR=<n>` (fit 2 exploration only; defaults 3 / 200 = the separator's
  own): a failed relaxation costs about strikes x no-improvement rounds before the explorer diversifies.
- `NP_SMART_ROT_BIG=1` (needs `NP_SMART_ROT=1`): parts whose convex hull is >= `NP_SMART_ROT_BIG_FRAC`
  (default 0.08) of sheet 0's area sample rotations ONLY from their smart set, coordinate descent clamps
  the rotation to +- `NP_SMART_ROT_TILT` degrees (default 3) around the nearest smart angle, and the
  perturbation swap snaps them to the nearest smart angle. Small parts keep the mixed sampling.
- `NP_BIN_GROUPFILL=1` group-and-fill start. BIG set = parts >= `NP_GROUP_MIN_FRAC` (0.05) of the sheet area,
  at most `NP_GROUP_MAX` (24) by area. Group tests (single-sheet packs) run in two phases: LBF for every
  single, pair, triple whose pairs all fit and quad whose triples all fit (largest area first; capped by
  `NP_GROUP_MAX_TESTS` 4000 and `NP_GROUP_MAX_SECS` 120 of wall time), then the LBF failures are re-tested with
  `NP_GROUP_TEST_ROUNDS` (150) strip-shrink rounds, most-constrained part first, within `NP_GROUP_BUDGET_FRAC`
  (0.25) of the run budget (taken out of the exploration budget). Partition: DFS for a FULL cover of the
  big set by <= k feasible groups at the smallest k whose free area holds the fillers x (1 + `NP_GROUP_SLACK`
  0.10), minimising the sum of group widths (pool of 5); if no full cover exists, the largest nb big parts are
  covered and the rest become fillers. The chosen groups are compacted (`NP_GROUP_COMPACT_ROUNDS` 300), laid
  into columns, fillers go in by best-fit-decreasing; a start that is not loss-free (e.g. per-sheet
  keep-outs the tests ignore) falls back to the BFD / LBF start. `NP_BIG_PIN=1` pins the grouped parts to
  their column (sampling box = the column, no cross-column perturbation swaps). `NP_GROUP_RETRY_ROUNDS=n`
  (iteration mode) gives each pooled partition n rounds to reach its k before falling back to the next.
- `NP_SMART_ROT=1` (all modes) continuous-rotation parts also sample a smart orientation set: the
  min-area bounding rectangle angle (rotating calipers on the hull) and the longest-edge angle, each +
  quarter turns (<= 8 angles, bboxes cached at build_part); drawn with probability `NP_SMART_ROT_P`
  (default 0.5), else the uniform grid. Used by LBF and the separator alike; coordinate descent still
  refines. Off => byte-identical.- `NP_SIMPL_MAXDEV=<d>` (all modes; model units, e.g. `0.5` for mm) caps how far the collision outline may
  stand proud of the exact outline. The simplify tolerance is an AREA ratio (1e-3), so its absolute error
  grows with part size (a 4.7 m^2 plate sat up to 9 mm outside its true outline); with the cap each part's
  tolerance is tightened 10x at a time until the outline is within `d` (exact outline below 1e-7). Big parts
  get more vertices (slower per eval); small parts are untouched. Off => byte-identical.
- `NP_EXPL_TARGET=1` (strip path, fit 0/1) exploration never shrinks past the sheet width: a 0.1% step that
  would overshoot it is clamped to exactly the sheet width, so the last step fights the real wall, not one
  up to 6 mm tighter. Off => byte-identical.
- `NP_EXPL_STRIKES=<n>` / `NP_EXPL_NOIMPR=<n>` (strip path, fit 0/1; defaults 3 / 200) the strip-exploration
  separator's strike limit / rounds without improvement - the fit 0/1 twin of `NP_BIN_STRIKES` /
  `NP_BIN_NOIMPR`. A failed width whose residual is a hairline touch sits on the overlap proxy's epsilon floor
  and never "improves", so it burns all 3 x 200 rounds; 2 / 50 makes failures ~6x cheaper and buys more
  perturbation (basin) hops per budget. Off => byte-identical.
- `NP_EXPL_VERBOSE=1` diagnostic trace on stderr: every relax outcome (width, round, loss, per-part rotation +
  bbox, non-zero pair/container losses with GLS weights), strike ends, perturbation swaps, compression
  attempts and run_strip passes. Print-only => byte-identical.

### Optional worker limits and warm repair research API

`NP_WORKERS=12` limits the native worker pool for ordinary **single-start** cold C API calls, useful when several external processes pack independent sheets. Accepted values are integers from 1 through 256; invalid values are ignored. Unset preserves existing defaults. Multi-start portfolios continue to use `NP_STARTS_WORKERS`; `NP_WORKERS` does not override that path. Explicit warm calls use `NP_WARM_WORKERS` instead (a positive integer; unset uses the existing worker selection).

`np_nest_warm_one` is an opt-in research API, not the default Grasshopper solve path. It accepts the existing `np_nest` arguments plus four arrays immediately after `part_rotations`: `warm_present`, `warm_tx`, `warm_ty`, `warm_angle`. Arrays contain one entry per input part, with transforms in the same source coordinates and radians as ordinary C API output. Supply one sheet; every input part is required. The Python research adapter is `tools/bench3dm/engines.py::solve_np_warm`.

The solver retains supplied transforms initially, appends every missing part outside the target sheet at a legal height-fitting orientation, and compresses the complete strip while allowing all neighbors to move. If conservative collision outlines or float conversion make the supplied seed overlap, up to 10% of the budget sanitizes the complete expanded seed. Optimization starts only after zero proxy loss. Appended continuous rotations use a finite orientation sample.

Return 0 means every requested part was returned on sheet 0 and native output quality checks passed. Return 1 means no complete repair; return 2 means invalid or unsupported input. On failure, supplied incumbent transforms are restored exactly and absent parts remain unplaced; overlap/out-of-bounds quality fields are -1 (unknown), not a clean-layout certificate. A complete validated best snapshot may be returned even when cancellation was requested. Independently validate the returned polygons before accepting a research candidate.

This path supports fit modes 0/1 and rejects reduced or unsupported inputs. Part-hole nesting is not preserved as a parent/child constraint: the repair collision stage treats outer outlines as solids, so it may move children out of holes or fail. It never drops required parts to claim success. No five-sheet result or optimality guarantee follows from enabling this API.
