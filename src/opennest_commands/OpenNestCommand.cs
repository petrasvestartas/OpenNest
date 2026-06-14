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

namespace opennest_commands
{
    // ONE command-line nester. Type "OpenNest": select SHEET(s), then make ONE mixed selection of parts and
    // markup. Every Grasshopper option is a clickable command-line option (Engine, Rotations, Seed, …).
    //
    // CLASSIFICATION (one mixed pick): a planar SURFACE becomes a part (outer loop = border, inner loops =
    // holes); a closed CURVE becomes a part border (closed curves inside it = holes); ANY other object that
    // sits inside a part's border becomes an ATTRIBUTE that travels with that part. Holes are kept empty by the
    // solver. Results bake into layers opennest_sheets / opennest_outlines / opennest_attributes, with a
    // sublayer per source layer, preserving each object's color / material / name / groups. The solve runs on a
    // BACKGROUND thread (UI pumped) so Rhino never freezes.
    [System.Runtime.InteropServices.Guid("c2e7b4d8-5a16-4e93-bf02-9d3c1a76e548")]
    public class OpenNestCommand : Command
    {
        public override string EnglishName => "OpenNest";

        // ---- sticky option state (defaults MATCH the Grasshopper components) ----
        static int s_engine = 0;        // 0 = NFP (OpenNest2), 1 = Collision (OpenNestCollision)
        static int s_nfpRot = 8, s_nfpPlace = 1, s_nfpSeed = 30, s_nfpIter = 10, s_nfpMut = 10, s_nfpPop = 10, s_nfpHoles = 1;
        static double s_nfpSpacing = 0;
        static bool s_nfpAllRot = true;
        static int s_colRot = 3600, s_colSeed = 100, s_colStarts = 1, s_colIter = 4000, s_colPoles = 48, s_colHoles = 1, s_colCompact = 1, s_colFit = 0;

        // A picked object: its geometry plus its real Rhino attributes (color/layer/material/name/groups).
        private class Pick { public GeometryBase Geom; public ObjectAttributes Attr; }
        // A nesting part: its boundary rings (outer + holes), the outer ring, source attributes, and the
        // attribute objects that were found inside it.
        private class Part { public Curve[] Rings; public Curve Outer; public ObjectAttributes Attr; public readonly List<Pick> Attrs = new List<Pick>(); }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            // 1) SHEETS.
            var sheetPicks = SelectPicks("Select SHEET(s) to nest onto, then press Enter", preselect: true);
            if (sheetPicks == null || sheetPicks.Count == 0) return Result.Cancel;
            var sheetObjs = sheetPicks.Select(p => GeometryToOutlines(p.Geom)).Where(o => o.Count > 0).ToList();
            if (sheetObjs.Count == 0) { RhinoApp.WriteLine("OpenNest: no closed sheet outlines."); return Result.Failure; }

            // 2) PARTS + ATTRIBUTES — one mixed pick, with the full option menu.
            doc.Objects.UnselectAll(); doc.Views.Redraw();
            var picks = SelectPartsWithOptions();
            if (picks == null || picks.Count == 0) return Result.Cancel;

            // 3) CLASSIFY. A closed BOUNDARY — planar surface, Brep, NURBS surface, SubD, extrusion, OR closed
            //    curve — is a PART if it stands alone, or an ATTRIBUTE if it sits inside another part. As an
            //    attribute the ORIGINAL geometry is kept verbatim (a brep/surface/subd rides along, not just an
            //    outline). HOLES come ONLY from a part surface's trimmed inner loops; open curves / text /
            //    points / hatches are always attributes.
            var parts = new List<Part>();
            var boundaryCands = new List<KeyValuePair<Pick, List<Curve>>>();   // surfaces/breps/subd + closed curves
            var otherCands = new List<Pick>();
            foreach (var pk in picks)
            {
                bool isBoundaryType = pk.Geom is Brep || pk.Geom is Surface || pk.Geom is Extrusion || pk.Geom is SubD
                                      || (pk.Geom is Curve c0 && c0.IsClosed);
                if (isBoundaryType)
                {
                    var outlines = GeometryToOutlines(pk.Geom);
                    if (outlines.Count >= 1) boundaryCands.Add(new KeyValuePair<Pick, List<Curve>>(pk, outlines));
                    else otherCands.Add(pk);   // a boundary that yields no closed outline -> treat as markup
                }
                else otherCands.Add(pk);
            }

