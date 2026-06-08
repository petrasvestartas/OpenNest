# Changelog

All notable changes to **OpenNest**, newest first. Version numbers match the Yak / Rhino
Package Manager releases (`https://yak.rhino3d.com/packages/opennest`) and the matching
`v<version>` git tags / GitHub Releases.

> This file is maintained **automatically**: on every release the publish workflow prepends a
> section listing the commits since the previous tag. The entries below were generated from git
> history (one section per released version).

## [2.70.0.0] - 2026-06-08
- expand CHANGELOG.md to every released version (v2.41-v2.69), generated from git history

## [2.69.0.0] - 2026-06-08
- add CHANGELOG.md (backfilled from release history) and auto-maintain it on every release: the publish CI prepends the commits since the last tag, plus GitHub auto release notes

## [2.68.0.0] - 2026-06-08
- stop publishing OpenNest_GH2: the Rhino 8 GH2 SDK is a dead-end vestigial WIP; GH2 will return as a separate Rhino 9 package built against the R9 SDK. Main OpenNest stays GH2-free.

## [2.67.0.0] - 2026-06-08
- split Grasshopper 2 into a separate OpenNest_GH2 yak package so Grasshopper 1 users no longer get the opennest_gh2.rhp Grasshopper2 load error

## [2.66.0.0] - 2026-06-08
- UI non nestable elements kept at the original positions and defualt openenst1 rotations is set to 4

## [2.65.0.0] - 2026-06-07
- docs

## [2.64.0.0] - 2026-06-07
- docs

## [2.63.0.0] - 2026-06-07
- Add standalone C++/C# examples apps + superbuild + docs

## [2.62.0.0] - 2026-06-07
- Grasshopper2 support WIP

## [2.61.0.0] - 2026-06-07
- OpenNest1 bugs

## [2.60.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.59.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.58.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.57.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.56.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.55.0.0] - 2026-06-07
- DOCS cpp api and python apis

## [2.54.0.0] - 2026-06-07
- Docs

## [2.53.0.0] - 2026-06-07
- milestone: rhino version

## [2.52.0.0] - 2026-06-06
- OpenNest1 corrections and other components threading

## [2.51.0.0] - 2026-06-06
- GH options labels - eto forms

## [2.50.0.0] - 2026-06-06
- Use Eto context menu for Choice dropdown (fixes Mac truncation)

## [2.49.0.0] - 2026-06-06
- WIP mac options buttons alignment and PCA

## [2.48.0.0] - 2026-06-06
- Fix macOS dropdown menu truncating Choice labels

## [2.47.0.0] - 2026-06-06
- Fix macOS option-widget value buttons: protrusion + "..." text

## [2.46.0.0] - 2026-06-06
- WIP mac options buttons alignment

## [2.45.0.0] - 2026-06-06
- docs: rewrite Build & Publish as a full developer/contributor guide

## [2.44.0.0] - 2026-06-06
- Maintenance / re-tag (no separate code changes).

## [2.43.0.0] - 2026-06-06
- Update README.md
- Update README.md
- Update credits section in README.md

## [2.42.0.0] - 2026-06-06
- Update README.md

## [2.41.0.0] - 2026-06-06
- **Repository consolidation.** Imported the native C++ engines (`nfp_nest`, `nest_physics`), the Grasshopper plugin + Rhino commands, the MkDocs documentation and the Windows→Yak CI into a single repository — the modern baseline.

---

_History before v2.41.0.0 predates the repository consolidation and is not tracked here._
