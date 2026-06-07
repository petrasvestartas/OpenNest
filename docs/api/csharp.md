# C# API (wrappers + nesting library)

The C# side has two layers you can build on:

1. **P/Invoke wrappers** — thin, blittable bindings to the native engines (closest to the [C++ C ABI](cpp.md)).
2. **Rhino nesting library** — `nest_rhino_lib` (geometry intake) + `nest_lib` (the solver driver) that turn
   RhinoCommon geometry into a nest and read the placements back.

All of it ships inside `opennest_2.gha` (also referenced by the `.rhp` command plug‑in and the console demo).

---

## Layer 1 — P/Invoke wrappers

### `NfpNest` — `nfp_nest.dll`

`src/opennest_2/nest_lib/NfpNestWrapper/NfpNestWrapper.cs`

```csharp
namespace NfpNest;

[StructLayout(LayoutKind.Sequential)] public struct NfpParams { /* see the C++ NfpParams table */ }

public static class NfpNestWrapper
{
    // Returns placed-instance count (>=0) or negative on error. Buffers length = sum(part_quantities).
    public static extern int  nfp_nest(int part_count, int[] part_vertex_counts, double[] part_xy,
        int[] part_quantities, int[] part_hole_counts, int[] part_hole_vertex_counts, double[] part_hole_xy,
        int sheet_count, int[] sheet_vertex_counts, double[] sheet_xy, int[] sheet_hole_counts,
        int[] sheet_hole_vertex_counts, double[] sheet_hole_xy, ref NfpParams parameters,
        double[] out_tx, double[] out_ty, double[] out_angle, int[] out_sheet_id, int[] out_part_index,
        out int out_n_sheets, out double out_fitness);

    public static extern void   nfp_cancel();
    public static extern void   nfp_cancel_reset();
    public static extern long   nfp_progress();      // GA generation reached
    public static extern double nfp_fitness();       // best fitness so far
    public static extern int    nfp_poll_layout(int instance_count, double[] out_tx, double[] out_ty,
        double[] out_angle, int[] out_sheet_id, int[] out_part_index, out int out_n_sheets);
}
```

### `NestPhysics` — `nest_physics.dll`

`src/opennest_2/nest_lib/NestPhysicsWrapper/NestPhysicsWrapper.cs`

```csharp
namespace NestPhysics;

[StructLayout(LayoutKind.Sequential)] public struct NpParams { /* see the C++ NpParams table */ }

public static class NestPhysicsWrapper
{
    public static extern int  np_nest(int part_count, int[] part_vertex_counts, double[] part_xy,
        int sheet_count, int[] sheet_outer_vertex_counts, double[] sheet_outer_xy, int[] sheet_hole_counts,
        int[] hole_vertex_counts, double[] hole_xy, int[] part_hole_counts, int[] part_hole_vertex_counts,
        double[] part_hole_xy, ref NpParams parameters,
        double[] out_tx, double[] out_ty, double[] out_angle, int[] out_sheet_id, out int out_n_sheets);

    public static extern void np_cancel();
    public static extern void np_cancel_reset();
    public static extern long np_progress();          // relaxation rounds done
    public static extern int  np_poll_layout(int part_count, double[] out_tx, double[] out_ty,
        double[] out_angle, int[] out_sheet_id, out int out_n_sheets);
}
```

The `NfpParams` / `NpParams` fields are identical to the native structs — see the field tables on the
[C++ API page](cpp.md). The library name is the bare `"nfp_nest"` / `"nest_physics"`, resolved to the platform
DLL/dylib shipped next to the `.gha`.

---

## Layer 2 — Rhino nesting library

### `nest_rhino_lib` — geometry intake

`nest_geo_util` (`nest_rhino_lib/nest_geo_util.cs`) — build a `nest_geo` from Rhino geometry:

```csharp
namespace nest_rhino_lib;

// curves: one Curve[] per PART (outer ring first, then hole rings). attributes_geometries: optional
// per-part geometry that travels with the part. hard_coded_input=true uses the groups AS GIVEN;
// false re-pairs outer+holes by containment.
public static nest_geo geo_to_nest_geo(List<Curve[]> curves, List<int> copies = null,
    List<double> simplify_parameters = null, List<GeometryBase[]> attributes_geometries = null,
    bool hard_coded_input = true);

// Build from RhinoDoc objects on a layer; auto-identifies outer+hole groups by containment.
public static nest_geo guid_to_nest_geo(List<Guid[]> guids, string boundary_layer = "outlines",
    List<int> copies = null, List<double> simplify_parameters = null);
```

`nest_geo` (`nest_rhino_lib/nest_geo.cs`) — the part collection the solver reads and writes:

| Member | Kind | Description |
| --- | --- | --- |
| `boundary_sorted` | field `List<List<Tuple<int,Polyline,BoundingBox,Curve>>>` | per‑part rings: outer first then holes; tuple = `(sourceIndex, nestingPolyline, bbox, originalCurve)` |
| `geometry` | field `List<GeometryBase>` | the input geometry per ring |
| `geometry_sorted` | field `List<List<int>>` | global geometry indices grouped per part |
| `geometry_attributes` | field `List<GeometryBase[]>` | attribute geometry carried with each part |
| `attributes` | field `List<ObjectAttributes>` | Rhino object attributes (colour/layer/material/name) per ring |
| `xforms` | field `List<List<Transform>>` | **written by the solver** — placement transform(s) per part |
| `copies` / `indices` | field `List<int>` | per‑part copy count / source index |
| `curve_to_polyline(curve, segment_division_length=0, hull=false, keep_all=false)` | method | curve → nesting polyline; `0 + keep_all` = exact (every vertex) |
| `identify_groups(seg=0, hull=false)` | method | pair outer rings with contained holes by containment |
| `hard_coded_input(ids, seg=0, hull=false)` | method | use a pre‑grouped ring set as one part |
| `bake_with_transforms()` | method | bake the placed (transformed) geometry + attributes to the doc |
| `offset_boundaries(distance, keepOriginal=true, flip=true)` | method | offset rings for spacing/gap |
| `duplicate()` | method | deep copy |