            // Largest first: inside an existing part -> ATTRIBUTE (keep the ORIGINAL geometry); otherwise a PART
            // (outer + the surface's trim holes).
            foreach (var bc in boundaryCands.OrderByDescending(b => BBoxArea(LargestRing(b.Value).GetBoundingBox(false))))
            {
                var outer = LargestRing(bc.Value);
                var owner = FindOwnerPart(parts, outer);
                if (owner != null) owner.Attrs.Add(bc.Key);
                else parts.Add(new Part { Rings = bc.Value.ToArray(), Outer = outer, Attr = bc.Key.Attr });
            }
            if (parts.Count == 0) { RhinoApp.WriteLine("OpenNest: no part boundaries (planar surfaces / closed curves) in the selection."); return Result.Failure; }

            // 4) Remaining markup (open curves, text, points, hatches) -> the part that contains it.
            int skipped = 0;
            foreach (var a in otherCands)
            {
                var owner = FindOwnerPart(parts, a.Geom);
                if (owner != null) owner.Attrs.Add(a); else skipped++;
            }
            if (skipped > 0) RhinoApp.WriteLine($"OpenNest: {skipped} markup object(s) were inside no part and were skipped.");

            // 5) Build nest_geo (boundaries only; identify_groups pairs holes by containment). Track ring counter
            //    -> part index so we can map a solved part back to its source object + attributes after sorting.
            var curvesGrouped = parts.Select(p => p.Rings).ToList();
            var copies = Enumerable.Repeat(1, curvesGrouped.Count).ToList();
            var counterToPart = new Dictionary<int, int>();
            int counter = 0;
            for (int pi = 0; pi < parts.Count; pi++)
                for (int r = 0; r < parts[pi].Rings.Length; r++) counterToPart[counter++] = pi;

            double diag = BBoxDiagonal(parts.SelectMany(p => p.Rings).Concat(sheetObjs.SelectMany(o => o)));
            // simplify = 0 -> take the boundary EXACTLY as drawn (every vertex kept; no colinear merge), so the
            // nesting polygon == the baked original and parts can't overlap.
            // hard_coded_input = FALSE -> identify_groups pairs a surface's trimmed inner loops to its outer as
            // HOLES by containment (the proven path; its Item1 carries the real source index, which the buggy
            // hard_coded_input does NOT). Loose closed curves never become holes here because the classification
            // above already pulled every closed curve sitting inside a part OUT of the parts list (as an
            // attribute) — so the only inner rings identify_groups ever sees are real surface trims.
            nest_geo geoLocal = nest_geo_util.geo_to_nest_geo(curvesGrouped, copies, new List<double> { 0, 0 }, null, hard_coded_input: false);
            if (geoLocal.boundary_sorted == null || geoLocal.boundary_sorted.Count == 0)
            { RhinoApp.WriteLine("OpenNest: no closed part boundaries detected."); return Result.Failure; }

            var helper = new nest_geo();
            var plinesList = sheetObjs
                .Select(o => o.Select(c => helper.curve_to_polyline(c, 0, false, true)).Where(pl => pl != null && pl.Count >= 4).ToList())   // 0 + keep_all = exact sheet outline
                .Where(l => l.Count > 0).ToList();
            if (plinesList.Count == 0) { RhinoApp.WriteLine("OpenNest: no valid sheet outlines."); return Result.Failure; }
            double gap = diag * 0.03;

