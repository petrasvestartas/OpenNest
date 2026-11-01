"""Render a placements.json layout (or the identity instance) to a PNG with matplotlib.

Usage:
  python -I tools/bench3dm/render_png.py --instance <instance.json> --placements <placements.json> --out <png> [--dpi 60]

Draws every used sheet stacked vertically (sheet-local frame, full sheet rectangle, 15 mm margin frame),
parts filled by sheet colour with holes cut out (evenodd), part ids labelled, and a utilisation title per sheet.
"""
import argparse
import json
import math
from pathlib import Path

import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.path import Path as MplPath
from matplotlib.patches import PathPatch, Rectangle


def place(ring, ang, tx, ty):
    c, s = math.cos(ang), math.sin(ang)
    return [(c * x - s * y + tx, s * x + c * y + ty) for x, y in ring]


def ring_path(rings):
    verts, codes = [], []
    for r in rings:
        if len(r) < 3:
            continue
        verts.extend(r + [r[0]])
        codes.extend([MplPath.MOVETO] + [MplPath.LINETO] * (len(r) - 1) + [MplPath.CLOSEPOLY])
    return MplPath(verts, codes) if verts else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--instance", required=True)
    ap.add_argument("--placements", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--dpi", type=int, default=60)
    ap.add_argument("--labels", action="store_true")
    a = ap.parse_args()

    inst = json.load(open(a.instance))
    pl = json.load(open(a.placements))
    W, H = inst["sheet"]["w"], inst["sheet"]["h"]
    inset = inst["solve_sheet"]["inset"]
    margin = inst.get("margin", 0.0)
    parts = {p["id"]: p for p in inst["parts"]}

    by_sheet = {}
    for it in pl["instances"]:
        by_sheet.setdefault(it["sheet"], []).append(it)
    sheets = sorted(s for s in by_sheet if s >= 0)
    n = max(1, len(sheets))
    fig_w = W / 1000.0 * 2.2
    fig_h = (H / 1000.0 * 2.2 + 0.35) * n
    fig, axes = plt.subplots(n, 1, figsize=(fig_w, fig_h), squeeze=False)
    cmap = plt.get_cmap("tab20")
    for row, s in enumerate(sheets):
        ax = axes[row][0]
        ax.add_patch(Rectangle((0, 0), W, H, fill=False, lw=1.2, ec="black"))
        ax.add_patch(Rectangle((margin, margin), W - 2 * margin, H - 2 * margin, fill=False, lw=0.6, ec="gray", ls="--"))
        area = 0.0
        for it in by_sheet[s]:
            p = parts[it["part"]]
            rings = [place(p["outer"], it["angle_rad"], it["tx"] + inset, it["ty"] + inset)]
            for h in p.get("holes", []):
                rings.append(place(h, it["angle_rad"], it["tx"] + inset, it["ty"] + inset))
            path = ring_path(rings)
            if path is None:
                continue
            col = cmap(it["part"] % 20)
            ax.add_patch(PathPatch(path, facecolor=col, edgecolor="black", lw=0.5, alpha=0.85))
            area += p.get("area_net", 0.0)
            if a.labels:
                xs = [q[0] for q in rings[0]]
                ys = [q[1] for q in rings[0]]
                ax.text(sum(xs) / len(xs), sum(ys) / len(ys), str(it["part"]), fontsize=6, ha="center", va="center")
        ax.set_xlim(-50, W + 50)
        ax.set_ylim(-50, H + 50)
        ax.set_aspect("equal")
        ax.set_title(f"{Path(a.placements).parent.name}  sheet {s}: {len(by_sheet[s])} parts, util {100 * area / (W * H):.1f}%", fontsize=9)
        ax.tick_params(labelsize=6)
    unplaced = len(by_sheet.get(-1, []))
    if unplaced:
        fig.suptitle(f"UNPLACED: {unplaced}", color="red")
    fig.tight_layout()
    fig.savefig(a.out, dpi=a.dpi)
    print("wrote", a.out, "sheets", len(sheets), "unplaced", unplaced)


if __name__ == "__main__":
    main()
