// Optimize-based 2D nester (100% C++ port; NO Rust executed).
//
// Parses the 47 parts from sample_polygons.svg and packs them into a 510x635 sheet using
// nest's FULL optimize() pipeline (InitialPlacer -> exploration_phase -> compression_phase),
// not the greedy single-pass placement of main_nest.cpp.
//
// The shared nesting machinery (build_part / run_strip / greedy_fill) lives in solver/driver.hpp
// so the C-ABI shared library (nest_physics_capi.cpp) drives the solver identically. This file is
// just the CLI: parse geometry -> greedy sequential bin fill -> SVG.
#include "solver/driver.hpp"
#include <cstdio>
#include <cstdlib>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <algorithm>
#include <optional>
#include <limits>
#include <cmath>

using namespace nest;

// ---- minimal SVG <polyline points="..."> parser (from main_nest.cpp) ----
static std::vector<std::vector<Point>> parse_polylines(const std::string& path) {
    std::ifstream f(path);
    std::stringstream ss; ss << f.rdbuf();
    std::string txt = ss.str();
    std::vector<std::vector<Point>> out;
    const std::string key = "points=\"";
    size_t pos = 0;
    while ((pos = txt.find(key, pos)) != std::string::npos) {
        pos += key.size();
        size_t end = txt.find('"', pos);
        std::string body = txt.substr(pos, end - pos);
        pos = end + 1;
        std::vector<Point> pts;
        std::stringstream bs(body);
        std::string tok;
        while (bs >> tok) {
            size_t comma = tok.find(',');
            if (comma == std::string::npos) continue;
            float x = std::stof(tok.substr(0, comma));
            float y = std::stof(tok.substr(comma + 1));
            pts.push_back(Point(x, y));
        }
        if (pts.size() >= 3) out.push_back(std::move(pts));
    }
    return out;
}

// ---- parse the REAL part geometry from a CGSHOP parts_510x635.json ----
// Each part has shape.data = [[x,y],...] at x100 scale; we read every "data" array after
// "parts" and scale the coordinates (e.g. 0.01 to map the 51000x63500 instance to 510x635).
static std::vector<std::vector<Point>> parse_json_parts(const std::string& path, f32 scale) {
    std::ifstream f(path);
    std::stringstream ss; ss << f.rdbuf();
    std::string txt = ss.str();
    std::vector<std::vector<Point>> out;
    const std::string key = "\"data\"";
    size_t pos = txt.find("\"parts\"");
    if (pos == std::string::npos) pos = 0;
    while ((pos = txt.find(key, pos)) != std::string::npos) {
        pos += key.size();
        size_t open = txt.find('[', pos);
        if (open == std::string::npos) break;
        int depth = 0;
        std::vector<f32> nums;
        std::string num;
        auto flush = [&]() { if (!num.empty()) { nums.push_back((f32)std::atof(num.c_str())); num.clear(); } };
        size_t i = open;
        for (; i < txt.size(); ++i) {
            char c = txt[i];
            if (c == '[') ++depth;
            else if (c == ']') { flush(); --depth; if (depth == 0) { ++i; break; } }
            else if (c == ',') flush();
            else if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E') num.push_back(c);
            else flush();
        }
        pos = i;
        std::vector<Point> pts;
        for (size_t k = 0; k + 1 < nums.size(); k += 2) pts.push_back(Point(nums[k] * scale, nums[k + 1] * scale));
        if (pts.size() >= 3) out.push_back(std::move(pts));
    }
    return out;
}

static void export_svg(const std::string& out_path, const std::vector<SheetPlacement>& placements,
                       int n_sheets, f32 sheet_w, f32 sheet_h) {
    const f32 GAP = 40.0f, MARGIN = 20.0f;
    f32 total_w = MARGIN * 2 + n_sheets * sheet_w + (n_sheets - 1) * GAP;
    f32 total_h = MARGIN * 2 + sheet_h + 30;
    const char* colors[] = {"#4a4e69", "#e63946", "#2a9d8f", "#e9c46a", "#264653", "#f4a261",
                            "#457b9d", "#a8dadc", "#6d6875", "#b5838d", "#e76f51", "#52b788"};
    std::ofstream svg(out_path);
    svg.precision(8); // faithfully represent f32 coords (avoid spurious near-edge crossings)
    svg << "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n";
    svg << "<svg xmlns=\"http://www.w3.org/2000/svg\" version=\"1.1\" width=\"" << (int)total_w
        << "\" height=\"" << (int)total_h << "\">\n";
    svg << "<rect width=\"" << (int)total_w << "\" height=\"" << (int)total_h << "\" fill=\"#1a1a2e\"/>\n";
    for (int b = 0; b < n_sheets; ++b) {
        f32 ox = MARGIN + b * (sheet_w + GAP);
        svg << "<rect x=\"" << ox << "\" y=\"" << MARGIN << "\" width=\"" << sheet_w << "\" height=\"" << sheet_h
            << "\" fill=\"#16213e\" stroke=\"#9a8c98\" stroke-width=\"1.5\"/>\n";
        svg << "<text x=\"" << (ox + sheet_w / 2) << "\" y=\"" << (MARGIN + sheet_h + 20)
            << "\" font-size=\"14\" font-family=\"monospace\" text-anchor=\"middle\" fill=\"#c9ada7\">Sheet "
            << (b + 1) << " (510x635)</text>\n";
    }
    for (auto& po : placements) {
        f32 ox = MARGIN + po.sheet * (sheet_w + GAP);
        const char* col = colors[po.color_id % 12];
        svg << "<polygon points=\"";
        for (auto& p : po.verts) svg << (p.x + ox) << "," << (p.y + MARGIN) << " ";
        svg << "\" fill=\"" << col << "\" fill-opacity=\"0.65\" stroke=\"white\" stroke-width=\"0.8\"/>\n";
    }
    svg << "</svg>\n";
    svg.close();
}

