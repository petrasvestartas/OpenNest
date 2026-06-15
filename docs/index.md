---
hide:
  - navigation
  - toc
---

<div class="hero" markdown>

<img src="assets/opennest.svg" alt="OpenNest" class="hero-logo">

# OpenNest

<p class="hero-tagline" markdown>2D polygonal nesting for Rhino — pack irregular parts onto sheets with minimal waste, fast. Parts and sheets can have holes, small parts nest inside larger parts' holes, and sheets can be non‑rectangular. The engines are native C++.</p>

[Get started](components/opennest2.md){ .md-button .md-button--primary }
[View on GitHub](https://github.com/petrasvestartas/OpenNest){ .md-button }

</div>

<div class="slideshow">
<img src="assets/gallery/gallery_01.jpg" alt="OpenNest nesting result" loading="eager">
<img src="assets/gallery/gallery_02.jpg" alt="">
<img src="assets/gallery/gallery_03.jpg" alt="">
<img src="assets/gallery/gallery_04.jpg" alt="">
<img src="assets/gallery/gallery_05.jpg" alt="">
<img src="assets/gallery/gallery_06.jpg" alt="">
<img src="assets/gallery/gallery_07.jpg" alt="">
<img src="assets/gallery/gallery_08.jpg" alt="">
<img src="assets/gallery/gallery_09.jpg" alt="">
</div>

## Rhino3D

<div class="grid cards" markdown>

-   :material-puzzle-outline: &nbsp; __Rhino Grasshopper__

    ---

    A full set of components with a live on‑canvas preview, for Grasshopper 1 and 2 — identical inputs, outputs and tutorials in both editors, each with a downloadable example.

    [:octicons-arrow-right-24: OpenNest2](components/opennest2.md)

-   :material-cube-scan: &nbsp; __Rhino Command__

    ---

    The `OpenNest` command nests directly in the viewport — select sheets and parts, then bake to layers, carrying each part's markings, colours and object data.

    [:octicons-arrow-right-24: Rhino command](rhino/index.md)

-   :material-code-braces: &nbsp; __From code__

    ---

    Call the same engines directly through a plain C ABI — from C++, C# or Python — with a runnable example for every function.

    [:octicons-arrow-right-24: APIs](api/cpp/index.md)

</div>

## C++, C# &amp; Python

<div class="grid cards" markdown>

-   :simple-cplusplus: &nbsp; __C++__

    ---

    Two native engines through a plain C ABI; build from source with CMake. Eight self‑contained examples.

    [:octicons-arrow-right-24: C++ examples](api/cpp/index.md)

-   :simple-dotnet: &nbsp; __C#__

    ---

    P/Invoke the same engines from .NET, no Rhino required. The eight examples mirror the C++ set.

    [:octicons-arrow-right-24: C# examples](api/csharp/index.md)

-   :material-language-python: &nbsp; __Python__

    ---

    Drive the engines from Python, including the Rhino 8 Script Editor, via `compas_nest` (`pip install compas_nest`).

    [:octicons-arrow-right-24: Python examples](api/python/index.md)

</div>

## Why OpenNest

<div class="grid cards" markdown>

-   __Any sheet shape__

    Sheets don't have to be rectangles — nest into non‑rectangular offcuts and remnants, holes and all.

-   __Native C++ speed__

    Two self‑contained C++ engines — NFP + genetic algorithm, and a physics/collision solver — with no heavy dependencies.

-   __Carries your data__

    Markings, colours, text and object attributes travel with each part through placement and baking.

</div>

## Install

<div class="grid cards" markdown>

-   :material-numeric-1-circle: &nbsp; __Grasshopper&nbsp;+&nbsp;Rhino&nbsp;command__

    ---

    In Rhino: run `_PackageManager` → search OpenNest → Install, then restart Rhino.

    Works on Rhino 8 and 9, Windows and macOS — delivers the Grasshopper 1 components and the `OpenNest` command.

-   :material-numeric-2-circle: &nbsp; __Grasshopper&nbsp;2 (Rhino&nbsp;9&nbsp;WIP)__

    ---

    Grasshopper 2 loads manually: after the install above, open Grasshopper 2 and load `opennest_gh2.rhp` from the package's `grasshopper2/` folder.

    [:octicons-arrow-right-24: Step‑by‑step with screenshots](components/grasshopper2.md)

-   :material-numeric-3-circle: &nbsp; __Python__

    ---

    Run `pip install compas_nest` — the same C++ engines from Python, including the Rhino 8 Script Editor.

    [:octicons-arrow-right-24: Python examples](api/python/index.md)

</div>

<figure class="install-figure" markdown>
![Installing OpenNest from the Rhino Package Manager](assets/install-package-manager.png){ .bordered }
<figcaption>Rhino Package Manager — search “OpenNest”, then Install.</figcaption>
</figure>
