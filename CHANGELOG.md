# Changelog

All notable changes to **OpenNest**, newest first. Version numbers match the Yak / Rhino Package
Manager releases (`https://yak.rhino3d.com/packages/opennest`) and the `v<version>` git tags /
GitHub Releases. Each entry describes what the release actually changed.

> New releases are appended automatically by the publish workflow (it lists the commits made since
> the previous tag), so a clear commit subject per push becomes the changelog line — refine an entry
> here anytime if you want more detail.

## [2.71.0.0] - 2026-06-08
- rewrite CHANGELOG entries as plain-language descriptions of what each release actually changed (from the diffs), not raw commit subjects

## [2.70.0.0] - 2026-06-08
- Expanded `CHANGELOG.md` from grouped summaries to one entry per released version (v2.41 through v2.69), generated from git history.

## [2.69.0.0] - 2026-06-08
- Added a `CHANGELOG.md` (backfilled from release history) to the repository.
- Publish CI now auto-maintains the changelog by prepending the commits made since the previous tag on every release, and enables GitHub auto-generated release notes on the Release page.

## [2.68.0.0] - 2026-06-08
- Reversed the previous split: stopped publishing the `OpenNest_GH2` package entirely because the Rhino 8 Grasshopper 2 SDK is a dead-end WIP; the main OpenNest package is now Grasshopper-2-free.
- Grasshopper 2 support is deferred to return later as a separate package built against the Rhino 9 SDK; CI no longer builds or pushes the GH2 packages.

## [2.67.0.0] - 2026-06-08
- Split the Grasshopper 2 plug-in out into its own separate `OpenNest_GH2` Yak package so that Grasshopper-1-only users no longer receive `opennest_gh2.rhp` and hit the `Grasshopper2` load error.
- Publish CI updated to build and push two packages (OpenNest and OpenNest_GH2) for Windows and macOS, with the GH2 build guarded so a pre-alpha SDK failure can't block the main release.

