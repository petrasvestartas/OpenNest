# `np_nest` (P/Invoke) — physics packing (into holes)

The penetration‑depth engine, called directly with no Rhino. Denser than NFP and parts can nest **into** holes.
One output slot **per part** (original order). Returns `0` on success. Angle is in **radians**. The field meanings
match the [native `NpParams` struct](../cpp/np_nest.md).

## Declaration

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NpParams
{
    public int num_rotations;
    public double spacing, simplify_tolerance;   // spacing is IGNORED (offset upstream)
    public int seed;
    public double time_budget_secs;
    public long iter_budget;
    public int iter_mode, max_sheets, n_starts, part_holes_mode, pole_max, final_compact, fit_mode;
}

[DllImport("nest_physics", CallingConvention = CallingConvention.Cdecl)]
static extern int np_nest(
    int part_count, int[] part_vertex_counts, double[] part_xy,
    int[] part_rotations,   // per-part rotation override (0 = free continuous; null ok)
    int sheet_count, int[] sheet_outer_vertex_counts, double[] sheet_outer_xy,
    int[] sheet_hole_counts, int[] hole_vertex_counts, double[] hole_xy,
    int[] part_hole_counts, int[] part_hole_vertex_counts, double[] part_hole_xy,
    ref NpParams parameters,
    double[] out_tx, double[] out_ty, double[] out_angle, int[] out_sheet_id, out int out_n_sheets);
```

## Example

```csharp
// reuse pvc/pxy/svc/sxy from the nfp_nest example — physics nests one slot PER PART
int partCount = pvc.Count;
var phc = new int[partCount];                 // no part holes
int sheetCount = 1; var shc = new int[sheetCount];

var q = new NpParams
{
    num_rotations = 16, seed = 1,
    iter_mode = 0, time_budget_secs = 2.0,     // 0 = wall-clock budget
    n_starts = 1, max_sheets = 6, fit_mode = 0
};
var ntx = new double[partCount]; var nty = new double[partCount];
var nang = new double[partCount]; var nsid = new int[partCount];

int rc = np_nest(partCount, pvc.ToArray(), pxy.ToArray(), null,
    sheetCount, svc.ToArray(), sxy.ToArray(), shc, null, null,
    phc, null, null, ref q, ntx, nty, nang, nsid, out int nSheets);

Console.WriteLine($"np_nest  : rc={rc}, {nSheets} sheet(s)");
for (int i = 0; i < partCount; i++)
    Console.WriteLine($"   part {i} -> sheet {nsid[i]}  ({ntx[i],7:F2}, {nty[i],7:F2}) @ {nang[i],6:F3} rad");
```

!!! note "`spacing` is ignored"
    The physics engine does not offset parts. Apply gaps upstream with
    `nest_geo.offset_nesting_boundary(gap/2)` and `nest_sheets.offset_sheet_boundary(gap/2)` before solving.

> Inside the plugin these wrappers live in `nest_lib/NestPhysicsWrapper/NestPhysicsWrapper.cs`, with
> `np_progress` / `np_poll_layout` / `np_cancel` for live preview.
