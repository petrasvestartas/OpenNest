#pragma once

#include <vector>
#include <optional>
#include <memory>
#include <unordered_map>
#include <mutex>
#include <shared_mutex>
#include <cstdint>

#include "Point.h"
#include "NestConfig.h"
#include "clipper2/clipper.h"

namespace nest {

class NFP;

// ---------- DbCacheKey ----------
struct DbCacheKey {
    std::optional<int> A;
    std::optional<int> B;
    float ARotation = 0;
    float BRotation = 0;
    std::vector<std::shared_ptr<NFP>> nfp;   // owned copies (cloned on insert)
    int Type = 0;   // 0 = outer pair NFP, 1 = inner-fit entry (sheet/part source spaces overlap)
};

// ---------- NfpPair ----------
struct NfpPair {
    NFP* A = nullptr;
    NFP* B = nullptr;
    std::shared_ptr<NFP> nfp;
    float ARotation = 0;
    float BRotation = 0;
    int Asource = 0;
    int Bsource = 0;
};

// ---------- PlacementItem ----------
struct PlacementItem {
    int id = 0;
    float rotation = 0;
    double x = 0;
    double y = 0;
    int source = 0;
};

// ---------- SheetPlacementItem ----------
struct SheetPlacementItem {
    int sheetId = 0;
    int sheetSource = 0;
    std::vector<PlacementItem> sheetplacements;
    std::vector<PlacementItem> placements;
};

// ---------- PopulationItem ----------
struct PopulationItem {
    bool processing = false;  // C# uses 'object processing = null'; we use bool flag
    std::optional<double> fitness;
    std::vector<float> Rotation;
    std::vector<std::shared_ptr<NFP>> placements;
};

// ---------- SheetPlacement ----------
struct SheetPlacement {
    std::optional<double> fitness;
    std::vector<std::vector<SheetPlacementItem>> placements;
    int index = 0;
};

// ---------- NestItem ----------
struct NestItem {
    std::shared_ptr<NFP> Polygon;
    int Quantity = 0;
    bool IsSheet = false;
};

// ---------- DataInfo ----------
struct DataInfo {
    int index = 0;
    std::vector<std::shared_ptr<NFP>> sheets;
    std::vector<int> sheetids;
    std::vector<int> sheetsources;
    std::vector<std::vector<std::shared_ptr<NFP>>> sheetchildren;
    PopulationItem individual;
    NestConfig config;
    std::vector<int> ids;
    std::vector<int> sources;
    std::vector<std::vector<std::shared_ptr<NFP>>> children;
};

// ---------- ClipCacheItem ----------
struct ClipCacheItem {
    int index = 0;
    Clipper2Lib::Paths64 nfpp;
};

// ---------- NfpCache / dbCache ----------
// Forward declare for circular reference
class dbCache;

class NfpCache {
public:
    NfpCache();

    // Move constructor/assignment must update dbCache's back-pointer
    NfpCache(NfpCache&& other) noexcept;
    NfpCache& operator=(NfpCache&& other) noexcept;

    // No copy
    NfpCache(const NfpCache&) = delete;
    NfpCache& operator=(const NfpCache&) = delete;

    std::unordered_map<uint64_t, std::vector<std::shared_ptr<NFP>>> nfpCache;
    std::unique_ptr<dbCache> db;
};

class dbCache {
public:
    explicit dbCache(NfpCache* w) : window(w) {}

    void setWindow(NfpCache* w) { window = w; }

    bool has(const DbCacheKey& obj);
    void insert(const DbCacheKey& obj, bool inner = false);
    std::vector<std::shared_ptr<NFP>> find(const DbCacheKey& obj, bool inner = false);

private:
    uint64_t getKey(const DbCacheKey& obj) const;

    NfpCache* window;
    // shared_mutex: concurrent has()/find() (the placeParts hot path) take a shared lock and run in
    // parallel; only insert() takes an exclusive lock. Critical for the parallel-population place phase.
    std::shared_mutex lockobj;
};

} // namespace nest