            // AUTO-OVERFLOW: duplicate the selected sheet set to the RIGHT so parts that don't fit spill onto
            // copies. Make plenty of copies up front (sized to the part area, capped) — only the USED sheets
            // get their frame baked at the end, so empty trailing copies don't clutter the document.
            var setBB = BoundingBox.Empty;
            foreach (var s in plinesList) foreach (var pl in s) setBB.Union(pl.BoundingBox);
            double setW = (setBB.IsValid ? (setBB.Max.X - setBB.Min.X) : 0) + gap;
            double partAreaSum = parts.Sum(p => BBoxArea(p.Outer.GetBoundingBox(false)));
            double sheetAreaSum = plinesList.Sum(s => s.Count > 0 ? BBoxArea(s[0].BoundingBox) : 0);
            int sheetCopies = Math.Min(40, Math.Max(3, (int)Math.Ceiling(partAreaSum * 1.8 / Math.Max(1.0, sheetAreaSum)) + 2));
            var baseSheets = plinesList.ToList();
            for (int copy = 1; copy < sheetCopies && setW > 1e-6; copy++)
                foreach (var s in baseSheets)
                    plinesList.Add(s.Select(pl => { var q = new Polyline(pl); q.Transform(Transform.Translation(copy * setW, 0, 0)); return q; }).ToList());

            nest_sheets sheetsLocal = new nest_sheets(plinesList, new List<double> { gap, gap }, new List<int>(), plinesList.Count);

            string engineName = s_engine == 0 ? "NFP" : "Collision";
            int holeCount = geoLocal.boundary_sorted.Count(g => g.Count > 1);
            RhinoApp.WriteLine($"OpenNest [{engineName}]: {geoLocal.boundary_sorted.Count} part(s) ({holeCount} with holes), {parts.Sum(p => p.Attrs.Count)} attribute(s), {plinesList.Count} sheet(s). Solving in the background…");

            // 6) SOLVE on a background thread (pump the UI).
            nest_lib.rhino_example nfp = null;
            opennest_2.NpRun col = null;
            if (s_engine == 0)
            {
                var p = new List<double> { s_nfpRot, 0, s_nfpPlace, s_nfpSpacing, s_nfpSeed, 1, s_nfpMut, s_nfpPop, 0 };
                nfp = new nest_lib.rhino_example(ref sheetsLocal, ref geoLocal, p, s_nfpIter);
                nfp.TryAllRotations = s_nfpAllRot ? 1 : 0; nfp.UseHoles = s_nfpHoles; nfp.ExactNfp = 1;
            }
            else
            {
                var p = new List<double> { s_colRot, s_colSeed, s_colStarts };
                col = new opennest_2.NpRun();
                int fitMode = (s_colFit == 0) ? 1 : 0;
                col.Flatten(sheetsLocal, geoLocal, p, s_colIter, partHolesMode: s_colHoles, poles: s_colPoles, compact: (s_colCompact != 0), fitMode: fitMode);
            }

            Exception solveError = null;
            var task = System.Threading.Tasks.Task.Run(() =>
            {
                try { if (nfp != null) nfp.static_solver(ref geoLocal); else col.Solve(); }
                catch (Exception ex) { solveError = ex; }
            });
            while (!task.IsCompleted)
            {
                string msg = nfp != null ? $"OpenNest [NFP] solving…  gen {SafeGen(nfp)}/{s_nfpIter}" : "OpenNest [Collision] solving…  (please wait)";
                RhinoApp.SetCommandPrompt(msg + "   (Rhino stays responsive)");
                try { Eto.Forms.Application.Instance?.RunIteration(); } catch { }   // cross-platform UI pump (Win + Mac)
                System.Threading.Thread.Sleep(40);
            }
            if (solveError != null)
            {
                RhinoApp.WriteLine("OpenNest: solve failed — " + (solveError is DllNotFoundException ? "native solver DLL not found. " : "") + solveError.Message);
                return Result.Failure;
            }
            if (col != null) col.Assemble();

