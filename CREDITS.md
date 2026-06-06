# Credits

OpenNest's nesting engines are **C++ implementations**. The algorithms were translated to C++ from,
and build substantially on, the **published methods and ideas** of the projects below — with many additions
of our own (see the **Enhancements** section of the
[online docs](https://petrasvestartas.github.io/OpenNest/references/#enhancements)). We gratefully credit the
original authors for the underlying techniques.

## Nesting algorithms (methods translated & extended)

- **SVGnest** — Jack Qiao. No‑fit‑polygon + genetic‑algorithm method behind the NFP engine; our `nfp_nest`
  is a C++ re‑implementation. <https://github.com/Jack000/SVGnest>
- **Deepnest** — Jack Qiao. Desktop evolution of SVGnest. <https://github.com/Jack000/Deepnest>
- **DeepNestPort / DeepNestSharp** — C# ports that informed the translation.
  <https://github.com/Dragnalith/DeepnestPort> · <https://github.com/Wedg/DeepNestSharp>

## Physics / strip‑packing (method behind `nest_physics`)

- **jagua‑rs** — Jeroen Gardeyn. Collision‑detection engine for 2D irregular cutting & packing.
  <https://github.com/JeroenGar/jagua-rs>
- **sparrow** — *"A fast and reliable heuristic for 2D irregular strip packing"*, Jeroen Gardeyn,
  Greet Vanden Berghe & Tony Wauters (KU Leuven), 2025. <https://github.com/JeroenGar/sparrow>

## Packing research studied

- **Shadoks** — the team's CG:SHOP 2024 *maximum polygon packing* solvers; studied for packing strategy.
  <https://cgshop.ibr.cs.tu-bs.de/competition/cg-shop-2024/>
- …and **many other references** on irregular nesting and restart/diversification strategies.

## Geometry libraries (used directly)

- **Clipper2** — Angus Johnson. Polygon clipping & offsetting. <https://github.com/AngusJohnson/Clipper2>
- **Boost.Polygon** — Minkowski‑sum / NFP construction. <https://www.boost.org/>
- **Eigen** — linear algebra in the Minkowski helper. <https://eigen.tuxfamily.org/>

## OpenNest

Original **OpenNest** Grasshopper plugin, the C++ translation, and all enhancements by **Petras Vestartas**.
<https://github.com/petrasvestartas/OpenNest>

---

Each dependency keeps its own license; the OpenNest engines are independent implementations of the *methods*
above. If you redistribute OpenNest, include the upstream licenses for the components you bundle.
