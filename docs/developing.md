# Build & Publish

This page is for developers who want to **use** OpenNest from source or **contribute** to it. It covers the toolchains you need, how to build the two native C++ engines and the Grasshopper plugin, how to load the result into Rhino, and how publishing/docs deploy.

## 1. Prerequisites

| Tool | Version | Used for |
|---|---|---|
| CMake | 3.20+ | Configuring/building the native engines |
| MSVC | Visual Studio 2022 (Windows) | Compiling the C++ DLLs on Windows |
| Clang | default toolchain (macOS) | Compiling the C++ dylibs on macOS |
| .NET SDK | **8.0.x** | Building the C# Grasshopper plugin |

!!! note
    The plugin targets `net7.0` / `net48` runtimes, but you build it with the **.NET 8.0.x SDK** (this is what CI uses). The .NET 7 SDK alone is not sufficient. Building the `net48` target on Windows works because the project sets `EnableWindowsTargeting=true`.

Both engines are **fully self-contained** — no external dependencies. Clipper2 and a minimal Boost.Polygon subset are **vendored** in `src/opennest_cpp`.

## 2. Repository layout

| Folder | Builds / contains |
|---|---|
| `src/opennest_cpp` | `nfp_nest.dll` — NFP/SVGnest GA engine. Vendored Clipper2 (static) + a minimal Boost.Polygon subset. C++17. |
| `src/nest_physics_cpp` | `nest_physics.dll` — physics/overlap solver. Header-only, threads only, no external deps. C++20. |
| `src/opennest_2` | `opennest_2.gha` — the .NET / C# Grasshopper plugin. |
| `docs/` | MkDocs Material source (`index.md`, `developing.md`, `references.md`, `components/`). |
| `grasshopper_plugin/` | Packaged Yak distributions per platform (`opennest_win/`, `opennest_mac/`): `.yak` binaries + `manifest.yml`. |
| `examples/` | Downloadable Grasshopper definitions + `.3dm` tutorial files. |
| `icons/` | Icon design source assets. |
| `.github/` | CI workflows, PR/issue templates. |

## 3. Build the native engines

Each engine is its own standalone CMake project, so configure and build them **one folder at a time**. Both produce a self-contained shared library (no `lib` prefix, to match the P/Invoke names).

!!! note "Self-contained DLLs"
    Every engine links the **static MSVC runtime** (`/MT`) on Windows (and `-static -static-libgcc -static-libstdc++` under MinGW/GCC), so the resulting DLL/dylib has no external runtime dependency and loads in Rhino without the VC++ redistributable.

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

## 4. Build the Grasshopper plugin

The plugin is `src/opennest_2/opennest_2.csproj`, built with the **.NET 8.0.x SDK**. It multi-targets three frameworks:

| TFM | Loaded by |
|---|---|
| `net7.0-windows` | Rhino 8 on Windows (CI ships **only** this one for the Windows Yak package) |
| `net7.0` | Rhino 8 on macOS |
| `net48` | Rhino 7 |

It references the McNeel `Grasshopper` meta-package (v8.0.23304.9001), which pulls in RhinoCommon transitively — there is no explicit `RhinoCommon` reference.

Build all targets:

```powershell
dotnet build src/opennest_2/opennest_2.csproj -c Release
```

The `.gha` lands per TFM at:

- `src/opennest_2/bin/Release/net7.0-windows/opennest_2.gha` (Rhino 8 Windows)
- `src/opennest_2/bin/Release/net7.0/opennest_2.gha` (Rhino 8 macOS)
- `src/opennest_2/bin/Release/net48/opennest_2.gha` (Rhino 7)

A `PostBuild` target copies each native engine library next to the `.gha` (guarded by `Exists(...)`, so a missing source is silently skipped):

| Native lib | Windows source | macOS source |
|---|---|---|
| `nest_physics` | `nest_physics.dll` (next to the csproj) | `../nest_physics_cpp/build/nest_physics.dylib` |
| `nfp_nest` | `../opennest_cpp/build/Release/nfp_nest.dll` | `../opennest_cpp/build/nfp_nest.dylib` |

!!! warning "nest_physics.dll is copied from a different place"
    On Windows, `nest_physics.dll` is copied from the **project root next to the csproj** — *not* from a `build/Release/` folder like `nfp_nest`. After building `nest_physics`, stage its DLL there yourself; otherwise the `Exists(...)` guard skips it and the physics P/Invoke fails at runtime.

## 5. Run it locally

### Users — Rhino Package Manager

In Rhino 8, run `_PackageManager`, search for **OpenNest**, and install. This downloads the latest release from the McNeel Yak server — no build required.

### Developers — load your local build

