# OpenNest C++ example

Self-contained. The first build downloads OpenNest and compiles the two 2D engines it uses — `nfp_nest`
and `nest_physics` — from source (Windows, macOS, Linux — no prebuilt binaries needed). OpenNest's third
engine, `nest_spectral` (3D mesh packing), is not used by these examples and is not built.

- **Windows:** double-click `run.bat` (needs CMake + Visual Studio).
- **macOS / Linux:** `./run.command` (needs CMake + a C++17 compiler).
