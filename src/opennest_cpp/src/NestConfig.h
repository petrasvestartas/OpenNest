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
    double scale = 25;
    double clipperScale = 10000000;
    bool exploreConcave = false;
    int mutationRate = 10;
    int populationSize = 120;
    int rotations = 4;
    double spacing = 10;
    double sheetSpacing = 0;
    bool mergeLines = false;
    bool simplify = false;

    // Port features (don't exist in the original DeepNest project)
    bool clipByHull = false;
    bool clipByRects = true;
    int seed = -1;
    float rotation_limit = 360.0f;

    // Faithful mode (C-API mode 0): mirror the canonical C# GeneticAlgorithm.cs + Background.placeParts
    // fitness EXACTLY (same RNG call order, single elitism, canonical fitness) for parity verification.
    // When true, the divergent C++ heuristics (clustering/SA/rebuild/adaptive mutation) are bypassed.
    bool faithful = false;

    // Exact NFP: nest full-resolution parts (no Douglas-Peucker reduction, no conservative dilation).
    // NFP guarantees non-collision directly -> tightest packing (parts touch, no gap), but slower.
    bool exactNfp = false;

    // Placement improvements
    int edgeSamples = 2;         // 0=off, N=samples per edge of feasible region
    int compactionPasses = 2;    // 0=off, N=compaction iterations after placement
    double gravityWeight = 0.1;  // 0=off, weight for centroid-distance scoring
    bool tryAllRotations = false;   // try all valid rotations per part, pick best

    // Evaluate a whole GA generation's candidates concurrently on a shared (thread-safe) NFP cache,
    // instead of one candidate per NestIterate. Candidate 0 warms the cache (its NFP precompute is
    // internally parallel), then the rest run in parallel reading the warm cache -> big first-run
    // speedup for exact mode. Result is identical to serial (cache is a pure (source,rotation) map;
    // GA trajectory depends only on fitness + RNG, not eval order).
    bool parallelPopulation = false;
};

} // namespace nest
