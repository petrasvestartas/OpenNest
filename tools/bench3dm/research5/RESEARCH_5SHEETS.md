# Closing the last 0.19 sheets: diagnosis, commercial techniques, experiments (2026-10-18)

Scope: Edward job, `job1_fixed_round` (solve sheet 5980 x 1980, 58 parts = 4.304 solve-sheets). Best layout so far
`out/groupfill_np_forced8_round_insert` (OpenNest Collision GroupFill-Insert, NestFab's plate assignment): 5 sheets at
87.5 / 88.2 / 85.1 / 80.5 / 70.3 % solve utilisation, 5 fillers left = sheet 6. Scripts in this folder; nothing
instance-specific is proposed as a rule.

## 1. Diagnosis: the loss is GEOMETRY of the plate layout, not search budget

`diag_free.py` rasterises the free space of a sheet (5 mm pixels, 3.5 mm conservative bias) and tests every leftover
part at every 1-degree rotation at every position (FFT correlation of part mask with the blocked mask).

Leftovers of forced8 (sheet 6): P007/P008 3777 x 402 mm (4.4 % of a sheet each), P018 2380 x 262, P021 2002 x 256,
P043 556 x 537. Four of the five are LONG THIN STRIPS (63 %, 40 %, 33 % of the sheet width).

| forced8 sheet | plates | util | free | free components after 50 mm erosion (largest, % of sheet) | largest inscribed rect | leftovers that fit anywhere, any angle |
|---|---|---|---|---|---|---|
| 3 (idx 2) | 5,10,14 | 85.1 % | 14.9 % | 22 comps, largest 0.68 % | 410 x 230 mm | none (0/360 angles each) |
| 4 (idx 3) | 1,3,13,28 | 80.5 % | 19.5 % | 9 comps, largest 1.60 % | 1230 x 225 mm | none |
| 5 (idx 4) | 2,9,15,25 | 70.3 % | 29.7 % | 10 comps, largest 2.78 % | 750 x 415 mm | none, not even P043 (556 x 537) |

Engine confirmation (`exp_joint.py`, part C): inserting P007 into the frozen sheet-5 layout with 4000 rounds x 2 starts
x 2 seeds, and P043 likewise, is refused every time (78-90 s each). The balanced-fill "no sheet" refusals are therefore
geometric impossibilities, not search failures. Verdict: **(a)**. Nothing in the insertion stage (budget, order, seeds,
sampler) can place these parts; the plates-alone layout has to change.

What the reference does with the SAME plates: NestFab's sheet 0 = plates 2, 9, 15, 25 + strips 7, 8 + four smalls
(35, 42, 43, 56) at 78.0 % solve utilisation (73.8 % net). Its plate angles are the same as ours within 1 degree for
plates 2 and 9 (178 / 0 deg), plate 15 differs by a 180-degree flip, plate 25 by 90 degrees. The difference is the
POSITIONS: the two 3777 x 402 strips lie between plate 2 and plate 9 (0.7 / 179.6 deg), i.e. the plates were placed
with the strips, not before them. NestFab's own free space is just as shredded as ours (largest free component after
50 mm erosion 0.2-1.7 % of a sheet, largest inscribed rectangle 1950 x 50 ... 1230 x 180 mm) because it is a finished
layout; the lesson is that free space must be consumed by the long parts WHILE the plates are positioned.

Why our plates-alone pack shreds the free space: fit_mode 1 = strip packing of the group with WIDTH as the only
pressure; with 4790 mm plates the width is fixed by the plates, so after feasibility the GLS has no objective left and
the plates sit wherever the relaxation left them (tilted 2-3 degrees, wedge-shaped gaps, PNG `forced8_diag/free_sheet4.png`).
A bottom-left-fill with contact would have stacked them flush.

Also measured: the "big set" threshold area >= 5 % misclassifies the strips as fillers although they span 63 % of the
sheet width; part difficulty must be max(area share, length / sheet width, width / sheet height), not area alone.

## 2. What the commercial and academic engines do (techniques only)

