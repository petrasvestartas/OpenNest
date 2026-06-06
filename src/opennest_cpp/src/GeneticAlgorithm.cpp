#include "GeneticAlgorithm.h"
#include <limits>
#include <numeric>

namespace nest {

bool GeneticAlgorithm::StrictAngles = false;

GeneticAlgorithm::GeneticAlgorithm(const std::vector<std::shared_ptr<NFP>>& adam, const NestConfig& config)
    : Config(config), currentBestFitness(std::numeric_limits<double>::max())
{
    if (config.seed > -1)
        r = Random(config.seed);
    else
        r = Random();

    // Build default angles
    defaultAngles.resize(adam.size());
    for (size_t i = 0; i < adam.size(); i++) {
        defaultAngles[i] = static_cast<float>(static_cast<int>(i * 90) % 360);
    }

    // Add the original solution. ALWAYS canonical DISCRETE angles: floor(rnd*rotations)*(360/rotations).
    // (The old continuous-angle path — random*360 + rotation_limit jitter — thrashed the NFP cache and
    // produced arbitrary orientations; discrete keeps the cache effective and the result predictable.)
    std::vector<float> angles(adam.size());
    for (size_t i = 0; i < adam.size(); i++) {
        if (StrictAngles) {
            angles[i] = defaultAngles[i];
        } else {
            angles[i] = static_cast<float>(std::floor(r.NextDouble() * Config.rotations)) * (360.0f / Config.rotations);
        }
    }

    PopulationItem first;
    first.placements = adam;
    first.Rotation = angles;
    population.push_back(first);

    // Canonical: fill the rest of the population by mutating the seed.
    while (static_cast<int>(population.size()) < config.populationSize)
        population.push_back(this->mutate(population[0]));
}

void GeneticAlgorithm::CreateRandomizedSolution(const std::vector<std::shared_ptr<NFP>>& adam) {
    auto shuffledPlacements = adam;

    // Fisher-Yates shuffle
    for (int i = static_cast<int>(shuffledPlacements.size()) - 1; i > 0; i--) {
        int j = r.Next(i + 1);
        std::swap(shuffledPlacements[i], shuffledPlacements[j]);
    }

    std::vector<float> randomRotations(adam.size());
    for (size_t i = 0; i < randomRotations.size(); i++) {
        randomRotations[i] = static_cast<float>(r.NextDouble() * 360.0);
    }

    PopulationItem item;
    item.placements = shuffledPlacements;
    item.Rotation = randomRotations;
    population.push_back(item);
}

void GeneticAlgorithm::CreateShuffledSolution(const std::vector<std::shared_ptr<NFP>>& adam) {
    auto newPlacements = adam;

    int shuffleCount = static_cast<int>(newPlacements.size()) / 2;
    int startIdx = r.Next(0, static_cast<int>(newPlacements.size()) - shuffleCount);

    for (int i = startIdx + shuffleCount - 1; i > startIdx; i--) {
        int j = startIdx + r.Next(i - startIdx + 1);
        std::swap(newPlacements[i], newPlacements[j]);
    }

    std::vector<float> partialRandomRotations(adam.size());
    for (size_t i = 0; i < partialRandomRotations.size(); i++) {
        if (r.NextDouble() < 0.5) {
            partialRandomRotations[i] = static_cast<float>(std::floor(r.NextDouble() * Config.rotations)) * (360.0f / Config.rotations);
        } else {
            partialRandomRotations[i] = (i < defaultAngles.size()) ? defaultAngles[i] : 0;
        }
    }

    PopulationItem item;
    item.placements = newPlacements;
    item.Rotation = partialRandomRotations;
    population.push_back(item);
}

PopulationItem GeneticAlgorithm::mutate(const PopulationItem& p) {
    // Always canonical (discrete) — the heuristic path below is dead/unused (kept for reference).
    return mutateFaithful(p);

    PopulationItem clone;
    clone.placements = p.placements;
    clone.Rotation = p.Rotation;

    float effectiveMutationRate = (Config.mutationRate / 100.0f) * adaptiveMutationRate;

    if (generationsWithoutImprovement > 5) {
        for (size_t i = 0; i < clone.placements.size(); i++) {
            auto rand = r.NextDouble();
            if (rand < effectiveMutationRate * 1.5f) {
                auto j = r.Next(static_cast<int>(clone.placements.size()));
                std::swap(clone.placements[i], clone.placements[j]);

                if (r.NextDouble() < 0.5f) {
                    auto angle = static_cast<float>(std::floor(r.NextDouble() * Config.rotations)) * (360.0f / Config.rotations);
                    clone.Rotation[i] = angle;
                }
            }
        }
    } else {
        for (size_t i = 0; i < clone.placements.size(); i++) {
            auto rand = r.NextDouble();
            if (rand < effectiveMutationRate) {
                auto j = r.Next(static_cast<int>(clone.placements.size()));
                std::swap(clone.placements[i], clone.placements[j]);
            }

            rand = r.NextDouble();
            if (rand < effectiveMutationRate) {
                auto angle = static_cast<float>(std::floor(r.NextDouble() * Config.rotations)) * (360.0f / Config.rotations);
                angle += (0.5f - static_cast<float>(r.NextDouble())) * Config.rotation_limit;

                if (r.NextDouble() < 0.3f && generationsWithoutImprovement < 3) {
                    angle = clone.Rotation[i] + (0.3f - static_cast<float>(r.NextDouble())) * Config.rotation_limit;
                }
                clone.Rotation[i] = angle;
            }
        }
    }

    clone = ApplyIntelligentClustering(clone);
    return ApplyLocalRefinement(clone);
}

PopulationItem GeneticAlgorithm::ApplyIntelligentClustering(PopulationItem solution) {
    if (r.NextDouble() < 0.3)
        return solution;

    PopulationItem clustered;
    clustered.placements = solution.placements;
    clustered.Rotation = solution.Rotation;

    if (static_cast<int>(clustered.placements.size()) < 4)
        return clustered;

    if (r.NextDouble() < 0.5) {
        // Sort by bounding box area (descending)
        struct Pair { std::shared_ptr<NFP> nfp; float rot; double area; };
        std::vector<Pair> pairs(clustered.placements.size());
        for (size_t i = 0; i < clustered.placements.size(); i++) {
            double area = 0;
            auto& pts = clustered.placements[i]->Points;
            if (!pts.empty()) {
                double minX = std::numeric_limits<double>::max();
                double minY = std::numeric_limits<double>::max();
                double maxX = std::numeric_limits<double>::lowest();
                double maxY = std::numeric_limits<double>::lowest();
                for (auto& pt : pts) {
                    minX = std::min(minX, pt.x);
                    minY = std::min(minY, pt.y);
                    maxX = std::max(maxX, pt.x);
                    maxY = std::max(maxY, pt.y);
                }
                area = (maxX - minX) * (maxY - minY);
            }
            pairs[i] = {clustered.placements[i], clustered.Rotation[i], area};
        }
        std::sort(pairs.begin(), pairs.end(), [](const Pair& a, const Pair& b) {
            return a.area > b.area;
        });
        for (size_t i = 0; i < pairs.size(); i++) {
            clustered.placements[i] = pairs[i].nfp;
            clustered.Rotation[i] = pairs[i].rot;
        }
    } else {
        // Group by vertex count
        struct Item { std::shared_ptr<NFP> nfp; float rot; int vertCount; };
        std::vector<Item> items(clustered.placements.size());
        for (size_t i = 0; i < clustered.placements.size(); i++) {
            items[i] = {clustered.placements[i], clustered.Rotation[i], clustered.placements[i]->length()};
        }
        std::sort(items.begin(), items.end(), [](const Item& a, const Item& b) {
            return a.vertCount > b.vertCount;
        });
        for (size_t i = 0; i < items.size(); i++) {
            clustered.placements[i] = items[i].nfp;
            clustered.Rotation[i] = items[i].rot;
        }
    }

    return clustered;
}

PopulationItem GeneticAlgorithm::ApplyLocalRefinement(PopulationItem solution) {
    if (r.NextDouble() < 0.2)
        return solution;

    PopulationItem refined;
    refined.placements = solution.placements;
    refined.Rotation = solution.Rotation;

    int refinementCount = std::min(3, static_cast<int>(refined.placements.size()) / 2);
    std::vector<int> indicesToRefine;

    while (static_cast<int>(indicesToRefine.size()) < refinementCount) {
        int idx = r.Next(static_cast<int>(refined.placements.size()));
        bool found = false;
        for (auto v : indicesToRefine) if (v == idx) { found = true; break; }
        if (!found) indicesToRefine.push_back(idx);
    }

    for (int idx : indicesToRefine) {
        float maxAdjustment = Config.rotation_limit * 0.5f;
        std::vector<float> angleAdjustments = {
            -maxAdjustment, maxAdjustment,
            -maxAdjustment * 0.5f, maxAdjustment * 0.5f,
            -maxAdjustment * 0.2f, maxAdjustment * 0.2f
        };

        float bestAngle = refined.Rotation[idx];
        for (float adjustment : angleAdjustments) {
            float originalAngle = refined.Rotation[idx];
            refined.Rotation[idx] = NormalizeAngle(originalAngle + adjustment);

            if (r.NextDouble() < 0.3) {
                bestAngle = refined.Rotation[idx];
                break;
            } else {
                refined.Rotation[idx] = originalAngle;
            }
        }
        refined.Rotation[idx] = bestAngle;
    }

    TryReorderPlacements(refined);
    return refined;
}

void GeneticAlgorithm::TryReorderPlacements(PopulationItem& solution) {
    if (r.NextDouble() > 0.4)
        return;

    int windowSize = std::min(4, static_cast<int>(solution.placements.size()) / 3);
    if (windowSize < 2) return;

    int startIdx = r.Next(0, static_cast<int>(solution.placements.size()) - windowSize);

    for (int attempt = 0; attempt < 3; attempt++) {
        int i = startIdx + r.Next(windowSize);
        int j = startIdx + r.Next(windowSize);

        if (i != j) {
            std::swap(solution.placements[i], solution.placements[j]);
            std::swap(solution.Rotation[i], solution.Rotation[j]);

            if (r.NextDouble() >= 0.5) {
                std::swap(solution.placements[i], solution.placements[j]);
                std::swap(solution.Rotation[i], solution.Rotation[j]);
            }
        }
    }
}

float GeneticAlgorithm::NormalizeAngle(float angle) {
    while (angle < 0) angle += 360;
    while (angle >= 360) angle -= 360;
    return angle;
}

PopulationItem GeneticAlgorithm::tournamentSelect(int tournamentSize) {
    std::vector<PopulationItem*> tournament;
    for (int i = 0; i < tournamentSize; i++) {
        int idx = r.Next(static_cast<int>(population.size()));
        tournament.push_back(&population[idx]);
    }

    PopulationItem* best = tournament[0];
    for (size_t i = 1; i < tournament.size(); i++) {
        if (tournament[i]->fitness.has_value() &&
            (!best->fitness.has_value() || tournament[i]->fitness.value() < best->fitness.value())) {
            best = tournament[i];
        }
    }
    return *best;
}

float GeneticAlgorithm::calculateDiversity() {
    if (population.size() <= 1) return 0;

    float totalDiversity = 0;
    int comparisons = 0;

    int limit1 = std::min(5, static_cast<int>(population.size()));
    int limit2 = std::min(10, static_cast<int>(population.size()));

    for (int i = 0; i < limit1; i++) {
        for (int j = i + 1; j < limit2; j++) {
            int differences = 0;
            for (size_t k = 0; k < population[i].placements.size(); k++) {
                if (k < population[j].placements.size() &&
                    population[i].placements[k]->Id != population[j].placements[k]->Id) {
                    differences++;
                }
            }
            float diversity = differences / static_cast<float>(std::max(1, static_cast<int>(population[i].placements.size())));
            totalDiversity += diversity;
            comparisons++;
        }
    }

    return comparisons > 0 ? totalDiversity / comparisons : 0;
}

PopulationItem GeneticAlgorithm::randomWeightedIndividual(PopulationItem* exclude) {
    std::vector<PopulationItem*> pop;
    for (auto& p : population) {
        if (exclude && &p == exclude) continue;
        pop.push_back(&p);
    }

    auto rand = r.NextDouble();
    float lower = 0;
    auto weight = 1.0f / static_cast<float>(pop.size());
    float upper = weight;

    for (size_t i = 0; i < pop.size(); i++) {
        if (rand >= lower && rand < upper) {
            return *pop[i];
        }
        lower = upper;
        upper += 2.0f * weight * ((static_cast<float>(pop.size()) - static_cast<float>(i)) / static_cast<float>(pop.size()));
    }

    return *pop[0];
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

// Canonical C# mutate: per gene, prob 0.01*mutationRate to swap with the NEXT part, and independently
// the same prob to re-roll its angle. No clustering/refinement/adaptive rate. Matches RNG call order.
PopulationItem GeneticAlgorithm::mutateFaithful(const PopulationItem& p) {
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
            clone.Rotation[i] = static_cast<float>(std::floor(r.NextDouble() * Config.rotations)) * (360.0f / Config.rotations);
        }
    }
    return clone;
}

