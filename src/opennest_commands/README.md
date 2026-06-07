# OpenNest Rhino commands (`opennest_commands.rhp`)

Command-line (no Grasshopper) front-end for the two nesting engines. A Grasshopper `.gha` **cannot**
expose Rhino commands, so these live in a separate Rhino plug-in (`.rhp`) that reuses the nesting code in
`opennest_2` via a project reference. Both files install into the same folder from one Yak package.

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

`dotnet build opennest_commands.csproj -c Release` also builds `opennest_2` (project reference) and produces
`opennest_commands.rhp`. For Rhino to find both, the `.rhp` must sit in the **same folder** as `opennest_2.gha`
+ `nest_physics.dll` + `nfp_nest.dll`. The Yak workflow (`publish.yml`) does this automatically: it copies the
`.rhp` into each per-OS package next to the `.gha`, so one Yak install delivers the Grasshopper components and
the Rhino commands together.

To load locally, copy `opennest_commands.rhp` next to `opennest_2.gha` and drag it onto Rhino (or add it via
`PlugInManager`). `OpenNestCommandPlugIn.OnLoad` installs an `AssemblyResolve` handler so the `.rhp` can find
`opennest_2.gha` even if Grasshopper hasn't loaded it yet (the `.gha` extension defeats the default resolver).