Sources: public papers/patents only; the Optalog RE notes are already in memory `commercial-nesting-engines-anatomy.md`.
Hellier's thesis PDF (Nottingham eprints 52345) is not fetchable without a download, so only its chapter titles
("Irregular packing with a non-greedy bottom-left fill approach", ch. 7) and the published papers are used.

1. **Knapsack assignment that favours big pieces, one bin at a time** (Martinez-Sykora, Alvarez-Valdes, Bennell, Ruiz,
   Tamarit, EJOR 258 (2017) "Partial Bin Packing"; restated in Wang, Chang, Man, IJIEC 13 (2022) 491-506): maximise
   sum (s_i / s_max)^2 over the pieces put in the current bin subject to area; the squared weight deliberately assigns
   the large pieces first because a count/area-greedy assignment "assigns smaller pieces first ... leaves larger pieces
   behind, resulting in an overall poor solution". Local search LS2 then moves pieces from the LEAST utilised bin into
   the others (remove one piece from a target bin, try the moved pieces, put the removed piece back) and accepts if a
   bin disappears or a bin's utilisation rises. This is exactly our group-and-fill + leftover repack; what we lack is
   the per-bin PACKER that takes the assigned set as a whole (theirs is overlap minimisation on the whole set).
2. **Overlap minimisation on the WHOLE assigned set of a fixed bin** (Wang/Chang/Man 2022; Zhang et al. C&OR 2022
   "iteratively doubling local search ... limited rotations": waste-least-first assignment + swap pieces between bins
   + overlap minimisation per bin; Abeysooriya, Bennell, Martinez-Sykora 2018 jostle for bins with free rotation).
   All of them place plates and fillers TOGETHER with every piece mandatory; the bin either becomes feasible or the
   assignment changes. Our fit_mode 1 instead maximises the COUNT and drops whichever part is hardest, which is why we
   had to freeze plates and lost the joint placement.
