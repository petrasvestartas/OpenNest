# grasshopper_plugin/

Committed release artefacts. **CI writes this folder** — the "Build & Publish" workflow
(`.github/workflows/publish.yml`) regenerates the `.yak` files on every push to `main` and commits them
with the version bump. Nothing here is built by hand.

```text
opennest_win/   manifest.yml + icon.png + the two Windows .yak files (rh8_0, rh9_0)
opennest_mac/   manifest.yml + icon.png + the two macOS .yak files   (rh8_0, rh9_0)
```

`opennest_win/manifest.yml` is the **canonical** version: the workflow reads it, takes the higher of that and
the version live on the Yak server, bumps it, and writes the new value into both manifests.

## Editing a manifest.yml

Three rules, all easy to break silently:

- **Keep the `keywords:` guid entries.** They are declared by hand, not auto-added. `yak build` only inspects
  the package **root**, and the multi-targeted rh8-win package has an empty root by design — so yak would
  write `keywords: []`, print one warning and still exit 0. Those guids are Grasshopper's only Package-Restore
  path back to this package (it matches by plug-in name first, then plug-in ID, and our name `opennest_2`
  never matches the package name `OpenNest`). The reasoning is spelled out in the manifests themselves, and
  publish.yml's "Verify Yak package contents" step fails the release if either guid goes missing.
- **Stay ASCII**, comments included — as belt-and-braces, not because anything is known to corrupt them.
  The version-bump step rewrites both files with `Get-Content -Raw | ... | Set-Content`, and on these
  **BOM-less** files that round-trip is **lossless**. Measured: a copy of each manifest with an em dash, a
  curly quote, `é` and U+2001 added to the comments, run through the workflow's exact pipeline under Windows
  PowerShell 5.1 (where both cmdlets default to the ANSI code page, so read-cp1252 + write-cp1252 is a
  byte-level identity on a cp1252 machine) came back **byte-identical** — same length, zero differing bytes,
  all 11 non-ASCII bytes intact. The step actually runs under `shell: pwsh`, where both cmdlets default to
  UTF-8 without BOM per PowerShell 7's docs — not measured here, no `pwsh` on the machine this was checked
  on. What *does* destroy the characters is a **UTF-8 BOM**: same files, same 5.1 pipeline, BOM added → 14
  non-ASCII bytes in, 3 out. So keep the rule (it costs nothing and stops the file depending on a shell
  default staying put), but it is not guarding against a live bug. Plain quotes, plain hyphens; no curly
  quotes and no em dashes.
- **Never write `version:` followed by digits or dots anywhere else in the file**, comments included. The
  bump step's regex is unanchored and global, so a second match would be rewritten along with line 3. Both
  manifests currently match exactly once — checked.

## What is inside a package

The macOS packages and the Rhino-9 Windows package are flat: `manifest.yml`, `icon.png`, `opennest_2.gha`
with its managed dependencies, the three native engines, `opennest_commands.rhp`, and the manual-load
`grasshopper2/` subfolder.

From the **next** release onwards the **Rhino-8 Windows** package is .NET **multi-targeted** instead, because
Rhino 8 on Windows runs on either .NET Core or .NET Framework 4.8 (`_SetDotNetRuntime`) and a .NET Core
`.gha` cannot load under .NET Framework:

```text
opennest-<next version>-rh8_0-win.yak
├── manifest.yml     <- ROOT, outside the framework folders (required)
├── icon.png
├── net48/           <- complete .NET Framework 4.8 payload
├── net7.0/          <- complete .NET Core payload
└── grasshopper2/    <- manual-load GH2 download (not a framework name; Rhino ignores it)
```

> **Note** — that layout does **not** describe the `.yak` files committed next to this ReadMe. Unzip
> `opennest-2.93.0-rh8_0-win.yak` and it is still **flat**: `manifest.yml`, `icon.png`, `LICENSE`,
> `README.md`, `opennest_2.gha` + its `.deps.json`/`.runtimeconfig.json`/`.pdb`, the three engine DLLs,
> `opennest_commands.rhp` + its two JSON files, and `grasshopper2/` — no `net48/`, no `net7.0/`.
> The multi-targeted staging lives in `.github/workflows/publish.yml` and first ships in the next release.

Rhino loads the folder matching the runtime it is on, so one install serves both — no runtime switch.
Yak supports this from Rhino 8.2 onwards; see McNeel's
["The Anatomy of a Package"](https://developer.rhino3d.com/guides/yak/the-anatomy-of-a-package/#a-note-on-net-multi-targeting)
and ["Creating a Multi-targeted Rhino Plug-In Package"](https://developer.rhino3d.com/guides/yak/creating-a-multi-targeted-rhino-plugin-package/).

> **Warning** — a multi-targeted package must have **no `.rhp`/`.gha` in its root**: one loadable file there
> makes Rhino ignore the framework folders entirely and fall back to root-only probing. The workflow asserts
> this after staging.

## Building a package by hand

Only needed when reproducing what CI does. Stage the layout above in a folder, then from inside it:

```powershell
# Windows
& "C:\Program Files\Rhino 8\System\Yak.exe" build --platform win
& "C:\Program Files\Rhino 8\System\Yak.exe" push opennest-2.93.0-rh8_0-win.yak
```

```bash
# macOS
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" build --platform mac
"/Applications/Rhino 8.app/Contents/Resources/bin/yak" push opennest-2.93.0-rh8_0-mac.yak
```

**Filename**: `yak build` names the file itself — `<name>-<version>-<distribution>-<platform>.yak` with the
package name **lowercased** and the version's trailing component dropped. The manifest carries a 4-part
`version: 2.93.0.0`, so the artefact is `opennest-2.93.0-…`, not `OpenNest-2.93.0.0-…`. Substitute whatever
version the manifest holds; the committed `.yak` files next to this ReadMe show the exact current names.

`yak login` once first (`yak login --ci` for the token stored as the `YAK_TOKEN` repo secret). Yak derives the
distribution tag by inspecting the payload; the workflow forces the intended tag by renaming the output.

Grasshopper 2 install instructions for end users: [`GRASSHOPPER2_INSTALL.txt`](GRASSHOPPER2_INSTALL.txt).
