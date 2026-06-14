#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Polyline
from compas.scene import Scene
from compas_nest import nest_geo, nest_sheets, opennest_collision, offset_geo, offset_sheets

BLACK = Color.from_hex("#000000")
BLUE = Color.from_hex("#0072B2")
CLEARANCE = 1.0   # gap (model units) around every element and from the sheet edges

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

# 1) parts + a single sheet
geo = nest_geo()
geo.add_part(rect(0, 0, 30, 12), copies=6)
geo.add_part(rect(0, 0, 24, 24), copies=4)
sheets = nest_sheets.from_size(120, 120, count=1)

# 2) offset for clearance (parts grow, sheets shrink), then solve on the offset geometry
offset_g = offset_geo(geo, CLEARANCE)
offset_s = offset_sheets(sheets, CLEARANCE)
result = opennest_collision().solve(offset_g, offset_s)

# 3) draw the ORIGINAL parts at the solved poses, so the clearance gaps are visible
scene = Scene()
scene.clear()
for sheet in sheets.sheets:
    scene.add(sheet["outline"], color=BLACK)
for group in result.placed_polylines(geo):    # pass the original geo for un-offset outlines
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
scene.draw()
