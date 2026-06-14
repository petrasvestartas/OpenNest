# 01 · Collision

Nest a mix of parts — bars, a square with a hole, strips, triangles and an L — onto a sheet with a hole, with the
physics (collision) engine. Sheet outlines are drawn black, the placed parts blue.

Paste into the Rhino 8 **Script Editor** (Python 3); `# r: compas_nest` installs the package on first **Run**. [compas_nest source](https://github.com/petrasvestartas/compas_nest/blob/main/examples/01_collision_viewer.py)

```python
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
```
