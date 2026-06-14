# `nfp_nest` (P/Invoke) — clean polygon packing

NFP + genetic algorithm, called directly with no Rhino. Returns the number of placed instances; outputs are
caller‑allocated, one slot **per instance** (`sum(part_quantities)`). Angle is in **degrees**. The field meanings
match the [native `NfpParams` struct](../cpp/nfp_nest.md).

## Declaration

```csharp
[StructLayout(LayoutKind.Sequential)]
public struct NfpParams
{
    public int placementType, rotations, mutationRate, populationSize, seed;
    public double curveTolerance, clipperScale, spacing, sheetSpacing, rotationLimit;
    public int useHoles, exploreConcave, clipByHull, clipByRects, simplify,
               mode, generations, numSeeds, useParallel;
    public double timeBudgetSecs;
    public int maxSheets, edgeSamples, compactionPasses, tryAllRotations, exactNfp;
}

[DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
static extern int nfp_nest(
    int part_count, int[] part_vertex_counts, double[] part_xy, int[] part_quantities,
    int[] part_rotations,   // per-part rotation override (0 = global, 1 = fixed; null ok)
    int[] part_hole_counts, int[] part_hole_vertex_counts, double[] part_hole_xy,
    int sheet_count, int[] sheet_vertex_counts, double[] sheet_xy,
    int[] sheet_hole_counts, int[] sheet_hole_vertex_counts, double[] sheet_hole_xy,
    ref NfpParams parameters,
    double[] out_tx, double[] out_ty, double[] out_angle,
    int[] out_sheet_id, int[] out_part_index, out int out_n_sheets, out double out_fitness);
```

## Example

```csharp
// 3 rectangles + a triangle, 3 copies each, onto one 150x150 sheet
var pvc = new List<int>(); var pxy = new List<double>();
void Rect(double w, double h) { pvc.Add(4); pxy.AddRange(new[] { 0d, 0, w, 0, w, h, 0, h }); }
Rect(30, 12); Rect(20, 20); Rect(40, 8);
pvc.Add(3); pxy.AddRange(new[] { 0d, 0, 24, 0, 0, 24 });          // a triangle

int partCount = pvc.Count;
var pqty = new int[partCount]; for (int i = 0; i < partCount; i++) pqty[i] = 3;
var phc  = new int[partCount];                                    // no part holes

var svc = new List<int> { 4 };
var sxy = new List<double> { 0, 0, 150, 0, 150, 150, 0, 150 };
int sheetCount = 1; var shc = new int[sheetCount];

int instances = 0; foreach (var q in pqty) instances += q;

var p = new NfpParams
{
    placementType = 1, rotations = 4, mutationRate = 10, populationSize = 10, seed = 1,
    curveTolerance = 0.3, clipperScale = 1e7, mode = 1, generations = 10, useParallel = 1
};
var tx = new double[instances]; var ty = new double[instances]; var ang = new double[instances];
var sid = new int[instances]; var pidx = new int[instances];

int placed = nfp_nest(partCount, pvc.ToArray(), pxy.ToArray(), pqty, null, phc, null, null,
    sheetCount, svc.ToArray(), sxy.ToArray(), shc, null, null,
    ref p, tx, ty, ang, sid, pidx, out int nSheets, out double fitness);

Console.WriteLine($"nfp_nest : placed {placed}/{instances} on {nSheets} sheet(s), fitness {fitness:F3}");
for (int i = 0; i < instances; i++)
    Console.WriteLine($"   part {pidx[i]} -> sheet {sid[i]}  ({tx[i],7:F2}, {ty[i],7:F2}) @ {ang[i],5:F1} deg");
```

To place instance `i`: rotate the source part `pidx[i]` by `ang[i]°` about the origin, then translate by
`(tx[i], ty[i])` (sheet‑local), on sheet `sid[i]` (`-1` = didn't fit).

> Inside the plugin these wrappers live in `nest_lib/NfpNestWrapper/NfpNestWrapper.cs`, with
> `nfp_progress` / `nfp_poll_layout` / `nfp_cancel` for live preview.
