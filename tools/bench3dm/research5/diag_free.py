"""Free-space diagnosis of a nesting layout: for the chosen sheets, rasterise the free space (solve sheet minus
placed solve rings) and test, for every leftover part and every rotation, whether it fits ANYWHERE (FFT
correlation of the part mask with the blocked mask). Reports largest inscribed axis-aligned rectangle, free-area
fragmentation after a 50 mm erosion, and a per-part verdict. Run with python -I from the repo root.
usage: diag_free.py --instance I --placements P --sheets 3,4 --leftover-sheet 5 --out DIR [--px 5] [--bias half|mm]
"""
import argparse, json, math, time
from pathlib import Path
import numpy as np
from shapely.geometry import Polygon, box
from shapely import affinity, ops, vectorized
from scipy.signal import fftconvolve

ap = argparse.ArgumentParser()
ap.add_argument("--instance", required=True); ap.add_argument("--placements", required=True)
ap.add_argument("--sheets", default="3,4"); ap.add_argument("--leftover-sheet", type=int, default=5)
ap.add_argument("--leftovers", default=None, help="explicit part ids, overrides --leftover-sheet")
ap.add_argument("--out", required=True); ap.add_argument("--px", type=float, default=5.0)
ap.add_argument("--angle-step", type=float, default=1.0)
ap.add_argument("--holes-free", action="store_true", help="treat placed parts' holes as free space")
ap.add_argument("--bias", default="half", help="conservative dilation: 'half' pixel diagonal or a number in mm")
a = ap.parse_args()
out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
inst = json.load(open(a.instance, encoding="utf-8")); plc = json.load(open(a.placements, encoding="utf-8"))
parts = {int(p["id"]): p for p in inst["parts"]}
W, H = float(inst["solve_sheet"]["w"]), float(inst["solve_sheet"]["h"])
px = a.px; nx, ny = int(math.ceil(W / px)), int(math.ceil(H / px))
bias = px * math.sqrt(2) / 2 if a.bias == "half" else float(a.bias)
xs = (np.arange(nx) + 0.5) * px; ys = (np.arange(ny) + 0.5) * px
GX, GY = np.meshgrid(xs, ys)  # [ny, nx]


def solve_poly(pid):
    s = parts[pid].get("solve") or {}
    poly = Polygon(s.get("outer", parts[pid]["outer"]), s.get("holes", parts[pid].get("holes", [])))
    return poly if poly.is_valid else poly.buffer(0)


def placed_poly(pid, tx, ty, ang):
    return affinity.translate(affinity.rotate(solve_poly(pid), math.degrees(ang), origin=(0, 0)), tx, ty)


def mask_of(geom, dil):
    g = geom.buffer(dil) if dil else geom
    return vectorized.contains(g, GX, GY)


def largest_rect(free):
    """largest axis-aligned all-free rectangle (histogram method), in mm."""
    h = np.zeros(free.shape[1], dtype=int); best = (0, 0, 0, 0, 0)
    for r in range(free.shape[0]):
        h = np.where(free[r], h + 1, 0)
        stack = []
        for c in range(len(h) + 1):
            cur = h[c] if c < len(h) else 0
            start = c
            while stack and stack[-1][1] >= cur:
                s0, hh = stack.pop()
                if hh * (c - s0) > best[0]:
                    best = (hh * (c - s0), (c - s0), hh, s0, r - hh + 1)
                start = s0
            stack.append((start, cur))
    return best[1] * px, best[2] * px, best[3] * px, best[4] * px


by_sheet = {}
for it in plc["instances"]:
    by_sheet.setdefault(it["sheet"], []).append(it)
if a.leftovers:
    leftovers = [int(x) for x in a.leftovers.split(",")]
else:
    leftovers = [it["part"] for it in by_sheet.get(a.leftover_sheet, [])]
sheet_ids = [int(x) for x in a.sheets.split(",")]
report = {"px": px, "bias_mm": bias, "sheets": {}}
try:
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
except Exception:  # noqa: BLE001
    plt = None

