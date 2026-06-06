# Principal Component Analysis

Returns an oriented bounding box aligned to a point set's **edges** — a minimum‑area rectangle for flat (planar) inputs and a minimum‑volume box for solids — together with the aligned plane and the box's 8 corner points. Unlike a plain covariance‑PCA, the box stays edge‑aligned even for cubes, squares, and other symmetric shapes.

## Inputs

| Parameter | Type | Access | Description |
| --- | --- | --- | --- |
| **Points** (P) | Point | list | Point set to analyze |

## Outputs

| Parameter | Type | Description |
| --- | --- | --- |
| **P** (Plane) | Plane | Principal-axis aligned plane |
| **B** (Box) | Box | Oriented bounding box |
| **Pt** (Points) | Point | Bounding box corner points |

## Example

**Files to download:**

- [⬇ principal_component_analysis.ghx](files/pca/principal_component_analysis.ghx)

![pca — principal_component_analysis](img/pca/principal_component_analysis.png)

![pca — principal_component_analysi_screenshot](img/pca/principal_component_analysi_screenshot.png)

