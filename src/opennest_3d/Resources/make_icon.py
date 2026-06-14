#!/usr/bin/env python3
"""Generate the Nest3D component icon as BOTH Nest3D.svg (vector source of truth) and Nest3D.png
(24x24, embedded in the .gha). One coordinate set drives both so they always match. Icon = an
isometric open bin with two packed cubes (3D bin-packing motif). Run: python make_icon.py
"""
import os
from PIL import Image, ImageDraw

OUT = os.path.dirname(os.path.abspath(__file__))
N = 24                      # icon is N x N user units
SS = 20                    # supersample factor for the PNG (render N*SS, downscale to N)

# ---- palette ----
BIN   = "#2C3E50"
EDGE  = "#1E293B"
A_TOP, A_LEFT, A_RIGHT = "#7CB3FB", "#3B82F6", "#2563EB"   # blue cube
B_TOP, B_LEFT, B_RIGHT = "#FBBF24", "#F59E0B", "#D97706"   # orange cube

# ---- isometric bin (open top), 2:1 iso ----
Rt, Rr, Rf, Rl = (12, 2.6), (21.4, 8.0), (12, 13.4), (2.6, 8.0)   # rim (opening) rhombus
Bl, Bf, Br     = (2.6, 14.0), (12, 19.4), (21.4, 14.0)            # lower wall corners
BIN_EDGES = [(Rt, Rr), (Rr, Rf), (Rf, Rl), (Rl, Rt),             # rim
             (Rl, Bl), (Rf, Bf), (Rr, Br),                       # verticals
             (Bl, Bf), (Bf, Br)]                                 # bottom-front


def cube(cx, cy, a, b, h):
    """Return (top, left, right) face point-lists for an iso cube; top vertex at (cx,cy)."""
    Tb, Tr_, Tf, Tl = (cx, cy), (cx + a, cy + b), (cx, cy + 2 * b), (cx - a, cy + b)
    Fl, Ff, Fr = (cx - a, cy + b + h), (cx, cy + 2 * b + h), (cx + a, cy + b + h)
    return ([Tb, Tr_, Tf, Tl], [Tl, Tf, Ff, Fl], [Tf, Tr_, Fr, Ff])


A = cube(9.2, 7.8, 3.0, 1.5, 4.5)     # blue, left
B = cube(15.0, 9.0, 2.8, 1.4, 4.2)    # orange, right (slightly forward)
# (top, left, right) faces in draw order, with their fills
FACES = [(A[0], A_TOP), (A[1], A_LEFT), (A[2], A_RIGHT),
         (B[0], B_TOP), (B[1], B_LEFT), (B[2], B_RIGHT)]


# ---------- PNG (Pillow, supersampled) ----------
def render_png():
    s = SS
    img = Image.new("RGBA", (N * s, N * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    lw = max(1, round(0.7 * s))
    for p, q in BIN_EDGES:
        d.line([(p[0] * s, p[1] * s), (q[0] * s, q[1] * s)], fill=BIN, width=lw, joint="curve")
    ew = max(1, round(0.35 * s))
    for pts, fill in FACES:
        d.polygon([(x * s, y * s) for x, y in pts], fill=fill, outline=EDGE, width=ew)
    img = img.resize((N, N), Image.LANCZOS)
    img.save(os.path.join(OUT, "OpenNest3D.png"))


# ---------- SVG (same coords) ----------
def render_svg():
    def pl(pts):
        return " ".join(f"{x},{y}" for x, y in pts)
    lines = "".join(
        f'<line x1="{p[0]}" y1="{p[1]}" x2="{q[0]}" y2="{q[1]}"/>' for p, q in BIN_EDGES)
    polys = "".join(
        f'<polygon points="{pl(pts)}" fill="{fill}" stroke="{EDGE}" stroke-width="0.4"/>'
        for pts, fill in FACES)
    svg = (
        f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {N} {N}" width="{N}" height="{N}">'
        f'<g fill="none" stroke="{BIN}" stroke-width="0.7" '
        f'stroke-linecap="round" stroke-linejoin="round">{lines}</g>'
        f'<g stroke-linejoin="round">{polys}</g>'
        f'</svg>\n')
    with open(os.path.join(OUT, "OpenNest3D.svg"), "w", encoding="utf-8") as f:
        f.write(svg)


if __name__ == "__main__":
    render_png()
    render_svg()
    print("wrote OpenNest3D.png + OpenNest3D.svg")
