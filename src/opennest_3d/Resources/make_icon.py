#!/usr/bin/env python3
"""Generate the OpenNest3D icon as OpenNest3D.svg (vector) + a 24x24 OpenNest3D.png. It is the OpenNest
logo (the 4 axonometric pinwheel blades from icons/SVG/opennest.svg) EXTRUDED into 3D slabs, with a bold
ink (black) outline -- i.e. the OpenNest mark, in 3D. One coordinate set drives both outputs.
Run: python make_icon.py
"""
import os
import math
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))
N = 24                 # icon is N x N user units
SS = 24                # PNG supersample factor (render N*SS, downscale to N)

TOP  = "#BDBDBD"       # grey top face (fill is grey)
WALL = "#8C8C8C"       # grey side walls (a touch darker -> gentle depth shading)
INK  = "#000000"       # black extrusion lines / outline
OUTLINE_TOP  = 1.2     # black line weight on top edges (24-space units)
OUTLINE_WALL = 1.0     # black line weight on the extrusion side edges
VX, VY = 2.8, 3.2      # extrusion vector (down-right) -> chunky 3D blocks (side faces read at 24px)

# The 4 black HATCH polygons of the OpenNest logo (icons/SVG/opennest.svg).
BLADES = [
    [(12.95, 7.77), (14.51, 10.48), (10.42, 12.84), (3.39, 10.30), (2.28, 7.82), (6.55, 5.13)],
    [(12.78, 14.03), (12.83, 16.58), (7.69, 21.50), (2.47, 22.06), (2.62, 19.02), (7.68, 13.36)],
    [(16.83, 9.73), (19.31, 10.84), (21.84, 17.88), (19.48, 21.96), (16.77, 20.40), (14.13, 14.00)],
    [(11.67, 4.16), (12.09, 6.65), (17.88, 9.11), (22.33, 6.70), (21.71, 4.14), (16.41, 1.59)],
]


def extr(p):
    return (p[0] + VX, p[1] + VY)


# Fit transform: extrude, take the bbox of all top+bottom points, scale+center into [margin, N-margin].
_all = []
for b in BLADES:
    for p in b:
        _all.append(p); _all.append(extr(p))
_xs = [p[0] for p in _all]; _ys = [p[1] for p in _all]
_minx, _maxx, _miny, _maxy = min(_xs), max(_xs), min(_ys), max(_ys)
_w, _h = _maxx - _minx, _maxy - _miny
_margin = 1.4
_target = N - 2 * _margin
_s = _target / max(_w, _h)
_offx = _margin + (_target - _w * _s) / 2.0
_offy = _margin + (_target - _h * _s) / 2.0


def T(p):
    return ((p[0] - _minx) * _s + _offx, (p[1] - _miny) * _s + _offy)


def centroid(pts):
    return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))


def faces(blade):
    """Return (top_face, visible_side_walls) in fitted 24-space coords."""
    top = [T(p) for p in blade]
    bot = [T(extr(p)) for p in blade]
    c = centroid(top)
    walls = []
    n = len(top)
    for i in range(n):
        a, b = top[i], top[(i + 1) % n]
        dx, dy = b[0] - a[0], b[1] - a[1]
        nx, ny = dy, -dx                                   # edge normal
        m = ((a[0] + b[0]) / 2, (a[1] + b[1]) / 2)
        if nx * (m[0] - c[0]) + ny * (m[1] - c[1]) < 0:    # make it the OUTWARD normal
            nx, ny = -nx, -ny
        if nx * VX + ny * VY > 0.0:                         # edge faces the extrusion -> visible side wall
            walls.append([a, b, bot[(i + 1) % n], bot[i]])
    return top, walls


# Painter's order: back to front along the extrusion vector.
ORDER = sorted(range(len(BLADES)),
               key=lambda i: (lambda c: c[0] * VX + c[1] * VY)(centroid([T(p) for p in BLADES[i]])))


def render_png():
    img = Image.new("RGBA", (N * SS, N * SS), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    sc = lambda p: (p[0] * SS, p[1] * SS)
    for i in ORDER:
        top, walls = faces(BLADES[i])
        for wq in walls:
            d.polygon([sc(p) for p in wq], fill=WALL, outline=INK, width=max(1, round(OUTLINE_WALL * SS)))
        d.polygon([sc(p) for p in top], fill=TOP, outline=INK, width=max(1, round(OUTLINE_TOP * SS)))
    img.resize((N, N), Image.LANCZOS).save(os.path.join(OUT, "OpenNest3D.png"))


def render_svg():
    pts = lambda poly: " ".join(f"{x:.2f},{y:.2f}" for x, y in poly)
    parts = []
    for i in ORDER:
        top, walls = faces(BLADES[i])
        for wq in walls:
            parts.append(f'<polygon points="{pts(wq)}" fill="{WALL}" stroke="{INK}" stroke-width="{OUTLINE_WALL}"/>')
        parts.append(f'<polygon points="{pts(top)}" fill="{TOP}" stroke="{INK}" stroke-width="{OUTLINE_TOP}"/>')
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {N} {N}" width="{N}" height="{N}">'
        f'<g stroke-linejoin="round" stroke-linecap="round">{"".join(parts)}</g></svg>\n')
    with open(os.path.join(OUT, "OpenNest3D.svg"), "w", encoding="utf-8") as f:
        f.write(svg)


if __name__ == "__main__":
    render_png()
    render_svg()
    print("wrote OpenNest3D.png + OpenNest3D.svg")
