#include "constructive.hpp"
#include <algorithm>
#include <cmath>
#include <iostream>
#include <stdexcept>
#include <thread>
using namespace constructive;
static void require(bool b,const char* what){if(!b)throw std::runtime_error(what);}
static Ring rect(double x,double y,double w,double h){return {{x,y},{x+w,y},{x+w,y+h},{x,y+h}};}
static bool near(Point a,double x,double y){return std::abs(a.x-x)<1e-7&&std::abs(a.y-y)<1e-7;}
int main(){try {
    Config c;c.rotations=1;c.scan_step=1;c.smart_orientations=false;c.conservative_proxies=false;
    Problem p;p.width=p.height=10;p.parts={Shape{rect(-2,3,4,2),{}}};
    Geometry g(p,c);auto v=g.positions(0,{},0,0,3,[] {return false;});
    require(!v.empty()&&near(v[0],2,-3),"arbitrary local origin not preserved");
    require(g.valid(0,{2,-3},{},0),"stock boundary contact should be valid");
    require(!g.valid(0,{1.999,-3},{},0),"stock excursion accepted");
    Placement a{0,0,0,0,2,-3};
    require(g.valid(0,{6,-3},{a},0),"material edge contact rejected");
    require(!g.valid(0,{5.99,-3},{a},0),"overlap accepted");
    require(g.valid(0,{2,-3},{a},1),"another sheet incorrectly obstructs");
    require(g.positions(0,{},0,0,2,[]{return true;}).empty(),"deadline ignored");

    Problem h;h.width=h.height=10;
    h.parts={Shape{rect(0,0,10,10),{rect(3,3,4,4)}},Shape{rect(0,0,4,4),{}}};
    Geometry holes(h,c);Placement frame{0,0,0,0,0,0};
    require(holes.valid(1,{3,3},{frame},0),"fixed material hole not preserved");
    require(!holes.valid(1,{2.99,3},{frame},0),"frame material overlap accepted");
    auto inside=holes.positions(1,{frame},0,0,4,[]{return false;});
    require(!inside.empty()&&near(inside.front(),3,3),"exact-fit hole contact not generated");
    Placement inner{1,0,0,1,3,3};
    require(holes.valid(0,{0,0},{inner},0),"moving hole cannot surround fixed part");
    auto around=holes.positions(0,{inner},0,0,4,[]{return false;});
    require(!around.empty()&&near(around.front(),0,0),"moving holed material NFP is filled");

    Problem d;d.width=d.height=10;d.parts={Shape{rect(0,0,2,2),{}}};
    d.defects={Shape{rect(0,0,5,5),{}}};Geometry defects(d,c);
    require(!defects.valid(0,{0,0},{},0),"stock defect ignored");
    auto dp=defects.positions(0,{},0,0,1,[]{return false;});
    require(!dp.empty()&&near(dp[0],5,0),"defect boundary contact missing");

    Problem n;n.width=n.height=6;n.parts={Shape{{{0,0},{6,0},{6,2},{2,2},{2,6},{0,6}},{}},Shape{rect(0,0,4,4),{}}};
    Geometry concave(n,c);Placement el{0,0,0,0,0,0};
    auto cp=concave.positions(1,{el},0,0,1,[]{return false;});
    require(!cp.empty()&&near(cp[0],2,2),"concavity treated as convex hull");

    Problem r;r.width=r.height=20;r.parts={Shape{rect(2,3,4,1),{}}};
    r.angles={{0,1.57079632679489661923}};r.mirror={true};Geometry rotation(r,c);
    require(rotation.variants_for(0).size()==4,"rotation/mirror variants missing");
    require(std::abs(rotation.variants()[1].shape.outer[0].x+3)<1e-9,"rotation did not preserve origin");
    require(rotation.variants()[2].mirrored&&rotation.variants()[2].shape.outer[0].x==-2,"reflection convention wrong");

    std::thread t1([&]{for(int i=0;i<3;++i)holes.positions(1,{frame},0,0,1,[]{return false;});});
    std::thread t2([&]{for(int i=0;i<3;++i)holes.positions(1,{frame},0,0,1,[]{return false;});});
    t1.join();t2.join();require(holes.stats().hits>=5,"shared NFP cache not reused");
    require(holes.stats().misses==1,"reverse reflected NFP did not reuse cached pair");
    bool rejected=false;try{Problem bad=p;bad.parts[0].outer[0].x=1e20;Geometry invalid(bad,c);}catch(const std::invalid_argument&){rejected=true;}
    require(rejected,"overflow input not rejected");
    auto invalid_shape=[&](Shape shape){bool bad=false;try{Problem q=p;q.parts={shape};Geometry rejected_shape(q,c);}catch(const std::invalid_argument&){bad=true;}return bad;};
    require(invalid_shape(Shape{{{0,0},{4,4},{0,5},{5,0}}, {}}),"self-crossing ring accepted");
    require(invalid_shape(Shape{rect(0,0,5,5),{rect(4,4,2,2)}}),"outside hole accepted");
    require(invalid_shape(Shape{rect(0,0,10,10),{rect(1,1,4,4),rect(2,2,4,4)}}),"overlapping holes accepted");
    Problem tiny;tiny.width=tiny.height=10;tiny.parts={Shape{rect(0,0,5,5),{rect(1,1,1,0.0000001)}},Shape{rect(0,0,0.1,0.1),{}}};
    Geometry filled(tiny,c);require(!filled.valid(1,{1,1},{Placement{0,0,0,0,0,0}},0),"collapsed hole did not conservatively fill");
    Problem too_wide;too_wide.width=4;too_wide.height=2;too_wide.parts={Shape{rect(0,0,4.000004,2),{}}};
    Geometry original_bounds(too_wide,c);
    require(!original_bounds.valid(0,{0,0},{},0)&&original_bounds.positions(0,{},0,0,1,[]{return false;}).empty(),"rounded outer protrusion accepted");
    Config fast=c;fast.conservative_proxies=true;Geometry fast_holes(h,fast);
    require(!fast_holes.positions(1,{frame},0,0,1,[]{return false;}).empty(),"proxy removed separate hole placement");
    Config smart=c;smart.smart_orientations=true;Geometry explicit_angles(r,smart);
    require(explicit_angles.variants_for(0).size()==4,"smart mode changed explicit angles");
    Problem diverse;diverse.width=diverse.height=10;diverse.parts={Shape{rect(0,0,2,2),{}}};
    Geometry alternatives(diverse,c);auto one=alternatives.positions(0,{},0,0,1,[]{return false;});
    auto four=alternatives.positions(0,{},0,0,4,[]{return false;});
    require(four.size()==4&&near(four.front(),one.front().x,one.front().y),"diversity changed first directional choice");
    require(std::any_of(four.begin(),four.end(),[](Point p){return p.x>4&&p.y>4;}),"diversity did not retain distant contact");
    Problem narrow; narrow.width=narrow.height=102;narrow.parts={Shape{rect(0,0,100,100),{}}};
    Geometry close_contacts(narrow,c);auto close=close_contacts.positions(0,{},0,0,4,[]{return false;});
    require(close.size()==4,"spatial diversity lost narrow-fit backfill candidates");
    for(auto q:close)require(close_contacts.valid(0,q,{},0),"diverse contact invalid");
    auto circle=[](double x,double y,double radius,int count){Ring r;for(int i=0;i<count;++i){double a=6.283185307179586*i/count;r.push_back({x+radius*std::cos(a),y+radius*std::sin(a)});}return r;};
    Problem cavities;cavities.width=cavities.height=100;
    cavities.parts={Shape{rect(0,0,100,100),{circle(20,20,10,1000),circle(80,80,10,1000)}},Shape{circle(0,0,5,80),{}}};
    Geometry fair_cavities(cavities,c);Placement solid_frame{0,0,0,0,0,0};
    auto low=fair_cavities.positions(1,{solid_frame},0,0,1,[]{return false;});
    auto high=fair_cavities.positions(1,{solid_frame},0,3,1,[]{return false;});
    require(!low.empty()&&!high.empty()&&low[0].x<40&&high[0].x>60,"complex first cavity starved another eligible hole");
    std::cout<<"geometry tests passed\n";return 0;
}catch(const std::exception& e){std::cerr<<e.what()<<"\n";return 1;}}
