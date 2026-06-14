# 06 · Pack (array)

Lay parts out in a simple grid with `pack` — a fixed number of elements per row, wrapping to the next row.
Deterministic, no nesting; it returns a `nest_result`, so `placed_polylines()` works as usual.

Paste into the Rhino 8 **Script Editor** (Python 3); `# r: compas_nest` installs the package on first **Run**. [compas_nest source](https://github.com/petrasvestartas/compas_nest/blob/main/examples/06_pack_array.py)

```python
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

# 2) pack into an array: a fixed number of elements per row (5), wrapping to the next row
result = pack(geo, columns=5, gap_x=1.0, gap_y=1.0)

# 3) draw the grid layout (elements blue)
scene = Scene()
scene.clear()
for group in result.placed_polylines():
    for part in group["parts"]:
        scene.add(part["outline"], color=BLUE)
        for hole in part["holes"]:
            scene.add(hole, color=BLUE)
scene.draw()
```
