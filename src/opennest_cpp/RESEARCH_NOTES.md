# nfp_research — experiment log

Protocol: one lever per experiment/commit, A/B-ed on `nfp_bench` (datasets rects/concave/rings,
seeds 7..47) vs the rolling baseline CSV. Keep if median util_strip +>=0.3pp on the target
dataset, nothing regresses >0.2pp, overlap_area ~ 0, speed levers within 15% on quality-neutral
runs. Refuted levers are reverted and logged here.

## Phase 0 — harness + correctness

### BUG-1 FIXED: multi-loop NFPs truncated to the largest loop (overlap!)
Clipper2's `MinkowskiSum` is a *boundary sweep* (union of edge quads). Its output is a set of
loops: the outer NFP boundary, possibly **multiple disjoint/pinched CCW lobes** (concave parts),
plus CW interior loops that are either **genuine pockets** (B fits inside A's concavity) or the
**deep-overlap artifact** (annulus interior). The old code (`process()`, `getOuterNfp`) kept only
the single largest loop:
- convex pairs: accidentally correct (artifact dropped),
- concave pairs: REAL exclusion lobes discarded → parts placed inside a dropped lobe, including
  EXACTLY on top of an identical part (repro: `out/part5.txt` seed 17, two copies coincident,
  overlap = exactly one part area = 28905.7).

Fix (NfpWorker.cpp): keep ALL loops. Primary = largest CCW loop; other CCW loops become children
with `forbiddenLobe=true` (emitted with outer winding so the NonZero union keeps them forbidden);
CW loops are kept as holes only if `isGenuineNfpHole()` validates that B placed inside does not
overlap A (artifact loops dropped — their area stays covered by the outer fill). Same treatment
in `Process2` (Boost path concatenated multiple outer paths into one self-intersecting ring).
`thenIterate` preserves lobes when swapping in hole-IFPs; `getInnerNfp` filters lobes out of IFP
loops. NOTE: this diverges from the C#/deepnest reference BY DESIGN — the reference has the same
bug class.

### BUG-2 FIXED: inner/outer NFP cache key collision (density loss + misplacements)
`getInnerNfp` cached sheet-IFP entries without setting `DbCacheKey.Type` (default 0). Sheet and
part `source` numbering both start at 0, so IFP key (sheet0, partJ, ARot=0, rotJ) was IDENTICAL
to the outer pair key (part0@0°, partJ@rotJ). Whichever inserted first poisoned the other:
getOuterNfp could return a sheet IFP as a "pair NFP" (giant exclusion → density loss) and
getInnerNfp could return a pair NFP as the "sheet region" (tiny/wrong feasible region →
misplacements). Fix: `Type=1` on all inner-fit insert/find. (Pre-fix this poisoning *masked*
BUG-1 on some datasets by making exclusions oversized.)

Verification: 3 datasets x modes {0,1} x 5 seeds — overlap_area = 0.000000 on all 30 runs.

Debug tooling kept (env-gated): `NFP_VERIFY_PLACE=1` traces every placement and flags positions
strictly inside the combined NFP; `NFP_DUMP_PAIR=1` dumps every pair NFP computed by `process()`.
Bench probes: `--probeNfp rotA rotB` (NFP loop dump + on-top containment check), `--dumpDataset`,
`--dumpPlacements`.

## Baseline (post-fix)
See `out/baseline.csv` (gens 20, pop 30, seeds 7-47). Gravity is the best placement
(rects .880, concave .636, rings .530); squeeze is 10-20x slower at equal-or-worse util.
Hole-fill unused — parts never nest inside part holes (Q2 target).

## Phase 1 — speed levers (all KEPT, all placement-bit-identical)

| Lever | What | Wall effect (concave m0 gens10 pop20 unless noted) |
|---|---|---|
| S2 | scalar bbox scoring, lazy PlacementItem, dead hull stores deleted | gravity neutral; squeeze -20% |
| S1 | incremental clipCache union read path (deepnest parity) + >=360 rotation-key normalization | rects -15%, concave -13%, rings -4% |
| S3 | squeeze hull: pre-sorted merge + monotone chain (same D3 chain/shoelace) | squeeze -51% (cum. -68% vs pre-S2) |
| S5 | sheetNfpClipperCache wired (IFP clipper conversion memoized); inpairs -> unordered_set | concave -17% cumulative vs pre-S1 |
| S4 | thread pool | DEFERRED — mode-1 gen-parallel (production default) forces inner UseParallel=false; no spawn on the hot path |

MEASURED (NFP_PROFILE, concave m0): placeParts = 80% of wall, only 68 NFP computes —
remaining cost is the per-candidate Clipper Difference/Union Execute, i.e. the algorithm
itself. Further whole-solve speed needs structural change (not micro-opt); Phase 2
converts the won speed into quality at fixed --timeBudget instead.

## Phase 2 — quality levers (fixed --timeBudget 10s, mode 1, gravity, 5 seeds)

### Q1 KEPT (recommendation): tryAllRotations=true
At fixed 10s budget (vs q1-control, same exe): rects .8836→.8871 (+0.35pp),
concave .6523→.6701 (+1.78pp), rings .5306→.5305 (neutral). Phase-1 speedups made
evaluating every rotation affordable; best-rotation beats first-valid-rotation.
ACTION for production: flip the OpenNest plugin's default (the C-API just passes the
flag through; faithful mode keeps it forced off). Bench experiments now run --tryAllRotations.

