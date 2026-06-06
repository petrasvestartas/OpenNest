# `src/` — source layout

Three projects: one C# Grasshopper plugin and two native C++ nesting engines it calls through
P/Invoke. Everything builds from source, **cross‑platform** — Windows (x64) and macOS
(universal: Intel `x86_64` + Apple Silicon `arm64`). Both engines are self‑contained (no external
dependencies — Clipper2 and a minimal Boost.Polygon subset are vendored in `opennest_cpp`).

| Folder | What it does | Output |
| --- | --- | --- |
| `opennest_2/` | The **Grasshopper plugin** (C#): the components, the UI, geometry intake, and the wrappers that drive the engines. | `opennest_2.gha` |
| `opennest_cpp/` | The **`nfp_nest`** engine (C++): nests parts with a no‑fit‑polygon genetic algorithm. | `nfp_nest.dll` / `nfp_nest.dylib` |
| `nest_physics_cpp/` | The **`nest_physics`** engine (C++): packs parts by overlap‑relaxation — it lets parts overlap, then slides them apart until they fit. | `nest_physics.dll` / `nest_physics.dylib` |

## Building the native engines

Each engine is a standalone CMake project; a top‑level `CMakeLists.txt` superbuild builds both at once.

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

It multi‑targets `net48` (Rhino 7), `net7.0-windows` (Rhino 8 Windows), and `net7.0` (Rhino 8 macOS).

## CI

`.github/workflows/publish.yml` builds **both** platforms — Windows libraries on `windows-latest`
and universal macOS dylibs on `macos-latest` — and publishes a **separate Yak package per OS**
(`…-rh8_0-win` and `…-rh8_0-mac`) at the same version. `.github/workflows/mac.yml` is a build‑only
macOS check that verifies the dylibs are universal (`lipo -archs`).
