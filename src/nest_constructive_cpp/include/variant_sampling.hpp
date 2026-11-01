#pragma once
#include <algorithm>
#include <cstdint>
#include <vector>

namespace constructive::detail {
// Input is already bounds-filtered and retains the authoritative allowed set.
// Front min(limit,size) entries are evenly spaced strata; the tail is a complete
// fallback, without duplicates. Restart rotation eventually visits every offset.
inline std::vector<int> variant_order(const std::vector<int>& fitting, int limit,
                                      int restart, std::uint64_t seed) {
    if (fitting.empty()) return {};
    const std::size_t n=fitting.size(), k=std::min(n,static_cast<std::size_t>(std::max(1,limit)));
    const std::size_t offset=restart==0 ? 0 : (seed % n + static_cast<std::size_t>(restart-1)) % n;
    std::vector<int> result; result.reserve(n);
    std::vector<bool> selected(n,false);
    for(std::size_t i=0;i<k;++i) {
        const std::size_t index=(offset+i*n/k)%n;
        selected[index]=true;result.push_back(fitting[index]);
    }
    for(std::size_t i=0;i<n;++i) {
        const std::size_t index=(offset+i)%n;
        if(!selected[index])result.push_back(fitting[index]);
    }
    return result;
}
}
