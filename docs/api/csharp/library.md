# Library reference (RhinoCommon)

The high‑level path uses three pieces.

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

## Spacing — offset upstream

The solvers no longer apply `spacing` themselves. To keep a gap, offset the **nesting boundary** before solving —
the original geometry is still what gets placed and output:

```csharp
geo.offset_nesting_boundary(spacing / 2);      // parts: outer grows out, holes shrink in
sheets.offset_sheet_boundary(spacing / 2);     // sheets: outer shrinks in, holes grow out
```

??? info "All members — nest_geo, nest_sheets, geo_to_nest_geo"

    `geo_to_nest_geo` full signature:

    ```csharp
    // hard_coded_input=true uses the ring groups AS GIVEN; false re-pairs outer+holes by containment.
    public static nest_geo geo_to_nest_geo(List<Curve[]> curves, List<int> copies = null,
        List<double> simplify_parameters = null, List<GeometryBase[]> attributes_geometries = null,
        bool hard_coded_input = true, List<int> rotations = null);

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
    | `rotations` | per‑part rotation override (0 = global; N = N orientations; 1 = fixed) |
    | `xforms` | **written by the solver** — placement transform(s) per part |
    | `copies` / `indices` | per‑part copy count / source index |
    | `offset_nesting_boundary(d)` | grow parts' nesting boundary by `d` (gap), output stays original |
    | `identify_groups(...)` | pair outer rings with contained holes |
    | `bake_with_transforms()` | bake placed geometry + attributes to the doc |
    | `duplicate()` | deep copy |

    `nest_sheets`:

    ```csharp
    public nest_sheets(List<List<Polyline>> unsorted_polylines, List<double> gap_xy,
                       List<int> row_count, int copies);
    public void offset_sheet_boundary(double margin);   // inset sheet boundary for spacing
    public Polyline[][] sheets;                          // the sheet outlines
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
