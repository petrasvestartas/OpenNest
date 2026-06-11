#pragma once
// Seeded synthetic part-set generators + a tiny text loader for the nfp_bench
// research CLI. Header-only, no deps beyond the standard library.
//
// All generators are deterministic in (name, seed): same inputs -> same parts,
// so every engine change can be A/B-ed on identical data.

#include <cmath>
#include <cstdint>
#include <fstream>
#include <random>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

namespace bench {

struct BenchPart {
    std::vector<double> xy;                 // outer loop, x0 y0 x1 y1 ...
    std::vector<std::vector<double>> holes; // hole loops, same layout
    int qty = 1;
};

struct BenchData {
    std::vector<BenchPart> parts;
    double sheetW = 2000, sheetH = 1000;
    std::string name;
};

inline std::mt19937& rng(uint32_t seed) {
    static thread_local std::mt19937 g;
    g.seed(seed);
    return g;
}

inline double frand(std::mt19937& g, double lo, double hi) {
    return std::uniform_real_distribution<double>(lo, hi)(g);
}
inline int irand(std::mt19937& g, int lo, int hi) {
    return std::uniform_int_distribution<int>(lo, hi)(g);
}

inline std::vector<double> rectXY(double w, double h) {
    return {0, 0, w, 0, w, h, 0, h};
}

// L-shape: outer w x h with a (w-t) x (h-t) corner bite.
inline std::vector<double> lShape(double w, double h, double t) {
    return {0, 0, w, 0, w, t, t, t, t, h, 0, h};
}

// T-shape: stem width s centered under a full-width cap of thickness t.
inline std::vector<double> tShape(double w, double h, double t, double s) {
    double a = (w - s) / 2;
    return {a, 0, a + s, 0, a + s, h - t, w, h - t, w, h, 0, h, 0, h - t, a, h - t};
}

// U-shape: w x h with a centered (w-2t) x (h-t) slot from the top.
inline std::vector<double> uShape(double w, double h, double t) {
    return {0, 0, w, 0, w, h, w - t, h, w - t, t, t, t, t, h, 0, h};
}

inline std::vector<double> triangle(double w, double h) {
    return {0, 0, w, 0, w * 0.5, h};
}

inline std::vector<double> trapezoid(double wBot, double wTop, double h) {
    double a = (wBot - wTop) / 2;
    return {0, 0, wBot, 0, wBot - a, h, a, h};
}

// n-pointed star (concave), outer radius R, inner radius r.
inline std::vector<double> star(int n, double R, double r) {
    std::vector<double> p;
    for (int i = 0; i < 2 * n; i++) {
        double rad = (i % 2 == 0) ? R : r;
        double a = M_PI * i / n;
        p.push_back(R + rad * std::cos(a));
        p.push_back(R + rad * std::sin(a));
    }
    return p;
}

// Regular n-gon centered at (cx,cy), used for circular-ish holes.
inline std::vector<double> ngon(int n, double cx, double cy, double r) {
    std::vector<double> p;
    for (int i = 0; i < n; i++) {
        double a = 2 * M_PI * i / n;
        p.push_back(cx + r * std::cos(a));
        p.push_back(cy + r * std::sin(a));
    }
    return p;
}

// --- datasets ---------------------------------------------------------------

// 30 rectangle instances, 3 size classes. Sanity/speed baseline.
inline BenchData makeRects(uint32_t seed) {
    BenchData d;
    d.name = "rects";
    auto& g = rng(seed);
    struct Cls { double wLo, wHi, hLo, hHi; int defs; };
    Cls classes[3] = {{260, 420, 160, 260, 3}, {140, 240, 90, 160, 4}, {60, 120, 40, 90, 3}};
    for (auto& c : classes) {
        for (int i = 0; i < c.defs; i++) {
            BenchPart p;
            p.xy = rectXY(frand(g, c.wLo, c.wHi), frand(g, c.hLo, c.hHi));
            p.qty = irand(g, 2, 4);
            d.parts.push_back(p);
        }
    }
    return d;
}

// ~40 concave/mixed instances: L/T/U, triangles, trapezoids, one star.
// The main density benchmark.
inline BenchData makeConcave(uint32_t seed) {
    BenchData d;
    d.name = "concave";
    auto& g = rng(seed);
    for (int i = 0; i < 4; i++) {
        BenchPart p;
        double w = frand(g, 180, 340), h = frand(g, 150, 300);
        p.xy = lShape(w, h, frand(g, 0.25, 0.45) * std::min(w, h));
        p.qty = irand(g, 2, 3);
        d.parts.push_back(p);
    }
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        double w = frand(g, 170, 300), h = frand(g, 150, 280);
        p.xy = tShape(w, h, frand(g, 0.25, 0.4) * h, frand(g, 0.25, 0.4) * w);
        p.qty = irand(g, 2, 3);
        d.parts.push_back(p);
    }
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        double w = frand(g, 170, 320), h = frand(g, 140, 260);
        p.xy = uShape(w, h, frand(g, 0.22, 0.35) * std::min(w, h));
        p.qty = irand(g, 1, 3);
        d.parts.push_back(p);
    }
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        p.xy = triangle(frand(g, 120, 260), frand(g, 100, 220));
        p.qty = irand(g, 2, 4);
        d.parts.push_back(p);
    }
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        double wb = frand(g, 140, 280);
        p.xy = trapezoid(wb, frand(g, 0.4, 0.7) * wb, frand(g, 90, 180));
        p.qty = irand(g, 1, 3);
        d.parts.push_back(p);
    }
    {
        BenchPart p;
        p.xy = star(5, frand(g, 90, 140), frand(g, 40, 60));
        p.qty = 2;
        d.parts.push_back(p);
    }
    return d;
}

