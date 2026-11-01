#pragma once
#include "constructive.hpp"
#include <algorithm>
#include <map>
#include <numeric>
#include <set>
#include <stdexcept>

namespace constructive::detail {
struct SheetPattern {
    std::vector<Placement> placements;
    std::vector<int> used;
    int count=0;
    double area=0,difficulty=0;
};
struct SheetCombination {
    std::vector<int> patterns,used;
    int count=0;
    double area=0,difficulty=0,rarity=0;
};
struct PatternCombinations {
    std::vector<SheetCombination> candidates;
    std::size_t attempts=0;
    bool stopped=false;
};
class SheetPatterns {
public:
    SheetPatterns(std::vector<int> demand,std::vector<double> area,std::vector<double> difficulty,
                  std::size_t capacity=128):demand_(std::move(demand)),area_(std::move(area)),
                  difficulty_(std::move(difficulty)),capacity_(std::max<std::size_t>(1,capacity)) {}
    const std::vector<SheetPattern>& patterns() const {return patterns_;}
    bool insert(bool completed,const std::vector<Placement>& placements,int sheet) {
        if(!completed)return false;
        SheetPattern p;p.used.assign(demand_.size(),0);
        for(auto v:placements)if(v.sheet==sheet) {
            if(v.part<0||v.part>=static_cast<int>(demand_.size())||++p.used[v.part]>demand_[v.part])return false;
            v.sheet=0;v.copy=p.used[v.part]-1;p.placements.push_back(v);
        }
        if(p.placements.empty())return false;
        for(const auto& old:patterns_)if(old.used==p.used)return false;
        for(std::size_t i=0;i<p.used.size();++i) {
            p.count+=p.used[i];p.area+=p.used[i]*area_[i];p.difficulty+=p.used[i]*difficulty_[i];
        }
        patterns_.push_back(std::move(p));
        if(patterns_.size()>capacity_) {
            const auto frequency=frequencies();
            const auto dense=std::max_element(patterns_.begin(),patterns_.end(),[](const auto& a,const auto& b){return a.area<b.area;});
            const double maxarea=std::max(1e-12,dense->area);
            std::size_t remove=patterns_.size()-1;double worst=1e300;
            for(std::size_t j=0;j<patterns_.size();++j) {
                if(&patterns_[j]==&*dense && capacity_>1)continue;
                double rare=0,sum=0;
                for(std::size_t i=0;i<demand_.size();++i)if(patterns_[j].used[i]) {
                    const double r=1./frequency[i];rare=std::max(rare,r);sum+=r;
                }
                const double value=2*rare+.1*sum+patterns_[j].area/maxarea;
                if(value<=worst){worst=value;remove=j;}
            }
            const bool retained=remove+1!=patterns_.size();
            patterns_.erase(patterns_.begin()+remove);return retained;
        }
        return true;
    }
    PatternCombinations combine(int max_sheets,const Stop& stop,std::size_t beam_width=64,
                                std::size_t attempt_limit=65536) const {
        PatternCombinations result;
        if(patterns_.empty()||max_sheets<=0)return result;
        beam_width=std::max<std::size_t>(1,std::min<std::size_t>(64,beam_width));
        const auto frequency=frequencies();
        std::vector<SheetCombination> beam(1);beam[0].used.assign(demand_.size(),0);
        for(int depth=0;depth<std::min(32,max_sheets);++depth) {
            std::map<std::vector<int>,SheetCombination> distinct;
            for(const auto& node:beam) {
                const int begin=node.patterns.empty()?0:node.patterns.back();
                for(int pi=begin;pi<static_cast<int>(patterns_.size());++pi) {
                    if((stop&&stop())||result.attempts>=attempt_limit){result.stopped=true;break;}
                    ++result.attempts;
                    const auto& p=patterns_[pi];bool fits=true;
                    for(std::size_t i=0;i<demand_.size();++i)if(node.used[i]+p.used[i]>demand_[i]){fits=false;break;}
                    if(!fits)continue;
                    SheetCombination next=node;next.patterns.push_back(pi);next.count+=p.count;
                    next.area+=p.area;next.difficulty+=p.difficulty;
                    for(std::size_t i=0;i<demand_.size();++i) {
                        next.used[i]+=p.used[i];if(p.used[i])next.rarity+=double(p.used[i])/std::max(1,frequency[i]);
                    }
                    // Equal residual demand gives the same untouched-sheet problem.
                    auto it=distinct.find(next.used);
                    if(it==distinct.end()||next.patterns.back()<it->second.patterns.back())distinct[next.used]=std::move(next);
                }
                if(result.stopped)break;
            }
            if(distinct.empty())break;
            std::vector<SheetCombination> options;options.reserve(distinct.size());
            for(auto& item:distinct)options.push_back(std::move(item.second));
            std::vector<std::size_t> ids(options.size());std::iota(ids.begin(),ids.end(),0);
            std::set<std::size_t> chosen;
            for(int axis=0;axis<4;++axis) {
                auto value=[&](std::size_t i){const auto& n=options[i];return axis==0?double(n.count):axis==1?n.area:axis==2?n.difficulty:n.rarity;};
                std::stable_sort(ids.begin(),ids.end(),[&](auto a,auto b){if(value(a)!=value(b))return value(a)>value(b);return options[a].area>options[b].area;});
                for(std::size_t k=0;k<std::min(options.size(),std::max<std::size_t>(1,beam_width/4));++k)chosen.insert(ids[k]);
                // One representative per objective/depth is offered for tail construction.
                result.candidates.push_back(options[ids.front()]);
            }
            for(auto i:ids){if(chosen.size()>=beam_width)break;chosen.insert(i);}
            beam.clear();for(auto i:chosen){if(beam.size()==beam_width)break;beam.push_back(std::move(options[i]));}
            if(result.stopped)break;
        }
        return result;
    }
    std::vector<Placement> compose(const SheetCombination& combination) const {
        std::vector<int> used(demand_.size(),0);std::vector<Placement> out;
        for(std::size_t sheet=0;sheet<combination.patterns.size();++sheet) {
            const int index=combination.patterns[sheet];
            if(index<0||index>=static_cast<int>(patterns_.size()))throw std::invalid_argument("invalid sheet pattern index");
            for(auto p:patterns_[index].placements) {
                if(used[p.part]>=demand_[p.part])throw std::invalid_argument("sheet patterns exceed demand");
                p.copy=used[p.part]++;p.sheet=static_cast<int>(sheet);out.push_back(p);
            }
        }
        return out;
    }
private:
    std::vector<int> frequencies() const {
        std::vector<int> result(demand_.size(),0);
        for(const auto& p:patterns_)for(std::size_t i=0;i<result.size();++i)if(p.used[i])++result[i];
        return result;
    }
    std::vector<int> demand_;
    std::vector<double> area_,difficulty_;
    std::size_t capacity_;
    std::vector<SheetPattern> patterns_;
};
}
