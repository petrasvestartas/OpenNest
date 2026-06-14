# C# API

P/Invoke the two native engines directly (no Rhino). You give part + sheet polygons as flat arrays; you get
back, per part, *move by (tx, ty)*, *rotate by an angle*, and *which sheet*.

Each example below is a self-contained project — download it, run `run.bat` (Windows) or `run.command`
(macOS); the run script builds the engines from source and runs the example (needs the .NET 8 SDK + CMake).

| # | Example |
| --- | --- |
| 01 | [Collision](01_collision.md) — physics nest (`np_nest`) |
| 02 | [NFP](02_nfp.md) — NFP + GA nest (`nfp_nest`) |
| 03 | [Pack array](03_pack_array.md) — grid, columns per row (`nfp_pack`) |
| 04 | [Pack distance](04_pack_distance.md) — grid, wrap at width (`nfp_pack`) |
| 05 | [Offset](05_offset.md) — grow/shrink a polygon (`nfp_offset_polygon`) |

Inside Rhino/Grasshopper the high-level `nest_geo_util` / `nest_sheets` / `rhino_example` API is used instead
(see the components). Angle is **degrees** for `nfp_nest`, **radians** for `np_nest`; `sheet_id == -1` means
the part didn't fit.
