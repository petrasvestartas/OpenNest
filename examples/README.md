# OpenNest standalone examples

Two minimal, Rhino‑free console apps that drive the native OpenNest engines through their plain
**C ABI** — one in **C++**, one in **C#**. Both build the same tiny problem (a few parts, one with a
hole, onto a sheet with a hole), nest with `nfp_nest` (NFP + GA) / `np_nest` (physics), and print the
placements.

```
examples/
├── CMakeLists.txt        # superbuild: builds the engines + both example apps
├── run_cpp.cmake         # helper used by the `run_examples` target
├── cpp_console/          # main.cpp + opennest.hpp + CMakeLists.txt  (header-only binding over the C ABI)
└── csharp_console/       # Program.cs + .csproj                      (P/Invokes the engine libs)
```

The C++ app uses **`opennest.hpp`** — a small header‑only binding that mirrors the
[compas_nest](https://petrasvestartas.github.io/compas_nest/) Python API
(`nest_geo.add_part(...)` → `opennest_collision{}.solve(geo, sheets)` → `result.placed_polylines()`).

## Build & run (CMake superbuild)

The superbuild uses the same `ExternalProject` pattern as the repo‑root `CMakeLists.txt`: it builds each
engine via its own standalone CMakeLists, then builds the two example apps against the produced libraries.

```bash
cmake -S examples -B examples/build            # add -A x64 on Windows (MSVC)
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release   # build + run both
```

`run_examples` runs the C++ executable and `dotnet run` for the C# app; each prints its computed layout.

These two apps are the runnable references for the [C++ API](../docs/api/cpp.md) and
[C# API](../docs/api/csharp.md) docs, and are built & run on Windows, macOS and Linux by
`.github/workflows/examples.yml`.
