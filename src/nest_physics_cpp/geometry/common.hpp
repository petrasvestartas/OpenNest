// Faithful C++ port of nest-rs (collision-detection engine for 2D irregular C&P).
// Layer 0: common helpers, geometric enums, FPA (float-precision-adjusted comparison).
//
// Original: nest-rs/src/util/fpa.rs, geometry/geo_enums.rs
#pragma once
#include <cstdint>
#include <cmath>
#include <cstring>
#include <vector>
#include <array>
#include <optional>
#include <algorithm>
#include <cassert>
#include <limits>
#include <stdexcept>
#include <string>

namespace nest {

using f32 = float;
using usize = std::size_t;

// Rust f32::min / f32::max propagate the non-NaN operand. Our data is NaN-free,
// so the simple branch form matches in all real cases and is faster than libm fmin.
inline f32 min_f(f32 a, f32 b) { return a < b ? a : b; }
inline f32 max_f(f32 a, f32 b) { return a > b ? a : b; }
inline f32 sq(f32 x) { return x * x; }
// f32::midpoint
inline f32 midpoint(f32 a, f32 b) { return (a + b) * 0.5f; }

inline constexpr f32 F32_MAX = std::numeric_limits<f32>::max();
inline constexpr f32 F32_MIN = std::numeric_limits<f32>::lowest(); // Rust f32::MIN == most negative
inline constexpr f32 PI_F = 3.14159265358979323846f;

// nest-rs geometry/geo_enums.rs
enum class GeoPosition { Exterior, Interior };
enum class GeoRelation { Intersecting, Enclosed, Surrounding, Disjoint };

// ---------------------------------------------------------------------------
// FPA: wrapper around float_cmp::approx_eq!(f32, a, b) (float-cmp 0.10.0).
// The macro uses F32Margin::default() == { epsilon: f32::EPSILON, ulps: 4 }.
// The exact algorithm (eq.rs): a==b  OR  |a-b| <= f32::EPSILON  OR  ULP-distance <= 4,
// where ULPs are computed on the order-preserving bit mapping `f32_ordered_bits`
// (ulps.rs), NOT raw bits.  PartialOrd returns Equal when approx-equal, else raw order.
// ---------------------------------------------------------------------------
inline uint32_t f32_ordered_bits(f32 f) {
    uint32_t bits;
    std::memcpy(&bits, &f, sizeof(bits));
    constexpr uint32_t SIGN_BIT = 1u << 31;
    return (bits & SIGN_BIT) ? ~bits : (bits ^ SIGN_BIT);
}

struct FPA {
    f32 v;
    FPA() : v(0.0f) {}
    FPA(f32 x) : v(x) {}

    static bool approx_eq(f32 a, f32 b) {
        if (a == b) return true;                                  // exact-equality fast path
        if (std::fabs(a - b) <= std::numeric_limits<f32>::epsilon()) return true; // epsilon branch
        // ULP comparison on order-preserving bits (wrapping_sub + saturating_abs <= 4)
        int32_t ai = static_cast<int32_t>(f32_ordered_bits(a));
        int32_t bi = static_cast<int32_t>(f32_ordered_bits(b));
        int64_t diff = static_cast<int64_t>(ai) - static_cast<int64_t>(bi);
        if (diff < 0) diff = -diff;
        return diff <= 4;
    }
};

inline bool operator==(FPA a, FPA b) { return FPA::approx_eq(a.v, b.v); }
inline bool operator!=(FPA a, FPA b) { return !(a == b); }
// partial_cmp: Equal if approx-eq, else raw ordering.
inline bool operator<=(FPA a, FPA b) { return (a == b) || a.v < b.v; }
inline bool operator>=(FPA a, FPA b) { return (a == b) || a.v > b.v; }
inline bool operator<(FPA a, FPA b) { return !(a == b) && a.v < b.v; }
inline bool operator>(FPA a, FPA b) { return !(a == b) && a.v > b.v; }

} // namespace nest
