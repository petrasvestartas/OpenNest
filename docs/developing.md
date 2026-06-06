# Build & Publish

## Build (developers)

All three native C++ engines live in the repo and build from source with **one command** (CMake — MSVC on
Windows, Clang on macOS); the plugin builds with .NET. The Windows commands are shown below; on macOS drop
`-A x64` (CMake selects Clang) and CI builds a **universal** `x86_64 + arm64` dylib.

```bash
# 1) all native engines  ->  src/<engine>/build/Release/{nfp_nest,nest_physics,minkowski}.dll
cmake -S . -B build -A x64
cmake --build build --config Release
#    minkowski needs Boost.Polygon + Eigen headers — add one of:
#    -DBOOST_INCLUDE_DIR=<dir> -DEIGEN_INCLUDE_DIR=<dir>   (e.g. a vcpkg install/x64-windows/include)
#    -DGET_LIBS=ON            (download Boost 1.78 + Eigen 3.4 at build time)
#    -DBUILD_MINKOWSKI=OFF    (skip it)

# 2) the Grasshopper plugin  ->  src/opennest_2/bin/Release/<tfm>/opennest_2.gha
#    (a PostBuild step copies the DLLs next to the .gha)
dotnet build src/opennest_2/opennest_2.csproj -c Release
```

| Folder | Builds | Notes |
|---|---|---|
| `src/opennest_cpp` | `nfp_nest.dll` | NFP/SVGnest GA. Vendored Clipper2 + Boost.Polygon (no external deps). |
| `src/nest_physics_cpp` | `nest_physics.dll` | Physics/overlap solver. Header‑only, no external deps. |
| `src/minkowski` | `minkowski.dll` | Minkowski/NFP helper. Needs Boost + Eigen. |
| `src/opennest_2` | `opennest_2.gha` | The .NET Grasshopper plugin (C#). |

## Publish (maintainer)

A GitHub Actions workflow (**Build & Publish — Windows + macOS → Yak**) builds the **Windows and macOS**
packages and pushes them to the McNeel **Yak** server.

- Trigger: **Actions → Build & Publish → Run workflow**.
- It **auto‑bumps the minor version** on every publish (each push is a clean new version) — no manual
  `manifest.yml` edit.
- Requires a `YAK_TOKEN` repository secret (from `yak login --ci`). Without it, the run still builds and
  attaches the `.yak` to a GitHub Release, but skips the push.
- The **macOS** leg builds a **universal** (x86_64 + arm64) package and publishes alongside Windows.
