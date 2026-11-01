#pragma once
#include "coverage_archive.hpp"

namespace constructive::detail {
// Fixed-stock whole-sheet prefixes: equal quantity vectors have identical
// remaining problems regardless of arrangements on their immutable sheets.
template<class Payload> class PrefixArchive {
public:
    struct Snapshot { std::vector<int> used;Payload state; };
    using Level=CoverageArchive<Snapshot>;
    explicit PrefixArchive(int max_sheets) {
        for(int i=0;i<std::min(32,std::max(0,max_sheets-1));++i)levels_.emplace_back(4);
    }
    bool insert(bool sheet_completed,Coverage metric,const std::vector<int>& used,const Payload& state) {
        if(!sheet_completed || metric.sheets<1 || metric.sheets>static_cast<int>(levels_.size()))return false;
        auto& level=levels_[metric.sheets-1];
        for(const auto& entry:level.entries()) if(entry.payload.used==used)return false;
        return level.insert(metric,{used,state},true);
    }
    const std::vector<Level>& levels() const {return levels_;}
private:
    std::vector<Level> levels_;
};
}