            // 7) BAKE into layers, preserving Rhino object data (UI thread, one undo record).
            uint undo = doc.BeginUndoRecord("OpenNest");
            try
            {
                int sheetLayer   = EnsureLayer(doc, "opennest_sheets",     Guid.Empty, System.Drawing.Color.Gray);
                int outlineParent = EnsureLayer(doc, "opennest_outlines",   Guid.Empty, System.Drawing.Color.Black);
                int attrParent    = EnsureLayer(doc, "opennest_attributes", Guid.Empty, System.Drawing.Color.DimGray);
                int baked = 0;
                var placedBB = BoundingBox.Empty;   // union of placed part outlines -> which sheets were used

                for (int i = 0; i < geoLocal.boundary_sorted.Count; i++)
                {
                    int outerCounter = geoLocal.boundary_sorted[i][0].Item1;
                    if (!counterToPart.TryGetValue(outerCounter, out int pi)) continue;
                    var part = parts[pi];
                    var xs = (geoLocal.xforms != null && i < geoLocal.xforms.Count) ? geoLocal.xforms[i] : null;
                    if (xs == null) continue;

                    for (int k = 0; k < xs.Count; k++)
                    {
                        var T = xs[k];
                        var guids = new List<Guid>();

                        // border + holes -> opennest_outlines (sublayer per source layer), source object's attrs
                        int outLayer = SubLayerForSource(doc, outlineParent, part.Attr);
                        foreach (var ring in part.Rings)
                        {
                            var c = ring.DuplicateCurve(); if (c == null) continue; c.Transform(T);
                            placedBB.Union(c.GetBoundingBox(false));
                            var at = DupAttr(part.Attr); at.LayerIndex = outLayer;
                            var id = doc.Objects.AddCurve(c, at);
                            if (id != Guid.Empty) { guids.Add(id); baked++; }
                        }

                        // attributes -> opennest_attributes (sublayer per source layer), each keeps its own attrs
                        foreach (var a in part.Attrs)
                        {
                            var g = a.Geom.Duplicate(); if (g == null) continue; g.Transform(T);
                            var at = DupAttr(a.Attr); at.LayerIndex = SubLayerForSource(doc, attrParent, a.Attr);
                            var id = AddGeometry(doc, g, at);
                            if (id != Guid.Empty) { guids.Add(id); baked++; }
                        }

                        // group this placed part instance (outline + its attributes) together
                        if (guids.Count > 0) doc.Groups.Add($"opennest_part_{i}_{k}", guids);
                    }
                }

                int sheetsUsed = AddSheetFrames(doc, sheetsLocal, sheetLayer, placedBB);
                doc.Views.Redraw();
                RhinoApp.WriteLine($"OpenNest [{engineName}]: done — baked {baked} object(s) into opennest_outlines / opennest_attributes, on {sheetsUsed} sheet(s).");
            }
            finally { doc.EndUndoRecord(undo); }
            return Result.Success;
        }

        private static int SafeGen(nest_lib.rhino_example n) { try { return n.CurrentGeneration; } catch { return 0; } }

        // ---- layers ----
        private static int EnsureLayer(RhinoDoc doc, string name, Guid parentId, System.Drawing.Color? color)
        {
            foreach (var lay in doc.Layers)
            {
                if (lay.IsDeleted) continue;
                if (string.Equals(lay.Name, name, StringComparison.Ordinal) && lay.ParentLayerId == parentId) return lay.Index;
            }
            var L = new Layer { Name = name, ParentLayerId = parentId };
            if (color.HasValue) L.Color = color.Value;
            return doc.Layers.Add(L);
        }

        // Sublayer (under parent) that is a COPY of the source object's original layer — same name AND colour —
        // so the original layer structure and colours are preserved and by-layer objects display correctly.
        private static int SubLayerForSource(RhinoDoc doc, int parentIndex, ObjectAttributes src)
        {
            var parent = doc.Layers.FindIndex(parentIndex);
            string srcName = "Default";
            System.Drawing.Color? srcColor = null;
            try
            {
                var sl = (src != null) ? doc.Layers.FindIndex(src.LayerIndex) : null;
                if (sl != null)
                {
                    if (!string.IsNullOrEmpty(sl.Name)) srcName = sl.Name;
                    srcColor = sl.Color;
                }
            }
            catch { }
            return EnsureLayer(doc, srcName, parent != null ? parent.Id : Guid.Empty, srcColor);
        }

        private static ObjectAttributes DupAttr(ObjectAttributes a) => a != null ? a.Duplicate() : new ObjectAttributes();

