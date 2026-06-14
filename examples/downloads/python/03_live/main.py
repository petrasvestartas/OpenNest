#! python3
# r: compas_nest

import time
from compas.colors import Color
from compas.geometry import Polyline
from compas.scene import Scene
from compas_nest import nest_geo, nest_sheets, opennest

BLUE = Color.from_hex("#0072B2")

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

# 1) a handful of parts on one sheet
geo = nest_geo()
for i in range(12):
    geo.add_part(rect(0, 0, 20 + (i % 4) * 6, 14))
sheets = nest_sheets.from_size(200, 200, count=1)

# 2) START the solve on a worker thread (non-blocking) and poll it
handle = opennest(generations=200, rotations=8, seed=7, verbose=False).start(geo, sheets)
while handle.is_running():
    time.sleep(0.1)
    snap = handle.snapshot()                 # current best layout (a nest_result)
    print("gen", handle.progress(), "placed", len(snap.placed))
result = handle.wait()                        # block for the final best

# 3) draw the final layout (elements blue)
scene = Scene()
scene.clear()
for group in result.placed_polylines():
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
scene.draw()
