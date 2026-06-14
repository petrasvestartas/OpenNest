#pragma once

#include <vector>
#include <string>
#include <optional>
#include <memory>
#include <unordered_map>
#include <mutex>
#include <atomic>
#include <functional>
#include <cmath>
#include <algorithm>
#include <numeric>

#include "Point.h"
#include "NFP.h"
#include "HelperTypes.h"
#include "NestConfig.h"
#include "GeometryUtil.h"
#include "D3.h"
#include "ClipperUtil.h"
#include "clipper2/clipper.h"
#include "MinkowskiConvolution.h"

namespace nest {

class NfpWorker {
public:
    NfpWorker() = default;
    ~NfpWorker() = default;

    // Move constructor/assignment — transfer state, create fresh mutex
    NfpWorker(NfpWorker&& o) noexcept
        : cacheProcess(std::move(o.cacheProcess)),
          cacheMutex_(std::make_unique<std::mutex>()),
          window(std::move(o.window)),
          callCounter(o.callCounter.load()),
          LastPlacePartTime(o.LastPlacePartTime),
          data(std::move(o.data)),
          parts_vec(std::move(o.parts_vec)),
          index(o.index),
          ResponseAction(std::move(o.ResponseAction)) {}

    NfpWorker& operator=(NfpWorker&& o) noexcept {
        if (this != &o) {
            cacheProcess = std::move(o.cacheProcess);
            cacheMutex_ = std::make_unique<std::mutex>();
            window = std::move(o.window);
            callCounter = o.callCounter.load();
            LastPlacePartTime = o.LastPlacePartTime;
            data = std::move(o.data);
            parts_vec = std::move(o.parts_vec);
            index = o.index;
            ResponseAction = std::move(o.ResponseAction);
        }
        return *this;
    }

    // Copy constructor/assignment — copy data, fresh mutex & window
    NfpWorker(const NfpWorker& o)
        : cacheProcess(o.cacheProcess),
          cacheMutex_(std::make_unique<std::mutex>()),
          window(),  // fresh NfpCache (non-copyable due to unique_ptr<dbCache>)
          callCounter(o.callCounter.load()),
          LastPlacePartTime(o.LastPlacePartTime),
          data(o.data),
          parts_vec(o.parts_vec),
          index(o.index),
          ResponseAction(o.ResponseAction) {}

    NfpWorker& operator=(const NfpWorker& o) {
        if (this != &o) {
            cacheProcess = o.cacheProcess;
            cacheMutex_ = std::make_unique<std::mutex>();
            window = NfpCache();  // fresh
            callCounter = o.callCounter.load();
            LastPlacePartTime = o.LastPlacePartTime;
            data = o.data;
            parts_vec = o.parts_vec;
            index = o.index;
            ResponseAction = o.ResponseAction;
        }
        return *this;
    }

    // --- Configuration flags (shared, not mutable per-instance) ---
    static bool EnableCaches;
    static bool UseParallel;

    // --- Instance caches (per-NfpWorker, protected by cacheMutex_) ---
    std::unordered_map<uint64_t, std::vector<std::shared_ptr<NFP>>> cacheProcess;
    std::unique_ptr<std::mutex> cacheMutex_ = std::make_unique<std::mutex>();
    NfpCache window;
    std::atomic<int> callCounter{0};
    long long LastPlacePartTime = 0;

    // --- Simple utility methods (pure, no mutable state) ---
    static std::shared_ptr<NFP> clone(const NFP& nfp);
    static std::vector<std::shared_ptr<NFP>> cloneNfp(const std::vector<std::shared_ptr<NFP>>& nfp, bool inner = false);
    static NFP rotatePolygon(const NFP& polygon, float degrees);
    static std::shared_ptr<NFP> getHull(const NFP& polygon);
    static NFP getFrame(const NFP& A);

    // --- Coordinate conversion (pure, no mutable state) ---
    static std::vector<Clipper2Lib::Path64> nfpToClipperCoordinates(NFP& nfp, double clipperScale = 10000000);
    static std::vector<Clipper2Lib::Path64> nfpToClipperWithShift(const NFP& nfp, double clipperScale, double dx, double dy);
    static std::vector<Clipper2Lib::Path64> innerNfpToClipperCoordinates(std::vector<std::shared_ptr<NFP>>& nfp, const NestConfig& config);
    static NFP toNestCoordinates(const Clipper2Lib::Path64& polygon, double scale);

    // --- NFP computation (instance — uses cacheProcess, window) ---
    std::vector<std::shared_ptr<NFP>> Process2(NFP& A, NFP& B, int type);
    std::shared_ptr<NFP> getOuterNfp(NFP& A, NFP& B, int type, bool inside = false);
    std::vector<std::shared_ptr<NFP>> getInnerNfp(NFP& A, NFP& B, int type, const NestConfig& config);

    // --- Placement (instance — calls NFP computation methods) ---
    SheetPlacement placeParts(std::vector<std::shared_ptr<NFP>> sheets, std::vector<std::shared_ptr<NFP>> parts, const NestConfig& config, int nestindex);
    void compactPlacements(
        std::vector<std::shared_ptr<NFP>>& placed,
        std::vector<PlacementItem>& placements,
        NFP& sheet, const NestConfig& config);

    // Opt-in hole-filling: union into `feasible` the positions where `candPart` fits INSIDE the holes
    // of already-placed parts (excludeIndex skips one host, e.g. the part being re-placed in compaction).
    // `placedOfpUnion` is the union of placed-part outer-NFPs already used for the sheet difference; the
    // hole regions are clipped against it so a part can't be offered a spot another part already occupies.
    void addHoleFillRegions(
        Clipper2Lib::Paths64& feasible,
        const Clipper2Lib::Paths64& placedOfpUnion,
        const std::vector<std::shared_ptr<NFP>>& placed,
        const std::vector<PlacementItem>& placements,
        NFP& candPart, const NestConfig& config, int excludeIndex);



    // --- Worker coordination (instance) ---
    DataInfo data;
    std::vector<std::shared_ptr<NFP>> parts_vec;
    int index = 0;
    std::function<void(SheetPlacement)> ResponseAction;

    void BackgroundStart(DataInfo data);
    // Evaluate one candidate end-to-end using ONLY local transient state (no this->data/parts_vec/index)
    // and the shared thread-safe caches (cacheProcess, window.db). Safe to call concurrently for the
    // parallel-population path. Returns the placement (does NOT invoke ResponseAction).
    // Precondition: d.individual.placements are deep-cloned (caller owns them) and d.sheets already have
    // Id/source/children assigned (set once by the caller, read-only here).
    SheetPlacement evaluateCandidate(DataInfo d);
    void sync();
    std::shared_ptr<NFP> getPart(int source, const std::vector<std::shared_ptr<NFP>>& parts);
    void thenIterate(NfpPair& processed, const std::vector<std::shared_ptr<NFP>>& parts);
    void thenDeepNest(std::vector<NfpPair>& processed, const std::vector<std::shared_ptr<NFP>>& parts);
    std::vector<NfpPair> pmapDeepNest(std::vector<NfpPair>& pairs);
    NfpPair process(NfpPair pair);
};

} // namespace nest
