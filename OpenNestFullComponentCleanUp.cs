using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using OpenNestLib;
using Grasshopper.Kernel.Types;


//To do
//Remove scaling matrices for optimization and instead apply tolerance to SvgConfing.Scalling and scalling 2 - not working
//Nest Meshes
//Nest RhinoObject Groups

namespace OpenNest {
    public class OpenNestFullComponentCleanUp : GH_Component {


        bool run = false;
        bool reset = false;
        bool staticSolver = false;

        Transform[] scalesInv = new Transform[0];
        Transform[] scales = new Transform[0];
        Transform[] orient = new Transform[0];



        Polyline[] sheetsRhino = new Polyline[0];
        int[] id = new int[0];
        Transform[] transforms = new Transform[0];
        List<IGH_GeometricGoo> geo = new List<IGH_GeometricGoo>();
        List<IGH_GeometricGoo> geo_Original = new List<IGH_GeometricGoo>();

        double spacing = 0;
        int placementType = 1;
        double tolerance = 0.1;
        int rotations = 4;
        int iterations = 0;
        double x = 0;
        int n = 0;
        int seed = 0;
        bool oneSheet = false;

        public NestingContext Context;


        protected override void AfterSolveInstance() {
            if (!this.run || reset) {
                return;
            }
            GH_Document gH_Document = base.OnPingDocument();
            if (gH_Document == null) {
                return;
            }
            GH_Document.GH_ScheduleDelegate gH_ScheduleDelegate = new GH_Document.GH_ScheduleDelegate(this.ScheduleCallback);
            gH_Document.ScheduleSolution(1, gH_ScheduleDelegate);
        }

        private void ScheduleCallback(GH_Document doc) {
            this.ExpireSolution(false);
        }

        public OpenNestFullComponentCleanUp()
          : base("OpenNest", "OpenNest",
              "OpenNest",
              "Params", "OpenNest") {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheets", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "Geo", "Geo", GH_ParamAccess.list);


            pManager.AddNumberParameter("Spacing", "Spacing", "Spacing", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Placement", "Placement", "Placement", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Tolerance", "Tolerance", "Tolerance", GH_ParamAccess.item, 0.1);
            pManager.AddIntegerParameter("Rotations", "Rotations", "Rotations", GH_ParamAccess.item, 4);
            pManager.AddIntegerParameter("Iterations", "Iterations", "Iterations", GH_ParamAccess.item, 1);
            pManager.AddIntegerParameter("Seed", "Seed", "Seed", GH_ParamAccess.item, 1);

            pManager.AddBooleanParameter("Reset", "Reset", "Reset", GH_ParamAccess.item, true);
            pManager.AddBooleanParameter("Run", "Run", "Run", GH_ParamAccess.item, false);

            for (int i = 0 + 2; i < pManager.ParamCount; i++) {
                pManager[i].Optional = true;
            }
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddCurveParameter("Sheets", "Sheets", "Sheets", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "Geo", "Geo", GH_ParamAccess.list);
            //pManager.AddCurveParameter("Holes", "Holes", "Holes", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("ID", "ID", "Polygon id number", GH_ParamAccess.list);
            pManager.AddTransformParameter("Transform", "Transform", "Transform", GH_ParamAccess.list);
            pManager.AddIntegerParameter("IDS", "IDS", "Sheet id number", GH_ParamAccess.list);
        }

        public Polyline[][] GooToOutlines(List<IGH_GeometricGoo> geo_) {

            geo = new List<IGH_GeometricGoo>();
            geo_Original = new List<IGH_GeometricGoo>();

            foreach (var goo in geo_) {
                if (goo != null) {
                    if (goo.IsValid) {
                        geo.Add(goo.DuplicateGeometry());
                        geo_Original.Add(goo.DuplicateGeometry());
                    }
                }
            }



            this.n = geo.Count;

            Polyline[][] outlines = new Polyline[this.n][];

            for (int i = 0; i < geo_.Count; i++) {



                switch (geo_[i].TypeName) {

                    case ("Brep"):
                    case ("Surface"):
                    bool flagBrep = geo_[i].CastTo<Brep>(out Brep b);
                    if (flagBrep)
                        if (b.IsValid) {
                            outlines[i] = OpenNestUtil.BrepLoops(b);
                        }
                    break;

                    case ("Mesh"):
                    bool flagMesh = geo_[i].CastTo<Mesh>(out Mesh m);
                    if (flagMesh)
                        if (m.IsValid) {
                            outlines[i] = OpenNestUtil.MeshLoops(m);
                        }
                    break;

                    case ("Curve"):
                    bool flagPolyline = geo_[i].CastTo<Curve>(out Curve c);

                    if (flagPolyline) {
                        if (c.IsValid) {
                            bool isPolyline = c.TryGetPolyline(out Polyline polyline);

                            if (isPolyline) {
                                if (polyline.IsValid && polyline.Count > 2 && OpenNestUtil.FastDistance(polyline[0], polyline[polyline.Count - 1]) < 0.01) {
                                    outlines[i] = new Polyline[] { polyline };
                                }

                            } else {
                                PolylineCurve polycurve = c.ToPolyline(20, 1, 0.00, 0.00, 0.00, 0.01, 0.00, 0.00, true);
                                bool notPolyline = polycurve.TryGetPolyline(out Polyline polycurvePolyline);
                                if (!notPolyline)
                                    Rhino.RhinoApp.WriteLine("wrongCurve");
                                outlines[i] = new Polyline[] { polycurvePolyline };
                            }

                        }
                    }
                    break;

                }
            }

            return outlines;
        }




