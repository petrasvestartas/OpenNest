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
See `out/baseline.csv`. Headline (gens 5, pop 10): rects ~0.85-0.88 util_strip, concave ~0.61-0.63,
rings ~0.53 (hole-fill unused — parts never nest inside part holes; Q2 target).
