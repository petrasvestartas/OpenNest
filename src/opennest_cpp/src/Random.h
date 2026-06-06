#pragma once

#include <cstdint>
#include <cmath>
#include <stdexcept>
#include <chrono>

namespace nest {

/// Port of .NET's System.Random (subtractive PRNG by Knuth).
/// Same seed produces identical sequence as C#.
class Random {
public:
    Random() {
        auto now = std::chrono::system_clock::now().time_since_epoch();
        int seed = static_cast<int>(std::chrono::duration_cast<std::chrono::milliseconds>(now).count());
        Init(seed);
    }

    explicit Random(int seed) {
        Init(seed);
    }

    /// Returns non-negative random integer [0, INT32_MAX).
    int Next() {
        return InternalSample();
    }

    /// Returns random integer [0, maxValue).
    int Next(int maxValue) {
        if (maxValue < 0)
            throw std::invalid_argument("maxValue must be non-negative");
        return static_cast<int>(Sample() * maxValue);
    }

    /// Returns random integer [minValue, maxValue).
    int Next(int minValue, int maxValue) {
        if (minValue > maxValue)
            throw std::invalid_argument("minValue must be <= maxValue");
        int64_t range = static_cast<int64_t>(maxValue) - minValue;
        if (range <= static_cast<int64_t>(MBIG)) {
            return static_cast<int>(Sample() * range) + minValue;
        }
        return static_cast<int>(static_cast<int64_t>(GetSampleForLargeRange() * range) + minValue);
    }

    /// Returns random double [0.0, 1.0).
    double NextDouble() {
        return Sample();
    }

private:
    static constexpr int MBIG = 2147483647;  // Int32.MaxValue
    static constexpr int MSEED = 161803398;
    static constexpr int MZ = 0;

    int SeedArray[56];
    int inext;
    int inextp;

    void Init(int Seed) {
        int ii;
        int mj, mk;

        // Initialize with the seed
        int subtraction = (Seed == (-2147483647 - 1)) ? 2147483647 : std::abs(Seed);
        mj = MSEED - subtraction;
        SeedArray[55] = mj;
        mk = 1;
        for (int i = 1; i < 55; i++) {
            ii = (21 * i) % 55;
            SeedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0) mk += MBIG;
            mj = SeedArray[ii];
        }
        for (int k = 1; k < 5; k++) {
            for (int i = 1; i < 56; i++) {
                SeedArray[i] -= SeedArray[1 + (i + 30) % 55];
                if (SeedArray[i] < 0) SeedArray[i] += MBIG;
            }
        }
        inext = 0;
        inextp = 21;
    }

    double Sample() {
        return InternalSample() * (1.0 / MBIG);
    }

    int InternalSample() {
        int retVal;
        int locINext = inext;
        int locINextp = inextp;

        if (++locINext >= 56) locINext = 1;
        if (++locINextp >= 56) locINextp = 1;

        retVal = SeedArray[locINext] - SeedArray[locINextp];

        if (retVal == MBIG) retVal--;
        if (retVal < 0) retVal += MBIG;

        SeedArray[locINext] = retVal;

        inext = locINext;
        inextp = locINextp;

        return retVal;
    }

    double GetSampleForLargeRange() {
        int result = InternalSample();
        bool negative = (InternalSample() % 2 == 0);
        if (negative) {
            result = -result;
        }
        double d = result;
        d += (static_cast<double>(MBIG) - 1.0);
        d /= 2.0 * static_cast<double>(MBIG) - 1.0;
        return d;
    }
};

} // namespace nest
