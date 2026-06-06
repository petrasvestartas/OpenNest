// Faithful C++ port of nest-rs collision_detection/obstacles/* (obstacle, filter, collector).
// ObstacleKey and PartKey are slotmap keys (same underlying SlotKey type).
#pragma once
#include "polygon.hpp"
#include "slotmap.hpp"
#include <memory>

namespace nest {

using ObstacleKey = SlotKey;   // key into CollisionEngine::obstacles_map
using PartKey = SlotKey; // key into Layout::placed_parts

// Entity inducing a obstacle (Rust enum ObstacleRef). Tagged union.
enum class ObstacleKind { PlacedPart, Exterior, Hole, QualityZone };

struct ObstacleRef {
    ObstacleKind kind = ObstacleKind::Exterior;
    // PlacedPart
    usize id = 0;
    RigidTransform dt;
    PartKey pk = NULL_KEY;
    // Hole / QualityZone
    usize idx = 0;
    usize quality = 0;

    static ObstacleRef placed_item(usize id, RigidTransform dt, PartKey pk) {
        ObstacleRef e; e.kind = ObstacleKind::PlacedPart; e.id = id; e.dt = dt; e.pk = pk; return e;
    }
    static ObstacleRef exterior() { ObstacleRef e; e.kind = ObstacleKind::Exterior; return e; }
    static ObstacleRef hole(usize idx) { ObstacleRef e; e.kind = ObstacleKind::Hole; e.idx = idx; return e; }
    static ObstacleRef inferior_quality_zone(usize quality, usize idx) {
        ObstacleRef e; e.kind = ObstacleKind::QualityZone; e.quality = quality; e.idx = idx; return e;
    }

    // Whether the entity induces a obstacle over its interior or exterior.
    GeoPosition scope() const {
        return kind == ObstacleKind::Exterior ? GeoPosition::Exterior : GeoPosition::Interior;
    }

    bool operator==(const ObstacleRef& o) const {
        if (kind != o.kind) return false;
        switch (kind) {
            case ObstacleKind::PlacedPart: return id == o.id && dt == o.dt && pk == o.pk;
            case ObstacleKind::Exterior: return true;
            case ObstacleKind::Hole: return idx == o.idx;
            case ObstacleKind::QualityZone: return quality == o.quality && idx == o.idx;
        }
        return false;
    }
    bool operator!=(const ObstacleRef& o) const { return !(*this == o); }
};

// Any spatial constraint affecting placement feasibility.
struct Obstacle {
    ObstacleRef entity;
    std::shared_ptr<Polygon> shape; // Arc<Polygon> in Rust
    bool dynamic = false;

    Obstacle() = default;
    Obstacle(ObstacleRef e, std::shared_ptr<Polygon> s, bool d) : entity(e), shape(std::move(s)), dynamic(d) {}
};

// ---- Obstacle filters ----
// Rust uses the ObstacleFilter trait (generics). In C++ we template the query
// functions on the filter type; each filter exposes `bool is_irrelevant(ObstacleKey)`.
struct NoFilter {
    bool is_irrelevant(ObstacleKey) const { return false; }
};

// Deems a single obstacle (itself) irrelevant.
struct SelfKeyFilter {
    ObstacleKey self;
    explicit SelfKeyFilter(ObstacleKey k) : self(k) {}
    bool is_irrelevant(ObstacleKey hk) const { return self == hk; }
};

// Deems a set of HazKeys irrelevant.
struct ObstacleKeyFilter {
    SecondaryMap<char> set; // present == irrelevant
    bool is_irrelevant(ObstacleKey hk) const {
        return const_cast<SecondaryMap<char>&>(set).contains(hk); // contains is logically const
    }
    void add(ObstacleKey k) { set.insert(k, 1); }
};

// BasicHazardCollector: SecondaryMap<ObstacleKey, ObstacleRef>. Also acts as a filter
// (entities already collected are irrelevant).
struct ObstacleCollector {
    SecondaryMap<ObstacleRef> map;

    bool contains_key(ObstacleKey hk) const { return const_cast<SecondaryMap<ObstacleRef>&>(map).contains(hk); }
    bool is_irrelevant(ObstacleKey hk) const { return contains_key(hk); }
    void insert(ObstacleKey hk, const ObstacleRef& e) { map.insert(hk, e); }
    void remove_by_key(ObstacleKey hk) { map.remove(hk); }
    bool empty() const { return map.empty(); }
    std::size_t len() const { return map.size(); }
    void clear() { map.clear(); }

    bool contains_entity(const ObstacleRef& e) const {
        bool found = false;
        map.for_each([&](ObstacleKey, const ObstacleRef& v) { if (v == e) found = true; });
        return found;
    }
    ObstacleKey key_of_entity(const ObstacleRef& e) const {
        ObstacleKey out = NULL_KEY;
        map.for_each([&](ObstacleKey k, const ObstacleRef& v) { if (v == e) out = k; });
        return out;
    }
    void remove_by_entity(const ObstacleRef& e) {
        ObstacleKey k = key_of_entity(e);
        assert(!k.is_null() && "ObstacleRef not found in collector");
        remove_by_key(k);
    }
    template <class F>
    void for_each(F f) const { map.for_each(f); }
};

} // namespace nest
