#pragma once

#include <vector>
#include <memory>
#include <optional>
#include <cmath>
#include <algorithm>
#include <unordered_map>
#include <thread>
#include <mutex>

#include "NFP.h"
#include "HelperTypes.h"
#include "NestConfig.h"
#include "NestingEngine.h"
#include "NfpWorker.h"
#include "GeometryUtil.h"

namespace nest {

class NestingContext {
public:
    std::vector<std::shared_ptr<NFP>> Polygons;
    std::vector<std::shared_ptr<NFP>> Sheets;

    int SheetsNotUsed = -1;
    double MaterialUtilization = 0;
    int PlacedPartsCount = 0;
    int Iterations = 0;

    SheetPlacement Current() const { return current; }
    bool HasCurrent() const { return hasCurrent; }

    void StartNest();
    void init();
    void NestIterate(int max_iterations);
    // Advance a WHOLE GA generation, evaluating its candidates concurrently (shared NFP cache).
    // Call once per generation (vs NestIterate which evaluates one candidate per call).
    void NestIterateGeneration();
    void AssignPlacement(const SheetPlacement& plcpr);

    void ReorderSheets();

    // Multi-seed parallel runner: runs N independent contexts, returns best
    static NestingContext RunParallelSeeds(
        const std::vector<std::shared_ptr<NFP>>& polygons,
        const std::vector<std::shared_ptr<NFP>>& sheets,
        const NestConfig& baseConfig,
        int numSeeds,
        int iterationsPerSeed,
        std::function<void(int seed, int iter, const NestingContext&)> progressCallback = nullptr);

    NestConfig config;  // per-context config (different seed for multi-seed)
    NestingEngine Nest;
    std::vector<NestItem> partsLocal;

private:
    SheetPlacement current;
    bool hasCurrent = false;
    bool offsetTreePhase = true;
};

} // namespace nest
