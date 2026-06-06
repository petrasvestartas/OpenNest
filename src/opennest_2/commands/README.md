# OpenNest Rhino commands

Command-line (no Grasshopper) front-end for the two nesting engines, added to the `opennest_2` assembly.

| Command | Engine | Native DLL |
|---|---|---|
| `OpenNestCollision` | penetration-depth overlap relaxation (`NpRun`) | `nest_physics.dll` / `np_nest` |
| `OpenNest2` | NFP + genetic algorithm (`nest_lib.rhino_example`, `Engine="cpp"`) | `nfp_nest.dll` |

Both commands reuse the **same** drivers the Grasshopper components use — no duplicated flatten/solve logic.

## Workflow

1. Run `OpenNestCollision` or `OpenNest2`.
2. **Select sheet(s)** — closed curves, surfaces, breps, meshes or SubDs. Each selected object becomes one
   sheet (its outer loop + any inner holes are kept together as keep-out regions).
3. **Select parts** — same geometry types. Holes vs. borders are detected automatically by
   `nest_geo.identify_groups` (containment + winding), so you just select everything.
4. **Options** (press Enter to accept each default): `Rotations`, `Iterations`/`Generations`, `Seed`.
5. Placed copies are baked (grouped per sheet via `nest_geo.bake_with_transforms`) and the sheet frames added.

## Geometry → outline

Each selection is reduced to its WorldXY outline before nesting (the flat "shadow" the engines pack):

- **Curve** — used as-is (must be closed).
- **Brep / Surface / Extrusion** — `rhino_conversions.BrepLoops` (planar → loops incl. holes; non-planar →
  `Mesh.CreateFromBrep` → `Mesh.GetOutlines(WorldXY)`).
- **Mesh** — `Mesh.GetOutlines(WorldXY)`.
- **SubD** — `SubD.ToBrep(SubDToBrepOptions.Default)` → the Brep path (the type the Grasshopper intake lacked).

## Build / load

`dotnet build opennest_2.csproj -c Release` produces `opennest_2.gha` with `nest_physics.dll` + `nfp_nest.dll`
copied alongside. Load it in Rhino 8 (drag-drop or Libraries folder) — the `Rhino.PlugIns.PlugIn` subclass in
`OpenNestCommandPlugIn.cs` registers the two commands.

> If a Rhino build does not surface commands from a `.gha`, compile these `commands/*.cs` (plus the reused
> `nest_rhino_lib`/`nest_lib` classes) into a thin separate `.rhp` — the command logic is identical either way.
