# C++ API

OpenNest nests with **two native engines**. Both do the same simple thing:

> **You give:** part shapes + sheet shapes.
> **You get back:** for each part — *move it by (tx, ty)*, *rotate it by an angle*, and *which sheet* it landed on.

You call them through a plain **C ABI** (works from any language).

| Want this… | Call | In | Example |
| --- | --- | --- | --- |
| Clean polygon packing, no overlap | **`nfp_nest`** | `nfp_nest.dll` / `.dylib` | [nfp_nest](nfp_nest.md) |
| Dense packing, parts into holes | **`np_nest`** | `nest_physics.dll` / `.dylib` | [np_nest](np_nest.md) |
| Simple row/grid layout (no nesting) | **`nfp_pack`** | `nfp_nest.dll` / `.dylib` | [nfp_pack](nfp_pack.md) |
| Grow / shrink one polygon (Clipper2) | **`nfp_offset_polygon`** | `nfp_nest.dll` / `.dylib` | [nfp_offset_polygon](nfp_offset_polygon.md) |
| Show progress / cancel a running solve | `*_progress` / `*_poll_layout` / `*_cancel` | both | [Run without freezing](progress.md) |

To place instance *i*: `final = Rotate(part, angle[i], origin) + (tx[i], ty[i])`, on sheet `sheet_id[i]`
(`-1` = didn't fit). `(tx, ty)` are sheet‑local — add the sheet's own position.

---

## Quick start — the header‑only binding

The easiest way to nest from C++ is the header‑only
[`opennest.hpp`](https://github.com/petrasvestartas/OpenNest/blob/main/examples/cpp_console/opennest.hpp) binding,
which mirrors the [compas_nest](../python.md) Python API — build a `nest_geo`, build a `nest_sheets`, call
`.solve()`, read the placed outlines:

```cpp
#include "opennest.hpp"
#include <cstdio>
using namespace opennest;

int main() {
    // 1) parts (one with a hole) + a sheet (with a hole)
    nest_geo geo;
    geo.add_part({{0,0},{30,0},{30,12},{0,12}}, /*holes*/ {},                         /*copies*/ 4);
    geo.add_part({{0,0},{20,0},{20,20},{0,20}}, {{{6,6},{14,6},{14,14},{6,14}}},      /*copies*/ 3);

    nest_sheets sheets;
    sheets.add_sheet({{0,0},{120,0},{120,120},{0,120}}, {{{50,50},{65,50},{65,65},{50,65}}});

    // 2) nest with the collision (physics) engine — swap to opennest_nfp{} for NFP + GA
    nest_result r = opennest_collision{}.solve(geo, sheets);

    // 3) read the placed (transformed) outlines, grouped per sheet
    printf("placed %zu instances on %d sheet(s)\n", r.placed().size(), r.n_sheets);
    for (const auto& group : r.placed_polylines())
        for (const auto& part : group.parts) {
            part.shape.outer;   // placed outer ring
            part.shape.holes;   // placed holes
        }
    return r.placed().empty() ? 1 : 0;
}
```

`opennest.hpp` is a thin wrapper over the raw C ABI documented on the per‑function pages — call the ABI directly
only when binding from another language.

## Build & run

The full program is
[`examples/cpp_console`](https://github.com/petrasvestartas/OpenNest/tree/main/examples/cpp_console). Build & run it
with the CMake **superbuild** in [`examples/`](https://github.com/petrasvestartas/OpenNest/tree/main/examples):

```bash
cmake -S examples -B examples/build                                  # add -A x64 on Windows
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release  # builds + runs both apps
```

Built & run on Windows, macOS and Linux by
[`examples.yml`](https://github.com/petrasvestartas/OpenNest/blob/main/.github/workflows/examples.yml).

---

## How a polygon crosses the boundary

A C ABI can't take a `vector<Polygon>`, so geometry is passed as **plain number arrays**. The rule is simple, and
the same for every function below:

- **One polygon** = a vertex count + a flat `x,y` list: `x0,y0, x1,y1, x2,y2, …`
- **A list of polygons** = a `count`, a *lengths* array (vertices in each), and **one** big `xy` buffer with every
  polygon's points concatenated.
- **Holes** repeat the same pattern: how many holes each part has, how many vertices each hole has, and one
  concatenated `xy`.

```c
// Two triangles as "a list of polygons":
int    count           = 2;
int    vertex_counts[] = { 3, 3 };
double xy[]            = { 0,0, 10,0, 0,8,    // triangle A
                          0,0, 10,0, 0,8 };  // triangle B
```

So the long signatures on the per‑function pages are just **"a list of polygons (with holes)"** spelled out —
once for the parts, once for the sheets. That's all the `_counts` / `_xy` arrays are.

??? note "Under the hood (for maintainers)"

    NFP engine in `src/opennest_cpp/src/`: `NestingEngine` (geometry + NFP + placement cost), `NestingContext`
    (GA orchestration), `NfpWorker` (NFP cache + Minkowski), `GeneticAlgorithm` (evolve), `NFP`, `GeometryUtil`,
    `NestConfig`. Physics engine in `src/nest_physics_cpp/`. Both build from source via CMake.
    Headers: `src/opennest_cpp/src/capi/nfp_nest_capi.h`, `src/nest_physics_cpp/nest_physics_capi.h`.
