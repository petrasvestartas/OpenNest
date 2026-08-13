# `src/` — source layout

**Three** native C++ nesting engines, plus the managed plug‑ins that call them through P/Invoke.
Everything builds from source, **cross‑platform** — Windows (x64) and macOS
(universal: Intel `x86_64` + Apple Silicon `arm64`). Every engine is self‑contained (no external
dependencies — Clipper2 and a minimal Boost.Polygon subset are vendored in `opennest_cpp`,
pocketfft in `nest_spectral_cpp`).

| Folder | What it does | Output |
| --- | --- | --- |
| `opennest_cpp/` | The **`nfp_nest`** engine (C++): nests parts with a no‑fit‑polygon genetic algorithm, with a MaxRects fast path for all‑rectangle jobs. | `nfp_nest.dll` / `nfp_nest.dylib` |
| `nest_physics_cpp/` | The **`nest_physics`** engine (C++): packs parts by overlap‑relaxation — it lets parts overlap, then slides them apart until they fit. | `nest_physics.dll` / `nest_physics.dylib` |
| `nest_spectral_cpp/` | The **`nest_spectral`** engine (C++): 3D mesh nesting by spectral/FFT packing (the `OpenNest3D` component). | `nest_spectral.dll` / `nest_spectral.dylib` |
| `opennest_2/` | The **Grasshopper 1 plugin** (C#): the components, the UI, geometry intake, and the wrappers that drive the engines. | `opennest_2.gha` |
| `opennest_commands/` | The **Rhino command** plug‑in (`OpenNest`). Reuses `opennest_2` via a project reference. | `opennest_commands.rhp` |
| `opennest_gh2/` | The **Grasshopper 2** plug‑in (Rhino 9 WIP only). | `opennest_gh2.rhp` |
| `opennest_console/` | Standalone console harness (`net9.0`) for driving the engines outside Rhino. | `opennest_console` (exe) |

## Building the native engines

Each engine is a standalone CMake project. The top‑level `CMakeLists.txt` superbuild builds **all three**
(`nfp_nest`, `nest_physics`, `nest_spectral`) in one shot — each into its own
`src/<engine>/build/Release/`, which is where `opennest_2.csproj` looks for them (it warns if
`nest_spectral` is missing). You can still configure and build any one engine on its own; see
[Build & Publish §3](https://petrasvestartas.github.io/OpenNest/developing/#3-build-the-native-engines).

**Windows (x64):**

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
```

**macOS (universal Intel + Apple Silicon):**

```bash
cmake -S . -B build -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64" -DCMAKE_BUILD_TYPE=Release
cmake --build build
```

## Building the plugin

`dotnet build` produces the `.gha`; its PostBuild step copies the matching native libraries
(`.dll` on Windows, `.dylib` on macOS) next to it so the P/Invoke calls resolve.

```bash
dotnet build src/opennest_2/opennest_2.csproj -c Release
```

It multi‑targets `net7.0-windows` (Rhino 8/9 Windows on .NET Core), `net48` (Rhino 8 Windows on
.NET Framework 4.8) and `net7.0` (Rhino 8/9 macOS). All three compile against the **Grasshopper 8**
SDK — `net48` is the Rhino‑8/.NET‑Framework build, not a Rhino 7 build.

## CI

`.github/workflows/publish.yml` builds **both** platforms — Windows libraries on `windows-latest`
and universal macOS dylibs on `macos-latest` — and publishes **one `OpenNest` package in four Yak
distributions** at the same version: `rh8_0-win`, `rh9_0-win`, `rh8_0-mac`, `rh9_0-mac`. There is no
`rh7_0` distribution. `.github/workflows/mac.yml` is a build‑only macOS check that verifies the
dylibs are universal (`lipo -archs`).
