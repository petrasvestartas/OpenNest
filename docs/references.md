# Credits

OpenNest's nesting engines are **C++ implementations**. The algorithms were translated to C++ from,
and build substantially on, the **published methods and ideas** of the projects below — with many additions
of our own (see the **[Enhancements](#enhancements)** section below). We gratefully credit the original
authors for the underlying techniques.

## Nesting algorithms (methods we translated & extended)

- **SVGnest** — Jack Qiao. The no‑fit‑polygon + genetic‑algorithm method behind OpenNest's NFP engine; our
  `nfp_nest` is a C++ re‑implementation. <https://github.com/Jack000/SVGnest>
- **Deepnest** — Jack Qiao. The desktop evolution of SVGnest. <https://github.com/Jack000/Deepnest>
- **DeepNestPort / DeepNestSharp** — C# ports that informed the translation.
  <https://github.com/Dragnalith/DeepnestPort> · <https://github.com/Wedg/DeepNestSharp>

## Physics / strip‑packing (method behind `nest_physics`)

- **jagua‑rs** — Jeroen Gardeyn. A collision‑detection engine for 2D irregular cutting & packing.
  <https://github.com/JeroenGar/jagua-rs>
- **sparrow** — *"A fast and reliable heuristic for 2D irregular strip packing"*, Jeroen Gardeyn,
  Greet Vanden Berghe & Tony Wauters (KU Leuven), 2025 — the overlap‑relaxation approach our `nest_physics`
  engine implements. <https://github.com/JeroenGar/sparrow>

## Packing research studied

- **Shadoks** — the team's CG:SHOP 2024 *maximum polygon packing* solvers; studied for packing strategy.
  <https://cgshop.ibr.cs.tu-bs.de/competition/cg-shop-2024/>
- …and **many other references** on irregular nesting, restart/diversification strategies, and metaheuristics
  that informed the speed and packing‑quality work (see the [Enhancements](#enhancements) section below).

## Geometry libraries (used directly)

- **Clipper2** — Angus Johnson. Polygon clipping & offsetting. <https://github.com/AngusJohnson/Clipper2>
- **Boost.Polygon** — Minkowski‑sum / NFP construction. <https://www.boost.org/>
- **Eigen** — linear algebra in the Minkowski helper. <https://eigen.tuxfamily.org/>

## OpenNest

- Original **OpenNest** Grasshopper plugin, the C++ translation, and all enhancements by
  **Petras Vestartas**. <https://github.com/petrasvestartas/OpenNest>

!!! note "Licensing"
    Each dependency keeps its own license. The OpenNest engines are independent implementations of the
    *methods* above. If you redistribute OpenNest, include the upstream licenses for the components you bundle
    (e.g. Clipper2, Boost, Eigen).

---

## Enhancements

OpenNest's nesting engines are **C++ implementations**. The algorithms were translated to C++ from
their JavaScript / Rust / C# originals and then **re‑engineered for speed, performance, and packing quality**
— drawing on a broad body of packing research (SVGnest / Deepnest, jagua‑rs / sparrow, the **Shadoks**
CG:SHOP polygon‑packing solvers, and many other references) and wrapped in a full Grasshopper user interface.
The original authors of the underlying methods are credited above.

### Foundation — C++ implementations

- **`nfp_nest`** — the no‑fit‑polygon / SVGnest–DeepNest genetic algorithm, **re‑implemented in C++** (from
  the JavaScript original and the C# ports) as a self‑contained engine with a C API for the plugin.
- **`nest_physics`** — a C++ implementation of the *sparrow* overlap‑relaxation method (jagua‑rs lineage):
  shrink the strip, then resolve overlaps by moving parts to minimise a penalty.
- **`minkowski`** — C++ Minkowski‑sum no‑fit‑polygon construction used by the managed path.

### Speed & performance (algorithm changes)

The engines were re‑engineered to be **faster, more performant, and to pack tighter** — informed by recent
packing literature (the Shadoks CG:SHOP entries, *sparrow*, *jagua‑rs*, and many others):

- **C++ throughout** — orders of magnitude faster than the interpreted originals; this is what makes the
  interactive live preview and a many‑restart search practical inside Grasshopper.
- **NFP caching** — each no‑fit polygon is computed once per (part, rotation) pair and reused.
- **Parallel NFP precompute** — the expensive cold first generation is built concurrently.
- **Quadtree broad‑phase collision** (physics engine) — fast overlap queries on many parts.
- **Penetration‑depth overlap penalty** — separates parts along the shortest distance, packing tighter than
  an area‑based penalty at equal effort.
- **Slide‑to‑contact compaction** — a cheap post‑placement pass that removes slack.
- **Bet‑and‑run / multi‑start** — spend the time budget across several seeds and keep the best, which
  reliably reaches denser layouts than one long run.
- **Bounded All‑Rotations** — try every orientation where it helps, capped so cost/memory stay bounded.
- **Exact vs. dilated NFP** — exact for tightest contact, conservative dilation when speed matters.

### Better packing

- **High rotation counts** — support for many discrete orientations (up to 3600); the number of rotations is
  the single biggest lever on packing density.
- **Nesting into holes** — small parts are placed **inside** larger parts' holes (no‑fit‑polygon void +
  explicit inner‑fit + holes‑first ordering + an in‑hole scoring bonus so the solver prefers them).
- **Sheet holes (keep‑outs)** — sheets can carry holes the solver routes around.
- **Non‑rectangular sheets** — the true (slanted / concave) sheet outline is honoured, not just its bounding
  box, via a trapezoidal slab‑sweep keep‑out.
- **Overflow handling** — parts that don't fit are placed *outside* the sheets instead of being silently
  dropped, so nothing is lost.
- **Oversized‑part prefilter** — parts that can't fit any sheet are detected up front.
- **Placement strategies** — Box, Gravity, Squeeze, and Bottom‑Left.
- **Simplify with guaranteed containment** — reduce a part's vertices for speed while **auto‑computing** the
  outward offset needed so the original outline can never end up outside the simplified one.

### Geometry intake

- **Surfaces, not just curves** — parts *and* sheets accept planar surfaces; outer and hole loops are
  extracted automatically.
- **Automatic hole detection** — inner loops contained by an outer loop become holes, carried through the
  whole pipeline.

### User interface (built from scratch)

- **On‑canvas option controls** — dropdowns and type‑in boxes (Rotations, Packing strategy, Spacing, Seed,
  Tries, …) styled black‑and‑white, instead of a single opaque options string.
- **Non‑blocking solve** — nesting runs on a background thread, so Rhino stays responsive.
- **Live preview** — the layout tightens on screen in real time as the search runs.
- **ESC to stop** — interrupt at any time and keep the best result found so far.
- **Run / Stop button** on the component body.

### Engineering

- All three C++ engines are **vendored and build from source** in one repository, with a one‑command CMake
  build and a Windows CI that publishes to the Yak package server.
