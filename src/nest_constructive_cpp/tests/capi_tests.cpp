#include "nest_constructive.h"
#include <cmath>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <string>
#define CHECK(x) do {if(!(x)) throw std::runtime_error(std::string("check failed: ")+#x);} while(0)
int main() {
    try {
        static_assert(sizeof(NcParams)==64);static_assert(sizeof(NcPlacement)==40);static_assert(sizeof(NcStats)==48);
        NcParams p{sizeof(NcParams),2,2,2,6,2,1,17,4,0,0,2,100000};
        NcStats stats{};stats.struct_size=sizeof(stats);
        double xy[]={-3,-5,7,-5,7,5,-3,5};
        int start=0,size=4,part=0,hole=0,qty=2,mirror=0;
        NcPlacement output[2];
        auto call=[&](int cap=2) {stats.struct_size=sizeof(stats);return nc_solve(xy,4,&start,&size,&part,&hole,1,&qty,&mirror,1,nullptr,nullptr,nullptr,0,20,10,&p,output,cap,&stats);};
        CHECK(call()==0);CHECK(stats.placed==2);CHECK(stats.sheets==1);
        CHECK(output[0].sheet==0&&output[1].sheet==0);CHECK(output[0].copy!=output[1].copy);
        const auto first0=output[0],first1=output[1];
        CHECK(call()==0);CHECK(first0.tx==output[0].tx&&first0.ty==output[0].ty&&first0.angle==output[0].angle);
        CHECK(first1.tx==output[1].tx&&first1.ty==output[1].ty&&first1.angle==output[1].angle);
        CHECK(call(1)==-2);
        p.struct_size=1;CHECK(call()==-1);p.struct_size=sizeof(p);
        xy[0]=std::numeric_limits<double>::quiet_NaN();CHECK(call()==-1);xy[0]=-3;
        size=2;CHECK(call()==-1);size=4;
        qty=-1;CHECK(call()==-1);qty=2;
        p.time_limit=-1;CHECK(call()==-1);p.time_limit=0;
        p.scan_step=0;CHECK(call()==-1);p.scan_step=2;
        part=2;CHECK(call()==-1);part=0;
        mirror=7;CHECK(call()==-1);mirror=0;
        qty=0;CHECK(call()==0);CHECK(stats.placed==0&&stats.requested==0);
        CHECK(std::string(nc_version()).find("Constructive")!=std::string::npos);
        std::cout<<"C API validation, quantity, deterministic solve and ABI checks passed\n";
        return 0;
    } catch(const std::exception& e) {std::cerr<<e.what()<<"; native: "<<nc_last_error()<<"\n";return 1;}
}
