# C# API

There are two ways to nest from C#:

| Path | Use when | Pages |
| --- | --- | --- |
| **High‑level (RhinoCommon)** | you have Rhino `Curve`/`Brep` geometry | this page + [Library reference](library.md) |
| **Low‑level (P/Invoke)** | no Rhino — talk to the engines directly | [nfp_nest](nfp_nest.md), [np_nest](np_nest.md), [nfp_pack](nfp_pack.md), [nfp_offset_polygon](nfp_offset_polygon.md) |

Everything ships inside `opennest_2.gha` (also used by the `.rhp` command and the console demo).

---

## High‑level: 3 steps (RhinoCommon)

Build the parts + sheets, run the solver, read where each part went.

```csharp
// 1) PARTS + SHEETS  — one Curve[] per part (outer ring first, then hole rings)
var geo = nest_rhino_lib.nest_geo_util.geo_to_nest_geo(
    curvesGrouped,                 // List<Curve[]>
    copies,                        // List<int>, one per part
    new List<double> { 0, 0 });    // 0,0 = take the boundary exactly (no simplify)

var sheets = new nest_rhino_lib.nest_sheets(
    sheetPolylines,                // List<List<Polyline>>, one list per sheet (outer + holes)
    new List<double> { gap, gap }, new List<int>(), sheetPolylines.Count);

// 2) RUN
var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, max_iterations: 10);
nest.static_solver(ref geo);       // call from a worker thread to keep Rhino responsive

// 3) RESULT  — for part p, copy c:
//    geo.xforms[p][c]                       -> the placement transform (move + rotate)
//    nest.output_polygon_sheet_ids[p][c]    -> which sheet it landed on (-1 = didn't fit)
```

The `parameters` list is positional:
`[0]=rotations [1]=wiggle [2]=placementType [3]=spacing [4]=seed [5]=simplifyTol [6]=mutation [7]=population [8]=time`.

Spacing is applied **upstream** now: offset the parts with `nest_geo.offset_nesting_boundary(gap/2)` and the
sheets with `nest_sheets.offset_sheet_boundary(gap/2)` before solving — the placed/output geometry stays the
original. See the [Library reference](library.md) for the full member list.

## Build & run (no Rhino)

[`examples/csharp_console`](https://github.com/petrasvestartas/OpenNest/tree/main/examples/csharp_console) is a
standalone, Rhino‑free console that `P/Invoke`s **both** engines directly. Build and run it with the CMake
**superbuild** in [`examples/`](https://github.com/petrasvestartas/OpenNest/tree/main/examples):

```bash
cmake -S examples -B examples/build                                  # add -A x64 on Windows
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release  # builds + runs both apps
```

It's built & run on Windows, macOS and Linux by
[`examples.yml`](https://github.com/petrasvestartas/OpenNest/blob/main/.github/workflows/examples.yml). The
native libraries are resolved by bare name, so `nfp_nest.dll` / `nest_physics.dll` (or the `.dylib`/`.so`) must
sit next to the exe — the `.csproj` copies them after build.

---

## Where it's used

- **Grasshopper components** — `component_nest2` / `component_nest1` / `component_nest` build the parts + sheets,
  run on a background thread, and bake. See [OpenNest2](../../components/opennest2.md),
  [OpenNest1](../../components/opennest1.md), [OpenNestCollision](../../components/opennest_collision.md).
- **Rhino command** — `OpenNestCommand` (the `.rhp`) drives the same library. See
  [Rhino Commands](../../rhino/index.md).
