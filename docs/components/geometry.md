# Geometry

Prepares parts for nesting: separates outlines (with holes) from attached attributes; optional simplify, convex hull and copies.

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Outlines** | Geometry | tree | Closed curves OR planar surfaces (cast per type internally).<br>If geometries have holes, create a data-tree first. <br>Place each element into indivdual branch. <br>Otherwise the algoritm will try to order polylines by checking possible holes. |
| **Simplify** | Number | item | segment divisions <br>0 = KEEP ALL vertices (no simplification - best for nesting fine outlines) <br>x>0 divides by distance <br>x<0 max 3 points per sub-segment (merges colinear within 10deg) _Default: -100._ |
| **Hull** | Boolean | item | Replace each outline with its convex hull _Default: false._ |
| **Copies** | Integer | list | Number of copies per part |
| **Attributes** | Geometry | tree | Additional geometry: points, lines, surfaces, meshes... <br>Use data-tree, one list of additional geometry per branch.. |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **Geometry** | Generic | Prepared parts ready for nesting |
| **Borders** | Curve | Outline border curves per part |

## Example

**Files to download:**

- [⬇ geometry.ghx](files/geometry/geometry.ghx)

![geometry — geometry](img/geometry/geometry.png)

![geometry — geometry_simplification](img/geometry/geometry_simplification.png)

![geometry — geometry_screenshot](img/geometry/geometry_screenshot.png)

![geometry — geometry_simplification_screenshot](img/geometry/geometry_simplification_screenshot.png)

