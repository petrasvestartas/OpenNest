using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper2.Components;
using Grasshopper2.Data;
using Grasshopper2.Parameters;
using Grasshopper2.UI;
using GrasshopperIO;
using Rhino.Geometry;

namespace opennest_gh2.components
{
    // Components that read/write the active Rhino document. GH2 solves on a worker thread by default, so these
    // are marked UiSingleThreaded -> their Process runs on the UI thread where RhinoDoc.ActiveDoc is safe.

    // Text: build text outlines as curves (GH1 ComponentText e47a4beb-a459-4915-90a7-a18adc56f33c).
    [IoId("e47a4beb-a459-4915-90a7-a18adc56f33c")]
    public class TextComponent : Component
    {
        public TextComponent() : base(new Nomen("Text", "Text outlines as curves for laser/milling.", "OpenNest", "Util"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public TextComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("text.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddPlane("Location", "L", "Location and orientation of the text.");
            inputs.AddText("Text", "T", "Text to display.");
            inputs.AddNumber("Size", "S", "Size of the text.", Access.Item, Requirement.MayBeMissing).Set(1.0);
            inputs.AddText("Font", "F", "Font; blank = optimal. 'Name True True' = bold italic.", Access.Item, Requirement.MayBeMissing).Set("");
        }
        protected override void AddOutputs(OutputAdder outputs) => outputs.AddCurve("Curves", "C", "Text as curves.", Access.Twig);

        protected override void Process(IDataAccess access)
        {
            if (!access.GetItem(0, out Plane location) || !location.IsValid) return;
            if (!access.GetItem(1, out string text) || string.IsNullOrEmpty(text)) return;
            access.GetItem(2, out double size); if (size == 0) size = 1;
            access.GetItem(3, out string font); font = font ?? "";

            string[] options = font.Split(' ');
            bool bold = false, italic = false;
            if (options.Length == 3) { font = options[0]; bold = options[1] == "True" || options[1] == "true" || options[1] == "1"; italic = options[2] == "True" || options[2] == "true" || options[2] == "1"; }
            else if (options.Length == 1) font = options[0];

            var doc = Rhino.RhinoDoc.ActiveDoc;
            var curves = new List<Curve>();
            if (!string.IsNullOrEmpty(font) && doc != null)
            {
                var te = doc.Objects.AddText(text, location, size, font, bold, italic);
                if (doc.Objects.Find(te) is Rhino.DocObjects.TextObject txt && txt.Geometry is TextEntity tt)
                {
                    var ex = tt.Explode();
                    if (ex != null) curves.AddRange(ex);
                }
                doc.Objects.Delete(te, true);
            }
            else
            {
                // No font / no doc -> built-in single-line Typewriter (no document needed).
                var tw = nest_rhino_lib.Typewriter.Regular;
                var c = tw.Write(text, location.Origin, location.XAxis * size, location.YAxis * size, 0, 0);
                if (c != null) curves.AddRange(c);
            }
            access.SetTwig(0, curves.ToArray());
        }
    }

    // Transform Object: transform a referenced Rhino object in place (GH1 TransformGuid 3718d986-f4f1-40ee-4c0f-84ad2b4e1114).
    [IoId("3718d986-f4f1-40ee-4c0f-84ad2b4e1114")]
    public class TransformGuidComponent : Component
    {
        public TransformGuidComponent() : base(new Nomen("Transform Object", "Transform a referenced Rhino object in place.", "OpenNest", "Util"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public TransformGuidComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("transform_element_rhino.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Guid", "G", "Referenced geometry as guid.");
            inputs.AddTransform("Transform", "X", "Transformation to apply.");
            inputs.AddBoolean("Run", "R", "Set true to apply the transform.", Access.Item, Requirement.MayBeMissing).Set(false);
        }
        protected override void AddOutputs(OutputAdder outputs) { }

        protected override void Process(IDataAccess access)
        {
            access.GetItem(2, out bool run);
            if (!run) return;
            if (!access.GetItem(0, out Guid guid) || guid == Guid.Empty) { access.AddWarning("No guid", "Connect a referenced object id."); return; }
            access.GetItem(1, out Transform x);
            var doc = Rhino.RhinoDoc.ActiveDoc;
            if (doc != null) doc.Objects.Transform(guid, x, false);
        }
    }

    // Rhino Objects: read referenced Rhino objects into planar Breps (GH1 RhinoObjects 3278ccf2-1258-4896-ae14-1b125e122bbd).
    [IoId("3278ccf2-1258-4896-ae14-1b125e122bbd")]
    public class RhinoObjectsComponent : Component
    {
        public RhinoObjectsComponent() : base(new Nomen("Rhino Objects", "Referenced Rhino objects -> planar Breps for nesting.", "OpenNest", "Util"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public RhinoObjectsComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("element_rhino.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            inputs.AddGeneric("Geometry as guid", "G", "Referenced geometry as guid (mesh, brep, curves).", Access.Tree);
            inputs.AddNumber("Tolerance", "T", "Matching tolerance.", Access.Item, Requirement.MayBeMissing).Set(0.01);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Breps", "B", "Planar Breps.", Access.Twig);
            outputs.AddGeneric("Geometry as guid", "G", "Referenced ids grouped per brep.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            if (!access.GetTree(0, out Tree<Guid> tree) || tree == null || tree.LeafCount == 0) return;
            access.GetItem(1, out double tol);
            var guids = tree.NonNullItems.ToList();
            if (guids.Count == 0) return;

            var data = SortGuidsByPlanarCurves(guids, tol);
            access.SetTwig(0, data.Item1);
            var ids = new List<Guid>();
            foreach (var kv in data.Item2) ids.AddRange(kv.Value);
            access.SetTwig(1, ids.ToArray());
        }

        // Port of GH1 RhinoObjects.SortGuidsByPlanarCurves (uses RhinoDoc -> UI thread only).
        private static Tuple<Brep[], Dictionary<int, List<Guid>>> SortGuidsByPlanarCurves(List<Guid> guids, double tol)
        {
            var doc = Rhino.RhinoDoc.ActiveDoc;
            var crvsInput = new Dictionary<int, Curve>();
            int id = 0;
            foreach (var g in guids)
            {
                var obj = doc?.Objects.Find(g)?.Geometry;
                if (obj is Curve curve) crvsInput.Add(id, curve);
                id++;
            }
            var crvClosed = new List<Curve>();
            foreach (var c in crvsInput) { c.Value.Domain = new Interval(0, 1); if (c.Value.IsClosed && c.Value.IsPolyline()) crvClosed.Add(c.Value); }
            Brep[] breps = Brep.CreatePlanarBreps(crvClosed, tol <= 0 ? 0.01 : tol) ?? new Brep[0];
            var guidsTree = new Dictionary<int, List<Guid>>();
            for (int i = 0; i < breps.Length; i++) guidsTree.Add(i, new List<Guid>());
            var hash = new HashSet<int>();
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in crvsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.PointAt(0.5);
                        if (breps[i].ClosestPoint(p).DistanceTo(p) < tol) { hash.Add(j); guidsTree[i].Add(guids[c.Key]); }
                    }
                    j++;
                }
            }
            return Tuple.Create(breps, guidsTree);
        }
    }

    // Unroll: unroll a developable Brep/mesh to flat, carrying curves/points (GH1 Unroll 3718d679-f4f1-10ee-4c0f-84ad2b4e7811).
    [IoId("3718d679-f4f1-10ee-4c0f-84ad2b4e7811")]
    public class UnrollComponent : Component
    {
        public UnrollComponent() : base(new Nomen("Unroll", "Unroll a developable Brep/mesh to flat.", "OpenNest", "Util"))
        { Threading = ThreadingState.UiSingleThreaded; }
        public UnrollComponent(IReader reader) : base(reader) { }
        protected override Grasshopper2.UI.Icon.IIcon IconInternal => icons.SvgVectorIcon.Load("unroll.svg");

        protected override void AddInputs(InputAdder inputs)
        {
            // All Access.Tree so Process runs ONCE (mixing Item/Twig makes GH2 iterate and the generic Item read
            // returns nothing -> the "No geometry" with a Brep wired). We take the first geometry + flatten the rest.
            inputs.AddGeneric("Mesh or Brep", "M", "Mesh or Brep to unroll.", Access.Tree, Requirement.MayBeMissing);
            inputs.AddCurve("Curves", "C", "Curves to unroll along the surface.", Access.Tree, Requirement.MayBeMissing);
            inputs.AddPoint("Points", "P", "Points to unroll along the surface.", Access.Tree, Requirement.MayBeMissing);
            inputs.AddText("Text", "TXT", "Text labels to unroll along the surface.", Access.Tree, Requirement.MayBeMissing);
            inputs.AddPoint("Text point", "TP", "Anchor points for the text labels.", Access.Tree, Requirement.MayBeMissing);
        }
        protected override void AddOutputs(OutputAdder outputs)
        {
            outputs.AddGeneric("Brep", "B", "Unrolled Brep.", Access.Twig);
            outputs.AddCurve("Curves", "C", "Unrolled curves.", Access.Twig);
            outputs.AddPoint("Points", "P", "Unrolled points.", Access.Twig);
            outputs.AddPoint("TextDotsLocation", "TXT L", "Unrolled text label locations.", Access.Twig);
            outputs.AddText("TextDots", "TXT", "Unrolled text labels.", Access.Twig);
        }

        protected override void Process(IDataAccess access)
        {
            // First non-null geometry across the input tree (generic Item read is unreliable; Tree read works).
            GeometryBase geo = null;
            if (access.GetTree(0, out Grasshopper2.Data.Tree<GeometryBase> gtree) && gtree != null && gtree.LeafCount > 0)
            {
                gtree.ToArrays(out GeometryBase[][] gb);
                foreach (var br in gb) { if (br == null) continue; foreach (var g in br) if (g != null) { geo = g; break; } if (geo != null) break; }
            }
            if (geo == null) { access.AddWarning("No geometry", "Connect a Brep, surface, extrusion or mesh to the first input."); return; }
            double mtol = Rhino.RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;

            Brep brep = null;
            switch (geo)
            {
                case Brep b: brep = b; break;
                case Extrusion ex: brep = ex.ToBrep(true); break;        // boxes/extrusions arrive as Extrusion, not Brep
                case Surface srf: brep = srf.ToBrep(); break;            // a single (untrimmed) surface
                case Mesh mesh:
                {
                    var faceBreps = new List<Brep>();
                    foreach (MeshFace mf in mesh.Faces)
                        faceBreps.Add(mf.IsQuad
                            ? NurbsSurface.CreateFromCorners(mesh.Vertices[mf.A], mesh.Vertices[mf.B], mesh.Vertices[mf.C], mesh.Vertices[mf.D]).ToBrep()
                            : NurbsSurface.CreateFromCorners(mesh.Vertices[mf.A], mesh.Vertices[mf.B], mesh.Vertices[mf.C]).ToBrep());
                    var joined = Brep.JoinBreps(faceBreps, mtol);
                    if (joined != null && joined.Length > 0) brep = joined[0];
                    break;
                }
            }
            if (brep == null) { access.AddError("Invalid", "Input must be a Brep, surface, extrusion, SubD or mesh — got " + geo.GetType().Name + "."); return; }

            Curve[] crvs = FlattenTree<Curve>(access, 1);
            Point3d[] pts = FlattenTree<Point3d>(access, 2);
            string[] txtIn = FlattenTree<string>(access, 3);
            Point3d[] txtLoc = FlattenTree<Point3d>(access, 4);
            var unroller = new Unroller(brep);
            if (crvs != null && crvs.Length > 0) unroller.AddFollowingGeometry(crvs);
            if (pts != null && pts.Length > 0) unroller.AddFollowingGeometry(pts);
            // Text dots ride along too: pair each label with its anchor point (GH1 Unroll: Text + Text point).
            if (txtIn != null && txtLoc != null && txtIn.Length > 0 && txtLoc.Length > 0)
            {
                int n = Math.Min(txtIn.Length, txtLoc.Length);
                for (int i = 0; i < n; i++)
                    if (txtIn[i] != null) unroller.AddFollowingGeometry(txtLoc[i], txtIn[i]);
            }

            Brep[] flat; Curve[] outCrvs; Point3d[] outPts; TextDot[] outDots;
            try { flat = unroller.PerformUnroll(out outCrvs, out outPts, out outDots); }
            catch (Exception ex) { access.AddError("Unroll failed", "Rhino could not unroll this geometry: " + ex.Message); return; }
            if (flat == null || flat.Length == 0) { access.AddWarning("Unroll failed", "Surface may not be developable (closed solids must have unrollable faces)."); return; }
            Transform xform = TransformToWorldXY(flat);
            foreach (var f in flat) f.Transform(xform);
            if (outCrvs != null) foreach (var c in outCrvs) c.Transform(xform);
            var movedPts = new List<Point3d>();
            if (outPts != null) foreach (var p in outPts) { var pp = new Point3d(p); pp.Transform(xform); movedPts.Add(pp); }
            // Unrolled text dots -> split into locations + label strings (GH1 outputs TextDotsLocation + TextDots).
            var dotLocs = new List<Point3d>(); var dotTxt = new List<string>();
            if (outDots != null) foreach (var d in outDots) { if (d == null) continue; d.Transform(xform); dotLocs.Add(d.Point); dotTxt.Add(d.Text); }

            var joinedFlat = Brep.JoinBreps(flat, mtol) ?? flat;
            access.SetTwig(0, joinedFlat);
            access.SetTwig(1, outCrvs ?? new Curve[0]);
            access.SetTwig(2, movedPts.ToArray());
            access.SetTwig(3, dotLocs.ToArray());
            access.SetTwig(4, dotTxt.ToArray());
        }

        // Read a Tree-access input and flatten every branch into one array (empty if missing).
        private static T[] FlattenTree<T>(IDataAccess access, int index)
        {
            var list = new List<T>();
            if (access.GetTree(index, out Grasshopper2.Data.Tree<T> t) && t != null && t.LeafCount > 0)
            {
                t.ToArrays(out T[][] branches);
                foreach (var br in branches) if (br != null) foreach (var v in br) if (v != null) list.Add(v);
            }
            return list.ToArray();
        }

        // GH1 transformThem: pick the rotation (0..pi/10 steps) giving the smallest footprint, map onto WorldXY.
        // Hardened: a degenerate orientation (e.g. some faces of a closed box) makes Box.GetCorners() return an
        // EMPTY array, so the original's c[0]/c[3] indexing throws on box breps — we skip those orientations.
        private static Transform TransformToWorldXY(Brep[] breps)
        {
            var area = new double[11]; var planes = new Plane[11];
            for (int num = 0; num <= 10; num++)
            {
                var pts = new List<Point3d>();
                Plane plane = Plane.WorldXY;
                plane.Transform(Transform.Rotation(num / 10.0 * Math.PI, Point3d.Origin));
                foreach (var brep in breps)
                {
                    var bc = new Box(plane, brep).GetCorners();
                    if (bc != null && bc.Length >= 8) pts.AddRange(bc);
                }
                if (pts.Count < 2) { area[num] = double.MaxValue; planes[num] = Plane.WorldXY; continue; }
                Box box2 = new Box(plane, pts);
                var c = box2.GetCorners();
                if (c == null || c.Length < 4) { area[num] = double.MaxValue; planes[num] = Plane.WorldXY; continue; }
                area[num] = c[0].DistanceTo(c[1]) * c[0].DistanceTo(c[3]);
                var pl = c[0].DistanceTo(c[1]) < c[0].DistanceTo(c[3])
                    ? new Plane(c[0], c[1], c[3]) : new Plane(c[0], c[3], c[1]);
                planes[num] = pl.IsValid ? pl : Plane.WorldXY;
            }
            Array.Sort(area, planes);
            return planes[0].IsValid ? Transform.PlaneToPlane(planes[0], Plane.WorldXY) : Transform.Identity;
        }
    }
}
