
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using Rhino.Geometry;
using OpenNestLib;
using DeepNestLib;

namespace OpenNestRhino {
    public class OpenNest : Command {

        bool run = false;
        bool reset = false;
        bool staticSolver = false;

        Transform[] scalesInv = new Transform[0];
        Transform[] scales = new Transform[0];
        Transform[] orient = new Transform[0];



        Polyline[] sheetsRhino = new Polyline[0];
        int[] id = new int[0];
        Transform[] transforms = new Transform[0];
        //List<IGH_GeometricGoo> geo = new List<IGH_GeometricGoo>();
        //    List<IGH_GeometricGoo> geo_Original = new List<IGH_GeometricGoo>();

        double spacing = 0;
        int placementType = 1;
        double tolerance = 0.1;
        int rotations = 4;
        int iterations = 0;
        double x = 0;
        int n = 0;
        int seed = 0;
        bool oneSheet = false;
        double cp = 0.01;

        public NestingContext Context;

        public OpenNest() {
            // Rhino only creates one instance of each command class defined in a
            // plug-in, so it is safe to store a refence in a static property.
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static OpenNest Instance {
            get; private set;
        }

        ///<returns>The command name as it appears on the Rhino command line.</returns>
        public override string EnglishName {
            get { return "OpenNest"; }
        }

        private void GetNestingSettings() {
            //Parameters
            Rhino.Input.Custom.GetOption gp = new Rhino.Input.Custom.GetOption();
            gp.SetCommandPrompt("OpenNest: Set nesting settings");

            Rhino.Input.Custom.OptionInteger Iterations = new Rhino.Input.Custom.OptionInteger(1, 1, 100);
            Rhino.Input.Custom.OptionDouble Spacing = new Rhino.Input.Custom.OptionDouble(0.01, 0.001, 10);
            Rhino.Input.Custom.OptionInteger Placement = new Rhino.Input.Custom.OptionInteger(1, 0, 4);
            Rhino.Input.Custom.OptionDouble Tolerance = new Rhino.Input.Custom.OptionDouble(0.1, 0.01, 10);
            Rhino.Input.Custom.OptionInteger Rotations = new Rhino.Input.Custom.OptionInteger(4, 0, 360);
            Rhino.Input.Custom.OptionInteger Seed = new Rhino.Input.Custom.OptionInteger(0, 1, 100);
            Rhino.Input.Custom.OptionDouble ClosestObjects = new Rhino.Input.Custom.OptionDouble(0.01, 0, 100);

            gp.AddOptionInteger("Iterations", ref Iterations);
            gp.AddOptionDouble("Spacing", ref Spacing);
            gp.AddOptionInteger("Placement", ref Placement);
            gp.AddOptionDouble("Tolerance", ref Tolerance);
            gp.AddOptionInteger("Rotations", ref Rotations);
            gp.AddOptionInteger("Seed", ref Seed);
            gp.AddOptionDouble("ClosestObjects", ref ClosestObjects);

            while (true) {
                Rhino.Input.GetResult get_rc = gp.Get();
                if (gp.CommandResult() != Rhino.Commands.Result.Success)
                    break;
            }

            this.iterations = Iterations.CurrentValue;
            this.spacing = Spacing.CurrentValue;
            this.placementType = Placement.CurrentValue;
            this.tolerance = Tolerance.CurrentValue;
            this.rotations = Rotations.CurrentValue;
            this.seed = Seed.CurrentValue;
            this.spacing *= (1 / tolerance);
            this.cp = ClosestObjects.CurrentValue;
        }

        private List<Polyline> GetSheets() {

            //Select Sheet
            var sheetsCurve = new List<Curve>();
            Rhino.DocObjects.ObjRef[] obj_refs;
            var rc = Rhino.Input.RhinoGet.GetMultipleObjects("OpenNest: Select polylines for sheets", false,
              Rhino.DocObjects.ObjectType.Curve, out obj_refs);
            if (rc != Result.Success || obj_refs == null)
                return null;

            foreach (var o in obj_refs) {
                var curve = o.Curve();
                if (curve == null)
                    return null;
                sheetsCurve.Add(curve);

            }

            if (sheetsCurve.Count == 0) {
                Rhino.RhinoApp.WriteLine("OpenNest: No sheets selectected: " + sheetsCurve.Count.ToString());
                return null;
            } else {
                Rhino.RhinoApp.WriteLine("OpenNest: Number of sheets: " + sheetsCurve.Count.ToString());
            }

            List<Polyline> sheets = sheetsCurve.ToPolylines(true);

            if (sheets.Count == 0) {
                Rhino.RhinoApp.WriteLine("OpenNest: No sheets selectected: " + sheetsCurve.Count.ToString());
                return null;
            }


            if (sheets.Count == 1) {

                Polyline sheetCopy = new Polyline(sheets[0]);
                sheets.Clear();

                Point3d p0 = sheetCopy.BoundingBox.PointAt(0, 0, 0);
                Point3d p1 = sheetCopy.BoundingBox.PointAt(1, 0, 0);
                Vector3d vec = p1 - p0;
                x = (p1.X - p0.X + this.spacing) / this.tolerance;
                for (int i = 0; i < 99; i++) {
                    sheets.Add(sheetCopy);
                }
            } else {
                this.x = 0;
            }
            
            return sheets;

        }

        private Tuple<Brep[], Dictionary<int, List<Guid>>> GetObjectsToNest(){
            //Select Objects To Nest

            const Rhino.DocObjects.ObjectType geometryFilter =
                Rhino.DocObjects.ObjectType.Annotation |
                Rhino.DocObjects.ObjectType.TextDot |
                Rhino.DocObjects.ObjectType.Point |
                Rhino.DocObjects.ObjectType.Curve |
                Rhino.DocObjects.ObjectType.Surface |
                Rhino.DocObjects.ObjectType.PolysrfFilter |
                Rhino.DocObjects.ObjectType.Mesh;

            Rhino.Input.Custom.GetObject go = new Rhino.Input.Custom.GetObject();
            go.SetCommandPrompt("OpenNest: Select objects for nesting");
            go.GeometryFilter = geometryFilter;
            go.GroupSelect = false;
            go.SubObjectSelect = false;
            go.EnableClearObjectsOnEntry(true);
            go.EnableUnselectObjectsOnExit(true);
            go.DeselectAllBeforePostSelect = false;


            bool bHavePreselectedObjects = false;

            for (; ; )
            {
                Rhino.Input.GetResult res = go.GetMultiple(1, 0);

                if (res == Rhino.Input.GetResult.Option) {
                    go.EnablePreSelect(false, true);
                    continue;
                } else if (res != Rhino.Input.GetResult.Object)
                    return null;//Rhino.Commands.Result.Cancel;

                if (go.ObjectsWerePreselected) {
                    bHavePreselectedObjects = true;
                    go.EnablePreSelect(false, true);
                    continue;
                }

                break;
            }

            if (bHavePreselectedObjects) {
                // Normally when command finishes, pre-selected objects will remain
                // selected, when and post-selected objects will be unselected.
                // With this sample, it is possible to have a combination of 
                // pre-selected and post-selected objects. To make sure everything
                // "looks the same", unselect everything before finishing the command.
                for (int i = 0; i < go.ObjectCount; i++) {
                    Rhino.DocObjects.RhinoObject rhinoObject = go.Object(i).Object();

                    if (null != rhinoObject)
                        rhinoObject.Select(false);
                }
                //doc.Views.Redraw();
            }

            List<Guid> guids = new List<Guid>();
            for (int i = 0; i < go.ObjectCount; i++) {
                guids.Add(go.Object(i).ObjectId);
            }

            Tuple<Brep[], Dictionary<int, List<Guid>>> data = OpenNestLib.OpenNestUtil.SortGuidsByPlanarCurves(guids, this.cp);

            Rhino.RhinoApp.WriteLine("OpenNest: Select object count = {0}", go.ObjectCount);
            this.n = data.Item1.Length;
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw();
            return data;

        }

        private void Nest(List<Polyline> sheets, Polyline[][] outlines, ref Tuple<Brep[], Dictionary<int, List<Guid>>> data) {

        

            this.scalesInv = new Transform[0];
            this.scales = new Transform[0];
            this.orient = new Transform[0];
            this.sheetsRhino = new Polyline[0];
            this.id = new int[0];
            this.transforms = new Transform[0];

            //Scale
            Transform scale = Transform.Scale(Point3d.Origin, 1 / tolerance);
            Transform scaleInv = Transform.Scale(Point3d.Origin, tolerance);

            for (int i = 0; i < sheets.Count; i++) {
                //sheets[i] = new Rectangle3d(sheets[i].Plane, sheets[i].Width * 1 / tolerance, sheets[i].Height * 1 / tolerance);
                Polyline polyline = new Polyline(sheets[i]);
                polyline.Transform(Transform.Scale(Point3d.Origin, 1 / tolerance));
                sheets[i] = polyline;
            }

            //Scale polylines and holes by tolerance
            scalesInv = new Transform[n];
            scales = new Transform[n];

            for (int i = 0; i < n; i++) {

                for (int j = 0; j < outlines[i].Length; j++) {
                    outlines[i][j].Transform(scale);
                }

                scalesInv[i] = scaleInv;
                scales[i] = scale;
            }




            //Translation
            orient = new Transform[n];


            for (int i = 0; i < n; i++) {

                int last = (outlines[i].Length - 1) % outlines[i].Length;
                Tuple<Polyline, Transform> projectedPolyline = OpenNestUtil.FitPolylineToPlane(new Polyline(outlines[i][last]));




                //Rhino.RhinoDoc.ActiveDoc.Objects.AddPolyline(projectedPolyline.Item1);

                for (int j = 0; j < outlines[i].Length; j++) {
                    outlines[i][j].Transform(projectedPolyline.Item2);
                }

                //foreach (var pp in outlines[i][0])
                //    Rhino.RhinoDoc.ActiveDoc.Objects.AddPoint(pp);

                orient[i] = projectedPolyline.Item2;
            }


            ////////////Run Solver////////////
            //OpenNestSolver sample = new OpenNestSolver();
            //sample.Context.LoadSampleData(sheets, outlines);
            //var output = sample.Run(spacing, iterations, placementType, rotations);

            //Context = new NestingContext(this.seed);
            Context = new NestingContext();
            Context.LoadSampleData(sheets, outlines);


            //Rhino.RhinoApp.WriteLine("outlines " + polyline[0].Count.ToString());



            Background.UseParallel = true;


            SvgNest.Config.placementType = (PlacementTypeEnum)(placementType % 3);
            //Rhino.RhinoApp.WriteLine(((PlacementTypeEnum)(placementType % 3)).ToString());
            SvgNest.Config.spacing = this.spacing;
            SvgNest.Config.rotations = this.rotations;
            SvgNest.Config.seed = this.seed;
            SvgNest.Config.clipperScale = 1e7;
            SvgNest.Config.simplify = (placementType == 4);
            SvgNest.Config.exploreConcave = (placementType == 5);
            SvgNest.Config.mergeLines = false;
            //SvgNest.Config.useHoles = false;

            Context.StartNest();

            if (iterations > 0) {
                for (int i = 0; i < iterations; i++) {
                    Context.NestIterate();
                }
            }

            if (Context.SheetsNotUsed != -1) {
                int sheetCount = Context.Sheets.Count - Context.SheetsNotUsed;
                this.sheetsRhino = new Polyline[sheetCount];
                for (int i = 0; i < sheetCount; i++) {
                    //Rhino.RhinoApp.WriteLine(Context.Sheets[i].id.ToString());
                    Polyline sheetPoly = Context.Sheets[i].ToPolyline();
                    sheetPoly.Transform(Transform.Translation(new Vector3d(this.x * i, 0, 0)));
                    this.sheetsRhino[i] = sheetPoly;
                }
            } else {
                this.sheetsRhino = Context.Sheets.ToPolylines();
            }




            this.id = Context.Polygons.ToIDArray();
            //Context.Polygons[i].sheet.Id;
            Transform[] polygonsTransforms = Context.Polygons.GetTransforms();



            this.transforms = new Transform[scalesInv.Length];

            List<int> polygonIDinSheet = new List<int>();

            for (int i = 0; i < scalesInv.Length; i++) {
                if (Context.Polygons[i].fitted) {
                    this.transforms[i] = scalesInv[i] * Transform.Translation(new Vector3d(this.x * Context.Polygons[i].sheet.id, 0, 0)) * polygonsTransforms[i] * orient[i] * scales[i];
                    polygonIDinSheet.Add(Context.Polygons[i].sheet.id);
                } else {
                    this.transforms[i] = Transform.Translation(new Vector3d(0, 0, 0));
                    //this.transforms[i] = scalesInv[i] * polygonsTransforms[i] * orient[i] * scales[i];
                    polygonIDinSheet.Add(-1);
                }
            }

            //Rhino.RhinoApp.WriteLine("OpenNest: " + n.ToString() + "  " + data.Item2.ToString());
            for (int i = 0; i < n; i++) {

                var goo = data.Item1[i].DuplicateBrep(); //if not duplicated grasshopper will change original ones
                goo.Transform(transforms[i]);
                data.Item1[i] = goo;

                //Rhino.RhinoDoc.ActiveDoc.Objects.AddBrep(goo);

                foreach (Guid guid in data.Item2[i]) {

                    Rhino.RhinoDoc.ActiveDoc.Objects.Transform(guid, transforms[i], false);//bake objects
                }
            }

            for (int i = 0; i < sheetsRhino.Length; i++)
                sheetsRhino[i].Transform(Transform.Scale(Point3d.Origin, tolerance));



            //Rhino.RhinoApp.WriteLine("Hi");


        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) {

            //1. Get Nesting Settings
            GetNestingSettings();

            //2. Get Sheets
            List<Polyline> sheets = GetSheets();
            if (sheets == null)
                return Result.Failure;
            if (sheets.Count == 0)
                return Result.Failure;

            //3.1 Get Objects to Nest
            Tuple<Brep[], Dictionary<int, List<Guid>>> data = GetObjectsToNest();

       
            //3.2 Get planar brep outlines for nesting
            Polyline[][] outlines = new Polyline[this.n][];
            for (int i = 0; i < data.Item1.Length; i++) {
                outlines[i] = OpenNestUtil.BrepLoops(data.Item1[i]);
            }

       
            //4. Nest
            System.Threading.Tasks.Task.Run(() => {
            Nest(sheets, outlines, ref data);
            });

            Rhino.RhinoApp.WriteLine("OpenNest: Nesting... Keep working while the nesting finishes.");


        



            return Rhino.Commands.Result.Success;

    
        }
    }
}
