# C++ API

Two native engines through a plain **C ABI**. You give part + sheet polygons; you get back, per
part, *move by (tx, ty)*, *rotate by an angle*, and *which sheet*. Angle is **degrees** for
`nfp_nest`, **radians** for `np_nest`; `sheet_id == -1` means the part didn't fit.

Each example below is a self-contained project — download it, run `run.bat` (Windows) or
`run.command` (macOS); the first build compiles the OpenNest engines from source.

| # | Example |
| --- | --- |
| 01 | [Collision](01_collision.md) — Nest parts (one with a hole) into a sheet (with a hole) with the physics (collision) engine |
| 02 | [NFP + GA](02_nfp.md) — Nest parts into a sheet with the NFP + genetic-algorithm engine (handles concave parts + holes) |
| 03 | [Live animation (NFP)](03_live.md) — Run the NFP engine on a background thread and poll the evolving best layout (progress, fitness, live poses) |
| 04 | [Clearance offset](04_offset.md) — Grow every part and shrink the sheet by a clearance with nfp_offset_polygon, then nest the offset geometry |
| 05 | [Attributes](05_attributes.md) — Carry a point (a part's centroid) and read it at the placed pose: attributes move with the part |
| 06 | [Pack (array)](06_pack_array.md) — Lay parts out in a deterministic grid: a fixed number of columns per row (no nesting) |
| 07 | [Pack (distance)](07_pack_distance.md) — Lay parts out in a grid, wrapping to a new row once a row reaches a maximum width |
| 08 | [Text (font)](08_text.md) — Render a label to single-stroke engraving polylines using the bundled OpenNest VDA font |