for sid in sheet_ids:
    t0 = time.time()
    its = by_sheet.get(sid, [])
    polys = [placed_poly(it["part"], it["tx"], it["ty"], it["angle_rad"]) for it in its]
    if a.holes_free:
        obst = ops.unary_union(polys)
    else:
        obst = ops.unary_union([Polygon(p.exterior) for p in polys])
    blocked = mask_of(obst, bias)
    free = ~blocked
    util = sum(p.area for p in polys) / (W * H)
    free_poly = box(0, 0, W, H).difference(obst)
    er = free_poly.buffer(-50)
    comps = [g for g in (er.geoms if er.geom_type == "MultiPolygon" else [er]) if not g.is_empty]
    comps_area = sorted([g.area for g in comps], reverse=True)
    rw, rh, rx, ry = largest_rect(free)
    sh = {"parts": [it["part"] for it in its], "util": round(100 * util, 2), "free_pct": round(100 * (1 - util), 2),
          "free_components_after_50mm_erosion": len(comps),
          "component_areas_pct": [round(100 * x / (W * H), 2) for x in comps_area[:8]],
          "largest_inscribed_rect_mm": [round(rw), round(rh)], "largest_rect_pos": [round(rx), round(ry)],
          "leftovers": {}}
    blocked_f = blocked.astype(np.float32)
    fits_draw = []
    for pid in leftovers:
        base = solve_poly(pid); area = base.area
        best = None; n_ang_fit = 0; n_ang = 0
        for ang in np.arange(0.0, 360.0, a.angle_step):
            n_ang += 1
            rp = affinity.rotate(base, ang, origin=(0, 0))
            minx, miny, maxx, maxy = rp.bounds
            pw, ph = int(math.ceil((maxx - minx) / px)) + 1, int(math.ceil((maxy - miny) / px)) + 1
            if pw > nx or ph > ny:
                continue
            lx = (np.arange(pw) + 0.5) * px + minx; ly = (np.arange(ph) + 0.5) * px + miny
            LX, LY = np.meshgrid(lx, ly)
            pm = vectorized.contains(rp.buffer(bias) if bias else rp, LX, LY).astype(np.float32)
            if pm.sum() == 0:
                continue
            # number of blocked pixels under the part for every placement fully inside the sheet
            cnt = fftconvolve(blocked_f, pm[::-1, ::-1], mode="valid")
            ok = cnt < 0.5
            if ok.any():
                n_ang_fit += 1
                if best is None:
                    r, c = np.argwhere(ok)[0]
                    tx = c * px - minx; ty = r * px - miny
                    best = (float(ang), float(tx), float(ty))
        sh["leftovers"][pid] = {"name": parts[pid]["name"], "area_pct": round(100 * area / (W * H), 2),
                                "bbox": [round(base.bounds[2] - base.bounds[0]), round(base.bounds[3] - base.bounds[1])],
                                "fits": best is not None, "angles_that_fit": n_ang_fit, "angles_tested": n_ang,
                                "example": best}
        if best is not None:
            fits_draw.append((pid, best))
    sh["seconds"] = round(time.time() - t0, 1)
    report["sheets"][sid] = sh
    print(f"sheet {sid}: util {sh['util']}% free {sh['free_pct']}% comps(50mm) {len(comps)} "
          f"areas% {sh['component_areas_pct'][:5]} largest rect {round(rw)}x{round(rh)} at ({round(rx)},{round(ry)}) "
          f"[{sh['seconds']}s]")
    for pid, v in sh["leftovers"].items():
        print(f"   leftover {pid} {v['name']} {v['bbox']} {v['area_pct']}%: {'FITS' if v['fits'] else 'no fit'} "
              f"({v['angles_that_fit']}/{v['angles_tested']} angles) {v['example']}")
    if plt is not None:
        fig, ax = plt.subplots(figsize=(14, 5))
        ax.add_patch(plt.Rectangle((0, 0), W, H, fill=True, color="#e8f4e8", ec="k"))
        for p in polys:
            ax.fill(*p.exterior.xy, color="#9ab", ec="k", lw=0.5)
            for h in p.interiors:
                ax.fill(*h.xy, color="#e8f4e8", ec="k", lw=0.3)
        for g in comps:
            ax.plot(*g.exterior.xy, color="g", lw=0.8, ls="--")
        ax.add_patch(plt.Rectangle((rx, ry), rw, rh, fill=False, ec="r", lw=1.5, ls=":"))
        for pid, (ang, tx, ty) in fits_draw:
            g = affinity.translate(affinity.rotate(solve_poly(pid), ang, origin=(0, 0)), tx, ty)
            ax.fill(*g.exterior.xy, color="orange", alpha=0.6, ec="r")
            ax.text(g.centroid.x, g.centroid.y, str(pid), fontsize=7, ha="center")
        ax.set_xlim(-50, W + 50); ax.set_ylim(-50, H + 50); ax.set_aspect("equal")
        ax.set_title(f"sheet {sid}: util {sh['util']}%, free {sh['free_pct']}%, {len(comps)} free comps (50 mm erosion), "
                     f"largest rect {round(rw)}x{round(rh)}; orange = a leftover that fits (one example each, "
                     f"independent)", fontsize=9)
        fig.tight_layout(); fig.savefig(out / f"free_sheet{sid}.png", dpi=110); plt.close(fig)
json.dump(report, open(out / "diag.json", "w"), indent=1)
print("wrote", out)