int main(int argc, char** argv) {
    std::string svg_in = (argc > 1) ? argv[1]
        : "C:/pc/3_code/code_cpp/shadoks-CGSHOP2024/sample_polygons.svg";
    std::string svg_out = (argc > 2) ? argv[2]
        : "C:/pc/3_code/code_cpp/shadoks-CGSHOP2024/nest_output.svg";
    // Budget arg: "120" = 120 SECONDS (anytime, wall-clock). "3000i" = 3000 RELAXATION ROUNDS
    // (deterministic, machine-independent, fully reproducible). Iteration mode is the way to get
    // identical results every run regardless of CPU speed.
    std::string budget_arg = (argc > 3) ? std::string(argv[3]) : std::string("120");
    if (!budget_arg.empty() && (budget_arg.back() == 'i' || budget_arg.back() == 'I')) {
        g_iter_mode = true;
        budget_arg.pop_back();
    }
    g_iter_count = 0;
    double total_budget = std::atof(budget_arg.c_str());
    // arg 4 (optional): worker count for best-of-N (0/absent => all hardware cores).
    if (argc > 4) nest::g_n_workers = (unsigned)std::atoi(argv[4]);
    if (argc > 5) nest::g_pole_max = (unsigned)std::atoi(argv[5]);
    std::printf("budget = %.0f %s | workers = %u\n", total_budget,
                g_iter_mode ? "relaxation rounds (deterministic)" : "seconds (wall-clock)",
                nest::resolve_n_workers());

    const f32 SHEET_W = 510.0f, SHEET_H = 635.0f;
    nest::ROT_N_SAMPLES = 32; // finer orientation search for elongated ribbon parts (default 16)

    // .json => REAL CGSHOP geometry (all entries are parts, x100 scale -> /100 for 510x635 units).
    // .svg  => display catalog (polyline[0] is the container thumbnail; parts are the rest).
    bool is_json = svg_in.size() >= 5 && svg_in.substr(svg_in.size() - 5) == ".json";
    std::vector<std::vector<Point>> polylines;
    usize first_part;
    if (is_json) { polylines = parse_json_parts(svg_in, 0.01f); first_part = 0; }
    else { polylines = parse_polylines(svg_in); first_part = 1; }
    std::printf("parsed %zu %s from %s\n", polylines.size(),
                is_json ? "real-geometry parts" : "polylines", svg_in.c_str());
    if (polylines.size() < (is_json ? 1u : 2u)) { std::printf("not enough geometry\n"); return 1; }

    std::vector<std::pair<Part, usize>> parts;
    for (usize i = first_part; i < polylines.size(); ++i) {
        auto it = build_part(parts.size(), polylines[i]);
        if (it) parts.emplace_back(std::move(*it), 1);
    }
    std::printf("built %zu parts (free continuous rotation)\n", parts.size());

    f32 total_material = 0.0f;
    for (auto& pr : parts) total_material += pr.first.area();
    std::printf("total material area = %.1f (%.2f%% of one 510x635 sheet)\n",
                total_material, 100.0 * total_material / (SHEET_W * SHEET_H));

    CollisionConfig engine; engine.quadtree_depth = 4; engine.cd_threshold = 16; engine.part_surrogate_config = surrogate_config();

    // Greedy sequential bin fill: fill each 510x635 sheet as much as possible, spill the rest to
    // the next sheet (one strip pack of the remainder per sheet, take the first 510-wide column).
    std::printf("\n--- greedy sequential bin fill (fill each sheet, spill the rest) ---\n");
    double t0 = now_seconds();
    Sheets res = greedy_fill(parts, engine, SHEET_W, SHEET_H, total_budget);
    double elapsed = now_seconds() - t0;

    std::vector<SheetPlacement> placements = std::move(res.placements);
    int n_sheets = res.n_sheets;

    std::printf("\n=== RESULT ===\n");
    std::printf("%d sheet%s for %zu parts:\n", n_sheets, n_sheets == 1 ? "" : "s", parts.size());
    for (int s = 0; s < n_sheets; ++s)
        std::printf("  sheet %d: %zu parts, used width %.1f / %.0f (%.0f%% of sheet width)\n",
                    s + 1, res.counts[s], res.widths[s], SHEET_W, 100.0f * res.widths[s] / SHEET_W);
    std::printf("  bounds violations = %d | brute-force overlaps = %d | all parts placed: %s\n",
                res.inf, res.bf, res.ok ? "YES" : "NO");
    std::printf("  total material = %.1f%% of one sheet | wall time %.1fs\n",
                100.0 * total_material / (SHEET_W * SHEET_H), elapsed);

    export_svg(svg_out, placements, n_sheets, SHEET_W, SHEET_H);
    std::printf("wrote %s (%d sheet%s)\n", svg_out.c_str(), n_sheets, n_sheets == 1 ? "" : "s");
    return 0;
}
