// RNG: Xoshiro256++ seeded via SplitMix64, matching rand 0.10's SmallRng on 64-bit.
// Float/int range sampling uses standard mappings (not bit-exact to rand's Uniform,
// but statistically equivalent — the heuristic's behaviour is unaffected).
#pragma once
#include "../geometry/common.hpp"
#include <cstdint>
#include <vector>

namespace nest {
using nest::f32;
using nest::usize;

struct SplitMix64 {
    uint64_t state;
    explicit SplitMix64(uint64_t seed) : state(seed) {}
    uint64_t next() {
        uint64_t z = (state += 0x9E3779B97F4A7C15ull);
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBull;
        return z ^ (z >> 31);
    }
};

struct Rng {
    uint64_t s[4];

    explicit Rng(uint64_t seed = 0) { seed_u64(seed); }
    void seed_u64(uint64_t seed) {
        SplitMix64 sm(seed);
        s[0] = sm.next(); s[1] = sm.next(); s[2] = sm.next(); s[3] = sm.next();
    }

    static uint64_t rotl(uint64_t x, int k) { return (x << k) | (x >> (64 - k)); }

    uint64_t next_u64() {
        uint64_t result = rotl(s[0] + s[3], 23) + s[0];
        uint64_t t = s[1] << 17;
        s[2] ^= s[0]; s[3] ^= s[1]; s[1] ^= s[2]; s[0] ^= s[3];
        s[2] ^= t;
        s[3] = rotl(s[3], 45);
        return result;
    }

    // f32 in [0, 1)
    f32 gen_f32() { return static_cast<f32>(next_u64() >> 40) * (1.0f / 16777216.0f); }

    // f32 in [lo, hi)
    f32 random_range_f32(f32 lo, f32 hi) { return lo + (hi - lo) * gen_f32(); }

    // integer in [lo, hi)
    usize random_range_usize(usize lo, usize hi) {
        usize range = hi - lo;
        return lo + static_cast<usize>(next_u64() % range);
    }
    int random_range_int(int lo, int hi) {
        int range = hi - lo;
        return lo + static_cast<int>(next_u64() % static_cast<uint64_t>(range));
    }

    template <class T>
    const T& choose(const std::vector<T>& v) { return v[random_range_usize(0, v.size())]; }

    // Fisher-Yates shuffle (back-to-front): yields a uniform permutation. NOTE: this is NOT
    // bit-identical to rand 0.10's SliceRandom::shuffle, which iterates front-to-back via a
    // chunked IncreasingUniform sampler — same uniform distribution, different visit order and
    // RNG-draw pattern (falls under the documented non-bit-exact-RNG deviation).
    template <class T>
    void shuffle(std::vector<T>& v) {
        for (usize i = v.size(); i > 1;) {
            --i;
            usize j = random_range_usize(0, i + 1);
            std::swap(v[i], v[j]);
        }
    }

    // Standard normal via Box-Muller (used for solution-pool selection stddev)
    f32 gen_normal() {
        f32 u1 = gen_f32();
        if (u1 < 1e-9f) u1 = 1e-9f;
        f32 u2 = gen_f32();
        return std::sqrt(-2.0f * std::log(u1)) * std::cos(2.0f * nest::PI_F * u2);
    }
};

} // namespace nest
