#include "GeneticAlgorithm.h"
#include <limits>

namespace nest {

GeneticAlgorithm::GeneticAlgorithm(const std::vector<std::shared_ptr<NFP>>& adam, const NestConfig& config)
    : Config(config)
{
    if (config.seed > -1)
        r = Random(config.seed);
    else
        r = Random();

    // Add the original solution. ALWAYS canonical DISCRETE angles: floor(rnd*rotations)*(360/rotations).
    // (The old continuous-angle path — random*360 + rotation_limit jitter — thrashed the NFP cache and
    // produced arbitrary orientations; discrete keeps the cache effective and the result predictable.)
    std::vector<float> angles(adam.size());
    for (size_t i = 0; i < adam.size(); i++) {
        // Per-part rotation override (rotationCount > 0): draw from the part's own set.
        int effRot = adam[i]->rotationCount > 0 ? adam[i]->rotationCount : Config.rotations;
        angles[i] = static_cast<float>(std::floor(r.NextDouble() * effRot)) * (360.0f / effRot);
    }

    PopulationItem first;
    first.placements = adam;
    first.Rotation = angles;
    population.push_back(first);

    // Canonical: fill the rest of the population by mutating the seed.
    while (static_cast<int>(population.size()) < config.populationSize)
        population.push_back(this->mutate(population[0]));
}

// Canonical C# mutate: per gene, prob 0.01*mutationRate to swap with the NEXT part, and independently
// the same prob to re-roll its angle. Matches the original engine's RNG call order exactly.
PopulationItem GeneticAlgorithm::mutate(const PopulationItem& p) {
    PopulationItem clone;
    clone.placements = p.placements;
    clone.Rotation = p.Rotation;

    double rate = 0.01 * Config.mutationRate;
    for (size_t i = 0; i < clone.placements.size(); i++) {
        if (r.NextDouble() < rate) {
            size_t j = i + 1;
            if (j < clone.placements.size())
                std::swap(clone.placements[i], clone.placements[j]);
        }
        if (r.NextDouble() < rate) {
            // Per-part rotation override: draw from THIS part's own orientation set
            // (rotationCount > 0), else the global setting. Same RNG call order either
            // way, so determinism holds when no overrides are present.
            int effRot = clone.placements[i]->rotationCount > 0 ? clone.placements[i]->rotationCount
                                                                : Config.rotations;
            clone.Rotation[i] = static_cast<float>(std::floor(r.NextDouble() * effRot)) * (360.0f / effRot);
        }
    }
    return clone;
}

std::vector<PopulationItem> GeneticAlgorithm::mate(const PopulationItem& male, const PopulationItem& female) {
    int cutpoint = static_cast<int>(std::round(
        std::min(std::max(r.NextDouble(), 0.1), 0.9) * (static_cast<int>(male.placements.size()) - 1)));

    std::vector<std::shared_ptr<NFP>> gene1(male.placements.begin(), male.placements.begin() + cutpoint);
    std::vector<float> rot1(male.Rotation.begin(), male.Rotation.begin() + cutpoint);

    std::vector<std::shared_ptr<NFP>> gene2(female.placements.begin(), female.placements.begin() + cutpoint);
    std::vector<float> rot2(female.Rotation.begin(), female.Rotation.begin() + cutpoint);

    // Fill gene1 with missing from female
    for (size_t i = 0; i < female.placements.size(); i++) {
        bool found = false;
        for (auto& g : gene1) {
            if (g->Id == female.placements[i]->Id) { found = true; break; }
        }
        if (!found) {
            gene1.push_back(female.placements[i]);
            rot1.push_back(female.Rotation[i]);
        }
    }

    // Fill gene2 with missing from male
    for (size_t i = 0; i < male.placements.size(); i++) {
        bool found = false;
        for (auto& g : gene2) {
            if (g->Id == male.placements[i]->Id) { found = true; break; }
        }
        if (!found) {
            gene2.push_back(male.placements[i]);
            rot2.push_back(male.Rotation[i]);
        }
    }

    PopulationItem child1;
    child1.placements = gene1;
    child1.Rotation = rot1;

    PopulationItem child2;
    child2.placements = gene2;
    child2.Rotation = rot2;

    return {child1, child2};
}

// Canonical C# generation(): sort, keep single elite, fill with mutated offspring of weighted parents.
// Selection is index-based so excluding the male shrinks the weight denominator exactly like C#
// (PopulationItem is a reference type there); RNG call order is one NextDouble per selection.
void GeneticAlgorithm::generation() {
    std::sort(population.begin(), population.end(), [](const PopulationItem& a, const PopulationItem& b) {
        double fa = a.fitness.has_value() ? a.fitness.value() : std::numeric_limits<double>::max();
        double fb = b.fitness.has_value() ? b.fitness.value() : std::numeric_limits<double>::max();
        return fa < fb;
    });

    auto pickIndex = [&](int excludeIdx) -> int {
        std::vector<int> idx;
        idx.reserve(population.size());
        for (int i = 0; i < static_cast<int>(population.size()); i++)
            if (i != excludeIdx) idx.push_back(i);
        if (idx.empty()) return 0;
        double rand = r.NextDouble();
        float lower = 0;
        float weight = 1.0f / static_cast<float>(idx.size());
        float upper = weight;
        for (int i = 0; i < static_cast<int>(idx.size()); i++) {
            if (rand >= lower && rand < upper) return idx[i];
            lower = upper;
            upper += 2.0f * weight * ((static_cast<float>(idx.size()) - i) / static_cast<float>(idx.size()));
        }
        return idx[0];
    };

    std::vector<PopulationItem> newpop;
    newpop.push_back(population[0]); // single elite (retains its fitness)
    while (static_cast<int>(newpop.size()) < Config.populationSize) {
        int maleIdx = pickIndex(-1);
        int femaleIdx = pickIndex(maleIdx);
        auto children = mate(population[maleIdx], population[femaleIdx]);
        newpop.push_back(this->mutate(children[0]));
        if (static_cast<int>(newpop.size()) < Config.populationSize)
            newpop.push_back(this->mutate(children[1]));
    }
    population = newpop;
}

} // namespace nest
