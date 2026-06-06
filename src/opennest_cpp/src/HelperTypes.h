#pragma once

#include <vector>
#include <string>
#include <optional>
#include <memory>
#include <unordered_map>
#include <mutex>
#include <shared_mutex>
#include <functional>
#include <cstdint>

#include "Point.h"
#include "NestConfig.h"
#include "clipper2/clipper.h"

namespace nest {

// Forward declarations — NFP defined in WP2
class NFP;

// ---------- NfpKey ----------
struct NfpKey {
    NFP* A = nullptr;
    NFP* B = nullptr;
    float ARotation = 0;
    float BRotation = 0;
    bool Inside = false;
    int AIndex = 0;
    int BIndex = 0;
    std::optional<int> Asource;
    std::optional<int> Bsource;

    std::string stringify() const;
};

// ---------- DbCacheKey ----------
struct DbCacheKey {
    std::optional<int> A;
    std::optional<int> B;
    float ARotation = 0;
    float BRotation = 0;
    std::vector<std::shared_ptr<NFP>> nfp;   // owned copies (cloned on insert)
    int Type = 0;
};

// ---------- NfpPair ----------
struct NfpPair {
    NFP* A = nullptr;
    NFP* B = nullptr;
    NfpKey Key;
    std::shared_ptr<NFP> nfp;
    float ARotation = 0;
    float BRotation = 0;
    int Asource = 0;
    int Bsource = 0;
};

// ---------- PlacementItem ----------
struct PlacementItem {
    std::optional<double> mergedLength;
    // mergedSegments omitted (typed as 'object' in C#, unused in core logic)
    int id = 0;
    std::shared_ptr<NFP> hull;
    std::shared_ptr<NFP> hullsheet;
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
    std::vector<std::shared_ptr<NFP>> paths;
    double area = 0;
};

// ---------- SheetPlacement ----------
struct SheetPlacement {
    std::optional<double> fitness;
    std::vector<float> Rotation;
    std::vector<std::vector<SheetPlacementItem>> placements;
    std::vector<std::shared_ptr<NFP>> paths;
    double area = 0;
    double mergedLength = 0;
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

// ---------- PolygonTreeItem ----------
struct PolygonTreeItem {
    std::shared_ptr<NFP> Polygon;
    PolygonTreeItem* Parent = nullptr;
    std::vector<std::shared_ptr<PolygonTreeItem>> Childs;
};

// ---------- NonameReturn ----------
struct NonameReturn {
    NfpKey key;
    std::vector<std::shared_ptr<NFP>> nfp;

    NonameReturn() = default;
    NonameReturn(const NfpKey& k, std::vector<std::shared_ptr<NFP>> n)
        : key(k), nfp(std::move(n)) {}
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

// ---------- Sheet ----------
// Sheet and RectangleSheet extend NFP — defined in NFP.h (WP2)
// Forward-declared here for reference in other headers.

} // namespace nest
