# Changelog

All notable changes to **OpenNest**, newest first. Version numbers match the
Yak / Rhino Package Manager releases (`https://yak.rhino3d.com/packages/opennest`)
and the matching `v<version>` git tags / GitHub Releases.

> From **v2.68.0.0** onward this file is appended **automatically** on every release —
> the publish workflow lists the commits since the previous tag. Earlier entries
> below were backfilled from git history (the auto-bumped minor versions are grouped
> where several shared the same work).

## [2.69.0.0] - 2026-06-08
- add CHANGELOG.md (backfilled from release history) and auto-maintain it on every release: the publish CI prepends the commits since the last tag, plus GitHub auto release notes

## [2.68.0.0] - 2026-06-08
- Stop publishing the `OpenNest_GH2` package. The Rhino 8 Grasshopper 2 SDK is a vestigial / broken WIP, and a GH2 plugin makes Rhino throw a `Grasshopper2` load error on every Grasshopper-1-only machine. The main **OpenNest** package is now Grasshopper-2-free; Grasshopper 2 will return later as a separate **Rhino 9** package built against the Rhino 9 SDK.

## [2.67.0.0] - 2026-06-08
- _(Superseded by 2.68.0.0)_ Split Grasshopper 2 into a separate `OpenNest_GH2` package — later withdrawn in favour of deferring Grasshopper 2 to Rhino 9.

## [2.66.0.0] - 2026-06-08
- OpenNest1 (NFP): parts that don't fit are kept at their original input location/orientation instead of being parked off-sheet.
- OpenNest1 default number of rotations changed to 4.

## [2.64.0.0 – 2.65.0.0] - 2026-06-07
- Documentation updates.

## [2.63.0.0] - 2026-06-07
- Added standalone **C++ and C# example apps** (CMake superbuild) showing how to drive the OpenNest API, wired into CI.

## [2.62.0.0] - 2026-06-07
- Initial **Grasshopper 2** component support (WIP).

## [2.61.0.0] - 2026-06-07
- OpenNest1 bug fixes.

## [2.54.0.0 – 2.60.0.0] - 2026-06-07
- New **C++ API**, **C# API** and **Python (compas_nest) API** documentation pages (iterative).

## [2.53.0.0] - 2026-06-07
- Rhino-version milestone work.

## [2.52.0.0] - 2026-06-06
- OpenNest1 corrections; background threading for the nesting components.

## [2.51.0.0] - 2026-06-06
- Grasshopper option labels via Eto forms.

## [2.46.0.0 – 2.50.0.0] - 2026-06-06
- macOS option-widget fixes: Eto context-menu Choice dropdown (fixes label truncation), value-button alignment / protrusion, PCA alignment.

## [2.45.0.0] - 2026-06-06
- Docs: "Build & Publish" rewritten as a full developer/contributor guide.

## [2.42.0.0 – 2.44.0.0] - 2026-06-06
- README updates (credits, usage).

## [2.41.0.0] - 2026-06-06
- **Repository consolidation.** Brought the native C++ engines (`nfp_nest`, `nest_physics`), the Grasshopper plugin + Rhino commands, the MkDocs documentation, and the Windows→Yak CI into a single repository. This is the modern repository baseline.

---

_History before v2.41.0.0 predates the repository consolidation and is not tracked here._
