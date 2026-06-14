#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Polyline
from compas.scene import Scene
from compas_nest import nest_geo, nest_sheets, opennest_collision

BLACK = Color.from_hex("#000000")
BLUE = Color.from_hex("#0072B2")

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

# 1) PARTS — several shapes (one with a hole) + a SHEET with a hole
geo = nest_geo()
geo.add_part(rect(0, 0, 30, 12), copies=4)                              # bars
geo.add_part(rect(0, 0, 20, 20), holes=[rect(6, 6, 8, 8)], copies=3)    # square with a hole
geo.add_part(rect(0, 0, 40, 8), copies=3)                              # strips
geo.add_part(Polyline([[0, 0, 0], [24, 0, 0], [0, 24, 0], [0, 0, 0]]), copies=5)   # triangles
geo.add_part(Polyline([[0, 0, 0], [24, 0, 0], [24, 8, 0], [8, 8, 0],
                       [8, 24, 0], [0, 24, 0], [0, 0, 0]]), copies=2)   # L-shapes

sheets = nest_sheets()
sheets.add_sheet(rect(0, 0, 150, 150), holes=[rect(60, 60, 20, 20)])

# 2) NEST
result = opennest_collision().solve(geo, sheets)

# 3) DRAW the sheets (black) + placed parts (blue) into the Rhino document
scene = Scene()
scene.clear()
for sheet in sheets.sheets:
    scene.add(sheet["outline"], color=BLACK)
    for hole in sheet["holes"]:
        scene.add(hole, color=BLACK)
for group in result.placed_polylines():
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
        for hole in part["holes"]:
            scene.add(hole, color=BLUE)
scene.draw()
