# Python API

**[compas_nest](https://petrasvestartas.github.io/compas_nest/)** is the COMPAS plugin for 2D nesting — the
Python route into the same OpenNest engines. The examples here are written for **Rhino 8 users**: paste any one
straight into the **Script Editor** (Python 3). The `# r: compas_nest` header installs the package on the first
**Run**, and a COMPAS `Scene` draws the result into your Rhino document.

There are **two engines**, same API — `opennest_collision()` (physics / overlap‑relaxation, dense, nests into
holes) and `opennest()` (NFP + genetic algorithm). Every nest is **3 steps**: build the parts + sheets, nest, draw.

## Example 1 — collision engine

A mix of parts — bars, a square with a hole, strips, triangles and an L — onto a sheet with a hole. The sheet
outlines are drawn black, the placed parts blue.

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

## Example 2 — NFP + genetic algorithm

The same richer mix of parts, but construct **`opennest`** (NFP + GA) instead. Sheets drawn black, parts blue.

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

## Example 3 — attributes that travel with the part

Several parts, each carrying a centroid `Point` as an attribute; it's transformed with the part and drawn in red.
Sheet outlines (with the hole) are drawn black.

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

## The key pieces

| Use this | For | Import |
| --- | --- | --- |
| `nest_geo` | the parts to nest (outlines + holes + copies + attributes) | `from compas_nest import nest_geo` |
| `nest_sheets` | the sheets to pack onto (outlines + keep‑out holes) | `from compas_nest import nest_sheets` |
| `opennest_collision` / `opennest` | run the solve | `from compas_nest import opennest_collision, opennest` |
| `nest_result` | read placements (returned by `.solve()`) | — |

Both engines follow the same pattern: construct with tuning params, then `.solve(geo, sheets)` → a `nest_result`.
Read it with `result.placed_polylines()` (grouped per sheet) or `result.transformation(placement)`. The few knobs
that matter:

```python
# physics engine
opennest_collision(iterations=4000, num_rotations=3600, spacing=0.0,
                   seed=100, n_starts=1, fit_mode=1)   # fit_mode 0 = fewest sheets, 1 = max fill
# NFP + GA engine
opennest(generations=20, rotations=8, spacing=0.0, seed=7, use_holes=True)
```

Add clearance with the offset helpers before solving:

```python
from compas_nest import offset_geo, offset_sheets
geo    = offset_geo(geo, 0.1)        # grow part outlines, shrink their holes
sheets = offset_sheets(sheets, 0.1)  # shrink sheet outlines, grow sheet holes
```

## Run it in the Rhino 8 Script Editor

Rhino 8's Python is **CPython 3.9** (`py39-rh8`). Open the **Script Editor** (`_ScriptEditor`), make a new
**Python 3** script, and put the requirements header at the very top — the first **Run** pip‑installs the package
(the editor freezes briefly while it does):

```python
#! python3
# r: compas_nest
```

To show results, draw straight into the Rhino document with a COMPAS **`Scene`** — no `compas_viewer` needed
(that's for a standalone window): `scene.add(geometry, color=…)` then `scene.draw()`.

!!! warning "Two honest caveats inside Rhino"

    1. **Compiled backend.** `compas_nest` is C++ (via nanobind), so it needs Rhino 8's **CPython** runtime — it
       will **not** load under IronPython. It works because PyPI ships a CPython‑3.9 wheel matching Rhino 8, so no
       local compiler is needed; COMPAS flags Rhino 8 CPython support as *experimental*.
    2. **Use `Scene`, not the viewer.** `compas_nest.viewer.animate()` opens a standalone `compas_viewer`
       (PyQt/OpenGL) window — that is **not** the Rhino viewport. Inside Rhino, draw with a COMPAS `Scene` as
       shown above (or bake via `compas_rhino.conversions`).

??? info "Installing into Rhino 8 — three paths"

    | Path | How |
    | --- | --- |
    | In‑editor header (recommended) | Put `# r: compas_nest` (or `# requirements: compas_nest`) under `#! python3` at the top; **Run** once to install. Pin with `# r: compas_nest==0.1.0`; isolate with `# venv: nesting`. |
    | Manual pip (most reliable) | Windows: `%USERPROFILE%\.rhinocode\py39-rh8\python.exe -m pip install compas_nest`  ·  macOS: `~/.rhinocode/py39-rh8/python3.9 -m pip install compas_nest` |
    | COMPAS helper | In a normal Python env with COMPAS installed: `install_in_rhino compas_nest` |

    `compas` (>=2.15,<3) and `numpy` (>=1.24) come in as dependencies. Verify with:

    ```python
    #! python3
    # r: compas_nest
    import compas, compas_nest
    print(compas.__version__, compas_nest.__version__)
    ```

    If `import numpy` ever fails with a `python39.dll` conflict, pin a known‑good numpy or install via the
    manual‑pip path and relaunch Rhino.

## API reference

??? info "nest_geo, nest_sheets"

    ```python
    class nest_geo(parts=None, name=None)
        add_part(outline, holes=None, copies=1, attributes=None) -> int
        # outline    : closed Polyline (outer ring)
        # holes      : list[Polyline], interior holes (kept empty)
        # copies     : number of identical copies to nest
        # attributes : list[Geometry] carried along with the placement (e.g. Points)

    class nest_sheets(sheets=None, name=None)
        add_sheet(outline, holes=None) -> int          # holes = forbidden interior regions
        @classmethod from_size(width, height, count=1, gap=None) -> nest_sheets
        origins() -> list[tuple[float, float]]          # each sheet's world origin
        to_arrays() -> dict                             # engine intake
    ```

    `nest_sheets.from_size` builds a row of identically sized rectangular sheets:

    ```python
    sheets = nest_sheets.from_size(510, 635, count=2)
    ```

??? info "the two engines"

    ```python
    class opennest_collision(iterations=4000, num_rotations=3600, spacing=0.0, seed=100,
        n_starts=1, part_holes_mode=1, pole_max=16, final_compact=2, fit_mode=1,
        max_sheets=0, time_budget_secs=0.0, simplify_tolerance=0.0, verbose=True)
        solve(geo, sheets) -> nest_result      # blocking
        start(geo, sheets) -> collision_solve  # non-blocking background handle

    class opennest(generations=10, rotations=8, placement_type=1, spacing=0.0, seed=30,
        mutation_rate=10, population_size=10, use_holes=True, try_all_rotations=False,
        exact_nfp=False, mode=1, num_seeds=4, use_parallel=True, curve_tolerance=0.3,
        clipper_scale=1e7, sheet_spacing=0.0, rotation_limit=360.0, time_budget_secs=0.0,
        max_sheets=0, verbose=True)
        solve(geo, sheets) -> nest_result      # blocking
        start(geo, sheets) -> nfp_solve        # non-blocking background handle
    ```

    `opennest_collision` is the physics / overlap‑relaxation engine (`np_nest`, dependency‑free, nests parts into
    holes). `opennest` is the NFP + genetic‑algorithm engine (`nfp_nest`, bundles Clipper2; carries part attributes
    through placement). Knob semantics line up with the native structs on the [C++ API](cpp.md) page.

??? info "nest_result"

    ```python
    class nest_result(placements, geo, sheet_origins, n_sheets, fitness=None)
        placed                          # property: placements that landed on a sheet
        unplaced                        # property: placements that did not fit
        transformation(placement) -> Transformation       # world move + rotate
        placed_polylines(geo=None) -> list[dict]           # grouped per sheet
        to_json(filepath, geo=None) -> str
        to_obj(filepath, geo=None) -> str
    ```

    `placed_polylines()` returns one dict per sheet (keys include `sheet_id` and `parts`); each entry in `parts`
    is a dict with `"outline"`, `"holes"`, and `"attributes"`.

??? info "background solving + live animation (standalone, not Rhino)"

    Both engines expose `.start(geo, sheets)` instead of `.solve(...)`, returning a non‑blocking handle
    (`collision_solve` / `nfp_solve`) you can poll while the solve runs on a worker thread:

    ```python
    progress()    -> int                 # rounds / generations reached
    is_running()  -> bool
    cancel()      -> None                # stop early, keep best so far
    snapshot()    -> nest_result         # current best layout
    wait()        -> nest_result         # block until done
    ```

    The `compas_nest.viewer.animate` helper drives a live `compas_viewer` window from one of these handles
    (standalone only — inside Rhino, draw with a `Scene` instead, as shown above):

    ```python
    from compas_nest import opennest_collision, offset_geo, offset_sheets
    from compas_nest.viewer import animate

    offset_g = offset_geo(geo, 0.1)
    offset_s = offset_sheets(sheets, 0.1)
    handle = opennest_collision(fit_mode=0, verbose=False).start(offset_g, offset_s)
    animate(handle, geo, offset_s, save="result.json", park=-635.0)
    ```

??? info "offset / clearance helpers"

    ```python
    offset_polyline(polyline, distance) -> Polyline | None   # single closed polyline (+out / -in)
    offset_geo(geo, distance)           -> nest_geo          # grow part outlines, shrink their holes
    offset_sheets(sheets, distance)     -> nest_sheets       # shrink sheet outlines, grow sheet holes
    ```

    All three use the bundled Clipper2.

## Where this fits

OpenNest's nesting engines are native **C++** with a **C#** wrapper for Grasshopper/Rhino — see the
[C++ API](cpp.md) and [C# API](csharp.md). **`compas_nest` is the Python/COMPAS route** over those same engines
(`np_nest` physics + `nfp_nest` NFP/GA), bound via nanobind. For the no‑code Grasshopper path, see
[OpenNest2](../components/opennest2.md).