// Canonical C# generation(): sort, keep single elite, fill with mutated offspring of weighted parents.
// Selection is index-based so excluding the male shrinks the weight denominator exactly like C#
// (PopulationItem is a reference type there); RNG call order is one NextDouble per selection.
void GeneticAlgorithm::generationFaithful() {
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

void GeneticAlgorithm::generation() {
    // Always canonical — the heuristic path below is dead/unused (kept for reference).
    generationFaithful();
    return;

    // Sort by fitness (lowest is best)
    std::sort(population.begin(), population.end(), [](const PopulationItem& a, const PopulationItem& b) {
        if (!a.fitness.has_value()) return false;
        if (!b.fitness.has_value()) return true;
        return a.fitness.value() < b.fitness.value();
    });

    auto bestFitness = population[0].fitness;
    bool improved = bestFitness.has_value() && (!currentBestFitness.has_value() || bestFitness.value() < currentBestFitness.value());

    if (improved) {
        currentBestFitness = bestFitness;
        generationsWithoutImprovement = 0;
        temperature = 1.0;
        saIterations = 0;
    } else {
        generationsWithoutImprovement++;
        if (generationsWithoutImprovement > 3) {
            ApplySimulatedAnnealing();
        }
        if (generationsWithoutImprovement > 25) {
            RebuildPopulation();
            return;
        }
    }

    // Adjust mutation rate
    float diversity = calculateDiversity();
    if (generationsWithoutImprovement > 10) {
        adaptiveMutationRate = std::min(3.0f, adaptiveMutationRate * 1.2f);
    } else if (diversity < DiversityThreshold) {
        adaptiveMutationRate = std::min(2.0f, adaptiveMutationRate * 1.1f);
    } else if (improved) {
        adaptiveMutationRate = std::max(0.5f, adaptiveMutationRate * 0.9f);
    }

    // Create new population
    std::vector<PopulationItem> newpopulation;

    // Elite preservation
    for (int i = 0; i < std::min(EliteCount, static_cast<int>(population.size())); i++) {
        newpopulation.push_back(population[i]);
    }

    // Fill with offspring
    while (static_cast<int>(newpopulation.size()) < static_cast<int>(population.size())) {
        PopulationItem male = tournamentSelect();
        PopulationItem female = tournamentSelect();

        // Ensure different parents
        int tries = 0;
        while (&female == &male && population.size() > 1 && tries < 10) {
            female = tournamentSelect();
            tries++;
        }

        auto children = mate(male, female);

        newpopulation.push_back(this->mutate(children[0]));
        if (static_cast<int>(newpopulation.size()) < static_cast<int>(this->population.size())) {
            newpopulation.push_back(this->mutate(children[1]));
        }
    }

    this->population = newpopulation;
}

void GeneticAlgorithm::RebuildPopulation() {
    PopulationItem bestSolution = population[0];

    population.clear();
    population.push_back(bestSolution);

    auto adam = bestSolution.placements;

    while (static_cast<int>(population.size()) < Config.populationSize) {
        if (static_cast<int>(population.size()) < static_cast<int>(Config.populationSize * 0.3)) {
            CreateRandomizedSolution(adam);
        } else if (static_cast<int>(population.size()) < static_cast<int>(Config.populationSize * 0.6)) {
            CreateShuffledSolution(adam);
        } else {
            auto randomIndex = r.Next(static_cast<int>(population.size()));
            auto mutant = CreateSANeighbor(population[randomIndex]);
            population.push_back(mutant);
        }
    }

    adaptiveMutationRate = 1.5f;
    generationsWithoutImprovement = 0;
    std::cout << "Population rebuilt after stagnation." << std::endl;
}

void GeneticAlgorithm::ApplySimulatedAnnealing() {
    if (saIterations >= MaxSaIterations || temperature < MinTemperature) {
        temperature = 1.0;
        saIterations = 0;
        return;
    }

    saIterations++;
    temperature *= coolingRate;

    for (int i = 0; i < 3; i++) {
        int idx = EliteCount + r.Next(static_cast<int>(population.size()) - EliteCount);
        if (idx < static_cast<int>(population.size())) {
            ApplySimulatedAnnealingToSolution(population[idx]);
        }
    }
}

void GeneticAlgorithm::ApplySimulatedAnnealingToSolution(PopulationItem& solution) {
    PopulationItem current;
    current.placements = solution.placements;
    current.Rotation = solution.Rotation;
    current.fitness = solution.fitness;

    auto neighbor = CreateSANeighbor(current);

    bool shouldAccept = false;
    if (r.NextDouble() < 0.3) {
        shouldAccept = true;
    } else {
        double acceptanceProbability = std::exp(-0.1 / temperature);
        shouldAccept = r.NextDouble() < acceptanceProbability;
    }

    if (shouldAccept) {
        solution.placements = neighbor.placements;
        solution.Rotation = neighbor.Rotation;

        if (r.NextDouble() < 0.5) {
            auto clustered = ApplyIntelligentClustering(solution);
            solution.placements = clustered.placements;
            solution.Rotation = clustered.Rotation;
        }
    }
}

PopulationItem GeneticAlgorithm::CreateSANeighbor(const PopulationItem& current) {
    PopulationItem neighbor;
    neighbor.placements = current.placements;
    neighbor.Rotation = current.Rotation;

    int changeCount = static_cast<int>(std::ceil(neighbor.placements.size() * 0.2 * temperature));
    changeCount = std::max(1, std::min(changeCount, static_cast<int>(neighbor.placements.size()) / 2));

    for (int i = 0; i < changeCount; i++) {
        int idx = r.Next(static_cast<int>(neighbor.placements.size()));

        if (r.NextDouble() < 0.5) {
            int swapIdx = r.Next(static_cast<int>(neighbor.placements.size()));
            std::swap(neighbor.placements[idx], neighbor.placements[swapIdx]);
        } else {
            neighbor.Rotation[idx] = static_cast<float>(r.NextDouble() * 360.0);
        }
    }

    return neighbor;
}

} // namespace nest
