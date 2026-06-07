# OpenNest

<div style="text-align:right" markdown>
<a href="https://ko-fi.com/petrasvestartas" target="_blank" rel="noopener"><img src="assets/kofi.webp" alt="Buy me a coffee" style="height:48px;border:0"></a>
</div>

**OpenNest** is a 2D polygonal nesting plugin for Rhino / Grasshopper: it packs irregular parts onto sheets
with minimal waste. Parts and sheets can have **holes**, small parts can nest **inside** larger parts' holes,
and sheets can be **non‑rectangular**. The nesting engines are C++ implementations, so the solve runs
fast with a live on‑canvas preview.

Every component has its own tutorial page with a worked example and **downloadable Grasshopper example files** —
browse them under the **Components** section in the sidebar. The three nesting components each ship a ready‑to‑open
example file you can download straight from its documentation page:
[**OpenNest1**](components/opennest1.md), [**OpenNest2**](components/opennest2.md), and
[**OpenNestCollision**](components/opennest_collision.md). To get started, install the plugin from the Rhino
Package Manager (see **Install** below) and drop the components onto your canvas.

![Parts nested to fill a shape with OpenNest](assets/screenshot05.png)

## Install

Rhino 8 → **Package Manager** (`_PackageManager`) → search **OpenNest** → *Install*.

![Installing OpenNest from the Rhino Package Manager](assets/install-package-manager.png)

[Browse the components](components/opennest2.md){ .md-button }
