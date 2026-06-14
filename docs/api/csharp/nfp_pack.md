# `nfp_pack` (P/Invoke) — simple row/grid layout

A tidy grid, no nesting and no search — the `compas_nest` `pack()` semantics. **array mode** (`max_width <= 0`):
wrap every `columns` items. **distance mode** (`max_width > 0`): wrap when the next part would exceed `max_width`.
Angle is always `0`, sheet id always `0`.

## Declaration

```csharp
[DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
static extern int nfp_pack(
    int part_count, int[] part_vertex_counts, double[] part_xy, int[] part_quantities,
    int columns, double gap_x, double gap_y, double max_width,
    double[] out_tx, double[] out_ty, double[] out_angle, int[] out_sheet_id);
```

## Example

```csharp
// 3 parts, 4 copies each, into rows of 5 with a 2-unit gap
var pvc = new List<int>(); var pxy = new List<double>();
void Rect(double w, double h) { pvc.Add(4); pxy.AddRange(new[] { 0d, 0, w, 0, w, h, 0, h }); }
Rect(30, 12); Rect(20, 20);
pvc.Add(3); pxy.AddRange(new[] { 0d, 0, 24, 0, 0, 24 });

int partCount = pvc.Count;
var pqty = new int[partCount]; for (int i = 0; i < partCount; i++) pqty[i] = 4;
int instances = 0; foreach (var q in pqty) instances += q;

var tx = new double[instances]; var ty = new double[instances];
var ang = new double[instances]; var sid = new int[instances];

int n = nfp_pack(partCount, pvc.ToArray(), pxy.ToArray(), pqty,
    columns: 5, gap_x: 2.0, gap_y: 2.0, max_width: 0.0,   // array mode
    tx, ty, ang, sid);

Console.WriteLine($"packed {n} instances");
for (int i = 0; i < instances; i++) Console.WriteLine($"   ({tx[i],6:F1}, {ty[i],6:F1})");
```

For **distance mode**, pass `columns: 0, max_width: 200` — it wraps whenever the next part would pass `x = 200`.
