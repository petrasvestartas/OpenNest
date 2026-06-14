# Build & Publish

This page is for developers who want to **use** OpenNest from source or **contribute** to it. It covers the toolchains, how to build the two native C++ engines and the three managed plug-ins (Grasshopper 1, the Rhino command, and Grasshopper 2), how to load the result into Rhino, and how publishing + docs deploy.

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| CMake | 3.20+ | Configuring/building the native engines |
| MSVC | Visual Studio 2022 (Windows) | Compiling the C++ DLLs on Windows |
| Clang | default toolchain (macOS) | Compiling the C++ dylibs on macOS |
| .NET SDK | **8.0.x** | Building all three managed plug-ins |

!!! note
    Use the **.NET 8.0.x SDK** even though most targets are `net7.0` / `net48` — that's what CI uses, and the Rhino-9 command + Grasshopper 2 targets are `net8.0`. Building the `net48` target on Windows works because the projects set `EnableWindowsTargeting=true`.

Both engines are **fully self-contained** — no external dependencies. Clipper2 and a minimal Boost.Polygon subset are **vendored** in `src/opennest_cpp`.

## 2. Repository layout

| Folder | Builds / contains |
|---|---|
| `src/opennest_cpp` | `nfp_nest.dll` — NFP/SVGnest GA engine. Vendored Clipper2 (static) + a minimal Boost.Polygon subset. C++17. The C ABI is in `src/capi/nfp_nest_capi.{h,cpp}`. |
| `src/nest_physics_cpp` | `nest_physics.dll` — physics/overlap-relaxation (collision) solver. Header-only, threads only, no external deps. C++20. C ABI in `nest_physics_capi.{h,cpp}`. |
| `src/opennest_2` | `opennest_2.gha` — the C# **Grasshopper 1** plug-in (components) + the shared nesting/`nest_geo`/`nest_sheets` logic the other managed projects reuse. |
| `src/opennest_commands` | `opennest_commands.rhp` — the **Rhino command** plug-in (`OpenNest` command). Reuses `opennest_2` via a project reference. |
| `src/opennest_gh2` | `opennest_gh2.rhp` — the **Grasshopper 2** plug-in (Rhino 9 WIP only). Built against the Rhino-9 GH2 SDK. |
| `tools/` | `gen_example_docs.py` (generates the C++/C# example pages) + `pack_examples.py` (zips the per-language download projects). |
| `examples/downloads/{cpp,csharp,python}` | Self-contained, runnable example projects (one per API example) that are zipped into `docs/api/*/downloads/`. |
| `docs/` | MkDocs Material source. `overrides/` holds the header template override (inline section tabs). |
| `grasshopper_plugin/` | Committed Yak manifests + `.yak` binaries per platform (`opennest_win/`, `opennest_mac/`). |
| `.github/` | CI workflows (`publish.yml`, `docs.yml`), PR/issue templates. |

!!! info "The C++ engine is the single source of truth"
    The Python package **[compas_nest](https://github.com/petrasvestartas/compas_nest)** does **not** keep its own copy of the engine — it vendors *this repo* as a git submodule and compiles `src/opennest_cpp` + `src/nest_physics_cpp` directly. Updating compas_nest to a new engine is just bumping that submodule, so the C++ here is the one source both packages build from.

## 3. Build the native engines

Each engine is its own standalone CMake project, so configure and build them **one folder at a time**. Both produce a self-contained shared library (no `lib` prefix, to match the P/Invoke names).

!!! note "Self-contained DLLs"
    Every engine links the **static MSVC runtime** (`/MT`) on Windows (and `-static-libgcc -static-libstdc++` under GCC), so the resulting DLL/dylib has no external runtime dependency and loads in Rhino without the VC++ redistributable.

### Windows (MSVC, x64)

```powershell
# nfp_nest.dll  ->  src/opennest_cpp/build/Release/nfp_nest.dll
cmake -S src/opennest_cpp -B src/opennest_cpp/build -A x64
cmake --build src/opennest_cpp/build --config Release

# nest_physics.dll  ->  src/nest_physics_cpp/build/Release/nest_physics.dll
cmake -S src/nest_physics_cpp -B src/nest_physics_cpp/build -A x64
cmake --build src/nest_physics_cpp/build --config Release
```

### macOS (Clang)

Drop `-A x64`. To match CI's universal build, pass the OSX arch flag (both engines need it explicitly):

```bash
cmake -S src/opennest_cpp     -B src/opennest_cpp/build     -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build src/opennest_cpp/build --config Release

cmake -S src/nest_physics_cpp -B src/nest_physics_cpp/build -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build src/nest_physics_cpp/build --config Release
```

!!! note "Optional flags"
    `nfp_nest` vendors a minimal Boost.Polygon at `src/opennest_cpp/third_party/boost_min`; override it with `-DBOOST_MIN_INCLUDE_DIR=<dir>` to point at a full Boost install. `nest_physics` can additionally build a standalone CLI with `-DNEST_PHYSICS_BUILD_CLI=ON`.

## 4. Build the managed plug-ins

All three are built with the **.NET 8.0.x SDK**:

```powershell
dotnet build src/opennest_2/opennest_2.csproj             -c Release   # GH1 components (.gha)
dotnet build src/opennest_commands/opennest_commands.csproj -c Release   # Rhino command (.rhp)
dotnet build src/opennest_gh2/opennest_gh2.csproj          -c Release   # Grasshopper 2 (.rhp, Rhino 9)
```

| Project | Output | Target frameworks | Loaded by |
|---|---|---|---|
| `opennest_2` | `opennest_2.gha` | `net7.0-windows`, `net7.0`, `net48` | Grasshopper 1 (Rhino 7/8, and classic GH in Rhino 9) |
| `opennest_commands` | `opennest_commands.rhp` | `net8.0-windows`, `net8.0`, `net7.0-windows`, `net7.0`, `net48` | Rhino's command line (the `net8`/RhinoCommon-9 flavor is what loads on Rhino 9) |
| `opennest_gh2` | `opennest_gh2.rhp` | `net8.0-windows`, `net8.0` | Grasshopper 2 (Rhino 9 WIP only), against the R9 GH2 SDK |

A `PostBuild` target copies each native engine library next to the `.gha` (guarded by `Exists(...)`, so a missing source is silently skipped):

| Native lib | Windows source | macOS source |
|---|---|---|
| `nest_physics` | `nest_physics.dll` (staged next to the csproj) | `../nest_physics_cpp/build/nest_physics.dylib` |
| `nfp_nest` | `../opennest_cpp/build/Release/nfp_nest.dll` | `../opennest_cpp/build/nfp_nest.dylib` |

!!! warning "nest_physics.dll is copied from a different place"
    On Windows, `nest_physics.dll` is copied from the **project root next to the csproj** — *not* from a `build/Release/` folder like `nfp_nest`. After building `nest_physics`, stage its DLL there yourself; otherwise the `Exists(...)` guard skips it and the physics P/Invoke fails at runtime.

## 5. Run it locally

### Users — Rhino Package Manager

Run `_PackageManager`, search **OpenNest**, install. The Package Manager picks the distribution matching your Rhino (it installs on Rhino 8 **and** 9), delivering the **Grasshopper 1 components** and the **`OpenNest` Rhino command**. No build required.

### Grasshopper 2 (Rhino 9 WIP) — manual load

GH2 is **not** auto-registered (a Yak-registered GH2 `.rhp` crashes Rhino's plug-in manager). The package drops the GH2 files in a `grasshopper2/` subfolder; you **load `opennest_gh2.rhp` by hand inside Grasshopper 2**. See the [Grasshopper 2 page](components/grasshopper2.md) for the walkthrough.

### Developers — load your local build

Close Rhino, then copy the built `.gha` + the native DLLs into the Grasshopper **Libraries** folder (`%AppData%\Grasshopper\Libraries`), and the `.rhp`s where Rhino/GH2 expect them.

!!! warning "Quit Rhino fully before refreshing"
    Rhino file-locks the loaded `.gha`/`.rhp` and native DLLs while running, so a rebuild cannot overwrite the installed copy. **Close Rhino completely** before copying a new build in, then restart.

## 6. How publishing works

Publishing is automated by the **Build & Publish (Windows + macOS → Yak)** workflow (`.github/workflows/publish.yml`).

- **Trigger:** every **push to `main`** auto-publishes a new version. A manual **Actions → Run workflow** is also available, with a `bump` input (`minor` / `patch` / `major`).
- **Version auto-bump:** the canonical version is `grasshopper_plugin/opennest_win/manifest.yml`; the base is the **max** of that and the live Yak version, bumped to a 4-part `X.Y.Z.0` string in both manifests.
- **Build matrix:** the `mac` job builds the engines as **universal** (`x86_64;arm64`) dylibs; the `windows` job (`needs: mac`) builds the Windows x64 DLLs + all three plug-ins, then assembles **four Yak distributions of one package** on the Windows runner.
- **Four distributions, one package:** the same `OpenNest` version ships as `rh8_0-win`, `rh8_0-mac` (net7 / RhinoCommon-8 command) and `rh9_0-win`, `rh9_0-mac` (net8 / RhinoCommon-9 command). The Package Manager installs the one matching the running Rhino — so GH1 + the command auto-install on **Rhino 8 and 9, Windows and macOS**.
- **Grasshopper 2 rides along, never auto-loaded:** `opennest_gh2.rhp` is staged into each package's `grasshopper2/` subfolder (a plain download), so Rhino's plug-in manager never registers it — the user loads it manually in GH2 (Rhino 9 only).
- **Release + tag:** the run commits the bumped manifests + refreshed `.yak` binaries, tags `v<version>`, and creates a GitHub Release with the `.yak` files attached. The Yak **push** is skipped when `YAK_TOKEN` is absent; everything else still runs.
- **`[skip ci]` guard:** the bot's release commit ends with `[skip ci]`, so its own commit to `main` doesn't re-trigger the run.

!!! warning "git pull after every push to main"
    CI pushes a release commit (version bump + refreshed packages) back to `main`, so your local `main` falls behind after each push. Run `git pull --rebase` before your next push.

## 7. Maintainer setup

1. **Workflow permissions** — Settings → Actions → General → **Read and write permissions** (both workflows need `contents: write`).
2. **`YAK_TOKEN` secret** — Settings → Secrets and variables → Actions. Generate with `yak login --ci`. Without it the run still builds, bumps, tags, and releases — only the Yak push is skipped. Yak tokens expire; re-run `yak login --ci` if pushes start being skipped.
3. **Pages source** — Settings → Pages → Source = **Deploy from a branch**, branch = **`gh-pages`**.

## 8. How the docs deploy

The **Docs** workflow (`.github/workflows/docs.yml`) builds and deploys this site.

- **Trigger:** push to `main` touching `docs/**`, `mkdocs.yml`, or the workflow (also `workflow_dispatch`).
- **What it does:** on `ubuntu-latest`, installs `mkdocs-material` and runs `mkdocs gh-deploy --force --no-history`, force-pushing a single-commit `gh-pages` branch that GitHub Pages serves.
- **Example pages are generated:** the C++/C# example pages + the per-language download zips are produced by `tools/gen_example_docs.py` and `tools/pack_examples.py` — run them after changing an example under `examples/downloads/`.

The published site is at <https://petrasvestartas.github.io/OpenNest/>.

## 9. Contributing

1. Work on a feature/fix **branch** (or a fork).
2. Open a **pull request** using `.github/PULL_REQUEST_TEMPLATE.md`: builds locally (native engines from source + the `.gha`), tested in Rhino with real geometry, docs updated if behavior/inputs/options changed, and no prebuilt binaries committed (CI builds from source).
3. File **issues** with the templates in `.github/ISSUE_TEMPLATE/` (attach a `.gh`/`.ghx` or `.3dm` for bugs).

!!! note
    Don't commit built binaries — CI builds everything from source and publishes on merge to `main`.

OpenNest is released under the **MIT License** (2019–2026 Petras Vestartas).