        // Add any geometry type to the doc with attributes (preserves color/material/name/groups in `at`).
        private static Guid AddGeometry(RhinoDoc doc, GeometryBase g, ObjectAttributes at)
        {
            switch (g)
            {
                case Curve c: return doc.Objects.AddCurve(c, at);
                case Brep b: return doc.Objects.AddBrep(b, at);
                case Mesh m: return doc.Objects.AddMesh(m, at);
                case Extrusion e: return doc.Objects.AddExtrusion(e, at);
                case SubD sd: return doc.Objects.AddSubD(sd, at);
                case Surface s: return doc.Objects.AddSurface(s, at);
                case Rhino.Geometry.Point p: return doc.Objects.AddPoint(p.Location, at);
                case PointCloud pc: return doc.Objects.AddPointCloud(pc, at);
                case TextEntity t: return doc.Objects.AddText(t, at);
                case Hatch h: return doc.Objects.AddHatch(h, at);
                default:
                    try { return doc.Objects.Add(g, at); } catch { return Guid.Empty; }
            }
        }

        // Interior representative point for containment: area centroid for closed/area geometry (NOT a perimeter
        // point), open-curve midpoint, point/dot location, text origin, else bounding-box center.
        private static Point3d RepPoint(GeometryBase g)
        {
            try
            {
                switch (g)
                {
                    case Curve c:
                        if (c.IsClosed) { var amp = AreaMassProperties.Compute(c); if (amp != null) return amp.Centroid; }
                        return c.PointAtNormalizedLength(0.5);
                    case Rhino.Geometry.Point p: return p.Location;
                    case TextDot d: return d.Point;
                    case TextEntity t: return t.Plane.Origin;
                    case Hatch h: { var amp = AreaMassProperties.Compute(h); if (amp != null) return amp.Centroid; break; }
                }
            }
            catch { }
            var bb = g.GetBoundingBox(false);
            return bb.IsValid ? bb.Center : Point3d.Origin;
        }

        // The part an attribute belongs to: smallest part whose outer border contains its centroid; if the
        // centroid test fails (concave parts, edge cases) fall back to the smallest part whose bbox fully
        // contains the attribute's bbox. Returns null only when the attribute is inside no part.
        private static Part FindOwnerPart(List<Part> parts, GeometryBase geom)
        {
            var owner = SmallestContaining(parts, RepPoint(geom));
            if (owner != null) return owner;
            var gbb = geom.GetBoundingBox(false);
            if (!gbb.IsValid) return null;
            Part best = null; double bestArea = double.MaxValue;
            foreach (var p in parts)
            {
                var pbb = p.Outer.GetBoundingBox(false);
                if (pbb.Min.X <= gbb.Min.X + 1e-6 && pbb.Min.Y <= gbb.Min.Y + 1e-6 &&
                    pbb.Max.X >= gbb.Max.X - 1e-6 && pbb.Max.Y >= gbb.Max.Y - 1e-6)
                {
                    double area = BBoxArea(pbb);
                    if (area < bestArea) { bestArea = area; best = p; }
                }
            }
            return best;
        }

