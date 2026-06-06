using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Commands;
using Rhino.DocObjects;
using Rhino.Geometry;
using Rhino.Input;
using Rhino.Input.Custom;
using nest_rhino_lib;

namespace opennest_2.commands
{
    // Shared command-line nesting flow for both engines: select sheets -> select parts -> read a few
    // options -> build nest_geo / nest_sheets (holes & borders auto-detected by nest_geo.identify_groups,
    // which works here because a command has a live RhinoDoc.ActiveDoc) -> solve (engine-specific) ->
    // bake the placed copies. The engine-specific bit is the single abstract Solve() below.
    //
    // Input geometry: closed curves AND Brep/Surface/Extrusion/Mesh/SubD, each reduced to its WorldXY
    // outline (the "shadow" the nester packs) via rhino_conversions + SubD.ToBrep. Parts are flattened so
    // identify_groups re-pairs every outer/hole; each SHEET object keeps its own outer+holes as one sheet.
    public abstract class NestCommandBase : Command
    {
        // Engine-specific solve. Must populate geo.xforms (one transform list per part group), which
        // bake_with_transforms() then reads. Reuses NpRun / rhino_example - the SAME drivers the
        // Grasshopper components use - so there is no duplicate flatten/solve logic.
        protected abstract void Solve(ref nest_sheets sheets, ref nest_geo geo, int rotations, int budget, int seed);

        protected virtual int DefaultRotations => 4;
        protected virtual int DefaultBudget => 2000;   // collision relaxation rounds / NFP generations
        protected virtual int DefaultSeed => 1;
        protected virtual string BudgetPrompt => "Iterations";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // Sheets: keep each selected object's outline loops together (outer + holes = one sheet).
            var sheetObjs = SelectOutlinesGrouped("Select SHEET(s) (curve / surface / brep / mesh / subd), then Enter");
            if (sheetObjs == null || sheetObjs.Count == 0) return Result.Cancel;

            // Parts: flatten everything; identify_groups pairs each outer boundary with its holes.
            var partCurves = SelectOutlinesGrouped("Select PARTS to nest (curve / surface / brep / mesh / subd), then Enter")
                ?.SelectMany(o => o).ToList();
            if (partCurves == null || partCurves.Count == 0) return Result.Cancel;

            int rotations = DefaultRotations, budget = DefaultBudget, seed = DefaultSeed;
            if (RhinoGet.GetInteger("Rotations (Enter = default)", true, ref rotations, 1, 360) == Result.Cancel) return Result.Cancel;
            if (RhinoGet.GetInteger(BudgetPrompt + " (Enter = default)", true, ref budget, 1, 1000000) == Result.Cancel) return Result.Cancel;
            if (RhinoGet.GetInteger("Seed (Enter = default)", true, ref seed, 0, 1000000) == Result.Cancel) return Result.Cancel;

            double diag = BBoxDiagonal(partCurves.Concat(sheetObjs.SelectMany(o => o)));
            double seg = diag > 0 ? diag * 0.01 : 1.0;   // tessellation length for any curved input
            var simplify = new List<double> { seg, 0 };  // [segment length, hull=off]

            var curvesGrouped = partCurves.Select(c => new Curve[] { c }).ToList();
            var copies = Enumerable.Repeat(1, curvesGrouped.Count).ToList();
            nest_geo geo = nest_geo_util.geo_to_nest_geo(curvesGrouped, copies, simplify, null, hard_coded_input: false);
            if (geo.boundary_sorted == null || geo.boundary_sorted.Count == 0)
            {
                RhinoApp.WriteLine("OpenNest: no closed part boundaries detected.");
                return Result.Failure;
            }
            int holeGroups = geo.boundary_sorted.Count(g => g.Count > 1);

            // Sheets: convert each object's loops to polylines (outer + holes); nest_sheets keeps them
            // in place (copies == count) and sorts each sheet's rings (largest = outer).
            var helper = new nest_geo();
            var plinesList = sheetObjs
                .Select(o => o.Select(c => helper.curve_to_polyline(c, seg)).Where(pl => pl != null && pl.Count >= 4).ToList())
                .Where(l => l.Count > 0)
                .ToList();
            if (plinesList.Count == 0) { RhinoApp.WriteLine("OpenNest: no valid sheet outlines."); return Result.Failure; }
            double gap = diag * 0.03;
            var sheets = new nest_sheets(plinesList, new List<double> { gap, gap }, new List<int>(), plinesList.Count);

