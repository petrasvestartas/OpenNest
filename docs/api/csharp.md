# C# API

## Runnable example

[`examples/csharp_console`](https://github.com/petrasvestartas/OpenNest/tree/main/examples/csharp_console) is a
standalone, Rhino‑free console that `P/Invoke`s **both** engines directly (the low‑level *Talk to the engines
directly* path shown further down). Build and run it with the CMake **superbuild** in
[`examples/`](https://github.com/petrasvestartas/OpenNest/tree/main/examples):

```bash
cmake -S examples -B examples/build                                  # add -A x64 on Windows
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release  # builds + runs both apps
```

It's built & run on Windows, macOS and Linux by
[`examples.yml`](https://github.com/petrasvestartas/OpenNest/blob/main/.github/workflows/examples.yml).

---

## Using the library (RhinoCommon)

The high‑level path uses RhinoCommon; **3 steps**: build the parts + sheets, run the solver, read where each part went.

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

That's the whole flow. The `parameters` list is positional:
`[0]=rotations [1]=wiggle [2]=placementType [3]=spacing [4]=seed [5]=simplifyTol [6]=mutation [7]=population [8]=time`.

Everything ships inside `opennest_2.gha` (also used by the `.rhp` command and the console demo).

---

## The 3 key pieces

| Use this | For | File |
| --- | --- | --- |
| `nest_geo_util.geo_to_nest_geo(...)` | turn Rhino curves into parts | `nest_rhino_lib/nest_geo_util.cs` |
| `nest_sheets(...)` | the sheets to pack onto | `nest_rhino_lib/nest_sheets.cs` |
| `rhino_example` | run the solve + read results | `nest_lib/Rhino_Implementation/rhino_example.cs` |

`rhino_example` has a few switches and the live‑preview fields you read while it solves:

```csharp
nest.UseHoles = 1;            // nest small parts into bigger parts' holes
nest.TryAllRotations = 1;     // try every rotation (tighter, slower)

nest.CurrentGeneration;       // read on a UI timer to show progress
nest.LiveSheets;              // current layout to draw mid-solve
nest.StopRequested = true;    // cancel the solve
```

---

??? info "All members — nest_geo, nest_sheets, geo_to_nest_geo"

    `geo_to_nest_geo` full signature:

    ```csharp
    // hard_coded_input=true uses the ring groups AS GIVEN; false re-pairs outer+holes by containment.
    public static nest_geo geo_to_nest_geo(List<Curve[]> curves, List<int> copies = null,
        List<double> simplify_parameters = null, List<GeometryBase[]> attributes_geometries = null,
        bool hard_coded_input = true);

    // Build straight from RhinoDoc objects on a layer (auto-pairs outer + holes by containment).
    public static nest_geo guid_to_nest_geo(List<Guid[]> guids, string boundary_layer = "outlines",
        List<int> copies = null, List<double> simplify_parameters = null);
    ```

    `nest_geo` — the part collection the solver reads and writes:

    | Member | Description |
    | --- | --- |
    | `boundary_sorted` | per‑part rings (outer first, then holes); tuple `(srcIndex, polyline, bbox, curve)` |
    | `geometry` / `geometry_sorted` | input geometry per ring / grouped per part |
    | `geometry_attributes` | attribute geometry carried with each part |
    | `attributes` | Rhino object attributes (colour/layer/material/name) per ring |
    | `xforms` | **written by the solver** — placement transform(s) per part |
    | `copies` / `indices` | per‑part copy count / source index |
    | `identify_groups(...)` | pair outer rings with contained holes |
    | `bake_with_transforms()` | bake placed geometry + attributes to the doc |
    | `offset_boundaries(...)` / `duplicate()` | offset rings for gap / deep copy |

    `nest_sheets`:

    ```csharp
    public nest_sheets(List<List<Polyline>> unsorted_polylines, List<double> gap_xy,
                       List<int> row_count, int copies);
    public void offset(double distance);     // offset sheet boundaries for spacing
    public Polyline[][] sheets;              // the sheet outlines
    ```

??? info "All members — rhino_example + rhino_conversions"

    ```csharp
    public rhino_example(ref nest_rhino_lib.nest_sheets sheets, ref nest_rhino_lib.nest_geo geometry,
                         List<double> parameters, int max_iterations = 3);

    public int    TryAllRotations;  // 1 = score every rotation per placement
    public int    ExactNfp = 1;     // 1 = exact NFP (no gap); 0 = simplify+dilate (fast)
    public int    UseHoles  = 1;    // 1 = nest parts into holes

    public void static_solver(ref nest_rhino_lib.nest_geo geometry);   // run the solve

    // results
    public List<List<Transform>> output_transforms;        // placement transform(s) per part
    public List<List<int>>       output_polygon_sheet_ids; // sheet per placement (-1 = unplaced)
    public List<List<Polyline>>  output_sheets;            // sheet outlines used

    // live preview (read from a UI timer while static_solver runs on a worker)
    public volatile int   CurrentGeneration;     public int    TotalGenerations { get; }
    public double         CurrentFitness;        public List<Polyline> LiveSheets;
    public List<Polyline> LiveBorders { get; }   public volatile bool  StopRequested;
    ```

    `rhino_conversions` — turn Rhino solids into nesting outlines:

    ```csharp
    public static Polyline[] BrepLoops(Brep b);   // planar Brep -> outer + hole loops
    public static Polyline[] MeshLoops(Mesh m);   // mesh -> XY outline polylines
    ```

??? info "Talk to the engines directly (P/Invoke wrappers)"

    The thin bindings under the library — use these only if you want to skip `rhino_example`.
    The `NfpParams` / `NpParams` fields match the native structs on the [C++ API page](cpp.md).
    Both `nfp_nest` and `np_nest` take an optional `part_rotations[]` array (one int per part:
    `0` = use the global rotations setting, `N>0` = only N orientations, `1` = fixed; `null` = no
    overrides) — this is what the Geometry component's per-part **Rotations** input feeds.

    ```csharp
    // namespace NfpNest — nfp_nest.dll
    NfpNestWrapper.nfp_nest(...);            // returns placed-instance count
    NfpNestWrapper.nfp_pack(...);            // simple row/grid layout (no nesting)
    NfpNestWrapper.nfp_offset_polygon(...);  // grow/shrink one polygon (Clipper2)
    NfpNestWrapper.nfp_progress();           // GA generation reached
    NfpNestWrapper.nfp_poll_layout(...);     // best layout so far
    NfpNestWrapper.nfp_cancel();             // stop early

    // namespace NestPhysics — nest_physics.dll
    NestPhysicsWrapper.np_nest(...);         // returns 0 on success
    NestPhysicsWrapper.np_progress();        // relaxation rounds done
    NestPhysicsWrapper.np_poll_layout(...);
    NestPhysicsWrapper.np_cancel();
    ```

    Files: `src/opennest_2/nest_lib/NfpNestWrapper/NfpNestWrapper.cs`,
    `.../NestPhysicsWrapper/NestPhysicsWrapper.cs`.

---

## Where it's used

- **Grasshopper components** — `component_nest2` / `component_nest1` / `component_nest` build the parts + sheets,
  run on a background thread, and bake. See [OpenNest2](../components/opennest2.md),
  [OpenNest1](../components/opennest1.md), [OpenNestCollision](../components/opennest_collision.md).
- **Rhino command** — `OpenNestCommand` (the `.rhp`) drives the same library. See [Rhino Commands](../rhino/index.md).
