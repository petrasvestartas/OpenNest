# Changelog

All notable changes to **OpenNest**, newest first. Version numbers match the Yak / Rhino Package
Manager releases (`https://yak.rhino3d.com/packages/opennest`) and the `v<version>` git tags /
GitHub Releases. Each entry describes what the release actually changed.

> New releases are appended automatically by the publish workflow (it lists the commits made since
> the previous tag), so a clear commit subject per push becomes the changelog line — refine an entry
> here anytime if you want more detail.

## [2.86.0.0] - 2026-06-15
- docs: sync component Input/Output tables with current components

## [2.85.0.0] - 2026-06-15
- docs

## [2.84.0.0] - 2026-06-15
- docs
- docs(home): scale the Ko-fi button to 125%

## [2.83.0.0] - 2026-06-15
- docs(home): use the official Ko-fi widget button

## [2.82.0.0] - 2026-06-14
- docs(home): nicer coffee-cup icon + thicker pink outline on the ko-fi pill

## [2.80.0.0] - 2026-06-14
- docs(home): subtle pink ko-fi pill (black text) instead of the badge image

## [2.79.0.0] - 2026-06-14
- docs(home): add the OpenNest logo above the intro paragraph

## [2.78.0.0] - 2026-06-14
- docs: inline header tabs (one-line header) + rewrite Build & Publish

## [2.77.0.0] - 2026-06-14
- docs: fix 3 broken download links (filename mismatches)

## [2.76.0.0] - 2026-06-14
- docs: professional left nav — drop toc.integrate, add section tabs + instant nav

## [2.75.0.0] - 2026-06-14
- docs: tighten Python example download wording + nav "API"->"Examples"
- docs: rename the three API nav sections + index titles to 'C++ Examples', 'C# Examples', 'Python Examples' (they are example galleries, not API references); landing-page links + the doc generator updated to match
- docs: add standalone Python example downloads (zips) to the OpenNest site

## [2.74.0.0] - 2026-06-14
- docs
- docs(grasshopper2): swap deleted gh2_1/2/3 screenshots for opennest2 + opennest_collsions
- docs(grasshopper2): Rhino 9 WIP manual-install walkthrough + screenshots

