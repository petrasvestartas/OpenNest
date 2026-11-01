#include "nest_constructive.h"
#include "constructive.hpp"
#include <algorithm>
#include <cmath>
#include <exception>
#include <limits>
#include <mutex>
#include <stdexcept>
#include <string>

namespace {
std::mutex solve_mutex;
std::atomic<bool> cancel_flag{false};
thread_local std::string last_error;
void require(bool ok,const char* message) { if(!ok) throw std::invalid_argument(message); }
bool finite(double x) { return std::isfinite(x); }
constructive::Config config(const NcParams* p) {
    constructive::Config c;
    if(p) {
        require(p->struct_size==sizeof(NcParams),"NcParams struct_size mismatch");
        require((p->flags & ~3)==0,"unknown configuration flags");
        c.smart_orientations=(p->flags & 1)!=0;
        c.conservative_proxies=(p->flags & 2)!=0;
        c.max_sheets=p->max_sheets;c.beam_width=p->beam_width;c.branch_parts=p->branch_parts;
        c.position_limit=p->position_limit;c.restarts=p->restarts;c.workers=p->workers;
        c.seed=p->seed;c.rotations=p->rotations;c.time_limit=p->time_limit;
        c.scan_step=p->scan_step;c.scale=p->scale;
    }
    require(c.max_sheets>0&&c.max_sheets<=100000,"invalid max_sheets");
    require(c.beam_width>0&&c.beam_width<=64,"invalid beam_width");
    require(c.branch_parts>0&&c.branch_parts<=128,"invalid branch_parts");
    require(c.position_limit>0&&c.position_limit<=128,"invalid position_limit");
    require(c.restarts>0&&c.restarts<=1000000,"invalid restarts");
    require(c.workers>0&&c.workers<=64,"invalid workers");
    require(c.rotations>0&&c.rotations<=3600,"invalid rotations");
    require(finite(c.time_limit)&&c.time_limit>=0,"invalid time_limit");
    require(finite(c.scan_step)&&c.scan_step>0,"invalid scan_step");
    require(finite(c.scale)&&c.scale>=100&&c.scale<=10000000,"invalid scale");
    return c;
}
}
extern "C" {
const char* nc_version(void) { return "OpenNest Constructive 0.1.0 ABI 1"; }
const char* nc_last_error(void) { return last_error.c_str(); }
void nc_cancel(void) { cancel_flag.store(true,std::memory_order_relaxed); }
int nc_solve(const double* xy,int point_count,
    const int* ring_starts,const int* ring_sizes,const int* ring_parts,const int* ring_holes,int ring_count,
    const int* quantities,const int* mirror_flags,int part_count,
    const double* angles,const int* angle_starts,const int* angle_counts,int angle_count,
    double width,double height,const NcParams* params,
    NcPlacement* output,int capacity,NcStats* stats) {
    last_error.clear();
    std::unique_lock<std::mutex> lock(solve_mutex,std::try_to_lock);
    if(!lock.owns_lock()) {last_error="another constructive solve is active";return -3;}
    cancel_flag.store(false,std::memory_order_relaxed);
    try {
        const auto c=config(params);
        require(!stats||stats->struct_size==sizeof(NcStats),"NcStats struct_size mismatch");
        require(part_count>=0&&part_count<=10000,"invalid part count");
        require(point_count>=0&&point_count<=2000000,"invalid point count");
        require(ring_count>=0&&ring_count<=100000,"invalid ring count");
        require(angle_count>=0&&angle_count<=1000000,"invalid angle count");
        require(finite(width)&&finite(height)&&width>0&&height>0,"invalid stock dimensions");
        require(std::max(width,height)*c.scale<=1e12,"stock exceeds integer coordinate range");
        require(!point_count||xy,"null coordinate buffer");
        require(!ring_count||(ring_starts&&ring_sizes&&ring_parts&&ring_holes),"null ring buffer");
        require(!part_count||quantities,"null quantity buffer");
        require(!angle_count||(angles&&angle_starts&&angle_counts),"null angle buffer");
        require((angle_starts==nullptr)==(angle_counts==nullptr),"incomplete angle offsets");
        require(capacity>=0,"invalid output capacity");
        constructive::Problem p;
        p.width=width;p.height=height;
        p.parts.resize(part_count);p.quantities.resize(part_count);
        p.angles.resize(part_count);p.mirror.resize(part_count);
        int requested=0;
        for(int i=0;i<part_count;++i) {
            require(quantities[i]>=0&&quantities[i]<=100000,"invalid quantity");
            require(requested<=100000-quantities[i],"too many requested copies");
            requested+=quantities[i];p.quantities[i]=quantities[i];
            if(mirror_flags) require(mirror_flags[i]==0||mirror_flags[i]==1,"invalid mirror flag");
            p.mirror[i]=mirror_flags&&mirror_flags[i]!=0;
            if(angle_starts) {
                const int first=angle_starts[i],count=angle_counts[i];
                require(first>=0&&count>=0&&first<=angle_count&&count<=angle_count-first,"invalid angle range");
                require(count<=3600,"too many angles per part");
                for(int j=first;j<first+count;++j) {
                    require(finite(angles[j]),"non-finite rotation");
                    p.angles[i].push_back(std::remainder(angles[j],6.2831853071795864769));
                }
            }
        }
        if(capacity<requested) {last_error="output capacity is below requested quantity";return -2;}
        require(!requested||output,"null output buffer");
        std::vector<bool> outer(part_count,false);
        for(int r=0;r<ring_count;++r) {
            int first=ring_starts[r],count=ring_sizes[r],part=ring_parts[r],hole=ring_holes[r];
            require(first>=0&&count>=3&&first<=point_count&&count<=point_count-first,"invalid ring range");
            require(part>=-1&&part<part_count,"invalid ring part index");
            require(hole==0||hole==1,"invalid ring hole flag");
            require(part!=-1||hole==0,"stock keep-outs must be outer rings");
            constructive::Ring ring;
            for(int j=first;j<first+count;++j) {
                const double x=xy[2*j],y=xy[2*j+1];
                require(finite(x)&&finite(y),"non-finite coordinate");
                require(std::max(std::abs(x),std::abs(y))*c.scale<=1e12,"polygon exceeds integer coordinate range");
                if(ring.empty()||x!=ring.back().x||y!=ring.back().y) ring.push_back({x,y});
            }
            if(ring.size()>1&&ring.front().x==ring.back().x&&ring.front().y==ring.back().y) ring.pop_back();
            require(ring.size()>=3,"degenerate ring");
            if(part==-1) p.defects.push_back({std::move(ring),{}});
            else if(hole) p.parts[part].holes.push_back(std::move(ring));
            else {require(!outer[part],"multiple outer rings for one part");outer[part]=true;p.parts[part].outer=std::move(ring);}
        }
        for(bool present:outer) require(present,"part has no outer ring");
        for(int i=0,k=0;i<part_count;++i) for(int copy=0;copy<p.quantities[i];++copy)
            output[k++]={i,copy,-1,0,0,0,0};
        if(stats) {*stats={};stats->struct_size=sizeof(NcStats);stats->requested=requested;}
        if(requested==0) return 0;
        auto result=constructive::solve(p,c,cancel_flag);
        // Variant numbering is stable and supplied by the geometry backend. Resolve
        // angle/mirror without recomputing expensive polygon caches.
        // The search exports the actual variant ordinal; rebuilding Geometry creates
        // only transformed material, never the lazy pair NFP cache.
        constructive::Geometry geometry(p,c);
        std::vector<int> offsets(part_count,0);
        for(int i=1;i<part_count;++i) offsets[i]=offsets[i-1]+p.quantities[i-1];
        std::vector<bool> seen(requested,false);
        std::vector<constructive::Placement> accepted;
        int sheets=0;
        for(const auto& placement:result.placements) {
            require(placement.part>=0&&placement.part<part_count,"solver returned invalid part");
            require(placement.copy>=0&&placement.copy<p.quantities[placement.part],"solver returned invalid copy");
            require(placement.sheet>=0&&placement.sheet<c.max_sheets,"solver returned invalid sheet");
            const int index=offsets[placement.part]+placement.copy;
            require(!seen[index],"solver returned duplicate copy");
            require(placement.variant>=0&&placement.variant<static_cast<int>(geometry.variants().size()),"solver returned invalid variant");
            const auto& v=geometry.variants()[placement.variant];
            require(v.part==placement.part,"variant/part mismatch");
            require(geometry.valid(placement.variant,{placement.x,placement.y},accepted,placement.sheet),"solver returned invalid geometry");
            accepted.push_back(placement);seen[index]=true;
            output[index]={placement.part,placement.copy,placement.sheet,v.mirrored?1:0,placement.x,placement.y,v.angle};
            sheets=std::max(sheets,placement.sheet+1);
        }
        if(stats) {
            stats->placed=static_cast<int>(accepted.size());stats->sheets=sheets;
            stats->iterations=result.iterations;stats->cancelled=result.cancelled?1:0;
            stats->nfp_hits=static_cast<int64_t>(result.geometry.hits);
            stats->nfp_misses=static_cast<int64_t>(result.geometry.misses);stats->elapsed=result.elapsed;
        }
        return static_cast<int>(accepted.size())==requested?0:1;
    } catch(const std::exception& e) {last_error=e.what();return -1;}
      catch(...) {last_error="unknown constructive solver error";return -1;}
}
}
