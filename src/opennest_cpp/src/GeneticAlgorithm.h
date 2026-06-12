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
};

} // namespace nest
