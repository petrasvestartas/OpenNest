#pragma once
#include <algorithm>
#include <cmath>
#include <limits>
#include <vector>

namespace constructive::detail {
struct Coverage {
    bool complete=false;
    int count=0,sheets=0;
    double area=0,difficulty=0,extent=0;
};
// Bounded nondominated coverage archive. It is deliberately separate from the
// public best-result order: fewer large/difficult parts can seed useful tails.
template<class Payload> class CoverageArchive {
public:
    struct Entry { Coverage metric; Payload payload; };
    explicit CoverageArchive(std::size_t capacity=8):capacity_(std::max<std::size_t>(1,capacity)) {}
    const std::vector<Entry>& entries() const { return entries_; }
    bool insert(Coverage m,const Payload& value,bool preserve_equal=false) {
        for (const auto& e:entries_) {
            if (dominates(e.metric,m)) return false;
            if (!preserve_equal && equal_coverage(e.metric,m) &&
                (e.metric.sheets<m.sheets || (e.metric.sheets==m.sheets && e.metric.extent<=m.extent))) return false;
        }
        entries_.erase(std::remove_if(entries_.begin(),entries_.end(),[&](const Entry& e) {
            return dominates(m,e.metric) || (!preserve_equal && equal_coverage(m,e.metric));
        }),entries_.end());
        entries_.push_back({m,value});
        if(entries_.size()>capacity_) prune();
        return true;
    }
private:
    std::size_t capacity_;
    std::vector<Entry> entries_;
    static bool equal_coverage(const Coverage& a,const Coverage& b) {
        return a.complete==b.complete && a.count==b.count && a.area==b.area && a.difficulty==b.difficulty;
    }
    static bool dominates(const Coverage& a,const Coverage& b) {
        if(a.complete!=b.complete)return a.complete;
        return a.count>=b.count && a.area>=b.area && a.difficulty>=b.difficulty &&
               (a.count>b.count || a.area>b.area || a.difficulty>b.difficulty);
    }
    void prune() {
        std::vector<double> crowd(entries_.size(),0);
        for(int axis=0;axis<3;++axis) {
            auto value=[&](std::size_t i) { const auto& m=entries_[i].metric;
                return axis==0?double(m.count):(axis==1?m.area:m.difficulty); };
            std::vector<std::size_t> order(entries_.size());
            for(std::size_t i=0;i<order.size();++i)order[i]=i;
            std::stable_sort(order.begin(),order.end(),[&](auto a,auto b){return value(a)<value(b);});
            const double range=value(order.back())-value(order.front());
            if(range<=0)continue;
            crowd[order.front()]=crowd[order.back()]=std::numeric_limits<double>::infinity();
            for(std::size_t j=1;j+1<order.size();++j)
                crowd[order[j]]+=(value(order[j+1])-value(order[j-1]))/range;
        }
        // Stable ties remove the newest point; established extremes survive.
        std::size_t remove=entries_.size()-1;
        for(std::size_t i=0;i<entries_.size();++i)if(crowd[i]<crowd[remove])remove=i;
        entries_.erase(entries_.begin()+remove);
    }
};
}
