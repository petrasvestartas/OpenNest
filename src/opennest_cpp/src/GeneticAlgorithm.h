#pragma once

#include <vector>
#include <algorithm>
#include <cmath>
#include <memory>
#include <iostream>

#include "NFP.h"
#include "HelperTypes.h"
#include "NestConfig.h"
#include "Random.h"

namespace nest {

class GeneticAlgorithm {
public:
    static bool StrictAngles;
    std::vector<PopulationItem> population;

    GeneticAlgorithm(const std::vector<std::shared_ptr<NFP>>& adam, const NestConfig& config);

    PopulationItem mutate(const PopulationItem& p);
    std::vector<PopulationItem> mate(const PopulationItem& male, const PopulationItem& female);
    void generation();
    PopulationItem randomWeightedIndividual(PopulationItem* exclude = nullptr);

    // Faithful (parity) variants — mirror canonical C# GeneticAlgorithm.cs exactly.
    PopulationItem mutateFaithful(const PopulationItem& p);
    void generationFaithful();

private:
    NestConfig Config;
    std::vector<float> defaultAngles;
    Random r;

    // Adaptive parameters
    std::optional<double> currentBestFitness;
    int generationsWithoutImprovement = 0;
    float adaptiveMutationRate = 1.0f;
    static constexpr int EliteCount = 5;
    static constexpr float DiversityThreshold = 0.1f;

    // Simulated annealing
    double temperature = 1.0;
    double coolingRate = 0.95;
    static constexpr double MinTemperature = 0.01;
    int saIterations = 0;
    static constexpr int MaxSaIterations = 20;

    void CreateRandomizedSolution(const std::vector<std::shared_ptr<NFP>>& adam);
    void CreateShuffledSolution(const std::vector<std::shared_ptr<NFP>>& adam);
    PopulationItem ApplyIntelligentClustering(PopulationItem solution);
    PopulationItem ApplyLocalRefinement(PopulationItem solution);
    void TryReorderPlacements(PopulationItem& solution);
    float NormalizeAngle(float angle);
    PopulationItem tournamentSelect(int tournamentSize = 3);
    float calculateDiversity();
    void ApplySimulatedAnnealing();
    void ApplySimulatedAnnealingToSolution(PopulationItem& solution);
    PopulationItem CreateSANeighbor(const PopulationItem& current);
    void RebuildPopulation();
};

} // namespace nest