3. **Promising-angle subsets** (Wang/Chang/Man 2022 two-stage angle set after Abeysooriya 2018; Burke-Hellier-Kendall-
   Whitwell 2006 try every allowed rotation per part and keep the least costly; Optalog: part's own edge angles + 0/90):
   candidate angles from the part's own geometry, then fine refinement. We have this (`NP_SMART_ROT`); on this group it
   is not the limiter (our plate angles match the reference within 1 degree for 2 of 4 plates).
4. **Bottom-left-fill with contact and a non-greedy sequence search** (Burke et al. 2006, OR 54:587; Hellier 2013 ch. 7;
   NestFab Standard): each part is pushed to a contact position bottom-left; the sequence is improved by tabu/hill
   climbing with 1/2/3/4/N-opt moves; long parts early in the sequence get a lane along a plate edge. Constructive
   contact placement never leaves wedge gaps between plates: this is the single property our GLS plate pack lacks.
5. **Two-axis pressure in the per-sheet objective** (Optalog squeezer cost = bbox height x 20 + pricer; Autodesk
   US 9495484 rigid-body nesting pulls parts toward a corner, alternates large and small parts by a size threshold,
   keeps a shake only if density rises): on a fixed sheet the packer must keep compressing in BOTH axes after
   feasibility so the parts form a block and the remainder is one coherent region (also what the user asked for the
   last sheet). Our fit_mode 1 compresses width only.
6. **Repair instead of refuse** (Gardeyn & Wauters EJOR 301 (2022) goal-driven ruin and recreate: fix a shrinking
   area budget, ruin part of the layout, recreate, late-acceptance; Mundim et al. 2018 limited-size containers): a
   fixed-bin solver that may temporarily overlap and repairs is the general form of "insert the strip, then move the
   plates a little". The sparrow GLS already has this machinery (overlap proxy + separation) but only in strip
   compression, never with a mandatory new part on a fixed sheet.

## 3. Experiments (collision engine `nest_physics.dll` 3a854e38, round instance, S+D levers; NFP `nfp_nest.dll` 2f1196b7)

All packs: fit_mode 1 (ONE sheet, MAX FILL), 3600 rotations, 8000 rounds x 2 starts unless noted. "ALL" = every
offered part placed, clean (0 overlaps / oob by the engine's own check).

| # | Script | Test | Result |
|---|---|---|---|
| C | exp_joint | insert P007 into forced8 sheet 5 frozen layout, 4000 rounds x 2 starts, seeds 100 / 7; P043 likewise | refused, refused, refused (78-90 s each) = geometry, not budget |
| A | exp_joint | plates 2,9,15,25 + strips 7,8 jointly | **6/6 ALL, 75.3 %**, 62 s / 77 s (seeds 100, 101) |
| B | exp_joint | NestFab's sheet-0 set: plates + 7,8 + 35,42,43,56 (10 parts) | **10/10 ALL, 78.0 %** in 166 s, both seeds = exactly the reference's solve utilisation for that sheet (78.0 %) |
| D | exp_joint | plates + 7,8,18,21 (8 parts) | 6/8: the engine DROPPED plates 2 and 9 (31.6 % and 18.2 %) to keep the four strips, util 30 %, 404 s = the COUNT objective of fit_mode 1 chooses the wrong subset |
| iii | exp_nfp | NFP engine (BLF + GA, rot 36/72, tilt 0/2, 10 gens x 30 pop, 900 s cap) on the A and B sets | never seats both 3777 mm strips: 5/6, 4/6, 4/6; 8/10 three times (misses 7, 8); 900-960 s each |
| v | exp_band | plates packed on a reduced-height strip (0.85 / 0.78 / 0.72 H) to leave a clean band, groups (2,9,15,25) and (1,3,13,28) | all six packs FAIL (2-3 of 4 plates) although plate area is only 78-95 % of the strip: the strip packer cannot form a compact plate block in two axes |
| iv | exp_axis | plates alone, axis-aligned big-part angles (`NP_SMART_ROT_BIG=1`) vs free tilt, then geometric fit + engine insertion of 7,8,18,21,43 | BIG: largest free component 6.9-7.3 % of the sheet, largest rect up to 2335 x 410, P018/P021/P043 fit geometrically, engine inserts 2 of them -> 70.6 / 70.0 %. Free tilt (= forced8): largest component 6.0 %, largest rect 455 x 960, NOTHING fits -> 66.5 %. Neither lets the 3777 mm strips in |
| iv' | exp_axis | search vs geometry on narrow fits | P021 fits geometrically at 2 of 180 angles (seed 100) but the GLS insertion refused it; at seed 101 it fits at 14 angles and is placed: single-part GLS insertion misses fits whose feasible angle window is 1-2 degrees |

Capacity arithmetic after fixing sheet 5 only: 87.5 + 88.2 + 85.1 + 80.5 + 78.0 = 4.19 sheets < 4.304 needed. The
reference's other sheets are 85.4 / 86.5 / 91.2 / 89.3 and they too hold the long parts (P021 on its sheet 3 at 0 deg
along the top edge, P018 on sheet 4 at 170 deg): the long-part-with-plates rule has to apply to every sheet, not only
to the one that happens to be last.

## 4. Ranked techniques for OpenNest (general, nothing instance-specific)

1. **Difficulty classes, not an area threshold; long parts are packed WITH the plates (driver, no engine change).**
   Evidence: A and B pack at the reference's density in 1-3 min while the same strips cannot be inserted afterwards
   (C, iv, balanced-fill refusals). Rule: class A = area >= a_min (5 %); class B = not A and max dimension >= l_min x
   sheet width (0.3); class C = rest. Phase 1 = cover of A (as now). Phase 2 = assign class B to cover groups by
   JOINT feasibility packs (group + its B parts + candidate, all must land; longest first; prefer the sheet with the
   most free area; a B part no group accepts goes to the leftover pool). Phase 3 = freeze A+B, insert C balanced (as
   now). On this job class B = 7, 8, 18, 21, 16, 22, 23, 24 (11, 12, 17 are already in A): at most 8 x 5 joint packs.
2. **Per-sheet packer objective = placed AREA with mandatory parts, not placed COUNT (engine, small).** Evidence: D
   dropped the two largest plates to keep four strips; Martinez-Sykora / Chang: count-greedy "leaves larger pieces
   behind". With this, phase 2 above becomes one pack per group (plates mandatory, strips optional, engine drops the
   least area) and the two-stage freeze is no longer needed for the long parts.
