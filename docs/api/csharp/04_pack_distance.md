# 04 · Pack distance

Lay parts out in a grid: wrap to a new row at a max width.

Project: [Windows](downloads/04_pack_distance_win.zip) · [macOS](downloads/04_pack_distance_mac.zip)

```csharp
// 04 pack distance — lay parts out in a grid: wrap to a new row at a max width.
using System;
using System.Runtime.InteropServices;

static class Program {
    [DllImport("nfp_nest", CallingConvention = CallingConvention.Cdecl)]
    static extern int nfp_pack(int part_count, int[] pvc, double[] pxy, int[] pqty,
        int columns, double gap_x, double gap_y, double max_width,
        double[] tx, double[] ty, double[] ang, int[] sid);

    static void Main() {
        int[] pvc = { 4, 4, 3 };
        double[] pxy = { 0,0, 30,0, 30,12, 0,12,  0,0, 20,0, 20,20, 0,20,  0,0, 24,0, 0,24 };
        int[] pqty = { 4, 4, 4 };                       // 12 instances

        var tx = new double[12]; var ty = new double[12]; var ang = new double[12]; var sid = new int[12];
        int n = nfp_pack(3, pvc, pxy, pqty, columns: 0, gap_x: 2.0, gap_y: 2.0, max_width: 120.0,  // distance mode
                         tx, ty, ang, sid);

        Console.WriteLine($"packed {n} instances (wrap at x=120)");
        for (int i = 0; i < n; i++) Console.WriteLine($"  ({tx[i]:F1}, {ty[i]:F1})");
    }
}
```
