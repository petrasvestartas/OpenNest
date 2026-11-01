// Standalone profiling utility; deliberately includes internals for decomposition counts.
#include "../geometry.cpp"
#include <chrono>
#include <fstream>
#include <iostream>
#include <sstream>
int main(int argc,char** argv) {
    // Check the linear edge walk against the definition on deterministic convex polygons.
    for(int n=3;n<18;++n)for(int m=3;m<18;++m){Clipper2Lib::Path64 a,b,all;
        for(int k=0;k<n;++k)a.emplace_back(std::llround(100000*std::cos(6.283185307179586*k/n)),std::llround(100000*std::sin(6.283185307179586*k/n)));
        for(int k=0;k<m;++k)b.emplace_back(std::llround(70000*std::cos(6.283185307179586*k/m+.13)),std::llround(70000*std::sin(6.283185307179586*k/m+.13)));
        for(auto x:a)for(auto y:b)all.push_back(x-y);
        auto expected=constructive::hull(all),got=constructive::convex_difference(a,b);
        if(constructive::has_area(Clipper2Lib::Xor({expected},{got},Clipper2Lib::FillRule::NonZero))){std::cerr<<"convex sum mismatch\n";return 5;}}
    if(argc<2)return 2;
    std::ifstream in(argv[1]);constructive::Problem p;int count;in>>p.width>>p.height>>count;
    for(int i=0;i<count;++i){constructive::Shape s;int rings;in>>rings;
        for(int r=0;r<rings;++r){int n;in>>n;constructive::Ring ring;
            for(int j=0;j<n;++j){double x,y;in>>x>>y;ring.push_back({x,y});}
            if(!r)s.outer=ring;else s.holes.push_back(ring);}
        p.parts.push_back(s);}
    if(!in)return 3;
    using Clock=std::chrono::steady_clock;
    constructive::Config c;c.rotations=1;c.scan_step=25;c.smart_orientations=false;
    for(int i=0;i<std::min(count,2);++i){auto t=Clock::now();constructive::IntegerShape s(constructive::material(p.parts[i],c.scale),std::min(p.width,p.height)*5e-4*c.scale);
        std::cout<<"part "<<i<<" rings "<<s.material.size()<<" outer_vertices "<<s.bulk.front().size()<<" pieces "<<s.pieces().size()<<" seconds "<<std::chrono::duration<double>(Clock::now()-t).count()<<std::endl;}
    if(argc>2) {
        c.rotations=4;constructive::Geometry g(p,c);std::ifstream csv(argv[2]);std::string line;std::getline(csv,line);
        std::vector<constructive::Placement> placed;std::vector<int> missing;
        while(std::getline(csv,line)){std::replace(line.begin(),line.end(),',',' ');std::istringstream row(line);
            int part,copy,sheet,mirror;double x,y,angle;row>>part>>copy>>sheet>>mirror>>x>>y>>angle;
            if(sheet<0){missing.push_back(part);continue;}
            int variant=-1;for(int v:g.variants_for(part))if(std::abs(std::remainder(g.variants()[v].angle-angle,6.283185307179586))<1e-6){variant=v;break;}
            if(variant<0)return 6;placed.push_back({part,copy,sheet,variant,x,y});}
        auto begin=Clock::now();int queries=0;
        for(int part:missing)for(int variant:g.variants_for(part)) {
            auto start=Clock::now();auto points=g.positions(variant,placed,0,0,4,[&]{return std::chrono::duration<double>(Clock::now()-begin).count()>20;});
            std::cout<<"query "<<part<<" variant "<<variant<<" positions "<<points.size()<<" seconds "<<std::chrono::duration<double>(Clock::now()-start).count()<<std::endl;
            if(++queries>=20||std::chrono::duration<double>(Clock::now()-begin).count()>20)goto done;
        }
        done: std::cout<<"queries "<<queries<<" seconds "<<std::chrono::duration<double>(Clock::now()-begin).count()<<" misses "<<g.stats().misses<<std::endl;return 0;
    }
    constructive::Geometry g(p,c);auto first=g.positions(0,{},0,0,1,[] {return false;});if(first.empty())return 4;
    auto t=Clock::now();auto out=g.positions(1,{{0,0,0,0,first[0].x,first[0].y}},0,0,6,[&]{return std::chrono::duration<double>(Clock::now()-t).count()>30;});
    std::cout<<"pair positions "<<out.size()<<" seconds "<<std::chrono::duration<double>(Clock::now()-t).count()<<" misses "<<g.stats().misses<<std::endl;
}