`nest_sheets` (`nest_rhino_lib/nest_sheets.cs`) — the sheets to nest onto:

```csharp
// unsorted_polylines: one List<Polyline> per sheet (outer + holes). gap_xy: array layout gaps.
public nest_sheets(List<List<Polyline>> unsorted_polylines, List<double> gap_xy, List<int> row_count, int copies);
public void offset(double distance);   // offset sheet boundaries for spacing
// public field: Polyline[][] sheets;
```

### `nest_lib` — the solver driver

`rhino_example` (`nest_lib/Rhino_Implementation/rhino_example.cs`) — drives a nest end to end:

```csharp
namespace nest_lib;

public rhino_example(ref nest_rhino_lib.nest_sheets nest_sheets, ref nest_rhino_lib.nest_geo geometry,
                     List<double> parameters, int max_iterations = 3);

public string Engine = "cpp";   // "cpp" = native nfp_nest.dll (default); "cs" = managed SvgNest
public int    TryAllRotations;  // 1 = score every rotation per placement (tightest, slower)
public int    ExactNfp = 1;     // 1 = full-resolution exact NFP (no gap); 0 = simplify+dilate (fast)
public int    UseHoles = 1;     // 1 = nest small parts into larger parts' holes

public void static_solver(ref nest_rhino_lib.nest_geo geometry);   // run the solve (background-thread safe)

// results
public List<List<Polyline>>  output_sheets;             // sheet outlines used
public List<List<Transform>> output_transforms;         // placement transform(s) per part
public List<List<int>>       output_polygon_sheet_ids;  // sheet index per placement (-1 = unplaced)

// live preview (read from a UI timer while static_solver runs on a worker)
public volatile int          CurrentGeneration;         // generation / round reached
public int                   TotalGenerations { get; }  // == max_iterations
public double                CurrentFitness;            // best fitness (display only)
public List<Polyline>        LiveSheets;                 // layout-frame sheet outlines
public List<Polyline>        LiveBorders { get; }        // live tightening borders (swapped ref)
public volatile bool         StopRequested;              // set true to cancel the solve
```

`rhino_conversions` (`nest_lib/Rhino_Implementation/rhino_conversions.cs`):

```csharp
public static Polyline[] BrepLoops(Brep b);   // planar Brep -> outer + hole loops (outer first)
public static Polyline[] MeshLoops(Mesh m);   // mesh -> XY outline polylines
```

---

## End‑to‑end example

The path the Grasshopper components and the `OpenNest` command use:

```csharp
// 1) Build the parts (one Curve[] per part: outer ring + optional hole rings) and the sheets.
var geo = nest_rhino_lib.nest_geo_util.geo_to_nest_geo(
    curvesGrouped,                 // List<Curve[]>
    copies,                        // List<int> (1 per part)
    new List<double> { 0, 0 },     // simplify: 0 = take the boundary exactly (no merge)
    attributesPerPart,             // List<GeometryBase[]> or null
    hard_coded_input: true);       // use the groups as given (holes come from the rings you supply)

var sheets = new nest_rhino_lib.nest_sheets(sheetPolylines, new List<double> { gap, gap },
                                            new List<int>(), sheetPolylines.Count);

// 2) Configure + run. parameters is positional:
//    [0]=rotations [1]=wiggle [2]=placementType [3]=spacing [4]=seed
//    [5]=simplifyTolerance [6]=mutation [7]=population [8]=time
var parameters = new List<double> { 8, 0, 1, 0, 30, 1, 10, 10, 0 };
var nest = new nest_lib.rhino_example(ref sheets, ref geo, parameters, max_iterations: 10);
nest.Engine = "cpp";
nest.TryAllRotations = 1;
nest.UseHoles = 1;
nest.static_solver(ref geo);       // run on a worker thread to keep the UI responsive

// 3) Read the placements: geo.xforms[part][copy] (or nest.output_transforms[part][copy]),
//    and nest.output_polygon_sheet_ids[part][copy] for the sheet each instance landed on.
```

For the **physics engine**, drive `NestPhysics.NestPhysicsWrapper.np_nest(...)` directly (the
`NpRun` helper in `component_nest.cs` shows the flatten → solve → assemble sequence).

---

## Entry points (where this API is used)

- **Grasshopper components** — `component_nest2` / `component_nest1` / `component_nest` build a `nest_geo` +
  `nest_sheets`, run `rhino_example`/`NpRun` on a background thread, and bake the result. See
  [OpenNest2](../components/opennest2.md), [OpenNest1](../components/opennest1.md),
  [OpenNestCollision](../components/opennest_collision.md).
- **Rhino command** — `OpenNestCommand` (the `.rhp`) drives the same library from the command line. See
  [Rhino Commands](../rhino/index.md).