Copy (or symlink) the built `.gha` and the native DLLs next to it into the Grasshopper **Libraries** folder:

```
%AppData%\Grasshopper\Libraries
```

i.e. `C:\Users\<you>\AppData\Roaming\Grasshopper\Libraries`.

!!! warning "Quit Rhino fully before refreshing"
    Rhino file-locks the loaded `.gha` and native DLLs while it is running, so a rebuild cannot overwrite the installed copy. **Close Rhino completely** before copying a new build into the Libraries folder, then restart it.

## 6. How publishing works

Publishing is fully automated by the **Build & Publish (Windows + macOS → Yak)** workflow (`.github/workflows/publish.yml`).

- **Trigger:** every **push to `main`** auto-publishes a new version (there is no path filter). A manual **Actions → Build & Publish → Run workflow** is also available, with a `bump` input (`minor` / `patch` / `major`, default `minor`). Push events always behave as `minor`.
- **Version auto-bump:** the canonical version lives in `grasshopper_plugin/opennest_win/manifest.yml`. The base is the **max** of that committed version and the live version on the Yak server, and the bump is written as a 4-part `X.Y.Z.0` string into both the Windows and macOS manifests.
- **Build matrix:** the `mac` job builds the three engines as **universal** (`x86_64;arm64`) dylibs; the `windows` job (which `needs: mac`) builds the Windows x64 DLLs, builds the plugin, then assembles **both** Yak packages on the Windows runner — the macOS `.yak` is built there from the `net7.0` managed build plus the universal dylibs (the Windows DLLs are removed first).
- **Release + tag:** the run commits the bumped manifests + refreshed `.yak` binaries under `grasshopper_plugin/`, tags `v<version>`, and creates a GitHub Release with both `.yak` files attached. This happens **even without** a Yak token — only the push to the Yak server is skipped when the token is absent.
- **`[skip ci]` guard:** the bot's release commit message ends with `[skip ci]`, so the workflow's own commit to `main` does **not** re-trigger the push-on-main run (no infinite loop).

!!! warning "git pull after every push to main"
    Because CI pushes a release commit (version bump + refreshed packages) back to `main`, your local `main` falls behind after each push. Run `git pull --rebase` before your next push to avoid a non-fast-forward rejection.

!!! note
    Publishes are serialized (`concurrency: publish-yak`, no cancel), and the job rebases onto `main` before tagging — so closely-spaced pushes queue instead of racing on the version bump.

## 7. Maintainer setup

Three one-time GitHub settings are required:

1. **Workflow permissions** — Settings → Actions → General → Workflow permissions → **Read and write permissions** (both workflows need `contents: write` to push commits/tags and the `gh-pages` branch).
2. **`YAK_TOKEN` repository secret** — Settings → Secrets and variables → Actions. Generate it with `yak login --ci` and paste the value.
   - Without the secret the run still builds, bumps, tags, and creates the Release — only the **push to Yak** is skipped (the step is guarded by `if: env.YAK_TOKEN != ''`).
   - Yak tokens expire; if pushes start being skipped, re-run `yak login --ci` and update the secret.
3. **Pages source** — Settings → Pages → Source = **Deploy from a branch**, branch = **`gh-pages`** (set this after the first successful Docs run, below).

## 8. How the docs deploy

The **Docs** workflow (`.github/workflows/docs.yml`) builds and deploys this site.

- **Trigger:** push to `main` that touches `docs/**`, `mkdocs.yml`, or `.github/workflows/docs.yml` (also runnable manually via `workflow_dispatch`).
- **What it does:** on `ubuntu-latest` it installs `mkdocs-material` and runs:

  ```bash
  mkdocs gh-deploy --force --no-history
  ```

  This builds the site and force-pushes a single-commit `gh-pages` branch, which GitHub Pages serves.

The published site is at <https://petrasvestartas.github.io/OpenNest/>.

## 9. Contributing

1. Work on a feature/fix **branch** (or a fork).
2. Open a **pull request** using `.github/PULL_REQUEST_TEMPLATE.md`. The checklist covers: builds locally (native engines from source + the `.gha`), tested in Rhino 8 / Grasshopper with real geometry, docs updated if behavior/inputs/options changed, and no prebuilt binaries committed (CI builds from source).
3. File **issues** with the templates in `.github/ISSUE_TEMPLATE/`:
   - `bug_report.md` — environment, repro steps, and a `.gh`/`.ghx` or `.3dm` attachment.
   - `feature_request.md` — use case, desired behavior, alternatives considered.

!!! note
    Don't commit built binaries — CI builds everything from source and publishes on merge to `main`.

OpenNest is released under the **MIT License** (2019–2026 Petras Vestartas).