## [2.66.0.0] - 2026-06-08
- OpenNest1 (NFP nester): parts that don't fit on any sheet are now left at their original input position and orientation instead of being parked off-sheet.
- Such unplaced parts are now correctly reported with sheet id -1 (taken from the solver's actual placement) rather than guessed from bounding-box containment.
- Changed the OpenNest1 default number of rotations from 2 to 4 (both the GH1 and GH2 components).

## [2.65.0.0] - 2026-06-07
- README only: added a Python section pointing to the `compas_nest` COMPAS plugin (`pip install compas_nest`), which drives the same engines from Python and the Rhino 8 Script Editor.
- No engine or component code changes.

## [2.64.0.0] - 2026-06-07
- Documentation and icon assets only: swapped the Grasshopper 1 doc page screenshot for a new nesting image and updated the OpenNest icon artwork.
- No engine or component code changes.

## [2.63.0.0] - 2026-06-07
- Added Grasshopper 1 and Grasshopper 2 documentation pages (with screenshots and downloadable example zips) and re-added the standalone C++/C# example apps plus their CMake superbuild.
- Gave the Grasshopper 2 Surface component the same Offset clearance and Attributes ports as the main Geometry component.
- Fixed the Grasshopper 2 Unroll component: all inputs are now whole-tree so it runs once and stops reporting "No geometry" when a valid Brep was wired.

## [2.62.0.0] - 2026-06-07
- Grasshopper 2 plug-in now actually ships inside the OpenNest package: dual-target build (net7.0-windows + portable net7.0) co-located with the GH1 components and Rhino command, so one install delivers all three.
- Grasshopper 2 Geometry/Surface components gained an Offset clearance input and expandable Attributes ports; Unroll now unrolls text labels and accepts surfaces/extrusions/boxes with crash-hardening; Pack Objects became tree-based (one row per branch); removed the Bounding Box Edges component.
- Reorganized the example definitions folder (per-component subfolders, GH2 `.ghx`/`.ghz` files).

## [2.61.0.0] - 2026-06-07
- Fixed OpenNest1 mapping bug where separate part curves sitting inside one another were wrongly swallowed as holes and lost their placement; each input curve is now treated as its own no-hole part (holes come only from surface input).
- Fixed a per-part index corruption in `nest_geo` where every part group reused the first curve's source index, and removed the Tries input (now a fixed single run).
- Added automatic sheet-overflow: the sheet set is duplicated to the right (sized to part area) so parts that don't fit spill onto copies; added a Reset input to the Grasshopper 2 OpenNest1 component.

## [2.60.0.0] - 2026-06-07
- Fixed the Grasshopper 2 nest launch/publish path to route through Grasshopper's normal solution machinery (DelayedExpire) so the viewport result and any downstream previews actually rebuild when a solve finishes.
- Documentation: reorganized the runnable-example sections of the C++ and C# API pages into proper sections with copy-paste build-and-run commands.

## [2.59.0.0] - 2026-06-07
- Reworked the Grasshopper 2 nesting solver into a one-shot async flow: the Run button launches a background solve, returns immediately, animates a live tightening preview, then publishes the result and shows the placed parts in the viewport without baking.
- Grasshopper 2 Geometry component now supports per-part Copies (one value for all, or one per part) and draws its prepared part borders directly in the viewport.
- Added standalone C++ and C# console example apps plus a CI workflow that builds and runs them on Windows, macOS and Linux; linked them from the C++/C# API docs.

## [2.58.0.0] - 2026-06-07
- Grasshopper 2 port largely completed: added the remaining components (Geometry-from-Rhino, Simplify, Pack Objects, Inscribed Circle, Region Slits, Box Packing, PCA, Text, Transform Object, Rhino Objects, Unroll) and a shared `NestComponentBase` for the solver components plus their upgraders.
- Grasshopper 2 solver components gained live on-canvas solve feedback: a status line and Run/Stop on the component body, ESC/stop that keeps the best-so-far result, and an animated viewport preview during the solve.
- Documentation: home page now presents OpenNest as three routes (Grasshopper, Rhino command, Python), and the C++ API page added a "How a polygon crosses the boundary" section.

## [2.57.0.0] - 2026-06-07
- Grasshopper 2 Geometry/Sheets/Merge/sheet-surface inputs now accept whole data trees like GH1: a flat list treats each curve as a part with auto-detected holes, while a data tree treats each branch as one pre-grouped part or sheet.
- Grasshopper 2 Geometry now passes the Simplify value through verbatim (matching GH1) instead of computing a segment length, with the merge-near-colinear default restored.
- OpenNestCollision in GH2 now serializes the process-global native `nest_physics` solve with a static lock and single-threaded mode so two solves can't enter the engine at once.

## [2.56.0.0] - 2026-06-07
- Grasshopper 2 port: added the OpenNest1 component plus input/utility components (Merge, Sheets-from-surfaces, Bounding Box Edges, etc.) and more GH1-to-GH2 upgraders.
- Added downloadable GH2 example files (`.ghz`) for every component and expanded the Python API docs with three full worked examples (collision engine, NFP+GA, attributes that travel with the part).

## [2.55.0.0] - 2026-06-07
- Documentation: added a Python API page showing nesting via the `compas_nest` package (including the Rhino 8 Script Editor) and substantially rewrote/tightened the C++ and C# API pages.
- Grasshopper 2 port: added the OpenNest2 (NFP + genetic algorithm) component with its nine embedded options, GH1-to-GH2 upgraders, and a one-click batch converter for GH1 example files to GH2 `.ghz`.

## [2.54.0.0] - 2026-06-07
- Documentation: added a C++ API page (native C ABI for `nfp_nest` and `nest_physics`, calling convention and placement contract) and a C# API page (P/Invoke wrappers plus the `nest_rhino_lib`/`nest_lib` nesting library).
- Grasshopper 2 icons now render as crisp true-vector icons from embedded SVG at runtime, with embedded PNG fallbacks added for each component.

## [2.53.0.0] - 2026-06-07
- Added a standalone Rhino **OpenNest** command (no Grasshopper needed): pick sheets then one mixed selection of parts and markup, it classifies by geometry/containment, nests on a background thread, and bakes results into layers carrying each part's color/material/name/groups.
- Started the Grasshopper 2 (GH2) port: new `opennest_gh2` plug-in with Geometry, Sheets and OpenNestCollision components, runtime SVG-to-vector icons, and a fresh set of SVG component icons.
- Docs: added a "Nesting in Rhino" tutorial page (with example `.3dm` and step images) for the new command; CI now publishes a version-agnostic Yak package.

## [2.52.0.0] - 2026-06-06
- OpenNest1 component rewritten to run the no-fit-polygon solve on a background thread with a live per-generation viewport preview, so Rhino no longer freezes; Run is now a toggle (auto re-runs on input change) with instant Reset and multi-start Tries.
- Added an EngineGate that serializes solves sharing a non-reentrant native engine (one gate for `nfp_nest` used by OpenNest1/OpenNest2, one for `nest_physics` used by OpenNestCollision), so multiple nesting components in a document queue and run in order instead of colliding.
- OpenNest2 and OpenNestCollision gained a latched live mode with debounced auto-restart on input change and a cached last-result that re-emits on re-expire instead of blanking.

## [2.51.0.0] - 2026-06-06
- Reworked the PCA component to produce a true edge-aligned oriented bounding box: a minimum-area rectangle for flat/planar point sets and a minimum-volume box for solids (robust to symmetry, unlike the old covariance axes); updated its description and outputs accordingly.
- Added a new standalone, Rhino-free `opennest_console` demo tool that drives both nesting engines (`nest_physics` and `nfp_nest`) directly via their C ABIs, nests the 47 example shadoks polygons onto a 510×635 sheet, and writes colored SVG results.

## [2.50.0.0] - 2026-06-06
- Fixed macOS Choice dropdowns truncating labels (e.g. "Bottom-Left") by switching the menu from a WinForms `ContextMenuStrip` to a native `Eto.Forms` context menu, which sizes items to their text correctly on Mac.

## [2.49.0.0] - 2026-06-06
- Removed the native `minkowski` C++ engine entirely (DLL, CMake/CI build steps, Boost/Eigen dependency) and reimplemented the PCA oriented-bounding-box component in pure managed C# (3×3 covariance + Jacobi eigensolver), so it has no native dependency.
- Refined the macOS Choice dropdown sizing again (measure with the menu's font, pin each item's width so the Mac shim can't truncate).
- Documentation/CI: dropped all minkowski/Boost/Eigen references from the dev guide, credits, and macOS+Windows publish workflows.

## [2.48.0.0] - 2026-06-06
- Fixed the macOS dropdown menu truncating Choice option labels: menu items are now built with Grasshopper's own helper and the menu is forced wide enough to fit the longest label measured in the menu's real font.

## [2.47.0.0] - 2026-06-06
- Fixed macOS option value buttons protruding past the component body on narrow layouts (the label column now shrinks instead of overflowing) and reserved space for the dropdown caret so text no longer runs under it.
- Fixed Choice value buttons showing just "..." instead of the actual value on macOS by giving the text layout enough height for the line to fit.

## [2.46.0.0] - 2026-06-06
- Fixed the nest-options widget on macOS: the Run/Options/value button band no longer drifts off the component body (layout now anchored with integer pixel math instead of relying on platform font metrics).
- Replaced the Unicode glyph icons (play/stop/chevron/caret) with vector-drawn shapes and added manual vertical text centering, so buttons render consistently on macOS where the GDI+ shim substitutes fonts.

## [2.45.0.0] - 2026-06-06
- Documentation: rewrote the Build & Publish page into a full developer/contributor guide (prerequisites table, repository layout, per-engine CMake build commands for Windows/macOS, minkowski Boost/Eigen flags, and how to build the .NET plugin).

## [2.44.0.0] - 2026-06-06
- Re-tag of the previous version (no code changes).

## [2.43.0.0] - 2026-06-06
- Documentation: simplified the README engine blurb and rewrote the Credits section to attribute the work to Petras Vestartas.
- No functional code changes; Yak repackage only.

## [2.42.0.0] - 2026-06-06
- Documentation: trimmed the README (removed stray blank lines and softened the engine-description wording).
- No functional code changes; the plugin was only repackaged for Yak.

## [2.41.0.0] - 2026-06-06
- **Repository consolidation.** Brought the native C++ engines (`nfp_nest` and `nest_physics`), the Grasshopper plugin, the Rhino commands, the MkDocs documentation and the Windows→Yak publish CI into a single repository — the modern OpenNest baseline.

---

_History before v2.41.0.0 predates the repository consolidation and is not tracked here._
