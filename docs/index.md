# OpenNest

<div style="text-align:right" markdown>
<a href="https://ko-fi.com/petrasvestartas" target="_blank" rel="noopener"><img src="assets/kofi.webp" alt="Buy me a coffee" style="height:48px;border:0"></a>
</div>

**OpenNest** is a 2D polygonal nesting plugin for **Rhino** — it packs irregular parts onto sheets with minimal
waste. Parts and sheets can have **holes**, small parts can nest **inside** larger parts' holes, and sheets can
be **non‑rectangular**. The nesting engines are C++ implementations, so the solve runs fast.

It works **two ways**, and the same package installs both (Windows and macOS):

- **[Rhino Grasshopper](components/opennest2.md)** — a full set of Grasshopper components with a live on‑canvas
  preview. Each component has its own tutorial page with a **downloadable example file**:
  [OpenNest1](components/opennest1.md), [OpenNest2](components/opennest2.md),
  [OpenNestCollision](components/opennest_collision.md), and the geometry / sheet / utility helpers.
- **[Rhino Commands](rhino/index.md)** — the **`OpenNest`** command nests **directly in the Rhino viewport**
  (no Grasshopper): select your sheets, select your parts, and it bakes the result into layers — carrying each
  part's markings, colours and object data along.

![Parts nested to fill a shape with OpenNest](assets/screenshot05.png)

## Install

Rhino 8 → **Package Manager** (`_PackageManager`) → search **OpenNest** → *Install*. One install delivers the
Grasshopper components **and** the `OpenNest` Rhino command.

![Installing OpenNest from the Rhino Package Manager](assets/install-package-manager.png)

[Rhino Grasshopper →](components/opennest2.md){ .md-button .md-button--primary }
[Rhino Commands →](rhino/index.md){ .md-button }
