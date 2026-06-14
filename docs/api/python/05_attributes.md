# 05 · Attributes

Several parts, each carrying a centroid `Point` as an **attribute**; it's transformed with the part and drawn in
red. Any COMPAS geometry can ride along this way. Sheet outlines (with the hole) are drawn black.

Project: [Download `05_attributes.zip`](downloads/05_attributes.zip) — standalone (`run.bat` / `run.command` `pip install` compas_nest and run it). Or paste into the Rhino 8 **Script Editor** (Python 3) — `# r: compas_nest` installs it on first **Run**. [compas_nest source](https://github.com/petrasvestartas/compas_nest/blob/main/examples/05_attributes.py)

```python
#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Point, Polyline, centroid_points
from compas.scene import Scene
from compas_nest import nest_geo, nest_sheets, opennest_collision

BLACK = Color.from_hex("#000000")
BLUE = Color.from_hex("#0072B2")
RED = Color.from_hex("#C0392B")

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

def centroid(polyline):
    return Point(*centroid_points(list(polyline.points)[:-1]))

# 1) several parts, each carrying a centroid point that travels with the placement
geo = nest_geo()
for outline in [
    rect(0, 0, 30, 12),
    rect(0, 0, 20, 20),
    rect(0, 0, 40, 8),
    Polyline([[0, 0, 0], [24, 0, 0], [0, 24, 0], [0, 0, 0]]),                 # triangle
    Polyline([[0, 0, 0], [24, 0, 0], [24, 8, 0], [8, 8, 0],
              [8, 24, 0], [0, 24, 0], [0, 0, 0]]),                            # L-shape
]:
    geo.add_part(outline, attributes=[centroid(outline)], copies=3)

sheets = nest_sheets()
sheets.add_sheet(rect(0, 0, 150, 150), holes=[rect(60, 60, 20, 20)])

# 2) nest
result = opennest_collision().solve(geo, sheets)

# 3) draw sheets (black) + placed outlines (blue) + carried centroid points (red)
scene = Scene()
scene.clear()
for sheet in sheets.sheets:
    scene.add(sheet["outline"], color=BLACK)
    for hole in sheet["holes"]:
        scene.add(hole, color=BLACK)
for group in result.placed_polylines():
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
        for attribute in part["attributes"]:
            scene.add(attribute, color=RED)
scene.draw()
```
