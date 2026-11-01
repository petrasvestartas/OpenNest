// NCB1 whitespace input; no JSON runtime or legacy solver dependency.
#include "nest_constructive.h"
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include <limits>

template<class T> void read(std::istream& in,T& value) {
    if (!(in>>value)) throw std::runtime_error("invalid/truncated NCB1 input");
}
int main(int argc,char**argv) {
    try {
        if(argc<3 || argc>5) {std::cerr<<"usage: constructive_bench input.ncb placements.csv [seconds] [max_sheets]\n";return 64;}
        std::ifstream in(argv[1]); std::string magic;read(in,magic);
        if(magic!="NCB1") throw std::runtime_error("expected NCB1");
        double width,height;read(in,width);read(in,height);
        NcParams p{};p.struct_size=sizeof(p);
        read(in,p.max_sheets);read(in,p.beam_width);read(in,p.branch_parts);read(in,p.position_limit);
        read(in,p.restarts);read(in,p.workers);read(in,p.seed);read(in,p.rotations);read(in,p.flags);
        read(in,p.time_limit);read(in,p.scan_step);read(in,p.scale);
        if(argc>=4)p.time_limit=std::stod(argv[3]);
        if(argc>=5)p.max_sheets=std::stoi(argv[4]);
        int np,nr,nv,na;read(in,np);read(in,nr);read(in,nv);read(in,na);
        if(np<1||np>1000000||nr<1||nr>10000000||nv<3||nv>10000000||na<0||na>10000000)
            throw std::runtime_error("NCB1 counts outside benchmark limits");
        std::vector<int> qty(np),mirror(np),astart(np),acount(np),rstart(nr),rsize(nr),owner(nr),hole(nr);
        long long copies=0;
        for(int i=0;i<np;++i){read(in,qty[i]);read(in,mirror[i]);read(in,astart[i]);read(in,acount[i]);
            if(qty[i]<1)throw std::runtime_error("invalid quantity");copies+=qty[i];}
        if(copies>1000000)throw std::runtime_error("too many copies");
        for(int i=0;i<nr;++i){read(in,rstart[i]);read(in,rsize[i]);read(in,owner[i]);read(in,hole[i]);}
        std::vector<double>xy(2*nv),angles(na);
        for(auto&v:xy)read(in,v);for(auto&v:angles)read(in,v);
        std::string extra;if(in>>extra)throw std::runtime_error("unexpected trailing NCB1 tokens");
        std::vector<NcPlacement> out(static_cast<size_t>(copies));NcStats stats{};stats.struct_size=sizeof(stats);
        std::cerr<<"version="<<nc_version()<<" parts="<<np<<" copies="<<copies<<" vertices="<<nv<<" max_sheets="<<p.max_sheets<<" seconds="<<p.time_limit<<" workers="<<p.workers<<" flags="<<p.flags<<"\n";
        int rc=nc_solve(xy.data(),nv,rstart.data(),rsize.data(),owner.data(),hole.data(),nr,
            qty.data(),mirror.data(),np,angles.data(),astart.data(),acount.data(),na,width,height,&p,out.data(),static_cast<int>(copies),&stats);
        if(rc<0){std::cerr<<"rc="<<rc<<" error="<<nc_last_error()<<"\n";return 1;}
        std::ofstream csv(argv[2]);if(!csv)throw std::runtime_error("cannot open output CSV");
        csv<<"part,copy,sheet,mirrored,tx,ty,angle\n"<<std::setprecision(17);
        for(const auto&v:out)csv<<v.part<<','<<v.copy<<','<<v.sheet<<','<<v.mirrored<<','<<v.tx<<','<<v.ty<<','<<v.angle<<'\n';
        csv.close();if(!csv)throw std::runtime_error("CSV write failed");
        std::cout<<std::setprecision(17)<<"rc="<<rc<<" placed="<<stats.placed<<" requested="<<stats.requested
            <<" sheets="<<stats.sheets<<" iterations="<<stats.iterations<<" cancelled="<<stats.cancelled
            <<" nfp_hits="<<stats.nfp_hits<<" nfp_misses="<<stats.nfp_misses<<" elapsed="<<stats.elapsed<<"\n";
        return rc==0?0:2;
    }catch(const std::exception&e){std::cerr<<e.what()<<'\n';return 1;}
}
