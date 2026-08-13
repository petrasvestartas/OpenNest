# Build & Publish

This page is for developers who want to **use** OpenNest from source or **contribute** to it. It covers the toolchains, how to build the three native C++ engines and the three managed plug-ins (Grasshopper 1, the Rhino command, and Grasshopper 2), how to load the result into Rhino, and how publishing + docs deploy.

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| CMake | 3.20+ | Configuring/building the native engines |
| MSVC | Visual Studio 2022 (Windows) | Compiling the C++ DLLs on Windows |
| Clang | default toolchain (macOS) | Compiling the C++ dylibs on macOS |
| .NET SDK | **8.0.x** | Building all three managed plug-ins |

!!! note
    Use the **.NET 8.0.x SDK** even though most targets are `net7.0` / `net48` — that's what CI uses, and the Rhino-9 command + Grasshopper 2 targets are `net8.0`. Building the `net48` target on Windows works because the projects set `EnableWindowsTargeting=true`.

All three engines are **fully self-contained** — no external dependencies. Everything they need is **vendored** in-repo: Clipper2 and a minimal Boost.Polygon subset in `src/opennest_cpp`, pocketfft in `src/nest_spectral_cpp`.

## 2. Repository layout

| Folder | Builds / contains |
|---|---|
| `src/opennest_cpp` | `nfp_nest.dll` — NFP/SVGnest GA engine. Vendored Clipper2 (static) + a minimal Boost.Polygon subset. C++17. The C ABI is in `src/capi/nfp_nest_capi.{h,cpp}`. |
| `src/nest_physics_cpp` | `nest_physics.dll` — physics/overlap-relaxation (collision) solver. Header-only, threads only, no external deps. C++20. C ABI in `nest_physics_capi.{h,cpp}`. |
| `src/nest_spectral_cpp` | `nest_spectral.dll` — 3D mesh nesting by spectral/FFT packing (the `OpenNest3D` component). Vendored header-only pocketfft; CPU by default, optional cuFFT backend with `-DNEST_SPECTRAL_CUDA=ON`. C++17. C ABI in `nest_spectral_capi.{h,cpp}`. |
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

