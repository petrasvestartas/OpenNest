#pragma once

#include <vector>

namespace nest {

/// Minkowski convolution (NFP computation) using the Boost.Polygon edge-pair
/// algorithm — the same engine used by deepnest-next's node-calculateNFP and the
/// repo's minkowski.dll. The implementation lives in MinkowskiConvolution.cpp so
/// the (heavy) Boost.Polygon headers stay out of every translation unit that only
/// needs the result type. Stateless / reentrant: safe to call from worker threads.
class MinkowskiConvolution {
public:
    struct Result {
        std::vector<std::vector<double>> outerPaths;  // each: x0,y0,x1,y1,... (real coords)
        std::vector<std::vector<double>> holes;       // each: x0,y0,x1,y1,... (real coords)
    };

    /// Fixed scale used by callers (e.g. getInnerNfp) for kScale-precision Clipper
    /// int64 ops. The convolution itself uses an internal dynamic scale (see .cpp).
    static constexpr double kScale = 10000000.0;

    /// NFP of polygon A (outer ring Apts + holes Aholes) against polygon B (Bpts),
    /// i.e. the Minkowski sum A ⊕ (−B), referenced to B's first vertex. All point
    /// arrays are flat interleaved x,y in real (unscaled) coordinates.
    static Result compute(
        const std::vector<double>& Apts,
        const std::vector<std::vector<double>>& Aholes,
        const std::vector<double>& Bpts);
};

} // namespace nest
