#pragma once
// Minimal SVG writer for nfp_bench layouts. No dependencies.
// Sheets are stacked vertically; placed parts drawn fill-by-instance-index;
// unplaced parts drawn in a red column right of the sheets.

#include <cstdio>
#include <string>
#include <vector>

namespace bench {

struct SvgInstance {
    // Transformed coordinates (already rotated + translated, sheet-relative).
    std::vector<double> xy;                  // outer loop
    std::vector<std::vector<double>> holes;  // hole loops
    int sheetId = -1;                        // -1 = unplaced
};

inline void writeSvg(const std::string& path,
                     const std::vector<SvgInstance>& instances,
                     int nSheets, double sheetW, double sheetH) {
    const double margin = 20.0;
    if (nSheets < 1) nSheets = 1;
    double unplacedW = sheetW * 0.4;
    double totalW = sheetW + unplacedW + 3 * margin;
    double totalH = nSheets * (sheetH + margin) + margin;

    FILE* f = std::fopen(path.c_str(), "w");
    if (!f) return;
    std::fprintf(f,
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 %.1f %.1f\">\n"
        "<rect width=\"100%%\" height=\"100%%\" fill=\"#fff\"/>\n",
        totalW, totalH);

    // Sheet outlines. Sheet k occupies y offset margin + k*(sheetH+margin).
    for (int s = 0; s < nSheets; s++) {
        double oy = margin + s * (sheetH + margin);
        std::fprintf(f,
            "<rect x=\"%.1f\" y=\"%.1f\" width=\"%.1f\" height=\"%.1f\" "
            "fill=\"none\" stroke=\"#333\" stroke-width=\"2\"/>\n",
            margin, oy, sheetW, sheetH);
    }

    auto emitPath = [&](const SvgInstance& inst, double ox, double oy, const char* fill) {
        std::string dd;
        char buf[64];
        auto loopToPath = [&](const std::vector<double>& loop) {
            for (size_t i = 0; i + 1 < loop.size(); i += 2) {
                std::snprintf(buf, sizeof(buf), "%c%.2f %.2f ", i == 0 ? 'M' : 'L',
                              ox + loop[i], oy + loop[i + 1]);
                dd += buf;
            }
            dd += "Z ";
        };
        loopToPath(inst.xy);
        for (auto& h : inst.holes) loopToPath(h);
        std::fprintf(f,
            "<path d=\"%s\" fill=\"%s\" fill-rule=\"evenodd\" fill-opacity=\"0.75\" "
            "stroke=\"#000\" stroke-width=\"1\"/>\n",
            dd.c_str(), fill);
    };

    // Placed parts (color by instance index), unplaced in the right column.
    double upY = margin;
    for (size_t i = 0; i < instances.size(); i++) {
        const auto& inst = instances[i];
        char fill[32];
        if (inst.sheetId >= 0) {
            std::snprintf(fill, sizeof(fill), "hsl(%d,70%%,55%%)", (int)((i * 47) % 360));
            double oy = margin + inst.sheetId * (sheetH + margin);
            emitPath(inst, margin, oy, fill);
        } else {
            // bbox-normalize unplaced parts into the column
            double minx = 1e300, miny = 1e300, maxy = -1e300;
            for (size_t k = 0; k + 1 < inst.xy.size(); k += 2) {
                if (inst.xy[k] < minx) minx = inst.xy[k];
                if (inst.xy[k + 1] < miny) miny = inst.xy[k + 1];
                if (inst.xy[k + 1] > maxy) maxy = inst.xy[k + 1];
            }
            emitPath(inst, sheetW + 2 * margin - minx, upY - miny, "#e33");
            upY += (maxy - miny) + 10;
        }
    }
    std::fprintf(f, "</svg>\n");
    std::fclose(f);
}

} // namespace bench
