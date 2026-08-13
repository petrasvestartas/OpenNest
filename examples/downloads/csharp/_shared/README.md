# OpenNest C# example

Self-contained. The run script downloads OpenNest, builds the two 2D engines it uses — `nfp_nest` and
`nest_physics` — from source, and runs the example (Windows, macOS, Linux — needs .NET 8 SDK + CMake +
a C++ compiler). OpenNest's third engine, `nest_spectral` (3D mesh packing), is not used here and is
not built.

- **Windows:** double-click `run.bat`.
- **macOS / Linux:** `./run.command`.