        // PARTS + ATTRIBUTES selection with the full clickable option menu (Engine-conditional).
        private List<Pick> SelectPartsWithOptions()
        {
            var go = new GetObject();
            go.GeometryFilter = ObjectType.Curve | ObjectType.Brep | ObjectType.Surface | ObjectType.Mesh
                              | ObjectType.Extrusion | ObjectType.SubD | ObjectType.Annotation | ObjectType.Point
                              | ObjectType.TextDot | ObjectType.Hatch | ObjectType.PointSet;
            go.SubObjectSelect = false;
            go.EnablePreSelect(false, true);
            go.EnablePostSelect(true);

            for (;;)
            {
                int eng = s_engine;
                go.SetCommandPrompt("Select PARTS + markup to nest — set options below, then pick and press Enter");
                go.ClearCommandOptions();
                int oEngine = go.AddOptionList("Engine", new[] { "NFP", "Collision" }, s_engine);

                var oiRot = new OptionInteger(eng == 0 ? s_nfpRot : s_colRot, 1, 3600);
                var oiSeed = new OptionInteger(eng == 0 ? s_nfpSeed : s_colSeed, 0, 1000000);
                var oiIter = new OptionInteger(eng == 0 ? s_nfpIter : s_colIter, 1, 1000000);
                var odSpacing = new OptionDouble(s_nfpSpacing, 0, 1e9);
                var oiMut = new OptionInteger(s_nfpMut, 0, 100);
                var oiPop = new OptionInteger(s_nfpPop, 1, 100000);
                var otAllRot = new OptionToggle(s_nfpAllRot, "No", "Yes");
                var oiStarts = new OptionInteger(s_colStarts, 1, 64);
                var oiPoles = new OptionInteger(s_colPoles, 4, 64);

                int oPlace = -1, oHoles = -1, oCompact = -1, oFit = -1;
                if (eng == 0)
                {
                    go.AddOptionInteger("Rotations", ref oiRot);
                    oPlace = go.AddOptionList("Placement", new[] { "Box", "Gravity", "Squeeze", "BottomLeft" }, s_nfpPlace);
                    go.AddOptionDouble("Spacing", ref odSpacing);
                    go.AddOptionInteger("Seed", ref oiSeed);
                    go.AddOptionInteger("Iterations", ref oiIter);
                    go.AddOptionInteger("Mutation", ref oiMut);
                    go.AddOptionInteger("Population", ref oiPop);
                    go.AddOptionToggle("AllRotations", ref otAllRot);
                    oHoles = go.AddOptionList("ElementHoles", new[] { "Off", "Fill" }, s_nfpHoles);
                }
                else
                {
                    go.AddOptionInteger("Rotations", ref oiRot);
                    go.AddOptionInteger("Seed", ref oiSeed);
                    go.AddOptionInteger("Starts", ref oiStarts);
                    go.AddOptionInteger("Iterations", ref oiIter);
                    go.AddOptionInteger("Poles", ref oiPoles);
                    oHoles = go.AddOptionList("ElementHoles", new[] { "Off", "Fill", "FillFirst" }, s_colHoles);
                    oCompact = go.AddOptionList("Compact", new[] { "Off", "BottomLeft", "Multi" }, s_colCompact);
                    oFit = go.AddOptionList("Fit", new[] { "OneSheet", "AllParts" }, s_colFit);
                }

                var res = go.GetMultiple(1, 0);

                if (eng == 0)
                {
                    s_nfpRot = oiRot.CurrentValue; s_nfpSeed = oiSeed.CurrentValue; s_nfpIter = oiIter.CurrentValue;
                    s_nfpSpacing = odSpacing.CurrentValue; s_nfpMut = oiMut.CurrentValue; s_nfpPop = oiPop.CurrentValue; s_nfpAllRot = otAllRot.CurrentValue;
                }
                else
                {
                    s_colRot = oiRot.CurrentValue; s_colSeed = oiSeed.CurrentValue; s_colIter = oiIter.CurrentValue;
                    s_colStarts = oiStarts.CurrentValue; s_colPoles = oiPoles.CurrentValue;
                }

                if (res == GetResult.Option)
                {
                    int idx = go.OptionIndex();
                    if (idx == oEngine) s_engine = go.Option().CurrentListOptionIndex;
                    else if (idx == oPlace) s_nfpPlace = go.Option().CurrentListOptionIndex;
                    else if (idx == oHoles) { if (eng == 0) s_nfpHoles = go.Option().CurrentListOptionIndex; else s_colHoles = go.Option().CurrentListOptionIndex; }
                    else if (idx == oCompact) s_colCompact = go.Option().CurrentListOptionIndex;
                    else if (idx == oFit) s_colFit = go.Option().CurrentListOptionIndex;
                    continue;
                }
                if (res != GetResult.Object) return null;

                var picks = new List<Pick>();
                for (int i = 0; i < go.ObjectCount; i++) { var pk = ToPick(go.Object(i)); if (pk != null) picks.Add(pk); }
                return picks;
            }
        }

        // Simple pick (sheets) — captures geometry + attributes too.
        private static List<Pick> SelectPicks(string prompt, bool preselect)
        {
            var go = new GetObject();
            go.SetCommandPrompt(prompt);
            go.GeometryFilter = ObjectType.Curve | ObjectType.Brep | ObjectType.Surface | ObjectType.Mesh | ObjectType.Extrusion | ObjectType.SubD;
            go.SubObjectSelect = false;
            go.EnablePreSelect(preselect, true);
            go.EnablePostSelect(true);
            go.GetMultiple(1, 0);
            if (go.CommandResult() != Result.Success) return null;
            var picks = new List<Pick>();
            for (int i = 0; i < go.ObjectCount; i++) { var pk = ToPick(go.Object(i)); if (pk != null) picks.Add(pk); }
            return picks;
        }