3. **Portfolio of plate orientations per group: axis-aligned (BIG) and free tilt both tried, keep the layout whose
   free space accepts the most class-B+C area (driver).** Evidence: iv (+4 % on this group, largest free component
   7 % vs 6 %, 2 more parts) while the main thread found BIG harmful for the (5,10) pair: neither setting dominates,
   so it is a per-group portfolio choice, which is also how Optalog runs its nesters.
4. **Insert-and-repair for narrow fits (engine, medium).** Evidence: iv' (P021 geometrically fits at 2/180 angles,
   refused by the single-part GLS). A warm start of `np_nest` from a given layout, with the frozen parts movable under
   a small penalty, turns insertion into the sparrow separation problem it already solves (Gardeyn & Wauters repair,
   NestFab "renest", Optalog compaction with slide + rotate). Needed for the last 1-3 % per sheet, not for the strips.
5. **Two-axis compaction for the remnant sheet only (already built by the main thread).** Evidence: the band
   experiment fails for plate groups (the strip packer cannot form a block of big plates on a reduced height), while
   it works on the 5-part leftover sheet (57 % -> 28 % bbox). Keep it as the last-sheet post-pass only.
6. **Not worth pursuing now:** the NFP engine as per-sheet packer (iii: 15 min per pack and never seats the strips),
   bigger insertion budgets (C), long-and-thin-first insertion order (the strips fit at no order once plates are frozen).

## 5. Engine / driver changes to brief

**Driver (Python prototype first, then the C++ port):** `groupfill` phase 2 "long-part assignment": after the MILP
cover, for each class-B part in order of decreasing length, for each cover group in order of decreasing free area:
`pack(group + assigned_B[group] + [b], fill_gens, fill_pop, seed, cap)`; accept iff every offered part lands (clean);
stop at the first accepting group. Then phase 3 freezes A+B and runs the balanced insertion of class C. Cost on this
job: at most 8 x 5 joint packs of 1-3 min. Acceptance: the forced-group replay reaches >= 78 % on the (2,9,15,25)
sheet with 7 and 8 on it; leftovers after phase 3 fewer than today's 5-6; generality guard on the np_bench sets with
a long-part class present and absent (class B empty must reproduce today's pipeline exactly).

**Engine change E1 (`nest_physics.dll`, fit_mode 1):** optional per-part priority array appended to the C API
(`part_priority`: 0 = normal, 1 = mandatory; NULL = today), mirrored in `NestPhysicsWrapper.cs` / `engines.py`.
Behaviour: (a) multi-start / cut selection scores a layout by placed solve AREA with all mandatory parts placed (a
layout missing a mandatory part ranks below any layout that has them all); (b) the initial LBF order and the choice
of which part crosses the cut prefer evicting non-mandatory, small-area parts; (c) byte-identical when the array is
NULL (np_bench `--dump` gates). Acceptance: experiment D returns the four plates plus as many strips as fit (never a
dropped plate); A and B unchanged.

**Engine change E2 (later):** warm start = optional initial `tx/ty/angle` per part + `movable_penalty`, so a new part
can be inserted into an existing clean layout with the sparrow separation moving neighbours by millimetres; used for
class-C insertion and for the leftover repack. Acceptance: the iv' case (P021, seed 100) is placed; existing modes
untouched.

**Measured contradictions to the main thread's working assumptions:** (1) insertion / fill quality is NOT the lever
for the last sheets: nothing fits once the plates are frozen (C, iv, balanced "no sheet" lines); (2) the collision
engine is NOT weaker than NestFab per sheet: given NestFab's exact sheet-0 set it reproduces 78.0 % in 166 s (B); the
gap is the assignment of long parts plus the count objective; (3) reduced-height (two-axis) compaction does not work
for plate groups with the strip packer (v), only for small leftover sets; (4) the NFP engine with round offsets does
not rescue this (iii).
