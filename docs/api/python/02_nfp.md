# 02 · NFP + GA

The same richer mix of parts, but constructed with **`opennest`** (NFP + genetic algorithm) instead. Sheets drawn
black, parts blue.

Project: [Download `02_nfp.zip`](downloads/02_nfp.zip) — standalone (`run.bat` / `run.command` `pip install` compas_nest and run it). Or paste into the Rhino 8 **Script Editor** (Python 3) — `# r: compas_nest` installs it on first **Run**. [compas_nest source](https://github.com/petrasvestartas/compas_nest/blob/main/examples/02_nfp_viewer.py)

```python
#! python3
# r: compas_nest

from compas.colors import Color
from compas.geometry import Polyline
from compas.scene import Scene
from compas_nest import nest_geo, nest_sheets, opennest

BLACK = Color.from_hex("#000000")
BLUE = Color.from_hex("#0072B2")

def rect(x0, y0, w, h):
    return Polyline([[x0, y0, 0], [x0 + w, y0, 0],
                     [x0 + w, y0 + h, 0], [x0, y0 + h, 0], [x0, y0, 0]])

# 1) a richer mix of parts (one with a hole, triangles, an L) + a sheet with a hole
geo = nest_geo()
geo.add_part(rect(0, 0, 30, 12), copies=4)
geo.add_part(rect(0, 0, 20, 20), holes=[rect(6, 6, 8, 8)], copies=3)
geo.add_part(rect(0, 0, 40, 8), copies=3)
geo.add_part(Polyline([[0, 0, 0], [24, 0, 0], [0, 24, 0], [0, 0, 0]]), copies=5)
geo.add_part(Polyline([[0, 0, 0], [24, 0, 0], [24, 8, 0], [8, 8, 0],
                       [8, 24, 0], [0, 24, 0], [0, 0, 0]]), copies=2)

sheets = nest_sheets()
sheets.add_sheet(rect(0, 0, 150, 150), holes=[rect(60, 60, 20, 20)])

# 2) nest with the NFP + genetic‑algorithm engine
result = opennest(generations=20, rotations=8, seed=7).solve(geo, sheets)

# 3) draw the sheets (black) + placed parts (blue)
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