            RhinoApp.WriteLine($"OpenNest: {geo.boundary_sorted.Count} part group(s) ({holeGroups} with holes), {plinesList.Count} sheet(s). Solving...");

            try { Solve(ref sheets, ref geo, rotations, budget, seed); }
            catch (DllNotFoundException ex)
            {
                RhinoApp.WriteLine("OpenNest: native solver DLL not found next to the plug-in. " + ex.Message);
                return Result.Failure;
            }

            var ids = geo.bake_with_transforms();
            AddSheetFrames(doc, sheets);
            doc.Views.Redraw();
            RhinoApp.WriteLine($"OpenNest: baked {ids.Count} placed object(s).");
            return Result.Success;
        }

        // Select any nestable geometry; return one closed-curve list per selected object (so a sheet's
        // hole stays attached to its outer). Callers flatten for parts.
        private static List<List<Curve>> SelectOutlinesGrouped(string prompt)
        {
            var go = new GetObject();
            go.SetCommandPrompt(prompt);
            go.GeometryFilter = ObjectType.Curve | ObjectType.Brep | ObjectType.Surface
                              | ObjectType.Mesh | ObjectType.Extrusion | ObjectType.SubD;
            go.SubObjectSelect = false;
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return null;

            var result = new List<List<Curve>>();
            for (int i = 0; i < go.ObjectCount; i++)
            {
                var outlines = GeometryToOutlines(go.Object(i).Geometry());
                if (outlines.Count > 0) result.Add(outlines);
            }
            return result;
        }

        // Reduce any supported geometry to closed WorldXY outline curves. Curves pass through; Brep/Surface/
        // Extrusion/SubD go through BrepLoops (planar -> loops incl. holes; non-planar -> mesh XY outline);
        // Mesh uses its XY outline. SubD is converted via ToBrep first (the one type the GH path lacked).
        public static List<Curve> GeometryToOutlines(GeometryBase geo)
        {
            var result = new List<Curve>();
            switch (geo)
            {
                case Curve c:
                    if (c.IsClosed) result.Add(c.DuplicateCurve());
                    else RhinoApp.WriteLine("OpenNest: skipped an open curve (nesting needs closed outlines).");
                    break;
                case Brep b:
                    AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(b));
                    break;
                case Mesh m:
                    AddPolylines(result, nest_lib.rhino_conversions.MeshLoops(m));
                    break;
                case SubD sd:
                    var sbrep = sd.ToBrep(SubDToBrepOptions.Default);
                    if (sbrep != null) AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(sbrep));
                    break;
                case Surface s:   // also catches Extrusion (Extrusion : Surface)
                    var brep = s.ToBrep();
                    if (brep != null) AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(brep));
                    break;
                default:
                    RhinoApp.WriteLine($"OpenNest: unsupported geometry type {geo?.ObjectType}.");
                    break;
            }
            return result;
        }

        private static void AddPolylines(List<Curve> dst, Polyline[] pls)
        {
            if (pls == null) return;
            foreach (var pl in pls)
                if (pl != null && pl.IsValid && pl.Count >= 4)
                {
                    var c = pl.ToNurbsCurve();
                    if (c != null && c.IsClosed) dst.Add(c);
                }
        }

        private static double BBoxDiagonal(IEnumerable<Curve> curves)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (var c in curves) bb.Union(c.GetBoundingBox(false));
            return bb.IsValid ? bb.Diagonal.Length : 0;
        }

        private static void AddSheetFrames(RhinoDoc doc, nest_sheets sheets)
        {
            for (int s = 0; s < sheets.sheets.Length; s++)
            {
                if (sheets.sheets[s] == null) continue;
                foreach (var pl in sheets.sheets[s])
                    if (pl != null && pl.Count >= 2) doc.Objects.AddPolyline(pl);
            }
        }
    }
}