        protected override void SolveInstance(IGH_DataAccess DA) {
            try {

                this.reset = DA.Fetch<bool>("Reset");
                this.run = DA.Fetch<bool>("Run");
                this.iterations = DA.Fetch<int>("Iterations");
                this.spacing = DA.Fetch<double>("Spacing");
                this.placementType = DA.Fetch<int>("Placement");
                this.tolerance = DA.Fetch<double>("Tolerance");
                this.rotations = DA.Fetch<int>("Rotations");
                this.seed = DA.Fetch<int>("Seed");
                this.spacing *= (1 / tolerance);

                //Rhino.RhinoApp.WriteLine("ITER" + iterations.ToString());

               if (((this.reset) || this.scales.Length == 0) || (this.iterations > 0)) {
                    //Input

                   //Rhino.RhinoApp.WriteLine("setup" + rotations.ToString());

                    List<Polyline> sheets = DA.FetchList<Curve>("Sheets").ToPolylines(true);

                    if (sheets.Count == 1) {

                        Polyline sheetCopy = new Polyline(sheets[0]);
                        sheets.Clear();

                        Point3d p0 = sheetCopy.BoundingBox.PointAt(0, 0, 0);
                        Point3d p1 = sheetCopy.BoundingBox.PointAt(1, 0, 0);
                        Vector3d vec = p1 - p0;
                        x = (p1.X - p0.X+this.spacing) / this.tolerance;
                        for (int i = 0; i < 499; i++) {

                            //Polyline sheetMoved = new Polyline(sheetCopy);
                            //sheetMoved.Transform(Transform.Translation(vec * i));
                            sheets.Add(sheetCopy);

                        }
                    } else {
                        this.x = 0;
                    }


                    List<IGH_GeometricGoo> geo_ = DA.FetchList<IGH_GeometricGoo>("Geo");

                    Polyline[][] outlines = GooToOutlines(geo_);





                    ////////////Solution////////////



                    base.Message = "Setup";

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


                    ONest.Config.placementType = (PlacementTypeEnum)(placementType%3);
                    //Rhino.RhinoApp.WriteLine(((PlacementTypeEnum)(placementType % 3)).ToString());
                    ONest.Config.spacing = this.spacing;
                    ONest.Config.rotations = this.rotations;
                    ONest.Config.seed = this.seed;
                    ONest.Config.clipperScale = 1e7;
                    ONest.Config.simplify = (placementType == 4);
                    ONest.Config.exploreConcave = (placementType == 5);
                    ONest.Config.mergeLines = false;
                    //ONest.Config.useHoles = false;

                    Context.StartNest();



                }




                if ((run || this.scales.Length == 0) && iterations == 0) {
                    Context.NestIterate();
                    base.Message = "Run " + Context.Iterations.ToString() + " \n Current Best: " + Context.Current.fitness;
                }

                if (iterations > 0) {
                    for (int i = 0; i < iterations; i++) {
                        Context.NestIterate();
                    }
                    base.Message = "Static Solver " + Context.Iterations.ToString() + " \n Current Best: " + Context.Current.fitness;
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
                        this.transforms[i] = scalesInv[i] * Transform.Translation(new Vector3d (this.x*Context.Polygons[i].sheet.id, 0, 0)) * polygonsTransforms[i] * orient[i] * scales[i];
                        polygonIDinSheet.Add(Context.Polygons[i].sheet.id);
                    } else {
                        this.transforms[i] = Transform.Translation(new Vector3d(0,0,0));
                        //this.transforms[i] = scalesInv[i] * polygonsTransforms[i] * orient[i] * scales[i];
                        polygonIDinSheet.Add(-1);
                    }
                }


            for (int i = 0; i < n; i++) {
                IGH_GeometricGoo goo = geo_Original[i].DuplicateGeometry(); //if not duplicated grasshopper will change original ones
                goo.Transform(transforms[i]);
                geo[i] = goo;
            }

            for (int i = 0; i < sheetsRhino.Length; i++)
                sheetsRhino[i].Transform(Transform.Scale(Point3d.Origin, tolerance));









                    ////////////Output////////////
                    DA.SetDataList(0, sheetsRhino); //Sheets
               DA.SetDataList(1, geo);//Surfaces or outlines
               DA.SetDataList(2, id);//ID
               DA.SetDataList(3, transforms);//transformations
                DA.SetDataList(4,polygonIDinSheet);
             
                } catch (Exception e) {
                base.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, e.ToString());
            }

        }

    


        protected override System.Drawing.Bitmap Icon {
            get {

                return Properties.Resources.NestIcon;
            }
        }


        public override Guid ComponentGuid {
            get { return new Guid("ff729e8d-6e1b-4b19-9dd6-d1c136996c89"); }
        }
    }
}
