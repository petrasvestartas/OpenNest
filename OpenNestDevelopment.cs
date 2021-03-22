
using Rhino;
using Rhino.Commands;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using OpenNestLib;

namespace OpenNestRhino {
    public class OpenNestDevelopment : Command {

        OpenNestRhinoContext openNestRhinoContext;

        //bool run = false;
        //bool reset = false;
        //bool staticSolver = false;

        //Transform[] scalesInv = new Transform[0];
        //Transform[] scales = new Transform[0];
        //Transform[] orient = new Transform[0];


        //Polyline[] sheetsRhino = new Polyline[0];
        //int[] id = new int[0];
        //Transform[] transforms = new Transform[0];

        //double spacing = 0;
        //int placementType = 1;
        //double tolerance = 0.1;
        //int rotations = 4;
        //int iterations = 0;
        //double x = 0;
        //int n = 0;
        //int seed = 0;
        //bool oneSheet = false;
        //double cp = 0.01;

        //public NestingContext Context;

        public OpenNestDevelopment() {
            Instance = this;
        }

        ///<summary>The only instance of this command.</summary>
        public static OpenNestDevelopment Instance {
            get; private set;
        }

        ///<returns>The command name as it appears on the Rhino command line.</returns>
        public override string EnglishName {
            get { return "OpenNestDevelopment"; }
        }

        private void GetNestingSettings() {
            //Parameters
            Rhino.Input.Custom.GetOption gp = new Rhino.Input.Custom.GetOption();
            gp.SetCommandPrompt("OpenNest: Set nesting settings");

            Rhino.Input.Custom.OptionInteger Iterations = new Rhino.Input.Custom.OptionInteger(1, 1, 100);
            Rhino.Input.Custom.OptionDouble Spacing = new Rhino.Input.Custom.OptionDouble(0.01, 0.001, 1E10);
            Rhino.Input.Custom.OptionInteger Placement = new Rhino.Input.Custom.OptionInteger(1, 0, 4);
            Rhino.Input.Custom.OptionDouble Tolerance = new Rhino.Input.Custom.OptionDouble(0.1, 0.01, 10);
            Rhino.Input.Custom.OptionInteger Rotations = new Rhino.Input.Custom.OptionInteger(4, 0, 360);
            Rhino.Input.Custom.OptionInteger Seed = new Rhino.Input.Custom.OptionInteger(0, 1, 100);
            Rhino.Input.Custom.OptionDouble ClosestObjects = new Rhino.Input.Custom.OptionDouble(0.05, 0, 100);

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

            openNestRhinoContext.iterations = Iterations.CurrentValue;
            openNestRhinoContext.spacing = Spacing.CurrentValue;
            openNestRhinoContext.placementType = Placement.CurrentValue;
            openNestRhinoContext.tolerance = Tolerance.CurrentValue;
            openNestRhinoContext.rotations = Rotations.CurrentValue;
            openNestRhinoContext.seed = Seed.CurrentValue;
            openNestRhinoContext.spacing *= (1 / openNestRhinoContext.tolerance);
            openNestRhinoContext.cp = ClosestObjects.CurrentValue;
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
                openNestRhinoContext.x = (p1.X - p0.X + openNestRhinoContext.spacing) / openNestRhinoContext.tolerance;
                for (int i = 0; i < 99; i++) {
                    sheets.Add(sheetCopy);
                }
            } else {
                openNestRhinoContext.x = 0;
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

            Tuple<Brep[], Dictionary<int, List<Guid>>> data = OpenNestLib.OpenNestUtil.SortGuidsByPlanarCurves(guids, openNestRhinoContext.cp);

            Rhino.RhinoApp.WriteLine("OpenNest: Select object count = {0}", go.ObjectCount);
            openNestRhinoContext.n = data.Item1.Length;
            Rhino.RhinoDoc.ActiveDoc.Views.Redraw();
            return data;

        }

        protected override Result RunCommand(RhinoDoc doc, RunMode mode) {

            openNestRhinoContext = new OpenNestRhinoContext();

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
            Polyline[][] outlines = new Polyline[openNestRhinoContext.n][];
            for (int i = 0; i < data.Item1.Length; i++) {
                outlines[i] = OpenNestUtil.BrepLoops(data.Item1[i]);
            }

       
            //4. Nest
            System.Threading.Tasks.Task.Run(() => {
                data=OpenNestLib.Helpers.Nest(sheets, outlines, data, openNestRhinoContext);
            });

            Rhino.RhinoApp.WriteLine("OpenNest: Nesting... Keep working while the nesting finishes.");
            return Rhino.Commands.Result.Success;

    
        }
    }
}
