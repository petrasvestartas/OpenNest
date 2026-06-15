# OpenNest

<div style="text-align:right">
<script type='text/javascript' src='https://storage.ko-fi.com/cdn/widget/Widget_2.js'></script>
<script type='text/javascript'>kofiwidget2.init('Support me on Ko-fi', '#ff0091', 'R5R14L13K');kofiwidget2.draw();</script>
</div>

<p style="text-align:center" markdown>
  <img src="assets/opennest.svg" alt="OpenNest logo" class="home-logo" style="height:110px;width:auto">
</p>

**OpenNest** is a 2D polygonal nesting plugin for **Rhino** — it packs irregular parts onto sheets with minimal
waste. Parts and sheets can have **holes**, small parts can nest **inside** larger parts' holes, and sheets can
be **non‑rectangular**. The nesting engines are C++ implementations, so the solve runs fast.

It works **three ways**:

- **[Rhino Grasshopper](components/opennest2.md)** — a full set of Grasshopper components with a live on‑canvas
  preview, for **both Grasshopper 1 and Grasshopper 2** (the new Rhino 8 editor). The components are the **same in
  both editors** — same inputs, outputs and behaviour — so the **same tutorials apply** to either:
  [OpenNest1](components/opennest1.md), [OpenNest2](components/opennest2.md),
  [OpenNestCollision](components/opennest_collision.md), and the geometry / sheet / utility helpers (each with a
  **downloadable example file**).
- **[Rhino Commands](rhino/index.md)** — the **`OpenNest`** command nests **directly in the Rhino viewport**
  (no Grasshopper): select your sheets, select your parts, and it bakes the result into layers — carrying each
  part's markings, colours and object data along.
- **[Python Examples](api/python/index.md)** — drive the same engines from Python, including the **Rhino 8 Script Editor**,
  via the [`compas_nest`](https://petrasvestartas.github.io/compas_nest/) package.

The Grasshopper components (**Grasshopper 1 and Grasshopper 2**) **and** the `OpenNest` command all ship in the
same Rhino package (Windows and macOS); the Python route installs separately with `pip install compas_nest`.

![Parts nested to fill a shape with OpenNest](assets/screenshot05.png)

## Install

Rhino 8 → **Package Manager** (`_PackageManager`) → search **OpenNest** → *Install*. One install delivers the
Grasshopper 1 **and** Grasshopper 2 components **and** the `OpenNest` Rhino command.

![Installing OpenNest from the Rhino Package Manager](assets/install-package-manager.png)

[Rhino Grasshopper →](components/opennest2.md){ .md-button .md-button--primary }
[Rhino Commands →](rhino/index.md){ .md-button }
[Python Examples →](api/python/index.md){ .md-button }
