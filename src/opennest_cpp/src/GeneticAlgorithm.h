#pragma once

#include <vector>
#include <algorithm>
#include <cmath>
#include <memory>

#include "NFP.h"
#include "HelperTypes.h"
#include "NestConfig.h"
#include "Random.h"

namespace nest {

// Canonical SVGnest/DeepNest genetic algorithm over (insertion order, rotations):
// single elite + crossover + per-gene mutation, RNG call order matching the original
// C# engine (the "faithful" scheme — it IS the only scheme; the experimental
// adaptive/SA variants were measured, refuted and removed).
class GeneticAlgorithm {
public:
    std::vector<PopulationItem> population;

    GeneticAlgorithm(const std::vector<std::shared_ptr<NFP>>& adam, const NestConfig& config);

    PopulationItem mutate(const PopulationItem& p);
    std::vector<PopulationItem> mate(const PopulationItem& male, const PopulationItem& female);
    void generation();

private:
    NestConfig Config;
    Random r;

    // One rotation gene for `part`. Legacy: floor(rnd*N)*(360/N) on the part's grid (N = its
    // rotationCount override, else Config.rotations). Smart rotations (unconstrained parts with an
    // own-angle set): with probability 0.5 one of the part's own angles, else the grid draw.
    float drawAngle(const NFP& part);
};

} // namespace nest
