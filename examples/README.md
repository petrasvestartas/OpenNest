# OpenNest standalone examples

Two minimal, Rhino‑free console apps that drive the native OpenNest engines through their plain
**C ABI** — one in **C++**, one in **C#**. Both build the same tiny problem (3 rectangles + a triangle
onto one 150×150 sheet), call `nfp_nest` (NFP + GA) and `np_nest` (physics), and print the placements.

```
examples/
├── CMakeLists.txt        # superbuild: builds the engines + both example apps
├── run_cpp.cmake         # helper used by the `run_examples` target
├── cpp_console/          # main.cpp + CMakeLists.txt   (loads the engine libs at runtime)
└── csharp_console/       # Program.cs + .csproj        (P/Invokes the engine libs)
```

## Build & run (CMake superbuild)

The superbuild uses the same `ExternalProject` pattern as the repo‑root `CMakeLists.txt`: it builds each
engine via its own standalone CMakeLists, then builds the two example apps against the produced libraries.

```bash
cmake -S examples -B examples/build            # add -A x64 on Windows (MSVC)
cmake --build examples/build --config Release
cmake --build examples/build --target run_examples --config Release   # build + run both
```

`run_examples` runs the C++ executable and `dotnet run` for the C# app; each prints its computed layout.

## Run an app on its own

- **C++** — the engine libraries are copied next to `nest_demo` by the build; just run it from that folder.
- **C#** — `dotnet run --project examples/csharp_console -c Release` (the `.csproj` copies the engine
  libraries next to the exe; build the engines first via the superbuild or any prior `src/*/build`).

These two apps are the runnable references for the [C++ API](../docs/api/cpp.md) and
[C# API](../docs/api/csharp.md) docs, and are built & run on Windows, macOS and Linux by
`.github/workflows/examples.yml`.