### Q3 REFUTED: diverse seeded initial GA population
width-desc/height-desc/irregularity-desc + 2 shuffles replacing 5 mutants-of-adam:
rects .8871→.8871 (=), concave .6701→.6677 (−0.24pp), rings = . No gain anywhere, small
concave regression — at a 10s budget the GA already explores the order axis; deterministic
seeds just displace early mutation diversity. Reverted.

### Q4b KEPT: origin-anchored gravity pull (non-faithful)
gravityWeight distance now measured to the SHEET ORIGIN instead of the layout centroid
(placeParts + compaction). vs q1-allrot: rects .8871→.8991 (+1.20pp), concave
.6701→.6733 (+0.32pp), rings = . Probe: 4 fillers now pack 2x2 corner-aligned inside a
200^2 hole (previously fragmented). Note rings util_strip is pinned at .5305 by the host
arrangement (fillers in holes don't change strip width) — it can't see this lever.

### Q5 KEPT: compactionPasses 2 -> 4 (default)
10 seeds at fixed budget: 4 >= 2 everywhere (concave +0.6pp, rects +0.2pp); 2 was the
WORST of {0,2,4} (comp0 also beats comp2 on concave — compaction at fixed wall budget
trades GA generations for polish; 4 is the safe pick, never worse than 2).

### Q6 REFUTED (both variants): fitness re-normalization
Q6a (pure width fraction): rects +0.72pp, rings +0.23pp, BUT concave −0.4pp in two
independent seed batches. Q6c (normalized (2wFrac+hFrac)/3): rects −0.70pp, concave net
negative. INSIGHT: the GA is RANK-based, so the old "mixed-unit" fitness already ranks
single-sheet layouts by its dominant term 2w+h (gravity minarea); the variants merely
RE-WEIGHTED the objective (pure-w helps rects / hurts concave; ~w+h the reverse). There
is no universal winner on this axis — the theoretical "unit cleanup" has no practical
content for rank selection. Reverted. (Multi-sheet pressure: the +sheetarea term already
dominates = sheet count first; also fine.)

### Config sweeps REFUTED (defaults already optimal at fixed budget, concave)
edgeSamples: 0 → −0.15pp, 4 → −0.21pp vs default 2. populationSize: 20 → −0.61pp,
50 → −0.84pp, 120 (the PRODUCTION default!) → −0.59pp vs 30. RECOMMENDATION: the GH
plugin should send populationSize ~30, not 120 — at a fixed time budget the large
population wastes budget on per-generation breadth instead of generations.

## HEADLINE (fixed 10s budget, engine defaults each side, 5 seeds, mode 1 gravity)
post-bugfix engine with old defaults  →  final engine (S1-S5 + Q1/Q4b/Q5 defaults):
- rects:   util_strip .8832 → .9010  (+1.78pp)
- concave: util_strip .6433 → .6792  (+3.59pp)
- rings:   .5305 → .5305 (strip width is host-arrangement-bound; the in-hole packing
  improvement is real — see holetest2 probe — but invisible to this metric)
Plus the two correctness fixes: BEFORE this branch, concave layouts could contain parts
placed exactly on top of each other (overlap ~107k mm^2 on the concave dataset) and the
inner/outer cache collision silently corrupted feasible regions. SVGs: out/final_svg/.

### Q2 scope finding: hole-fill WORKS post-fix, but FRAGMENTS the hole
Probe (out/holetest.txt, holetest2.txt): a filler IS placed inside a host's hole (the
thenIterate hole-IFP children survive the union as feasible islands — the Type-collision
fix unblocked this). BUT in-hole positions are scored by the GLOBAL bbox (indifferent
among interior positions) + gravityWeight toward the LAYOUT centroid + tie-breaks on the
rotation-dependent SHIFT value, so fillers scatter mid-hole and fragment it: 4x80^2
fillers fit a 200^2 hole geometrically, only 2 land inside. The Q2 lever is therefore
IN-ISLAND packing bias (absolute bottom-left position tie-break / origin-anchored
gravity), not missing hole regions. addHoleFillRegions (part-in-part for parts placed
ON the sheet) remains a separate, smaller lever.
