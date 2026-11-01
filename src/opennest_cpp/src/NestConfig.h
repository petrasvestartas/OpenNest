#pragma once

namespace nest {

enum class PlacementTypeEnum {
    box = 0,
    gravity = 1,
    squeeze = 2
};

/// Nesting configuration
struct NestConfig {
    PlacementTypeEnum placementType = PlacementTypeEnum::box;
    double curveTolerance = 0.72;
    double clipperScale = 10000000;
    int mutationRate = 10;
    int populationSize = 120;
    int rotations = 4;
    double spacing = 10;
    double sheetSpacing = 0;
    bool simplify = false;
    int seed = -1;

    // Faithful mode (C-API mode 0): mirror the canonical C# GeneticAlgorithm.cs + Background.placeParts
    // fitness EXACTLY (same RNG call order, single elitism, canonical fitness) for parity verification.
    // When true, the divergent C++ heuristics (clustering/SA/rebuild/adaptive mutation) are bypassed.
    bool faithful = false;

    // Exact NFP: nest full-resolution parts (no Douglas-Peucker reduction, no conservative dilation).
    // NFP guarantees non-collision directly -> tightest packing (parts touch, no gap), but slower.
    bool exactNfp = false;

    // Exact void/hole forbidden zones: compute the inner-fit hole exclusion as the true Minkowski
    // sum H (+) (-B) instead of the convex hull of pairwise vertex sums. The hull over-excludes
    // usable material around NON-CONVEX voids (e.g. stone fractures); the exact sum lets parts pack
    // tight against concave void boundaries. Identical for convex voids; slower. Default off.
    bool exactVoids = false;

    // Placement improvements
    int edgeSamples = 2;         // 0=off, N=samples per edge of feasible region
    // Q5 MEASURED (10 seeds, fixed 10s budget): 4 passes >= 2 everywhere (concave +0.6pp,
    // rects +0.2pp); 2 was the worst of {0,2,4}.
    int compactionPasses = 4;    // 0=off, N=compaction iterations after placement
    double gravityWeight = 0.1;  // 0=off, weight for origin-distance scoring (Q4b)
    // Q1 MEASURED (fixed 10s budget): best-rotation beats first-valid-rotation
    // (concave +1.78pp, rects +0.35pp) — affordable after the Phase-1 speedups.
    bool tryAllRotations = true;    // try all valid rotations per part, pick best
    // P3 MEASURED: continuous slide-to-contact refinement inside compaction (ray-cast
    // toward origin within the feasible region). concave +0.2pp pooled, zero
    // regressions in 20 cells, wall unchanged; vertex-replace alone can only reach
    // feasible-region vertices, the per-ray optimum is usually mid-edge.
    bool slideCompaction = true;

    // Evaluate a whole GA generation's candidates concurrently on a shared (thread-safe) NFP cache,
    // instead of one candidate per NestIterate. Candidate 0 warms the cache (its NFP precompute is
    // internally parallel), then the rest run in parallel reading the warm cache -> big first-run
    // speedup for exact mode. Result is identical to serial (cache is a pure (source,rotation) map;
    // GA trajectory depends only on fitness + RNG, not eval order).
    bool parallelPopulation = false;

    // Smart (part-aware) orientation candidates. Off = the legacy grid behaviour, byte-identical.
    // On: every source part carries its OWN sensible angles (min-area bounding rectangle and
    // longest-edge alignment, each with quarter turns — NFP::ownAngles, computed once at context
    // init on the offset outline). Placement then tries [gene, own angles, (tilts), grid angles
    // nearest the gene] instead of a fixed 45-degree sweep from the gene, and the GA draws genes
    // from the own set half of the time. Fixes long plates that only fit near 0/180 being left
    // unplaced whenever the grid sweep misses that narrow window. Parts with a rotationCount
    // override (grain) are unaffected: they stay on their own N-step grid.
    bool smartRotations = false;
    // Upper bound on candidate orientations per placement in smart mode. The working count is
    // max(8, |own|+4); this cap can only trim GRID fill-ins — the gene and every own/tilt angle
    // are always evaluated.
    int rotationCandidateCap = 12;
    // Smart mode only: also try theta +/- tilt (degrees) for every own angle theta (0 = off).
    double rotationTiltDeg = 0;
};

} // namespace nest