## [2.73.0.0] - 2026-06-14
- Improvements of solvers, options component, unify api across python, cpp, csharp, rotations options, run two solver in parallel
- docs/api/python: restructure into the same 8-example layout as the C++ and C# pages (split the monolithic python.md into an Overview + one page per example: 01 collision, 02 nfp+ga, 03 live animation, 04 clearance offset, 05 attributes, 06 pack array, 07 pack distance, 08 text). Mirrors the compas_nest examples; keeps the hero image and the full Rhino 8 Script Editor context (# r: compas_nest install, Scene drawing, install paths, API reference). mkdocs nav + landing-page links updated
- examples: one download project per example instead of per-OS (CMake/.NET build natively on every platform from the same sources - not cross-compiling; the only per-OS bit was the launcher). Each NN_name.zip now carries both run.bat and run.command (run.command marked executable for macOS); 16 zips instead of 32, doc download links updated
- docs/api: full 8-example C++ and C# suite mirroring the compas_nest examples (01 collision, 02 nfp+ga, 03 live animation, 04 clearance offset, 05 attributes, 06 pack array, 07 pack distance, 08 text), every example proven to compile + run.
- GH2: resolve opennest_2 (the GH1 payload) in the Rhino plug-in load context
- GH2: ship the portable net8.0 build (no WindowsDesktop.App), not net8.0-windows
- GH2: minimal deps.json so the plugin loads on Rhino 9 (THE fix, confirmed)
- Add install_gh2_r9.ps1: deploy GH2 plugin to Rhino 9's Components folder (the supported way)
- GH2: ExcludeAssets=runtime on Grasshopper2 so the .rhp loads on Rhino 9 (the real fix)
- GH2: load WhenNeeded so Rhino doesn't fail it at startup
- GH2: load cleanly from a Yak package on Rhino 9 (no plug-in popup)
- GH2: add a minimal Rhino PlugIn shell so the .rhp loads without a popup
- publish: ship OpenNest_GH2 as a separate rh9 Yak package (Rhino 9); commands target net8
- docs: numbered, minimal C++/C# example pages with cross-platform downloads
- GH2: add per-part Rotations input (before Attributes) to Geometry + Geometry Surfaces
- GH2/Rhino 9: retarget opennest_gh2 to net8.0 + Rhino-9 GH2 SDK; separate rh9 yak package
- Geometry (Surfaces): add per-part Rotations input (before Attributes)
- docs: split C++ and C# API into individual per-function example subpages
- untrack stray out.svg demo outputs (gitignore them) Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
- Add NestOptions component (GH1) + Options input on OpenNest2 & OpenNestCollision
- OpenNest1: Spacing now offsets the parts + sheets upstream (it takes raw polylines)
- Geometry component: move Rotations input BEFORE Attributes (GH1 + GH2)
- resolve nfp_research merge: favor nfp_research for all NFP/rotation code, keep physics spacing fix
- checkpoint before nfp_research merge: per-element rotation (both solvers) + spacing-offset moved upstream
- dead-code sweep batch 3 (docs): cpp.md/csharp.md signatures updated for part_rotations + new nfp_pack / nfp_offset_polygon sections, Engine='cs' references removed, geometry.md documents the Offset and new per-part Rotations inputs, opennest2/collision pages note the per-part override, example console apps (cpp+csharp) fixed to the new ABI, stale minkowski engine-list refs fixed, CHANGELOG Unreleased entry covering correctness fixes + density gains + Rotations + new C functions + managed-solver removal
- dead-code sweep batch 2 (C# managed solver removed, ~4500 LOC; all 4 projects build clean): the Engine='cs' branch was UI-unreachable (every component hard-codes the native engine) and still contained the bugs fixed in C++. Deleted: rhino_example cs-branch + NumberInputForm + Engine field, the DeepNest placement worker (Background.cs reduced to the 4 live static utilities), C# GeneticAlgorithm/SvgParser/RawDetail/DxfParser/LineElement/MinkowskiWrapper (minkowski.dll interop)/example_default_input files, SvgNest launchWorkers/ResponseProcessor/toTree/cloneTree/cleanPolygon + solver fields, NestingContext cs-solver members + sample-data cluster (now data+init only), 4 dead load_sample_data overloads + AddPolygon/AddSheet helpers, nest_geo offset_boundaries/duplicate_openlines_and_flip/MergeWith, BBPolyline + sort_by_curve_ends files. VERIFIED-LIVE and kept (finder false-positives): ConvexHull2D (PCA component), extend_openlines (geo_rhino), SvgPoint/D3/Simplify, guid_to_nest_geo
- dead-code sweep batch 1 (C++ engine, ~1400 LOC removed, placements verified bit-identical): JS-port orbital-NFP cluster in GeometryUtil (never wired in - the engine uses Minkowski convolution + Clipper2), NestingEngine simplifyFunction's unreachable legacy tail + its helper cluster (GetMinimumBox/ClipSubject/exterior/...), GeneticAlgorithm refuted heuristic paths (adaptive/SA/clustering/tournament - the canonical scheme is now THE scheme), NfpWorker NewMinkowskiSum/shiftPolygon/inpairs/DisplayProgress, HelperTypes NfpKey/NonameReturn/PolygonTreeItem/mergedLength-chain/dead fields, NFP.h C#-mirror members never called (GetXml/ToString/Center/Area/slice/offsetx...) + Sheet/RectangleSheet, NestingContext AddSheet/AddRectanglePart/GetNextSource, NestConfig knobs with no engine reads (scale/exploreConcave/mergeLines/clipByHull/clipByRects/rotation_limit - ABI fields stay, map to nothing)
- per-element Rotations attribute end-to-end (user request: mixed rotation constraints in ONE nest - freeform parts 360, rectangular parts 4): optional Rotations input on the Geometry component (GH1 by-name lookup so old files keep working; GH2 fixed port), nest_geo.rotations plumbed like copies, both C-APIs gain part_rotations[] (0/NULL = exactly current behavior); NFP engine: NFP.rotationCount through all clone sites, per-part GA gene draws + placeParts orientation stepping + gene snap-to-grid enforcement (order mutation swaps placements but not positional rotation genes); physics engine: build_part maps N>0 to RotationRange::discrete_of (1 = fixed), 0 = continuous as before. Bench file format: 'part <qty> <rotations>'; verified locked part stays at 0 deg while others rotate, overlap=0, and no-override placements are bit-identical
- P3 strip-width shrink REFUTED (v1 50/50 r=3%: concave -0.39; v2 75/25 r=1%: concave -0.92, reverted): mechanism confirmed on 1/10 seeds (+0.66pp when a level lands) but the GA-greedy cannot re-establish feasibility at W*(1-r) within affordable slices - the engine is generation-throughput-bound at 10s. sparrow-style shrink needs temporary-overlap exploration + separation = the nest_physics paradigm; documented the NFP-solve -> physics-compress hybrid as the cross-branch breakthrough path
- P3-slide KEPT (weak): continuous slide-to-contact refinement in compactPlacements - ray-cast from the current position toward origin (left/down/diagonal) to the first feasible-boundary hit, presample+ternary the score along the ray, accept on improvement with landing-feasibility insurance. Vertex-replace can only reach feasible-region vertices; the per-ray optimum is usually mid-edge. concave +0.39pp batch1 / +0.03 batch2 (pooled +0.21pp), rects neutral, ZERO regressions in 20 cells, wall unchanged. Default on (config.slideCompaction, non-faithful). Design + two adversarial code-verified reviews from the breakthrough-design workflow
- P3 hull-growth term REFUTED at w=1 (rects neutral, concave pooled +0.06 inside noise band; reverted)
- Phase 3 wrap: contact lever kept (rects +0.89pp cumulative), es4-under-contact refuted, measurement wall documented (concave batch variance ~0.4pp needs 10+ seeds or ESICUP data); remaining survey levers ranked - warm-start 2-opt/beam (+3-6pp lit), pocket minimization (+2-4pp), NFP-edge sliding compaction (+1-3pp), and the sparrow-style two-phase compression paradigm (+8-15pp) which architecturally converges with the nest_physics engine
- P3-contact tolerance 0.2% -> 1%: wider near-tie set lets contact act more often; rects +0.35pp (.9064->.9099), concave +0.1pp pooled over 10 seeds (batches split +0.41/-0.25). Cumulative session: rects .8832->.9099
- P3-contact KEPT: touching-perimeter re-ranking of near-tie candidates (non-faithful, bbox modes) - collect all candidate positions, then among those within 0.2% of the best bbox/gravity score pick the one with maximum contact length against placed parts + sheet (Burke et al. touching perimeter; collinear-overlap segment math with AABB prefilter, capped at 64 evals). Fixed 10s budget: rects +0.54pp (.9010->.9064), concave +0.21pp on fresh seeds / -0.14pp on batch1 (noise), rings pinned; wall unchanged
- Phase 2 wrap: config sweeps refuted (edgeSamples 2 and pop 30 already optimal at fixed budget; production pop=120 LOSES 0.59pp to 30 - recommend the GH plugin send ~30) + HEADLINE at fixed 10s budget vs the post-bugfix engine with old defaults: rects +1.78pp (.883->.901), concave +3.59pp (.643->.679), rings pinned by host arrangement; nfp_nest.dll builds clean with all kept levers
- Q6 REFUTED (both variants, reverted): fitness re-normalization - the GA is rank-based so the old mixed-unit fitness already ranks single-sheet layouts by its dominant 2w+h term; pure-width helped rects (+0.72pp) but hurt concave (-0.4pp twice), normalized (2wFrac+hFrac)/3 hurt rects (-0.70pp); no universal winner on the re-weighting axis. Q5 logged
- Q1+Q5 KEPT as engine defaults: tryAllRotations=true (concave +1.78pp, rects +0.35pp at fixed budget) and compactionPasses 2->4 (10-seed sweep: 4 >= 2 everywhere, concave +0.6pp; 2 was the worst of {0,2,4}). C-API now honors -1 sentinel for tryAllRotations like the other placement knobs; NOTE the GH plugin still sends explicit 0/1 - flipping the component default is a follow-up on the C# side
- Q4b KEPT: origin-anchored gravity pull (non-faithful) - gravityWeight distance to the sheet origin instead of the layout centroid in placeParts + compaction. The centroid pull scattered parts mid-region wherever bbox growth tied (hole islands fragmented); origin pull packs bottom-left globally AND inside holes (probe: 4 fillers now 2x2 corner-aligned in a 200^2 hole). Fixed 10s budget vs q1-allrot: rects +1.20pp, concave +0.32pp, rings neutral (rings strip width is host-bound)
- Phase 2 log: Q1 KEPT (tryAllRotations at fixed 10s budget: concave +1.78pp, rects +0.35pp, rings neutral - recommend flipping the plugin default), Q3 REFUTED (diverse seeded GA initial orders: no gain, concave -0.24pp, reverted). KEY Q2 finding: hole-fill already works post-Type-fix (probes out/holetest*.txt) but in-hole placements FRAGMENT the hole - global-bbox score is indifferent inside islands, gravity pulls to layout centroid, tie-breaks use rotation-frame shift; next lever = in-island bottom-left bias
- S5: wire the dead sheetNfpClipperCache (sheet-IFP clipper conversion memoized per source+rotation, populated in serial Phase A so candidate threads only read) and replace the inpairs O(n^2)-inside-O(n^2) linear scan with an unordered_set keyed by makeProcessKey in both pair-collection sites. Placements bit-identical; concave -17% cumulative wall vs pre-S1
- S3: fast squeeze-mode hull scoring - replace the per-candidate clone(allpoints)+getHull (full re-sort + alloc per position) with a merge of two PRE-sorted point sets (layout hull sorted once per part, part points once per rotation candidate) running the same D3 monotone chain and shoelace order. Placements bit-identical on all datasets/modes; squeeze wall -51% (7.6s->2.5s vs pre-S2 on concave gens10 pop20)
- Phase 0 baseline (post-correctness-fix, pre-S1/S2 exe): gens 20 pop 30 seeds 7-47, datasets rects/concave/rings x mode1 placements box/gravity/squeeze + mode0 gravity; overlap=0 on all rows. Headlines: gravity best (rects .880, concave .636, rings .530); squeeze 10-20x slower at equal-or-worse util
- S1: restore the incremental clipCache union read path (deepnest parity) - the cache was written but never read, so the union of ALL placed parts' NFPs was rebuilt from scratch for every part x rotation. Now a cached (source, rotation) entry seeds the Clipper op with the union over placed[0..index] and only newer parts are added; ALL valid rotation candidates' unions are cached after the thread join (duplicate-quantity parts + losing rotations seed later placements); cached Execute outputs are re-added as Subjects directly. Also normalize trial rotations at >=360 (float key 360 != 0 silently missed every rotation-keyed cache). Placements bit-identical; wall: rects -15%, concave -13%, rings -4%
- S2: scalar bbox scoring in the placement candidate loops - replace the per-candidate 8-point heap polygon + getPolygonBounds with plain min/max arithmetic (placeParts + compactPlacements), build the winning PlacementItem/union copy once after the scan, and drop the dead per-candidate hull/hullsheet stores (incl. a getHull(sheet) per squeeze candidate). Placements bit-identical across all modes/placements (dumpPlacements diff); squeeze ~-20% wall, box/gravity neutral (union rebuild dominates - S1's target)
- fix two NFP correctness bugs: (1) multi-loop concave NFPs were truncated to the largest loop, discarding real exclusion lobes - parts could be placed exactly on top of each other; now all loops are kept (extra CCW loops = forbiddenLobe children with outer winding, CW loops validated as genuine pockets vs deep-overlap sweep artifacts); (2) inner-IFP cache entries never set DbCacheKey.Type, colliding with outer pair keys (sheet and part sources both start at 0) and poisoning both caches - now Type=1. Adds env-gated debug tooling (NFP_VERIFY_PLACE, NFP_DUMP_PAIR)
- nfp_research: add nfp_bench self-measuring CLI (seeded rects/concave/rings datasets + file loader, util/overlap metrics, SVG dump, NFP probe modes) - every engine change is now a measured A/B at fixed seed
- nest-physics-research: keep only the experiments - revert the accidentally-bundled docs (they're on main now), drop + gitignore the stray err.txt/out.txt/out.svg, and add a 15% speed cap to the research loop (tighter AND fast)
- docs

## [Unreleased] - nfp_research branch
- **Tighter, correct packings**: fixed two engine correctness bugs (concave parts could be placed
  exactly on top of each other; an internal cache collision corrupted feasible regions). Same solve
  time now packs measurably denser (rects +2.7pp, concave +4.1pp utilization on the benchmark), and
  hole nesting actually fills holes.
- **Per-part Rotations**: the Geometry component gained an optional **Rotations** input — one value
  per part (0 = inherit the solver setting, N = only N orientations, 1 = fixed/no rotation), so
  rectangular and freeform parts can share one nest with different rotation rules. Works with both
  OpenNest2 and OpenNestCollision; both C ABIs gained a `part_rotations[]` argument (**breaking
  native signature change** for `nfp_nest` / `np_nest` — bindings like compas_nest must add the
  argument, NULL keeps old behavior).
- **New C API functions**: `nfp_pack` (simple row/grid layout, array or max-width wrapping — the
  compas_nest `pack()` semantics) and `nfp_offset_polygon` (Clipper2 grow/shrink) so thin bindings
  need no geometry code of their own.
- **Removed the unused managed C# solver** (~6,000 lines): every component already used the native
  engines; the leftover `Engine="cs"` switch and the dead JS-port code in the C++ engine are gone.
  The C# layer is now purely data preparation + thin P/Invoke bindings.

## [2.72.0.0] - 2026-06-08
- OpenNest2 + OpenNestCollision: Run is now a standard Grasshopper input (GH1 + GH2), replacing the on-canvas Run button; refresh docs/components example files (GH2 .ghz set) + component images

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