Each engine is its own standalone CMake project, so you *can* configure and build them **one folder at a time**. Each produces a self-contained shared library (no `lib` prefix, to match the P/Invoke names). Build **all three** — the `.gha` copies whichever ones it finds and warns about the rest (see [§4](#4-build-the-managed-plug-ins)).

!!! tip "Or build all three at once"
    The repo-root `CMakeLists.txt` is an `ExternalProject` superbuild over the three standalone projects (their own `CMakeLists.txt` files are used unchanged), and it drops each library in exactly the `src/<engine>/build/Release/` path §4 expects:

    ```powershell
    cmake -S . -B build -A x64
    cmake --build build --config Release
    ```

    Drop `-A x64` on macOS. The per-folder commands below are still the way to pass per-engine flags such as `-DNEST_SPECTRAL_CUDA=ON` or `-DCMAKE_OSX_ARCHITECTURES`.

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

# nest_spectral.dll  ->  src/nest_spectral_cpp/build/Release/nest_spectral.dll
cmake -S src/nest_spectral_cpp -B src/nest_spectral_cpp/build -A x64
cmake --build src/nest_spectral_cpp/build --config Release
```

### macOS (Clang)

Drop `-A x64`. To match CI's universal build, pass the OSX arch flag (every engine needs it explicitly):

```bash
cmake -S src/opennest_cpp      -B src/opennest_cpp/build      -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build src/opennest_cpp/build --config Release

cmake -S src/nest_physics_cpp  -B src/nest_physics_cpp/build  -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build src/nest_physics_cpp/build --config Release

cmake -S src/nest_spectral_cpp -B src/nest_spectral_cpp/build -DCMAKE_OSX_ARCHITECTURES="x86_64;arm64"
cmake --build src/nest_spectral_cpp/build --config Release
```

!!! note "Optional flags"
    `nfp_nest` vendors a minimal Boost.Polygon at `src/opennest_cpp/third_party/boost_min`; override it with `-DBOOST_MIN_INCLUDE_DIR=<dir>` to point at a full Boost install. `nest_physics` can additionally build a standalone CLI with `-DNEST_PHYSICS_BUILD_CLI=ON`. `nest_spectral` builds CPU-only by default; `-DNEST_SPECTRAL_CUDA=ON` adds the cuFFT backend (needs the CUDA toolkit to build, but not to run — the DLL still loads and falls back to the CPU path on machines without a GPU).

## 4. Build the managed plug-ins

All three are built with the **.NET 8.0.x SDK**:

```powershell
dotnet build src/opennest_2/opennest_2.csproj             -c Release   # GH1 components (.gha)
dotnet build src/opennest_commands/opennest_commands.csproj -c Release   # Rhino command (.rhp)
dotnet build src/opennest_gh2/opennest_gh2.csproj          -c Release   # Grasshopper 2 (.rhp, Rhino 9)
```

| Project | Output | Target frameworks | Loaded by |
|---|---|---|---|
| `opennest_2` | `opennest_2.gha` | `net7.0-windows`, `net7.0`, `net48` | Grasshopper 1 (Rhino 8, and classic GH in Rhino 9). All three TFMs build against the **Grasshopper 8** SDK — `net48` is the Rhino-8/.NET-Framework build, **not** a Rhino 7 build |
| `opennest_commands` | `opennest_commands.rhp` | `net8.0-windows`, `net8.0`, `net7.0-windows`, `net7.0`, `net48` | Rhino's command line (the `net8`/RhinoCommon-9 flavor is what loads on Rhino 9) |
| `opennest_gh2` | `opennest_gh2.rhp` | `net8.0-windows`, `net8.0` | Grasshopper 2 (Rhino 9 WIP only), against the R9 GH2 SDK |

A `PostBuild` target copies each native engine library next to the `.gha`, straight out of its CMake build directory (guarded by `Exists(...)`, so a missing source is skipped — with a build warning, see below):

| Native lib | Windows source | macOS source |
|---|---|---|
| `nest_physics` | `../nest_physics_cpp/build/Release/nest_physics.dll` | `../nest_physics_cpp/build/nest_physics.dylib` |
| `nfp_nest` | `../opennest_cpp/build/Release/nfp_nest.dll` | `../opennest_cpp/build/nfp_nest.dylib` |
| `nest_spectral` | `../nest_spectral_cpp/build/Release/nest_spectral.dll` | `../nest_spectral_cpp/build/nest_spectral.dylib` |

!!! warning "Build the engines before the plug-in"
    Nothing is staged by hand any more. `nest_physics.dll` used to be copied from a **checked-in DLL next to the csproj**, so a rebuilt engine never reached the `.gha` (and never reached users); that file is gone and the copy now reads `build/Release/` like its siblings. Skip step 3 and each missing engine produces a build warning — `<engine> engine not built - <component> will fail at runtime` — and the corresponding P/Invoke throws `DllNotFoundException` once you run the component in Rhino.

## 5. Run it locally

### Users — Rhino Package Manager

Run `_PackageManager`, search **OpenNest**, install. The Package Manager picks the distribution matching your Rhino (it installs on Rhino 8 **and** 9), delivering the **Grasshopper 1 components** and the **`OpenNest` Rhino command**. No build required.

!!! note "Both Rhino 8 .NET runtimes are covered"
    Rhino 8 on Windows runs on either .NET Core or .NET Framework 4.8 (`_SetDotNetRuntime`), and a .NET Core `.gha` cannot load under .NET Framework. The `rh8_0-win` package therefore ships **both** builds in per-framework folders (`net48/` and `net7.0/`), so the components appear whichever runtime Rhino is on — **no manual runtime switch**. See [§6](#6-how-publishing-works).

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
- **`rh8_0-win` becomes .NET multi-targeted in the next release:** it is the only configuration where two runtimes exist, so that package uses Yak's per-framework folders (supported since Rhino 8.2, [RH-76604](https://mcneel.myjetbrains.com/youtrack/issue/RH-76604)) instead of a flat root:

    ```text
    opennest-<next version>-rh8_0-win.yak   # yak lowercases the package name and drops the manifest's
                                            # trailing 4th component: 2.93.0.0 -> opennest-2.93.0-…
    ├── manifest.yml          # at the ROOT, outside the framework folders
    ├── icon.png
    ├── net48/                # .NET Framework 4.8 payload  — .gha + managed deps + 3 engines + .rhp
    ├── net7.0/               # .NET Core payload           — .gha + deps.json/runtimeconfig + 3 engines + .rhp
    └── grasshopper2/         # unchanged manual-load download (not a framework name, so Rhino skips it)
    ```

    !!! note "The committed 2.93.0 packages are still flat"
        This tree describes what the workflow stages **from the next release onwards**. Unzip the
        `opennest-2.93.0-rh8_0-win.yak` committed under `grasshopper_plugin/opennest_win/` and it is flat —
        `manifest.yml`, `icon.png`, `LICENSE`, `README.md`, `opennest_2.gha` + its `.deps.json`/`.runtimeconfig.json`/`.pdb`,
        the three engine DLLs, `opennest_commands.rhp` + its two JSON files, and `grasshopper2/`. No `net48/`, no `net7.0/`.

    Rhino picks the folder matching the running runtime. **The package root must contain no `.rhp`/`.gha`** — a single loadable file there switches Rhino back to root-only probing and the `net48/` build is ignored again. The workflow asserts this after staging. `net7.0` is the folder name from McNeel's own guide and rolls forward to newer .NET (the payload is the `net7.0-windows` build).
- **The other three stay flat, on purpose:** `rh9_0-win` because `opennest_commands` has no RhinoCommon-**9** `net48` build — its `net48` TFM resolves RhinoCommon through the `Grasshopper 8.0.x` package, so a `net48/` folder there would ship a `.rhp` built against RhinoCommon 8, which Rhino 9 refuses to load (a `.rhp`'s load is gated on the SDK it was built against, unlike a `.gha`). Rhino 9's `/netfx` path is deprecated by McNeel anyway. `rh8_0-mac` / `rh9_0-mac` are flat because macOS has no .NET Framework at all — Rhino for Mac is .NET Core only, so there is no second runtime to target.
- **Grasshopper 2 rides along, never auto-loaded:** `opennest_gh2.rhp` is staged into each package's `grasshopper2/` subfolder (a plain download), so Rhino's plug-in manager never registers it — the user loads it manually in GH2 (Rhino 9 only). There is **no separate `OpenNest_GH2` Yak package**; a GH2 `.rhp` registered as a Yak plug-in makes Rhino's plug-in manager crash on startup.
- **The guid keywords are hand-written:** `yak build` derives its guid keyword by inspecting the package **root** only, and the multi-targeted `rh8_0-win` root is empty by design — so yak emits `keywords: []`, warns once and still exits 0. Grasshopper's [Package Restore](https://developer.rhino3d.com/guides/yak/package-restore-in-grasshopper/) matches a missing component by plug-in name and then plug-in ID, and our name (`opennest_2`) never matches the package name (`OpenNest`), so those ids are the only restore path. They are declared by hand in both `grasshopper_plugin/*/manifest.yml`, and the **Verify Yak package contents** step re-reads them out of each built `.yak` and fails the release if either is gone.
- **Every shipped package is re-opened and checked before it is pushed.** Releases up to and including 2.93.0.0 shipped an `rh8_0-win` package with a *single* .NET Core payload in a flat root — `opennest_2.gha` and `opennest_commands.rhp` both carrying `TargetFrameworkAttribute .NETCoreApp,Version=v7.0`, and no `net48/` anywhere — which is why Rhino 8 Windows users left on the .NET Framework runtime saw the package install but no components, and had to run `_SetDotNetRuntime`. Nothing in the release failed while producing it. So **Verify Yak package contents** now unzips each `.yak` and, for every framework payload the workflow staged, asserts: the `.gha`, the `.rhp` and all three native engines are present; the assembly's own `TargetFrameworkAttribute` matches the folder it sits in (a `net48/` folder holding a .NET Core build is rejected, as is a `net48/` folder containing a `runtimeconfig.json`); the `net48/` payload carries the six BCL assemblies .NET Framework 4.8 does not provide (`System.Buffers`, `System.Memory`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`, `System.Drawing.Common`, `System.Resources.Extensions`) — without them the `.gha` throws `FileNotFoundException` on load, which fails exactly as visibly as shipping no `net48/` at all; and that every file the build emitted for that TFM survived into the zip. A hard-coded policy list additionally requires `rh8_0-win` to be multi-targeted, so *deleting* the framework-folder arguments from its `Build-Pkg` call fails the release instead of quietly reintroducing the bug.
- **Release + tag:** the run prepends this version's entry to `CHANGELOG.md` (the commit subjects since the previous tag — so don't hand-maintain that file, just write clear commit subjects), commits the bumped manifests + refreshed `.yak` binaries + the changelog, tags `v<version>`, and creates a GitHub Release with the `.yak` files attached. The Yak **push** is skipped when `YAK_TOKEN` is absent; everything else still runs.
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
