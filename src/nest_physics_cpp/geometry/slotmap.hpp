// Minimal C++ port of the `slotmap` crate primitives used by nest-rs:
//   * SlotMap<V>    - stable generational keys; insert/remove/get/iterate.
//   * SecondaryMap<V> - associate values with keys from a SlotMap, version-validated.
// Key = (idx, version); a slot is occupied iff its version is odd (free = even),
// matching slotmap's versioning so stale keys never alias a recycled slot.
#pragma once
#include <cstdint>
#include <vector>
#include <optional>
#include <cassert>
#include <utility>

namespace nest {

struct SlotKey {
    uint32_t idx = 0;
    uint32_t version = 0; // 0 == null (even, never matches an occupied slot)
    bool is_null() const { return version == 0; }
    bool operator==(const SlotKey& o) const { return idx == o.idx && version == o.version; }
    bool operator!=(const SlotKey& o) const { return !(*this == o); }
};

inline constexpr SlotKey NULL_KEY{0, 0};

template <class V>
struct SlotMap {
    struct Slot {
        V value{};
        uint32_t version = 0; // even = free, odd = occupied
    };
    std::vector<Slot> slots;
    std::vector<uint32_t> free_idx;
    std::size_t count_ = 0;

    SlotKey insert(V v) {
        uint32_t idx;
        if (!free_idx.empty()) { idx = free_idx.back(); free_idx.pop_back(); }
        else { idx = static_cast<uint32_t>(slots.size()); slots.push_back(Slot{}); }
        Slot& s = slots[idx];
        s.value = std::move(v);
        s.version += 1; // free(even) -> occupied(odd)
        ++count_;
        return SlotKey{idx, s.version};
    }

    bool contains(SlotKey k) const {
        return !k.is_null() && k.idx < slots.size() && slots[k.idx].version == k.version;
    }
    V* get(SlotKey k) { return contains(k) ? &slots[k.idx].value : nullptr; }
    const V* get(SlotKey k) const { return contains(k) ? &slots[k.idx].value : nullptr; }
    V& operator[](SlotKey k) { assert(contains(k)); return slots[k.idx].value; }
    const V& operator[](SlotKey k) const { assert(contains(k)); return slots[k.idx].value; }

    std::optional<V> remove(SlotKey k) {
        if (!contains(k)) return std::nullopt;
        Slot& s = slots[k.idx];
        V out = std::move(s.value);
        s.version += 1; // occupied(odd) -> free(even)
        free_idx.push_back(k.idx);
        --count_;
        return out;
    }

    std::size_t size() const { return count_; }

    template <class F>
    void for_each(F f) const {
        for (uint32_t i = 0; i < slots.size(); ++i)
            if (slots[i].version & 1u) f(SlotKey{i, slots[i].version}, slots[i].value);
    }
    template <class F>
    void for_each_mut(F f) {
        for (uint32_t i = 0; i < slots.size(); ++i)
            if (slots[i].version & 1u) f(SlotKey{i, slots[i].version}, slots[i].value);
    }
};

template <class V>
struct SecondaryMap {
    struct Slot {
        V value{};
        uint32_t version = 0;
        bool present = false;
    };
    std::vector<Slot> slots;
    std::size_t count_ = 0;

    void insert(SlotKey k, V v) {
        if (k.idx >= slots.size()) slots.resize(k.idx + 1);
        Slot& s = slots[k.idx];
        if (!s.present) ++count_;
        s.value = std::move(v);
        s.version = k.version;
        s.present = true;
    }
    bool contains(SlotKey k) const {
        return k.idx < slots.size() && slots[k.idx].present && slots[k.idx].version == k.version;
    }
    void remove(SlotKey k) {
        if (contains(k)) { slots[k.idx].present = false; --count_; }
    }
    V* get(SlotKey k) { return contains(k) ? &slots[k.idx].value : nullptr; }
    std::size_t size() const { return count_; }
    bool empty() const { return count_ == 0; }
    void clear() { slots.clear(); count_ = 0; }

    template <class F>
    void for_each(F f) const {
        for (uint32_t i = 0; i < slots.size(); ++i)
            if (slots[i].present) f(SlotKey{i, slots[i].version}, slots[i].value);
    }
};

} // namespace nest