        private static Pick ToPick(ObjRef oref)
        {
            if (oref == null) return null;
            var geom = oref.Geometry();
            if (geom == null) return null;
            ObjectAttributes attr = null;
            try { var ro = oref.Object(); if (ro != null) attr = ro.Attributes.Duplicate(); } catch { }
            return new Pick { Geom = geom, Attr = attr ?? new ObjectAttributes() };
        }

        // Reduce any supported geometry to closed WorldXY outline curves (outer + holes). Returns empty for
        // anything that is NOT a closed boundary (open curve, text, point, hatch…) — those become attributes.
        public static List<Curve> GeometryToOutlines(GeometryBase geo)
        {
            var result = new List<Curve>();
            switch (geo)
            {
                case Curve c:
                    if (c.IsClosed) result.Add(c.DuplicateCurve());
                    break;
                case Brep b:
                    AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(b));
                    break;
                case Extrusion ex:
                    var exb = ex.ToBrep(); if (exb != null) AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(exb));
                    break;
                case Mesh m:
                    AddPolylines(result, nest_lib.rhino_conversions.MeshLoops(m));
                    break;
                case SubD sd:
                    var sbrep = sd.ToBrep(SubDToBrepOptions.Default); if (sbrep != null) AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(sbrep));
                    break;
                case Surface s:
                    var brep = s.ToBrep(); if (brep != null) AddPolylines(result, nest_lib.rhino_conversions.BrepLoops(brep));
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

        private static Curve LargestRing(List<Curve> rings)
        {
            Curve best = rings[0]; double bd = best.GetBoundingBox(false).Diagonal.Length;
            foreach (var r in rings) { double d = r.GetBoundingBox(false).Diagonal.Length; if (d > bd) { bd = d; best = r; } }
            return best;
        }

        private static double BBoxDiagonal(IEnumerable<Curve> curves)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (var c in curves) bb.Union(c.GetBoundingBox(false));
            return bb.IsValid ? bb.Diagonal.Length : 0;
        }

        // Bake the frame of each USED sheet (one whose left edge is at/left of the rightmost placed part).
        // Empty trailing overflow copies are skipped. Returns the number of sheets drawn.
        private static int AddSheetFrames(RhinoDoc doc, nest_sheets sheets, int layerIndex, BoundingBox placedBB)
        {
            var at = new ObjectAttributes { LayerIndex = layerIndex };
            double maxX = placedBB.IsValid ? placedBB.Max.X : double.NegativeInfinity;
            int used = 0;
            for (int s = 0; s < sheets.sheets.Length; s++)
            {
                var sh = sheets.sheets[s];
                if (sh == null) continue;
                var sbb = BoundingBox.Empty;
                foreach (var pl in sh) if (pl != null) sbb.Union(pl.BoundingBox);
                bool isUsed = (s == 0) || (sbb.IsValid && sbb.Min.X <= maxX + 1e-6);
                if (!isUsed) continue;
                used++;
                foreach (var pl in sh) if (pl != null && pl.Count >= 2) doc.Objects.AddPolyline(pl, at);
            }
            return used;
        }

        private static double BBoxArea(BoundingBox bb) => bb.IsValid ? (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y) : 0;

        // Smallest-area part whose outer border contains the point (tightest fit when nested).
        private static Part SmallestContaining(List<Part> parts, Point3d rep)
        {
            Part best = null; double bestArea = double.MaxValue;
            foreach (var p in parts)
            {
                var bb = p.Outer.GetBoundingBox(false);
                var plane = new Plane(new Point3d(0, 0, bb.Center.Z), Vector3d.ZAxis);
                try
                {
                    if (p.Outer.Contains(rep, plane, 0.01) == PointContainment.Inside)
                    {
                        double area = (bb.Max.X - bb.Min.X) * (bb.Max.Y - bb.Min.Y);
                        if (area < bestArea) { bestArea = area; best = p; }
                    }
                }
                catch { }
            }
            return best;
        }
    }
}
