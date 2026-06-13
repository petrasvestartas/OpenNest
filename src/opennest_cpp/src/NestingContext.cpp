#include "NestingContext.h"
#include <iostream>

namespace nest {

void NestingContext::StartNest() {
    hasCurrent = false;
    // Reset NestingEngine state without copy-assignment (NfpWorker has a mutex)
    Nest.config = config;
    NestingEngine::Config = config;
    Nest.ga.reset();
    Nest.nests.clear();
    Nest.background.cacheProcess.clear();
    Nest.background.window = NfpCache();
    Nest.background.callCounter = 0;
    Iterations = 0;
}

void NestingContext::init() {
    partsLocal.clear();
    std::vector<std::shared_ptr<NFP>> lsheets;
    std::vector<std::shared_ptr<NFP>> lpoly;

    // Assign sequential IDs
    for (size_t i = 0; i < Polygons.size(); i++)
        Polygons[i]->Id = static_cast<int>(i);
    for (size_t i = 0; i < Sheets.size(); i++)
        Sheets[i]->Id = static_cast<int>(i);

    // Clone polygons
    for (auto& item : Polygons) {
        auto clone = std::make_shared<NFP>();
        clone->Id = item->Id;
        clone->source = item->source;
        clone->Points = item->Points;
        clone->exactFlags = item->exactFlags;
        clone->rotationCount = item->rotationCount;
        if (!item->children.empty()) {
            for (auto& citem : item->children) {
                auto childClone = std::make_shared<NFP>();
                childClone->Id = citem->Id;
                childClone->source = citem->source;
                childClone->Points = citem->Points;
                childClone->exactFlags = citem->exactFlags;
                clone->children.push_back(childClone);
            }
        }
        lpoly.push_back(clone);
    }

    // Clone sheets
    for (auto& item : Sheets) {
        auto clone = std::make_shared<NFP>();
        clone->Id = item->Id;
        clone->source = item->source;
        clone->Points = item->Points;
        clone->exactFlags = item->exactFlags;
        clone->rotationCount = item->rotationCount;
        if (!item->children.empty()) {
            for (auto& citem : item->children) {
                auto childClone = std::make_shared<NFP>();
                childClone->Id = citem->Id;
                childClone->source = citem->source;
                childClone->Points = citem->Points;
                childClone->exactFlags = citem->exactFlags;
                clone->children.push_back(childClone);
            }
        }
        lsheets.push_back(clone);
    }

    // Offset tree phase
    if (offsetTreePhase) {
        // Group polys by source
        std::unordered_map<int, std::vector<std::shared_ptr<NFP>>> polyGroups;
        for (auto& p : lpoly) {
            int src = p->source.has_value() ? p->source.value() : -1;
            polyGroups[src].push_back(p);
        }

        for (auto& [src, group] : polyGroups) {
            NestingEngine::offsetTree(*group[0], 0.5 * config.spacing, config);
            // Copy offset points to all items in the group
            for (size_t i = 1; i < group.size(); i++) {
                group[i]->Points = group[0]->Points;
            }
        }

        for (auto& item : lsheets) {
            double gap = config.sheetSpacing - config.spacing / 2;
            NestingEngine::offsetTree(*item, -gap, config, true);
        }
    }

    // Group by source and create NestItems
    std::unordered_map<int, std::vector<std::shared_ptr<NFP>>> polyBySource;
    for (auto& p : lpoly) {
        int src = p->source.has_value() ? p->source.value() : -1;
        polyBySource[src].push_back(p);
    }
    // We need to maintain the order of sources as they appear in lpoly
    std::vector<int> polySourceOrder;
    for (auto& p : lpoly) {
        int src = p->source.has_value() ? p->source.value() : -1;
        bool found = false;
        for (auto s : polySourceOrder) if (s == src) { found = true; break; }
        if (!found) polySourceOrder.push_back(src);
    }

    for (int src : polySourceOrder) {
        auto& group = polyBySource[src];
        NestItem ni;
        ni.Polygon = group[0];
        ni.IsSheet = false;
        ni.Quantity = static_cast<int>(group.size());
        partsLocal.push_back(ni);
    }

    std::unordered_map<int, std::vector<std::shared_ptr<NFP>>> sheetBySource;
    for (auto& s : lsheets) {
        int src = s->source.has_value() ? s->source.value() : -1;
        sheetBySource[src].push_back(s);
    }
    std::vector<int> sheetSourceOrder;
    for (auto& s : lsheets) {
        int src = s->source.has_value() ? s->source.value() : -1;
        bool found = false;
        for (auto ss : sheetSourceOrder) if (ss == src) { found = true; break; }
        if (!found) sheetSourceOrder.push_back(src);
    }

    for (int src : sheetSourceOrder) {
        auto& group = sheetBySource[src];
        NestItem ni;
        ni.Polygon = group[0];
        ni.IsSheet = true;
        ni.Quantity = static_cast<int>(group.size());
        partsLocal.push_back(ni);
    }

    // Assign sequential source IDs
    int srcc = 0;
    for (auto& item : partsLocal) {
        item.Polygon->source = srcc++;
    }
}

void NestingContext::NestIterate(int max_iterations) {
    // Build parts + offset tree ONCE (matches C# NestingContext). Re-running init() every iteration
    // re-ran the expensive offsetTree on every candidate and dominated the runtime; the GA clones from
    // this template each step so it is never mutated.
    if (Iterations == 0) init();

    if (max_iterations == 0) return;

    Nest.launchWorkers(partsLocal);
    if (Nest.nests.empty()) return;

    auto& plcpr = Nest.nests.front();

    if (!hasCurrent || (plcpr.fitness.has_value() && current.fitness.has_value() && plcpr.fitness.value() < current.fitness.value())) {
        AssignPlacement(plcpr);
    }

    Iterations++;
}

void NestingContext::NestIterateGeneration() {
    if (Iterations == 0) init();

    Nest.launchWorkersParallel(partsLocal);
    if (Nest.nests.empty()) return;

    auto& plcpr = Nest.nests.front();
    if (!hasCurrent || (plcpr.fitness.has_value() && current.fitness.has_value() && plcpr.fitness.value() < current.fitness.value())) {
        AssignPlacement(plcpr);
    }

    Iterations++;
}

void NestingContext::AssignPlacement(const SheetPlacement& plcpr) {
    current = plcpr;
    hasCurrent = true;
    double totalSheetsArea = 0;
    double totalPartsArea = 0;
    PlacedPartsCount = 0;

    std::vector<std::shared_ptr<NFP>> placed;
    for (auto& item : Polygons) {
        item->sheet = nullptr;
    }

    std::vector<int> sheetsIds;

    for (auto& item : plcpr.placements) {
        for (auto& zitem : item) {
            int sheetid = zitem.sheetId;
            bool found = false;
            for (auto id : sheetsIds) if (id == sheetid) { found = true; break; }
            if (!found) sheetsIds.push_back(sheetid);

            // Find sheet
            std::shared_ptr<NFP> sheet;
            for (auto& s : Sheets) {
                if (s->Id == sheetid) { sheet = s; break; }
            }
            if (sheet) {
                totalSheetsArea += GeometryUtil::polygonArea(*sheet);
            }

            for (auto& ssitem : zitem.sheetplacements) {
                PlacedPartsCount++;
                // Find polygon by id
                std::shared_ptr<NFP> poly;
                for (auto& p : Polygons) {
                    if (p->Id == ssitem.id) { poly = p; break; }
                }
                if (poly) {
                    totalPartsArea += GeometryUtil::polygonArea(*poly);
                    placed.push_back(poly);
                    poly->sheet = sheet.get();
                    poly->x = ssitem.x + (sheet ? sheet->x : 0);
                    poly->y = ssitem.y + (sheet ? sheet->y : 0);
                    poly->Rotation = ssitem.rotation;
                }
            }
        }
    }

    // Count unused sheets
    int emptySheets = 0;
    for (auto& s : Sheets) {
        bool used = false;
        for (auto id : sheetsIds) if (id == s->Id) { used = true; break; }
        if (!used) emptySheets++;
    }
    SheetsNotUsed = emptySheets;

    MaterialUtilization = std::fabs(totalPartsArea / totalSheetsArea);

    // Mark unplaced polygons
    for (auto& item : Polygons) {
        bool isPlaced = false;
        for (auto& p : placed) if (p.get() == item.get()) { isPlaced = true; break; }
        if (!isPlaced) {
            item->x = -1000;
            item->y = 0;
        }
    }
}

void NestingContext::ReorderSheets() {
    double x = 0;
    double y = 0;
    int gap = 10;
    for (size_t i = 0; i < Sheets.size(); i++) {
        Sheets[i]->x = x;
        Sheets[i]->y = y;
        double maxx = -std::numeric_limits<double>::max();
        double minx = std::numeric_limits<double>::max();
        for (auto& pt : Sheets[i]->Points) {
            maxx = std::max(maxx, pt.x);
            minx = std::min(minx, pt.x);
        }
        double w = maxx - minx;
        x += w + gap;
    }
}

NestingContext NestingContext::RunParallelSeeds(
    const std::vector<std::shared_ptr<NFP>>& polygons,
    const std::vector<std::shared_ptr<NFP>>& sheets,
    const NestConfig& baseConfig,
    int numSeeds,
    int iterationsPerSeed,
    std::function<void(int seed, int iter, const NestingContext&)> progressCallback)
{
    // Set static Config once for offset tree helpers (all seeds share same base params)
    NestingEngine::Config = baseConfig;

    std::vector<std::unique_ptr<NestingContext>> contexts(numSeeds);
    std::mutex progressMutex;

    // Initialize all contexts (serial — offset tree helpers use static Config)
    for (int s = 0; s < numSeeds; s++) {
        contexts[s] = std::make_unique<NestingContext>();

        // Deep copy polygons
        for (auto& p : polygons) {
            auto clone = std::make_shared<NFP>();
            clone->Id = p->Id;
            clone->source = p->source;
            clone->Points = p->Points;
            clone->exactFlags = p->exactFlags;
            clone->rotationCount = p->rotationCount;
            for (auto& child : p->children) {
                auto cc = std::make_shared<NFP>();
                cc->Id = child->Id;
                cc->source = child->source;
                cc->Points = child->Points;
                cc->exactFlags = child->exactFlags;
                clone->children.push_back(cc);
            }
            contexts[s]->Polygons.push_back(clone);
        }

        // Deep copy sheets
        for (auto& sh : sheets) {
            auto clone = std::make_shared<NFP>();
            clone->Id = sh->Id;
            clone->source = sh->source;
            clone->Points = sh->Points;
            clone->exactFlags = sh->exactFlags;
            for (auto& child : sh->children) {
                auto cc = std::make_shared<NFP>();
                cc->Id = child->Id;
                cc->source = child->source;
                cc->Points = child->Points;
                cc->exactFlags = child->exactFlags;
                clone->children.push_back(cc);
            }
            contexts[s]->Sheets.push_back(clone);
        }
        contexts[s]->ReorderSheets();

        // Per-seed config with unique seed
        contexts[s]->config = baseConfig;
        contexts[s]->config.seed = s * 1000 + 42;

        contexts[s]->StartNest();
    }

    // Run all seeds in parallel
    std::vector<std::thread> threads;
    for (int s = 0; s < numSeeds; s++) {
        threads.emplace_back([&contexts, s, iterationsPerSeed, &progressCallback, &progressMutex]() {
            for (int i = 0; i < iterationsPerSeed; i++) {
                contexts[s]->NestIterate(iterationsPerSeed);
                if (progressCallback) {
                    std::lock_guard<std::mutex> lock(progressMutex);
                    progressCallback(s, contexts[s]->Iterations, *contexts[s]);
                }
            }
        });
    }

    for (auto& t : threads) t.join();

    // Find best context
    int bestIdx = 0;
    for (int s = 1; s < numSeeds; s++) {
        if (!contexts[s]->HasCurrent()) continue;
        if (!contexts[bestIdx]->HasCurrent() ||
            contexts[s]->Current().fitness.value_or(1e18) <
            contexts[bestIdx]->Current().fitness.value_or(1e18)) {
            bestIdx = s;
        }
    }

    // Return copy of best context
    NestingContext result;
    result.Polygons = std::move(contexts[bestIdx]->Polygons);
    result.Sheets = std::move(contexts[bestIdx]->Sheets);
    result.config = contexts[bestIdx]->config;
    result.SheetsNotUsed = contexts[bestIdx]->SheetsNotUsed;
    result.MaterialUtilization = contexts[bestIdx]->MaterialUtilization;
    result.PlacedPartsCount = contexts[bestIdx]->PlacedPartsCount;
    result.Iterations = contexts[bestIdx]->Iterations;
    result.current = contexts[bestIdx]->Current();
    result.hasCurrent = contexts[bestIdx]->HasCurrent();

    return result;
}

} // namespace nest
