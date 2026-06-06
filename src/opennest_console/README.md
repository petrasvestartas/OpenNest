# opennest_console

A standalone, **Rhino-free** demonstration of the two OpenNest nesting engines, driven directly through
their C ABIs — no Grasshopper, no RhinoCommon, no `nest_geo`/`nest_sheets` (those need `RhinoDoc.ActiveDoc`).

| Workflow | Native DLL | C entry | Algorithm |
|---|---|---|---|
| **OpenNestCollision** | `nest_physics.dll` | `np_nest` | penetration-depth overlap relaxation |
| **OpenNest2** | `nfp_nest.dll` | `nfp_nest` | Boost.Polygon no-fit-polygon + genetic algorithm |

It reuses the exact P/Invoke wrappers the Grasshopper plugin uses
(`opennest_2/nest_lib/NfpNestWrapper`, `.../NestPhysicsWrapper`) so the C-ABI contract has one source of truth.

## What it does

1. Parses the 48-element example `shadoks-CGSHOP2024/sample_polygons.svg` into plain polygon rings
   (first `<polyline>` = decorative container, skipped — same convention as `nest_physics_cpp/nest_physics.cpp`),
   leaving 47 parts.
2. Flattens them into the flat `int[]`/`double[]` arrays each engine expects (`NestFlatten`).
3. Runs each solver on a fixed **510×635** sheet (matching the C++ CLI) and reads back the per-part
   `tx / ty / angle / sheet_id` transforms.
4. Writes `out_collision.svg` and `out_nfp.svg` (parts coloured per sheet) plus a console summary.

## Run

```powershell
dotnet build opennest_console.csproj -c Release
cd bin/Release/net9.0
./opennest_console.exe [svgPath] [budget]
```

- `svgPath` — defaults to `C:\pc\3_code\code_cpp\shadoks-CGSHOP2024\sample_polygons.svg`
  (falls back to a generated set of rectangles if absent).
- `budget` — collision relaxation rounds (default 2000); the NFP generation count is derived from it.

## Native DLLs

The build's post-build step copies whichever solver DLLs exist next to the exe.
`nest_physics.dll` ships pre-built under `opennest_2/`. To enable the OpenNest2 path, build `nfp_nest.dll`:

```powershell
cmake -S opennest_cpp -B opennest_cpp/build
cmake --build opennest_cpp/build --config Release
```

Then rebuild this project (the post-build picks up `opennest_cpp/build/Release/nfp_nest.dll`).

## Transform contract (per placed part)

```
final_point = Rotate(part_point, angle, about (0,0)) + (tx, ty)   [+ sheet world origin]
```

`np_nest` reports `angle` in **radians**; `nfp_nest` reports it in **degrees**. `(tx, ty)` are sheet-local.