// 6 large parts with holes + 20 small parts that fit inside the holes.
// The hole-fill (Q2) benchmark; baseline utilization is expected to be poor.
inline BenchData makeRings(uint32_t seed) {
    BenchData d;
    d.name = "rings";
    auto& g = rng(seed);
    // 3 rectangular frames
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        double w = frand(g, 320, 430), h = frand(g, 260, 360);
        double m = frand(g, 55, 75); // wall thickness
        p.xy = rectXY(w, h);
        p.holes.push_back({m, m, w - m, m, w - m, h - m, m, h - m});
        p.qty = 1;
        d.parts.push_back(p);
    }
    // 3 ring-ish parts (square with circular hole)
    for (int i = 0; i < 3; i++) {
        BenchPart p;
        double w = frand(g, 280, 380);
        p.xy = rectXY(w, w);
        p.holes.push_back(ngon(24, w / 2, w / 2, frand(g, 0.3, 0.38) * w));
        p.qty = 1;
        d.parts.push_back(p);
    }
    // 20 small fillers (rects + triangles) sized to fit inside the holes
    for (int i = 0; i < 6; i++) {
        BenchPart p;
        p.xy = rectXY(frand(g, 60, 120), frand(g, 45, 90));
        p.qty = 2;
        d.parts.push_back(p);
    }
    for (int i = 0; i < 4; i++) {
        BenchPart p;
        p.xy = triangle(frand(g, 70, 130), frand(g, 60, 110));
        p.qty = 2;
        d.parts.push_back(p);
    }
    return d;
}

// Text format:
//   # comment / blank lines between sections
//   sheet <W> <H>            (optional, once)
//   part <qty>
//   x y                      (outer loop, one vertex per line)
//   <blank>
//   x y ...                  (optional further loops = holes)
// Each "part" runs until the next "part"/"sheet" keyword or EOF.
inline BenchData loadFile(const std::string& path) {
    std::ifstream f(path);
    if (!f) throw std::runtime_error("cannot open dataset file: " + path);
    BenchData d;
    d.name = path;
    std::string line;
    BenchPart* cur = nullptr;
    std::vector<double>* loop = nullptr;
    bool first = true;
    while (std::getline(f, line)) {
        if (first) { // strip UTF-8 BOM
            first = false;
            if (line.size() >= 3 && (unsigned char)line[0] == 0xEF &&
                (unsigned char)line[1] == 0xBB && (unsigned char)line[2] == 0xBF)
                line.erase(0, 3);
        }
        if (!line.empty() && line.back() == '\r') line.pop_back();
        std::istringstream ss(line);
        std::string tok;
        if (!(ss >> tok)) { loop = nullptr; continue; }   // blank line: close loop
        if (tok[0] == '#') continue;
        if (tok == "sheet") { ss >> d.sheetW >> d.sheetH; cur = nullptr; loop = nullptr; continue; }
        if (tok == "part") {
            d.parts.emplace_back();
            cur = &d.parts.back();
            if (!(ss >> cur->qty)) cur->qty = 1;
            loop = nullptr;
            continue;
        }
        if (!cur) throw std::runtime_error("vertex line before any 'part': " + line);
        double x = std::stod(tok), y;
        if (!(ss >> y)) throw std::runtime_error("bad vertex line: " + line);
        if (!loop) {
            if (cur->xy.empty()) loop = &cur->xy;
            else { cur->holes.emplace_back(); loop = &cur->holes.back(); }
        }
        loop->push_back(x);
        loop->push_back(y);
    }
    if (d.parts.empty()) throw std::runtime_error("no parts in dataset file: " + path);
    return d;
}

inline BenchData makeDataset(const std::string& name, uint32_t seed) {
    if (name == "rects")   return makeRects(seed);
    if (name == "concave") return makeConcave(seed);
    if (name == "rings")   return makeRings(seed);
    if (name.rfind("file:", 0) == 0) return loadFile(name.substr(5));
    throw std::runtime_error("unknown dataset: " + name +
                             " (use rects|concave|rings|file:<path>)");
}

} // namespace bench
