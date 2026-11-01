#include "constructive.hpp"
#include "clipper2/clipper.h"
#include <boost/polygon/polygon.hpp>
#include <algorithm>
#include <array>
#include <cmath>
#include <limits>
#include <map>
#include <mutex>
#include <set>
#include <stdexcept>
#include <tuple>

namespace constructive {
namespace {
using namespace Clipper2Lib;
using I = int64_t;
constexpr double coordinate_limit = 1e12; // sums/translations remain far below Clipper's 2^61 bound
I quantize(double value, double scale) {
    const double v = value * scale;
    if (!std::isfinite(v) || std::abs(v) > coordinate_limit)
        throw std::invalid_argument("constructive coordinate exceeds integer geometry range");
    return static_cast<I>(std::llround(v));
}
int orient(Point64 a,Point64 b,Point64 c) {
    const I x=b.x-a.x,y=c.y-a.y,u=b.y-a.y,v=c.x-a.x;
    int s1=(x>0?1:x<0?-1:0)*(y>0?1:y<0?-1:0);
    int s2=(u>0?1:u<0?-1:0)*(v>0?1:v<0?-1:0);
    if(s1!=s2)return s1>s2?1:-1;
    const auto p=Multiply(static_cast<uint64_t>(std::abs(x)),static_cast<uint64_t>(std::abs(y)));
    const auto q=Multiply(static_cast<uint64_t>(std::abs(u)),static_cast<uint64_t>(std::abs(v)));
    if(p==q)return 0;
    bool less=std::tie(p.carry,p.result)<std::tie(q.carry,q.result);
    return less?-s1:s1;
}
bool on_segment(Point64 a,Point64 b,Point64 p) {
    return orient(a,b,p)==0&&p.x>=std::min(a.x,b.x)&&p.x<=std::max(a.x,b.x)&&
        p.y>=std::min(a.y,b.y)&&p.y<=std::max(a.y,b.y);
}
bool segments_meet(Point64 a,Point64 b,Point64 c,Point64 d) {
    if(std::max(a.x,b.x)<std::min(c.x,d.x)||std::max(c.x,d.x)<std::min(a.x,b.x)||
       std::max(a.y,b.y)<std::min(c.y,d.y)||std::max(c.y,d.y)<std::min(a.y,b.y))return false;
    int x=orient(a,b,c),y=orient(a,b,d),u=orient(c,d,a),v=orient(c,d,b);
    return (x*y<0&&u*v<0)||(!x&&on_segment(a,b,c))||(!y&&on_segment(a,b,d))||
        (!u&&on_segment(c,d,a))||(!v&&on_segment(c,d,b));
}
void check_simple(const Path64& ring) {
    // Sort by edge left extent to avoid quadratic comparisons for ordinary outlines.
    std::vector<size_t> edges(ring.size());
    for(size_t i=0;i<edges.size();++i)edges[i]=i;
    auto left=[&](size_t i){return std::min(ring[i].x,ring[(i+1)%ring.size()].x);};
    std::sort(edges.begin(),edges.end(),[&](size_t a,size_t b){return left(a)<left(b);});
    for(size_t k=0;k<edges.size();++k) {
        size_t i=edges[k],ni=(i+1)%ring.size();
        auto a=ring[i],b=ring[ni];
        auto c=ring[(ni+1)%ring.size()];
        if(on_segment(a,b,c)||on_segment(b,c,a))throw std::invalid_argument("polygon ring backtracks");
        for(size_t j=k+1;j<edges.size()&&left(edges[j])<=std::max(a.x,b.x);++j) {
            size_t e=edges[j],ne=(e+1)%ring.size();
            if(e==ni||ne==i)continue;
            if(segments_meet(a,b,ring[e],ring[ne]))throw std::invalid_argument("self-intersecting polygon ring");
        }
    }
}
long double twice_area(const Path64& p) {
    long double a = 0;
    if(p.empty())return 0;
    for (size_t i=0, j=p.size()-1; i<p.size(); j=i++)
        a += static_cast<long double>(p[j].x-p[0].x)*(p[i].y-p[0].y)-static_cast<long double>(p[i].x-p[0].x)*(p[j].y-p[0].y);
    return a;
}
bool has_area(const Paths64& paths) {
    // Boolean output consists of simple rings: any non-collinear triple has material area.
    for (const auto& p: paths) if(p.size()>=3)
        for(size_t i=2;i<p.size();++i)if(orient(p[0],p[i-1],p[i])!=0)return true;
    return false;
}
Path64 path(const Ring& ring, double scale, bool hole=false) {
    Path64 p;
    for (const auto& q: ring) {
        Point64 v(quantize(q.x, scale), quantize(q.y, scale));
        if (p.empty() || p.back()!=v) p.push_back(v);
    }
    if (p.size()>1 && p.front()==p.back()) p.pop_back();
    if (p.size()<3 || twice_area(p)==0) {
        // Filling a sub-grid hole adds material conservatively; dropping an outer does not.
        if(hole)return {};
        throw std::invalid_argument("degenerate integer polygon outer ring");
    }
    check_simple(p);
    if (twice_area(p)<0) std::reverse(p.begin(),p.end());
    return p;
}
Paths64 material(const Shape& shape, double scale) {
    Paths64 outer = Union(Paths64{path(shape.outer,scale)},FillRule::NonZero);
    Paths64 holes;
    for (size_t index=0;index<shape.holes.size();++index) {
        Path64 h;
        try {h=path(shape.holes[index],scale,true);}
        catch(const std::exception& e){throw std::invalid_argument("hole "+std::to_string(index)+": "+e.what());}
        if(h.empty())continue;
        if (has_area(Difference(Paths64{h},outer,FillRule::NonZero)))
            throw std::invalid_argument("hole "+std::to_string(index)+" is outside polygon outer ring");
        if(has_area(Intersect(Paths64{h},holes,FillRule::NonZero)))
            throw std::invalid_argument("hole "+std::to_string(index)+" overlaps or contains another hole");
        holes.push_back(std::move(h));
    }
    Paths64 result = holes.empty()?outer:Difference(outer,Union(holes,FillRule::NonZero),FillRule::NonZero);
    if (!has_area(result)) throw std::invalid_argument("polygon has no material after holes");
    return result;
}
Paths64 translate(const Paths64& paths, I x, I y) {
    Paths64 out = paths;
    for (auto& p:out) for(auto& v:p) { v.x+=x; v.y+=y; }
    return out;
}
Path64 hull(Path64 p) {
    std::sort(p.begin(),p.end(),[](Point64 a,Point64 b){return std::tie(a.x,a.y)<std::tie(b.x,b.y);});
    p.erase(std::unique(p.begin(),p.end()),p.end());
    if(p.size()<3) return {};
    Path64 h(2*p.size()); size_t k=0;
    for(auto v:p) { while(k>=2 && orient(h[k-2],h[k-1],v)<=0) --k; h[k++]=v; }
    size_t t=k+1;
    for(size_t i=p.size()-1;i>0;--i) {
        auto v=p[i-1]; while(k>=t && orient(h[k-2],h[k-1],v)<=0) --k; h[k++]=v;
    }
    h.resize(k-1); return h;
}
Paths64 merge_convex(Paths64 pieces) {
    for(int round=0;round<32;++round) {
        std::map<std::pair<I,I>,std::vector<size_t>> vertex_owners;
        for(size_t i=0;i<pieces.size();++i)for(auto p:pieces[i])vertex_owners[{p.x,p.y}].push_back(i);
        std::vector<bool> used(pieces.size(),false);Paths64 next;size_t merged=0;
        for(size_t i=0;i<pieces.size();++i)if(!used[i]) {
            std::set<size_t> neighbors;
            for(auto p:pieces[i])for(auto j:vertex_owners[{p.x,p.y}])if(j!=i&&!used[j])neighbors.insert(j);
            Path64 joined;size_t chosen=pieces.size();
            for(auto j:neighbors) {
                Path64 vertices=pieces[i];vertices.insert(vertices.end(),pieces[j].begin(),pieces[j].end());
                auto h=hull(std::move(vertices));
                long double expected=twice_area(pieces[i])+twice_area(pieces[j]);
                // Floating area is only a cheap rejection. Integer Boolean difference
                // proves no material was added before accepting a convex merge.
                if(std::abs(twice_area(h)-expected)>std::max<long double>(1,std::abs(expected)*1e-14L))continue;
                if(has_area(Difference(Paths64{h},Paths64{pieces[i],pieces[j]},FillRule::NonZero)))continue;
                joined=std::move(h);chosen=j;break;
            }
            used[i]=true;
            if(chosen<pieces.size()){used[chosen]=true;++merged;next.push_back(std::move(joined));}
            else next.push_back(std::move(pieces[i]));
        }
        pieces=std::move(next);if(!merged)break;
    }
    return pieces;
}
Paths64 triangulate_simple(Path64 ring) {
    // Preserve source vertices rather than rounding intersections at trapezoid seams.
    bool removed=true;
    while(removed&&ring.size()>3) {
        removed=false;
        for(size_t i=0;i<ring.size();++i)if(orient(ring[(i+ring.size()-1)%ring.size()],ring[i],ring[(i+1)%ring.size()])==0) {
            ring.erase(ring.begin()+i);removed=true;break;
        }
    }
    Paths64 triangles;
    while(ring.size()>3) {
        bool ear=false;
        for(size_t i=0;i<ring.size();++i) {
            size_t before=(i+ring.size()-1)%ring.size(),after=(i+1)%ring.size();
            auto a=ring[before],b=ring[i],c=ring[after];
            if(orient(a,b,c)<=0)continue;
            bool contains=false;
            for(size_t j=0;j<ring.size();++j)if(j!=before&&j!=i&&j!=after) {
                auto q=ring[j];
                if(q.x<std::min({a.x,b.x,c.x})||q.x>std::max({a.x,b.x,c.x})||
                    q.y<std::min({a.y,b.y,c.y})||q.y>std::max({a.y,b.y,c.y}))continue;
                if(orient(a,b,q)>=0&&orient(b,c,q)>=0&&orient(c,a,q)>=0){contains=true;break;}
            }
            if(contains)continue;
            triangles.push_back({a,b,c});ring.erase(ring.begin()+i);ear=true;break;
        }
        if(!ear)return {};
    }
    if(ring.size()==3)triangles.push_back(ring);
    return triangles;
}
Path64 convex_difference(const Path64& a,const Path64& original_b) {
    Path64 b=original_b;for(auto& p:b){p.x=-p.x;p.y=-p.y;}
    auto lowest=[](const Path64& p){return static_cast<size_t>(std::min_element(p.begin(),p.end(),
        [](auto x,auto y){return std::tie(x.y,x.x)<std::tie(y.y,y.x);})-p.begin());};
    size_t ai=lowest(a),bi=lowest(b),i=0,j=0;Path64 out;out.reserve(a.size()+b.size());
    Point64 current(a[ai].x+b[bi].x,a[ai].y+b[bi].y);
    while(i<a.size()||j<b.size()) {
        out.push_back(current);
        Point64 ea=a[(ai+i+1)%a.size()]-a[(ai+i)%a.size()];
        Point64 eb=b[(bi+j+1)%b.size()]-b[(bi+j)%b.size()];
        int sign=orient({0,0},ea,eb);
        bool advance_a=i<a.size()&&(j==b.size()||sign>=0);
        bool advance_b=j<b.size()&&(i==a.size()||sign<=0);
        if(advance_a){current.x+=ea.x;current.y+=ea.y;++i;}
        if(advance_b){current.x+=eb.x;current.y+=eb.y;++j;}
    }
    return out;
}
struct IntegerShape {
    Paths64 material;
    Paths64 bulk;
    I min_x=0,min_y=0,max_x=0,max_y=0;
    mutable std::once_flag decompose_once;
    mutable Paths64 convex;
    explicit IntegerShape(Paths64 p,double proxy_tolerance=0):material(std::move(p)) {
        min_x=min_y=std::numeric_limits<I>::max(); max_x=max_y=std::numeric_limits<I>::min();
        for(const auto& r:material) for(auto q:r) {
            min_x=std::min(min_x,q.x);max_x=std::max(max_x,q.x);
            min_y=std::min(min_y,q.y);max_y=std::max(max_y,q.y);
        }
        for(const auto& r:material)if(twice_area(r)>0) {
            Paths64 candidate{r};
            if(proxy_tolerance>0) {
                const auto split=r.size()/2;
                Path64 one(r.begin(),r.begin()+split+1),two(r.begin()+split,r.end());two.push_back(r.front());
                one=RamerDouglasPeucker(one,proxy_tolerance);two=RamerDouglasPeucker(two,proxy_tolerance);
                one.pop_back();two.pop_back();one.insert(one.end(),two.begin(),two.end());auto simple=std::move(one);
                // Inflation has no speed benefit for already simple outlines and
                // would unnecessarily discard exact rectangle/triangle contacts.
                if(simple.size()>=3&&simple.size()+2<r.size()) {
                    auto expanded=InflatePaths(Paths64{simple},proxy_tolerance+2,JoinType::Miter,EndType::Polygon);
                    if(!expanded.empty()&&!has_area(Difference(Paths64{r},expanded,FillRule::NonZero)))candidate=std::move(expanded);
                }
            }
            bulk.insert(bulk.end(),candidate.begin(),candidate.end());
        }
    }
    const Paths64& pieces() const {
        std::call_once(decompose_once,[this] {
            if(bulk.size()==1) {
                bool is_convex=true;const auto& r=bulk.front();
                for(size_t i=0;i<r.size();++i)if(orient(r[i],r[(i+1)%r.size()],r[(i+2)%r.size()])<0){is_convex=false;break;}
                if(is_convex){convex.push_back(hull(r));return;}
                auto triangles=triangulate_simple(r);
                if(!triangles.empty()){convex=merge_convex(std::move(triangles));return;}
            }
            namespace bp=boost::polygon;
            bp::polygon_set_data<I> set;
            for(const auto& ring:bulk) {
                std::vector<bp::point_data<I>> pts;
                for(auto p:ring) pts.emplace_back(p.x,p.y);
                bp::polygon_data<I> poly; bp::set_points(poly,pts.begin(),pts.end());
                set.insert(poly,twice_area(ring)<0);
            }
            std::vector<bp::polygon_data<I>> trapezoids;
            set.get_trapezoids(trapezoids);
            for(const auto& polygon:trapezoids) {
                Path64 p;
                for(auto i=bp::begin_points(polygon);i!=bp::end_points(polygon);++i)
                    p.emplace_back(bp::x(*i),bp::y(*i));
                p=hull(std::move(p)); if(p.size()>=3) convex.push_back(std::move(p));
            }
            convex=merge_convex(std::move(convex));
        });
        return convex;
    }
};
Shape transformed(const Shape& input,double angle,bool mirror) {
    Shape result=input; double c=std::cos(angle),s=std::sin(angle);
    auto apply=[&](Ring& ring){for(auto& p:ring){double x=mirror?-p.x:p.x,y=p.y;p={c*x-s*y,s*x+c*y};}};
    apply(result.outer);for(auto& hole:result.holes)apply(hole);return result;
}
bool stopped(const Stop& stop) {return stop && stop();}
}

struct Geometry::Impl {
    Config config;
    I width,height;
    double source_width,source_height;
    std::vector<Variant> variants;
    std::vector<std::vector<int>> by_part;
    std::vector<std::shared_ptr<IntegerShape>> shapes; // variants followed by stock defects
    std::mutex mutex;
    std::map<std::pair<int,int>,std::shared_ptr<const Paths64>> cache;
    std::atomic<uint64_t> hits{0},misses{0};
    Impl(const Problem& p,const Config& c):config(c),width(quantize(p.width,c.scale)),height(quantize(p.height,c.scale)),source_width(p.width),source_height(p.height) {
        if(!std::isfinite(c.scale)||c.scale<=0||width<=0||height<=0||c.rotations<1)
            throw std::invalid_argument("invalid geometry scale, stock, or rotations");
        by_part.resize(p.parts.size());
        for(size_t part=0;part<p.parts.size();++part) {
            std::vector<double> angles = part<p.angles.size()?p.angles[part]:std::vector<double>{};
            if(angles.empty()) {
                constexpr double tau=6.2831853071795864769;
                for(int i=0;i<c.rotations;++i)angles.push_back(tau*i/c.rotations);
                if(c.smart_orientations) {
                    auto outer=hull(path(p.parts[part].outer,c.scale));
                    std::vector<std::pair<long double,double>> edges;
                    for(size_t i=0;i<outer.size();++i) {
                        auto d=outer[(i+1)%outer.size()]-outer[i];
                        edges.push_back({static_cast<long double>(d.x)*d.x+static_cast<long double>(d.y)*d.y,
                            -std::atan2(static_cast<double>(d.y),static_cast<double>(d.x))});
                    }
                    std::sort(edges.begin(),edges.end(),[](auto a,auto b){return a.first>b.first;});
                    std::vector<double> directions;
                    for(auto edge:edges) {
                        double a=std::remainder(edge.second,tau/4);
                        if(std::any_of(directions.begin(),directions.end(),[&](double b){return std::abs(std::remainder(a-b,tau/4))<1e-6;}))continue;
                        directions.push_back(a);
                        for(int turn=0;turn<4;++turn) {
                            double angle=std::fmod(a+turn*tau/4+tau,tau);
                            if(!std::any_of(angles.begin(),angles.end(),[&](double b){return std::abs(std::remainder(angle-b,tau))<1e-6;}))angles.push_back(angle);
                        }
                        if(directions.size()>=4)break;
                    }
                }
            }
            for(int mirrored=0;mirrored<((part<p.mirror.size()&&p.mirror[part])?2:1);++mirrored)
                for(double a:angles) {
                    if(!std::isfinite(a))throw std::invalid_argument("nonfinite rotation");
                    Variant v;v.part=static_cast<int>(part);v.angle=a;v.mirrored=mirrored!=0;
                    v.shape=transformed(p.parts[part],a,v.mirrored);
                    std::shared_ptr<IntegerShape> shape;
                    try {shape=std::make_shared<IntegerShape>(material(v.shape,c.scale),
                        c.conservative_proxies?std::min(p.width,p.height)*5e-4*c.scale:0);}
                    catch(const std::exception& e){throw std::invalid_argument("part "+std::to_string(part)+
                        " angle "+std::to_string(a)+" mirror "+std::to_string(mirrored)+": "+e.what());}
                    v.bounds={v.shape.outer.front().x,v.shape.outer.front().y,v.shape.outer.front().x,v.shape.outer.front().y};
                    for(auto q:v.shape.outer){v.bounds.min_x=std::min(v.bounds.min_x,q.x);v.bounds.max_x=std::max(v.bounds.max_x,q.x);
                        v.bounds.min_y=std::min(v.bounds.min_y,q.y);v.bounds.max_y=std::max(v.bounds.max_y,q.y);}
                    long double area=0;for(const auto& r:shape->material)area+=twice_area(r);
                    v.area=static_cast<double>(area/(2*c.scale*c.scale));
                    by_part[part].push_back(static_cast<int>(variants.size()));
                    variants.push_back(std::move(v));shapes.push_back(std::move(shape));
                }
        }
        for(const auto& d:p.defects)shapes.push_back(std::make_shared<IntegerShape>(material(d,c.scale),
            c.conservative_proxies?std::min(p.width,p.height)*5e-4*c.scale:0));
    }
    std::shared_ptr<const Paths64> nfp(int fixed,int moving,const Stop& stop) {
        auto key=std::make_pair(fixed,moving);
        {std::lock_guard<std::mutex> lock(mutex);auto i=cache.find(key);if(i!=cache.end()){++hits;return i->second;}
            auto reverse=cache.find({moving,fixed});
            if(reverse!=cache.end()) {
                auto reflected=std::make_shared<Paths64>(*reverse->second);
                for(auto& ring:*reflected)for(auto& p:ring){p.x=-p.x;p.y=-p.y;}
                cache.emplace(key,reflected);++hits;return reflected;
            }
        }
        if(stopped(stop))return {};
        const auto& a=shapes.at(fixed)->pieces();if(stopped(stop))return {};
        const auto& b=shapes.at(moving)->pieces();
        Paths64 sums;
        // Bulk outer convolution is conservative for holed shapes. Separate cavity
        // contacts below bypass this pruning and use the unchanged full material validator.
        for(const auto& x:a) {
            Paths64 row;
            for(const auto& y:b) {
                if(stopped(stop))return {};
                Path64 sum=convex_difference(x,y);if(sum.size()>=3)row.push_back(std::move(sum));
            }
            row=Union(row,FillRule::NonZero);
            sums.insert(sums.end(),std::make_move_iterator(row.begin()),std::make_move_iterator(row.end()));
        }
        auto result=std::make_shared<const Paths64>(Union(sums,FillRule::NonZero));
        if(stopped(stop))return {};
        {std::lock_guard<std::mutex> lock(mutex);auto inserted=cache.emplace(key,result);if(inserted.second)++misses;else{++hits;result=inserted.first->second;}}
        return result;
    }
    bool valid(int moving,I x,I y,const std::vector<Placement>& placed,int sheet) const {
        if(moving<0||static_cast<size_t>(moving)>=variants.size())return false;
        const auto& shape=*shapes[moving];
        const auto& bounds=variants[moving].bounds;
        const double dx=x/config.scale,dy=y/config.scale;
        if(bounds.min_x+dx < -1e-10||bounds.min_y+dy < -1e-10||
           bounds.max_x+dx>source_width+1e-10||bounds.max_y+dy>source_height+1e-10)return false;
        if(shape.min_x+x<0||shape.min_y+y<0||shape.max_x+x>width||shape.max_y+y>height)return false;
        Paths64 candidate=translate(shape.material,x,y);
        auto collides=[&](int other,I ox,I oy) {
            const auto& obstacle=*shapes.at(other);
            if(shape.max_x+x<=obstacle.min_x+ox||shape.min_x+x>=obstacle.max_x+ox||
               shape.max_y+y<=obstacle.min_y+oy||shape.min_y+y>=obstacle.max_y+oy)return false;
            return has_area(Intersect(candidate,translate(obstacle.material,ox,oy),FillRule::NonZero));
        };
        for(const auto& p:placed)if(p.sheet==sheet&&collides(p.variant,quantize(p.x,config.scale),quantize(p.y,config.scale)))return false;
        for(size_t d=variants.size();d<shapes.size();++d)if(collides(static_cast<int>(d),0,0))return false;
        return true;
    }
};

Geometry::Geometry(const Problem& p,const Config& c):impl_(std::make_unique<Impl>(p,c)){}
Geometry::~Geometry()=default;
const std::vector<Variant>& Geometry::variants()const{return impl_->variants;}
const std::vector<int>& Geometry::variants_for(int part)const{return impl_->by_part.at(part);}
GeometryStats Geometry::stats()const{return {impl_->hits.load(),impl_->misses.load()};}
bool Geometry::valid(int moving,Point p,const std::vector<Placement>& placed,int sheet)const {
    return impl_->valid(moving,quantize(p.x,impl_->config.scale),quantize(p.y,impl_->config.scale),placed,sheet);
}

std::vector<Point> Geometry::positions(int moving,const std::vector<Placement>& placed,int sheet,int direction,int limit,const Stop& stop) {
    auto& g=*impl_;
    if(limit<=0||stopped(stop))return {};
    const auto& shape=*g.shapes.at(moving);
    const auto& bounds=g.variants.at(moving).bounds;
    const I left=std::max(-shape.min_x,static_cast<I>(std::ceil(-bounds.min_x*g.config.scale-1e-5))),
        right=std::min(g.width-shape.max_x,static_cast<I>(std::floor((g.source_width-bounds.max_x)*g.config.scale+1e-5))),
        bottom=std::max(-shape.min_y,static_cast<I>(std::ceil(-bounds.min_y*g.config.scale-1e-5))),
        top=std::min(g.height-shape.max_y,static_cast<I>(std::floor((g.source_height-bounds.max_y)*g.config.scale+1e-5)));
    if(left>right||bottom>top)return {};
    Paths64 forbidden;
    std::vector<std::pair<int,Point64>> obstacles;
    for(const auto& p:placed)if(p.sheet==sheet)obstacles.push_back({p.variant,{quantize(p.x,g.config.scale),quantize(p.y,g.config.scale)}});
    for(size_t d=g.variants.size();d<g.shapes.size();++d)obstacles.push_back({static_cast<int>(d),{0,0}});
    for(auto obstacle:obstacles) {
        auto pair=g.nfp(obstacle.first,moving,stop);if(!pair)return {};
        auto shifted=translate(*pair,obstacle.second.x,obstacle.second.y);
        forbidden.insert(forbidden.end(),shifted.begin(),shifted.end());
    }
    if(stopped(stop))return {};
    // Collapse overlapping obstacles once. This makes the later point-location
    // rejection cheap and keeps internal obstacle vertices out of the proposal set.
    forbidden=Union(forbidden,FillRule::NonZero);
    if(stopped(stop))return {};
    Paths64 domain{{{left,bottom},{right,bottom},{right,top},{left,top}}};
    Paths64 feasible=Difference(domain,forbidden,FillRule::NonZero);
    std::set<std::pair<I,I>> candidates;
    auto add=[&](I x,I y){if(x>=left&&x<=right&&y>=bottom&&y<=top)candidates.emplace(x,y);};
    add(left,bottom);add(right,bottom);add(left,top);add(right,top);
    // Closed-set contacts survive even when feasible polygon area is zero.
    for(const auto* paths:{&feasible,&forbidden})for(const auto& ring:*paths)for(auto v:ring)add(v.x,v.y);
    std::set<I> rows{bottom,top};
    for(const auto& ring:feasible)for(auto p:ring)if(p.y>=bottom&&p.y<=top)rows.insert(p.y);
    for(const auto& ring:forbidden)for(auto p:ring)if(p.y>=bottom&&p.y<=top)rows.insert(p.y);
    I step=std::max<I>(1,quantize(std::max(g.config.scan_step,1/g.config.scale),g.config.scale));
    // Bound scan work independently of coordinate scale; critical boundary rows remain included.
    step=std::max<I>(step,(top-bottom)/2048+1);
    for(I y=bottom;y<top;y+=step)rows.insert(y);
    size_t visited=0;
    for(I y:rows) {
        if(stopped(stop))return {};
        if(++visited>8192)break;
        add(left,y);add(right,y);
        for(const auto* paths:{&feasible,&forbidden})for(const auto& ring:*paths) {
            if(stopped(stop))return {};
            for(size_t i=0,j=ring.size()-1;i<ring.size();j=i++) {
                auto a=ring[j],b=ring[i];
                if(a.y==b.y) {if(y==a.y){add(a.x,y);add(b.x,y);}continue;}
                if(y<std::min(a.y,b.y)||y>std::max(a.y,b.y))continue;
                long double x=a.x+static_cast<long double>(y-a.y)*(b.x-a.x)/(b.y-a.y);
                add(static_cast<I>(std::floor(x)),y);add(static_cast<I>(std::ceil(x)),y);
            }
        }
    }
    // Include alignment with material-hole vertices for zero-width cavities that
    // boolean polygon output cannot represent. Every proposal is independently checked.
    std::set<std::pair<I,I>> cavity_candidates;
    auto add_cavity=[&](I x,I y){if(x>=left&&x<=right&&y>=bottom&&y<=top){cavity_candidates.emplace(x,y);add(x,y);}};
    auto fits_box=[](const Path64& ring,I w,I h) {
        I lx=ring.front().x,rx=lx,by=ring.front().y,ty=by;
        for(auto p:ring){lx=std::min(lx,p.x);rx=std::max(rx,p.x);by=std::min(by,p.y);ty=std::max(ty,p.y);}
        return w<=rx-lx&&h<=ty-by;
    };
    auto ring_box=[](const Path64& ring){std::array<I,4> box{ring.front().x,ring.front().y,ring.front().x,ring.front().y};
        for(auto p:ring){box[0]=std::min(box[0],p.x);box[1]=std::min(box[1],p.y);box[2]=std::max(box[2],p.x);box[3]=std::max(box[3],p.y);}return box;};
    struct CavityTask {const Path64* fixed;const Path64* moving;Point64 offset;I left,bottom,right,top;};
    std::vector<CavityTask> cavity_tasks;
    for(auto obstacle:obstacles) {
        if(stopped(stop))return {};
        for(const auto& r:g.shapes[obstacle.first]->material)if(twice_area(r)<0) {
            if(!fits_box(r,shape.max_x-shape.min_x,shape.max_y-shape.min_y))continue;
            auto box=ring_box(r);
            for(const auto& m:shape.material)if(twice_area(m)>0)cavity_tasks.push_back({&r,&m,obstacle.second,
                box[0]+obstacle.second.x-shape.min_x,box[1]+obstacle.second.y-shape.min_y,
                box[2]+obstacle.second.x-shape.max_x,box[3]+obstacle.second.y-shape.max_y});
        }
    }
    for(auto obstacle:obstacles) {
        if(stopped(stop))return {};
        const auto& fixed=*g.shapes[obstacle.first];
        for(const auto& cavity:shape.material)if(twice_area(cavity)<0) {
            if(!fits_box(cavity,fixed.max_x-fixed.min_x,fixed.max_y-fixed.min_y))continue;
            auto box=ring_box(cavity);
            for(const auto& r:fixed.material)if(twice_area(r)>0)cavity_tasks.push_back({&r,&cavity,obstacle.second,
                fixed.max_x+obstacle.second.x-box[2],fixed.max_y+obstacle.second.y-box[3],
                fixed.min_x+obstacle.second.x-box[0],fixed.min_y+obstacle.second.y-box[1]});
        }
    }
    // Give every eligible cavity a share before sampling more vertices from any
    // one cavity. Uniform pair-index sampling avoids starving late contour edges.
    const size_t total_budget=32768;
    for(size_t task=0;task<cavity_tasks.size();++task) {
        if(stopped(stop))return {};
        const auto& t=cavity_tasks[task];
        auto add_task=[&](I x,I y){if(x>=t.left&&x<=t.right&&y>=t.bottom&&y<=t.top)add_cavity(x,y);};
        auto center=[](const Path64& ring){I lx=ring.front().x,rx=lx,by=ring.front().y,ty=by;
            for(auto p:ring){lx=std::min(lx,p.x);rx=std::max(rx,p.x);by=std::min(by,p.y);ty=std::max(ty,p.y);}return Point64((lx+rx)/2,(by+ty)/2);};
        auto fc=center(*t.fixed),mc=center(*t.moving);
        add_task(fc.x+t.offset.x-mc.x,fc.y+t.offset.y-mc.y);
        size_t budget=total_budget/cavity_tasks.size()+(task<total_budget%cavity_tasks.size()?1:0);
        const size_t combinations=t.fixed->size()*t.moving->size();
        budget=std::min(budget,combinations);
        for(size_t i=0;i<budget;++i) {
            size_t index=static_cast<size_t>(static_cast<long double>(i)*combinations/budget);
            auto a=(*t.fixed)[index/t.moving->size()],b=(*t.moving)[index%t.moving->size()];
            add_task(a.x+t.offset.x-b.x,a.y+t.offset.y-b.y);
        }
    }
    std::vector<std::pair<I,I>> ordered(candidates.begin(),candidates.end());
    direction=((direction%4)+4)%4;
    std::sort(ordered.begin(),ordered.end(),[direction](auto a,auto b) {
        auto key=[direction](auto p){return std::make_pair(direction&2?-p.second:p.second,direction&1?-p.first:p.first);};
        return key(a)<key(b);
    });
    using Key=std::pair<I,I>;
    std::map<Key,bool> validity;
    auto valid_candidate=[&](Key p) {
        auto known=validity.find(p);if(known!=validity.end())return known->second;
        bool allowed=true;
        if(!cavity_candidates.count(p)) {
            int winding=0;bool boundary=false;
            for(const auto& ring:forbidden) {
                auto location=PointInPolygon(Point64(p.first,p.second),ring);
                if(location==PointInPolygonResult::IsOn){boundary=true;break;}
                if(location==PointInPolygonResult::IsInside)winding+=twice_area(ring)>0?1:-1;
            }
            if(!boundary&&winding!=0)allowed=false;
        }
        bool valid=allowed&&g.valid(moving,p.first,p.second,placed,sheet);
        validity.emplace(p,valid);return valid;
    };
    std::vector<Point> result;
    std::vector<Key> selected;
    auto accept=[&](Key p){selected.push_back(p);result.push_back({p.first/g.config.scale,p.second/g.config.scale});};
    // The first candidate (and limit=1 behavior) remains the directional scan.
    for(auto p:ordered) {
        if(stopped(stop))return result;
        if(valid_candidate(p)){accept(p);break;}
    }
    if(result.empty()||limit==1)return result;
    std::vector<Key> columns=ordered,envelope=ordered;
    std::stable_sort(columns.begin(),columns.end(),[direction](Key a,Key b){
        auto key=[direction](Key p){return std::make_pair(direction&1?-p.first:p.first,direction&2?-p.second:p.second);};
        return key(a)<key(b);
    });
    long double existing_x=0,existing_y=0;
    for(const auto& p:placed)if(p.sheet==sheet) {
        existing_x=std::max(existing_x,static_cast<long double>((p.x+g.variants[p.variant].bounds.max_x)*g.config.scale));
        existing_y=std::max(existing_y,static_cast<long double>((p.y+g.variants[p.variant].bounds.max_y)*g.config.scale));
    }
    auto envelope_area=[&](Key p){return std::max(existing_x,static_cast<long double>(p.first)+bounds.max_x*g.config.scale)*
        std::max(existing_y,static_cast<long double>(p.second)+bounds.max_y*g.config.scale);};
    std::stable_sort(envelope.begin(),envelope.end(),[&](Key a,Key b){return envelope_area(a)<envelope_area(b);});
    const long double separation=std::max<long double>(1,std::min(shape.max_x-shape.min_x,shape.max_y-shape.min_y)*.05L);
    auto distance=[&](Key p){long double closest=std::numeric_limits<long double>::max();
        for(auto q:selected){long double x=p.first-q.first,y=p.second-q.second;closest=std::min(closest,x*x+y*y);}return closest;};
    for(int slot=1;slot<limit&&!stopped(stop);++slot) {
        std::vector<Key> farther;
        const std::vector<Key>* policy=&ordered;
        switch(slot%4) {
            case 1:policy=&columns;break;
            case 2:policy=&envelope;break;
            case 3:farther=ordered;std::stable_sort(farther.begin(),farther.end(),[&](Key a,Key b){return distance(a)>distance(b);});policy=&farther;break;
        }
        bool found=false;
        for(auto p:*policy) {
            if(stopped(stop))return result;
            if(std::find(selected.begin(),selected.end(),p)!=selected.end()||distance(p)<separation*separation)continue;
            if(valid_candidate(p)){accept(p);found=true;break;}
        }
        if(!found) {
            // Spatial diversity is a preference, never a reason to lose narrow fits.
            for(auto p:*policy) {
                if(stopped(stop))return result;
                if(std::find(selected.begin(),selected.end(),p)==selected.end()&&valid_candidate(p)){accept(p);found=true;break;}
            }
        }
        if(!found)break;
    }
    return result;
}
}
