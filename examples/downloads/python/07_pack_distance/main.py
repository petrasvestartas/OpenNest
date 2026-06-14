#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Polyline
from compas.scene import Scene
from compas_nest import nest_geo, pack

BLUE = Color.from_hex("#0072B2")

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

# 1) parts (one with a hole) with several copies each
geo = nest_geo()
geo.add_part(rect(0, 0, 30, 12), copies=6)
geo.add_part(rect(0, 0, 20, 20), holes=[rect(6, 6, 8, 8)], copies=6)

# 2) pack by distance: fill a row up to max_width, then wrap to the next row
result = pack(geo, max_width=120.0, gap_x=5.0, gap_y=5.0)

# 3) draw the layout (elements blue)
scene = Scene()
scene.clear()
for group in result.placed_polylines():
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
        for hole in part["holes"]:
            scene.add(hole, color=BLUE)
scene.draw()
