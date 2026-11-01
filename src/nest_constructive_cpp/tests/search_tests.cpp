#include "constructive.hpp"
#include "coverage_archive.hpp"
#include "variant_sampling.hpp"
#include "prefix_archive.hpp"
#include "sheet_patterns.hpp"
#include <atomic>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <set>
using namespace constructive;
static void require(bool value, const char* why) { if (!value) throw std::runtime_error(why); }
static Shape rectangle(double w,double h) { return {{{0,0},{w,0},{w,h},{0,h}}, {}}; }
static Config cfg() { Config c; c.time_limit=0;c.workers=1;c.restarts=3;c.rotations=1;c.max_sheets=2;
    c.beam_width=2;c.branch_parts=2;c.position_limit=3;c.scan_step=5;return c; }
static void verify(const Problem& p,const Config& c,const Result& r) {
    Geometry g(p,c);std::vector<Placement> previous;std::set<std::pair<int,int>> copies;
    for(const auto& v:r.placements) {
        require(copies.insert({v.part,v.copy}).second,"duplicate copy");
        require(g.valid(v.variant,{v.x,v.y},previous,v.sheet),"invalid placement");
        previous.push_back(v);
    }
}
int main() {
    try {
        {
            // Geometric regression: construction fits four rectangles, then
            // compaction frees a contact region that admits a fifth rectangle.
            Problem p;p.width=20;p.height=20;
            for(auto d:std::vector<std::pair<int,int>>{{9,5},{11,4},{12,3},{15,11},{6,9},{13,11},{12,11}})
                p.parts.push_back(rectangle(d.first,d.second));
            auto c=cfg();c.max_sheets=1;c.restarts=1;c.rotations=4;c.smart_orientations=false;
            c.beam_width=1;c.branch_parts=1;c.position_limit=4;c.scan_step=2;
            std::atomic<bool> stop{false};auto r=solve(p,c,stop);
            require(r.refill_added==1&&r.placements.size()==5,"last-sheet compaction space was not refilled");
            verify(p,c,r);
        }
        {
            // Five complementary whole sheets from different constructions.
            detail::SheetPatterns pool(std::vector<int>(10,1),std::vector<double>(10,1),std::vector<double>(10,1));
            for(int i=0;i<5;++i) {
                std::vector<Placement> sheet{{2*i,9,7,2*i,0,0},{2*i+1,4,7,2*i+1,10,0}};
                require(!pool.insert(false,sheet,7),"partial sheet admitted to patterns");
                require(pool.insert(true,sheet,7),"complementary sheet rejected");
                require(!pool.insert(true,sheet,7),"duplicate quantity pattern admitted");
            }
            auto choices=pool.combine(5,[]{return false;});
            auto full=std::find_if(choices.candidates.begin(),choices.candidates.end(),[](const auto& c){return c.count==10;});
            require(full!=choices.candidates.end()&&full->patterns.size()==5,"five complementary sheets failed to combine");
            auto placements=pool.compose(*full);std::set<std::pair<int,int>> copies;
            for(auto p:placements){require(p.copy==0&&copies.insert({p.part,p.copy}).second,"combined copy identities invalid");require(p.y==0&&(p.x==0||p.x==10),"composition moved a placement");}
            Problem geometry_problem;geometry_problem.width=20;geometry_problem.height=10;
            geometry_problem.parts.assign(10,rectangle(10,10));geometry_problem.angles.assign(10,{0});
            Result composed;composed.placements=placements;auto geometry_config=cfg();geometry_config.max_sheets=5;
            verify(geometry_problem,geometry_config,composed);
            require(pool.combine(5,[]{return true;}).candidates.empty(),"precancelled recombination did work");
            auto bounded=pool.combine(5,[]{return false;},64,3);
            require(bounded.stopped&&bounded.attempts<=3,"recombination exceeded node budget");
            auto again=pool.combine(5,[]{return false;});
            require(again.attempts==choices.attempts&&again.candidates.back().patterns==choices.candidates.back().patterns,"pattern recombination not deterministic");
        }
        {
            detail::SheetPatterns pool({2,1},{1,1},{1,1});
            pool.insert(true,{{0,0,0,0,3,4}},0);
            pool.insert(true,{{0,0,0,0,3,4},{1,0,0,1,8,4}},0);
            auto choices=pool.combine(3,[]{return false;});bool full=false;
            for(const auto& choice:choices.candidates) {
                require(choice.used[0]<=2&&choice.used[1]<=1,"pattern demand overlap accepted");
                if(choice.count==3) {
                    full=true;auto out=pool.compose(choice);std::set<std::pair<int,int>> ids;
                    for(auto p:out)require(ids.insert({p.part,p.copy}).second,"multiple-copy relabeling duplicated identity");
                }
            }
            require(full,"multiple-copy patterns failed to combine");
            detail::SheetCombination invalid;invalid.patterns={1,1};bool rejected=false;
            try{pool.compose(invalid);}catch(const std::invalid_argument&){rejected=true;}
            require(rejected,"composition accepted duplicated demand");
            detail::SheetPatterns small(std::vector<int>(8,1),{100,1,1,1,1,1,1,1},std::vector<double>(8,1),3);
            for(int i=0;i<8;++i)small.insert(true,{{i,0,0,i,0,0}},0);
            require(small.patterns().size()==3,"sheet pattern pool unbounded");
            require(std::any_of(small.patterns().begin(),small.patterns().end(),[](const auto& p){return p.used[0]==1;}),"dense pattern evicted");
            detail::SheetPatterns rare({1,1,1},{1,100,1},{1,1,1},3);
            rare.insert(true,{{0,0,0,0,0,0}},0);
            rare.insert(true,{{1,0,0,1,0,0}},0);
            rare.insert(true,{{1,0,0,1,0,0},{2,0,0,2,10,0}},0);
            rare.insert(true,{{2,0,0,2,0,0}},0);
            require(std::any_of(rare.patterns().begin(),rare.patterns().end(),[](const auto& p){return p.used[0]==1;}),"rare-part coverage evicted by common small pattern");
        }
        {
            detail::PrefixArchive<int> prefixes(7);
            require(!prefixes.insert(false,{false,20,3,2.8,9,.8},{20,0},1),"unfinished sheet entered prefix archive");
            require(prefixes.insert(true,{false,20,3,2.8,9,.8},{20,0},2),"dense completed prefix rejected");
            prefixes.insert(true,{false,40,5,3.1,12,.9},{20,20},3);
            require(prefixes.levels()[2].entries().size()==1 && prefixes.levels()[2].entries()[0].payload.state==2,
                    "sparse later-sheet solution erased dense shorter prefix");
            require(!prefixes.insert(true,{false,20,3,2.8,9,.1},{20,0},4),"equivalent remaining quantity vector duplicated");
            require(prefixes.insert(true,{false,20,3,2.8,9,.8},{0,20},5),"distinct equal-metric remaining demand erased");
            require(prefixes.levels()[2].entries().size()==2,"equal-metric demand diversity missing");
            require(detail::PrefixArchive<int>(1000000).levels().size()==32,"prefix level allocation unbounded");
        }
        {
            std::vector<int> allowed;for(int i=0;i<19;++i)allowed.push_back(i);
            std::set<int> explored;
            for(int restart=0;restart<20;++restart) {
                auto order=detail::variant_order(allowed,8,restart,123);
                require(order==detail::variant_order(allowed,8,restart,123),"variant sampling nondeterministic");
                require(std::set<int>(order.begin(),order.end())==std::set<int>(allowed.begin(),allowed.end()),"variant sampling changed allowed set");
                require(order.size()==allowed.size(),"duplicate sampled variants");
                explored.insert(order.begin(),order.begin()+8);
            }
            require(explored.size()==allowed.size(),"restart strata failed to explore allowed rotations");
            require(detail::variant_order(allowed,8,1,123)!=detail::variant_order(allowed,8,2,123),"restart samples did not rotate");
        }
        {
            detail::CoverageArchive<int> archive(4);
            archive.insert({false,10,2,10,1,.9},10); // many small/easy parts
            archive.insert({false,2,1,90,9,.8},2);   // fewer large/difficult parts
            require(archive.entries().size()==2,"coverage archive discarded fewer large parts");
            require(!archive.insert({false,1,1,5,.5,.1},1),"dominated state admitted");
            for(int i=11;i<=50;++i)archive.insert({false,i,2,100.-i,10.-i*.1,.8},i);
            require(archive.entries().size()<=4,"archive exceeds bounded capacity");
            bool large=false,count=false;
            for(const auto& e:archive.entries()){large|=e.payload==2;count|=e.payload==50;}
            require(large&&count,"archive lost coverage extremes");
            archive.insert({true,60,5,120,20,.9},60);
            require(archive.entries().size()==1 && archive.entries()[0].payload==60,"complete state must dominate partial archive");
        }
        std::atomic<bool> cancel{false};
        {
            // Both a 50x10 vertical stack and 90x5 side-by-side layout have
            // extent=min(xmax/100,ymax/10)=.5. Prefer the smaller occupied box.
            Problem p;p.width=100;p.height=10;p.parts={rectangle(50,5),rectangle(40,5)};
            p.angles={{0},{0}};
            auto c=cfg();c.restarts=1;c.max_sheets=1;c.position_limit=8;c.scan_step=5;
            auto r=solve(p,c,cancel);require(r.placements.size()==2,"envelope tie fixture incomplete");
            Geometry g(p,c);double xmin=1e10,ymin=1e10,xmax=-1e10,ymax=-1e10;
            for(const auto& placed:r.placements) {
                auto b=g.variants()[placed.variant].bounds;
                xmin=std::min(xmin,placed.x+b.min_x);ymin=std::min(ymin,placed.y+b.min_y);
                xmax=std::max(xmax,placed.x+b.max_x);ymax=std::max(ymax,placed.y+b.max_y);
            }
            require((xmax-xmin)*(ymax-ymin)<=450.00001,"extent tie did not prefer compact occupied envelope");
            verify(p,c,r);
        }
        {
            // Area-first puts the square on the only sheet; long-first must
            // retain its own branches instead of being overridden by area ties.
            Problem p;p.width=10;p.height=5;p.parts={rectangle(5,5),rectangle(10,2)};
            p.quantities={1,2};p.angles={{0},{0}};
            auto c=cfg();c.max_sheets=1;c.restarts=2;c.beam_width=1;c.branch_parts=2;
            auto r=solve(p,c,cancel);
            require(r.placements.size()==2,"long-first worker ranking overridden by public area comparator");
            for(const auto& placed:r.placements) require(placed.part==1,"wrong long-first construction");
            verify(p,c,r);
        }
        {
            // Both angles fit the stock bounds. The initially sampled angle is
            // blocked by a full-height defect; only the unsampled 90-degree one fits.
            Problem p;p.width=30;p.height=20;p.parts={rectangle(20,10)};
            p.angles={{0,1.57079632679489661923}};p.defects={rectangle(15,20)};
            auto c=cfg();c.max_sheets=1;c.restarts=1;c.variant_limit=1;
            Geometry g(p,c);const auto& ids=g.variants_for(0);
            require(ids.size()==2,"explicit rotations changed by implicit policy");
            require(g.positions(ids[0],{},0,0,3,[]{return false;}).empty(),"fallback fixture sampled angle unexpectedly fits");
            auto r=solve(p,c,cancel);
            require(r.placements.size()==1 && r.placements[0].variant==ids[1],"unsampled viable rotation was omitted");
            verify(p,c,r);
        }
        {
            Problem p;p.width=40;p.height=20;p.parts={rectangle(20,20)};p.quantities={4};
            auto c=cfg();auto r=solve(p,c,cancel);
            require(r.requested==4 && r.placements.size()==4 && r.sheets==2,"multiple copies across sheets");
            require(r.iterations==3,"finite deterministic restart budget");verify(p,c,r);
            require(r.tail_rebuilds==1 && r.retained_sheets==1,"whole-sheet prefix reconstruction exercised");
            auto again=solve(p,c,cancel);require(again.placements.size()==r.placements.size(),"deterministic count");
            for(std::size_t i=0;i<r.placements.size();++i) {
                auto a=r.placements[i],b=again.placements[i];
                require(a.part==b.part && a.copy==b.copy && a.sheet==b.sheet && a.variant==b.variant &&
                        a.x==b.x && a.y==b.y,"single-worker seeded determinism");
            }
        }
        {
            Problem p;p.width=20;p.height=20;p.parts={rectangle(50,50),rectangle(10,10)};p.quantities={1,2};
            auto c=cfg();c.branch_parts=1;c.max_sheets=1;auto r=solve(p,c,cancel);
            require(r.requested==3 && r.placements.size()==2,"oversized demand retained and smaller parts eligible");
            for(auto v:r.placements)require(v.part==1,"oversized part was placed");verify(p,c,r);
        }
        {
            Problem p;p.width=20;p.height=20;p.parts={rectangle(20,20)};p.quantities={3};
            auto c=cfg();auto r=solve(p,c,cancel);
            require(r.placements.size()==2 && r.requested==3 && r.sheets==2,"valid stock-limited partial result");verify(p,c,r);
        }
        {
            Problem p;p.width=30;p.height=20;p.parts={rectangle(20,10)};p.quantities={1};
            p.angles={{1.57079632679489661923}};p.defects={rectangle(15,20)};
            auto c=cfg();c.max_sheets=1;auto r=solve(p,c,cancel);
            require(r.placements.size()==1,"allowed rotated variant with stock defect");verify(p,c,r);
        }
        {
            Problem p;p.width=20;p.height=20;p.parts={rectangle(10,10)};p.quantities={0};
            auto c=cfg();auto r=solve(p,c,cancel);require(r.requested==0 && r.placements.empty(),"zero quantity");
            p.quantities={2};cancel.store(true);r=solve(p,c,cancel);
            require(r.cancelled && r.placements.empty() && r.requested==2,"pre-cancel valid empty partial");cancel.store(false);
        }
        {
            Problem p;p.width=40;p.height=20;p.parts={rectangle(10,10)};p.quantities={6};
            auto c=cfg();c.workers=2;c.restarts=4;auto r=solve(p,c,cancel);
            require(r.placements.size()==6,"shared-cache parallel result");verify(p,c,r);
        }
        std::cout<<"constructive search contracts passed\n";
    } catch(const std::exception& e) { std::cerr<<e.what()<<"\n";return 1; }
}
