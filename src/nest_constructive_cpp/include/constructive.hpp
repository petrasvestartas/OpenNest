#pragma once
#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <vector>

namespace constructive {
struct Point { double x=0, y=0; };
using Ring = std::vector<Point>;
struct Shape { Ring outer; std::vector<Ring> holes; };
struct Bounds { double min_x=0,min_y=0,max_x=0,max_y=0; };
struct Variant { int part=0; double angle=0; bool mirrored=false; Shape shape; Bounds bounds; double area=0; };
struct Placement { int part=0,copy=0,sheet=0,variant=0; double x=0,y=0; };
struct Problem {
    std::vector<Shape> parts;
    std::vector<int> quantities;
    std::vector<std::vector<double>> angles; // radians; empty selects uniform Config.rotations
    std::vector<bool> mirror;
    std::vector<Shape> defects; // same keep-outs on each rectangular stock sheet
    double width=0,height=0;
};
struct Config {
    int max_sheets=100,beam_width=3,branch_parts=4,position_limit=6;
    int restarts=20,workers=1,seed=100,rotations=16;
    int variant_limit=8; // sampled bounds-fitting variants per expansion; exhaustive failure fallback
    bool smart_orientations=true;
    bool conservative_proxies=true;
    double time_limit=30,scan_step=20,scale=100000;
};
using Stop = std::function<bool()>;
struct GeometryStats { std::uint64_t hits=0,misses=0; };
// Independent integer polygon backend, shared cached reflected-polygon sums.
class Geometry {
public:
    Geometry(const Problem&,const Config&);
    ~Geometry();
    Geometry(const Geometry&)=delete;
    const std::vector<Variant>& variants() const;
    const std::vector<int>& variants_for(int part) const;
    std::vector<Point> positions(int moving,const std::vector<Placement>& placed,
                                int sheet,int direction,int limit,const Stop& stop);
    bool valid(int moving,Point p,const std::vector<Placement>& placed,int sheet) const;
    GeometryStats stats() const;
private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};
struct Result {
    std::vector<Placement> placements;
    int requested=0,sheets=0,iterations=0;
    int tail_rebuilds=0,retained_sheets=0;
    int refill_added=0;
    bool cancelled=false;
    double elapsed=0;
    GeometryStats geometry;
};
// Fresh constructive beam search, randomized strategy portfolio and tail rebuilding.
Result solve(const Problem&,const Config&,std::atomic<bool>& cancelled);
}
