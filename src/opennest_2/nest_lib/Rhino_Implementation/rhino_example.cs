using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using Rhino.Geometry;
using Rhino;
using Eto.Forms;
using Sharp3DBinPacking.Internal;
using Rhino.Commands;
using System.Drawing;
using Rhino.UI;


namespace nest_lib
{
    public class NumberInputForm : Dialog<DialogResult>
    {
        private TextBox _inputTextBox;
        public double UserInput { get; private set; }

        

        public NumberInputForm()
        {
            Title = "How long the solver will run in seconds?  Save file first.";
            ClientSize = new Eto.Drawing.Size(400, 85);
            Resizable = false;

            var layout = new DynamicLayout { Padding = 10, Spacing = new Eto.Drawing.Size(20, 5) };

            _inputTextBox = new TextBox();
            layout.AddRow(new Label { Text = "Enter a duration (seconds):" }, _inputTextBox);

            var okButton = new Button { Text = "OK" };
            okButton.Click += OkButton_Click;

            var cancelButton = new Button { Text = "Cancel"};
            cancelButton.Click += (sender, e) => Close(DialogResult.Cancel);

            var buttonLayout = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                AlignLabels = true,
                Items = { okButton, cancelButton }
            };

            layout.AddRow(buttonLayout);

            Content = layout;
        }

        // Method to set a predefined number in the input field
        public void PredefineNumber(double number)
        {
            _inputTextBox.Text = number.ToString();
        }

        private void OkButton_Click(object sender, EventArgs e)
        {
            if (double.TryParse(_inputTextBox.Text, out double result))
            {
                UserInput = result;
                Close(DialogResult.Ok);
            }
            else
            {
                MessageBox.Show(this, "Please enter a valid number.", "Invalid Input", MessageBoxButtons.OK, MessageBoxType.Error);
            }
        }
    }

    public partial class rhino_example
    {
        private NestingContext context = new NestingContext();

        // Solver backend: "cpp" => nfp_nest.dll (canonical C++ engine, default), "cs" => managed SvgNest
        // GA (fallback/reference). Set by the component from the "engine" option before static_solver runs.
        public string Engine = "cpp";

        // C++ engine: 1 => evaluate ALL rotations per placement (tightest packing, ~4-5x slower first
        // run); 0 => GA explores rotations over generations (fast). Set from the "all_rotations" option.
        public int TryAllRotations = 0;

        // C++ engine: 1 => exact NFP on full-resolution parts (no simplify/dilate -> parts touch, no gap,
        // no overlap; slower) — DEFAULT; 0 => simplify+dilate (fast, tiny conservative gap). "exact_nfp".
        public int ExactNfp = 1;

        // 1 => allow smaller parts to nest INSIDE larger parts' holes (NfpParams.useHoles -> NestConfig.fillHoles).
        public int UseHoles = 1;




        //Output
        public List<List<Polyline>> output_sheets;
        public List<List<Transform>> output_transforms;
        public List<List<int>> output_polygon_sheet_ids;
        public List<Polyline> simplified_polygons = new List<Polyline>();
        private int max_iterations = 3;
        private long max_time_in_seconds = 0;
        private Transform[] to_xy = null; //transformation to XY plane for each sheet
        public int nest_counter = 0;

        // ---- live preview + cooperative cancel (read by the GH component while static_solver runs on a
        // background thread). The iteration loop publishes a fresh LiveBorders snapshot after each
        // generation; the UI thread reads the swapped reference for the orange "tightening" preview.
        // Polyline/Transform are pure geometry (no RhinoDoc access) so they are safe to build off the UI thread.
        public volatile bool StopRequested = false;
        public volatile int CurrentGeneration = 0;
        public int TotalGenerations => max_iterations;
        public double CurrentFitness = 0.0;   // display only (double can't be volatile in C#; a torn read just mislabels one frame)
        private volatile List<Polyline> _liveBorders = new List<Polyline>();
        public List<Polyline> LiveBorders => _liveBorders;          // swapped ref; never mutated in place
        public List<Polyline> LiveSheets = new List<Polyline>();    // layout-frame sheet outlines (static during a run)


        public rhino_example(
            ref nest_rhino_lib.nest_sheets nest_sheets, ref nest_rhino_lib.nest_geo geometry,
            List<double> parameters,
            int max_iterations = 3
            )
        {
            

            ///////////////////////////////////////////////////////////////////////////////////
            //global parameters
            ///////////////////////////////////////////////////////////////////////////////////
            SvgNest.Config.placementType = PlacementTypeEnum.gravity;  // gravity (bottom-left) packs tighter; the within-sheet cost (width*2+height) lives in this branch
            SvgNest.Config.curveTolerance = 0.72;
            SvgNest.Config.scale = 25;
            SvgNest.Config.clipperScale = 10000000;
            SvgNest.Config.exploreConcave = false;
            SvgNest.Config.mutationRate = 10;     // canonical SVGnest/DeepNest: applied as 0.01*rate => ~10% per gene (was 100 = always mutate = random search)
            SvgNest.Config.populationSize = 10;   // canonical GA pool; one NestGeneration() evaluates this many candidates = one generation
            SvgNest.Config.rotations = 4;
            SvgNest.Config.spacing = 10;
            SvgNest.Config.sheetSpacing = 0;
            SvgNest.Config.useHoles = false;
            SvgNest.Config.timeRatio = 0.5;
            SvgNest.Config.mergeLines = false;
            SvgNest.Config.simplify = false;
            SvgNest.Config.clipByHull = false;
            SvgNest.Config.clipByRects = true; //clip by AABB + MinRect
            SvgNest.Config.seed = -1;

            for (int i = 0; i < geometry.boundary_curves_non_sorted.Count; i++)
                if (geometry.boundary_sorted.Count > 1)
                {
                    SvgNest.Config.useHoles = true;
                    break;
                }


            for (int i = 0; i < parameters.Count - 1; i++)
            {
                //RhinoApp.WriteLine(parameters[i].ToString()); 
                switch (i)
                {
                    case (0):
                        SvgNest.Config.rotations = Math.Max(1, (int)parameters[i]);
                        break;
                    case (1):
                        SvgNest.Config.rotation_limit = Math.Min(360.0f, (float)Math.Abs(parameters[i]));//create convex hulls
                        break;
                    case (2):
                        SvgNest.Config.placementType = (PlacementTypeEnum)((int)Math.Min(Math.Max((int)parameters[i], 0), 2));
                        break;
                    case (3):
                        SvgNest.Config.spacing = parameters[i];
                        break;
                    case (4):
                        SvgNest.Config.seed = (int)parameters[i];
                        break;
                    case (5):
                        SvgNest.Config.simplify = parameters[i] < 0.0001;//create convex hulls
                        SvgNest.Config.curveTolerance = parameters[i];
                        break;
                    case (6):
                        SvgNest.Config.mutationRate = (int)parameters[i];
                        //Rhino.RhinoApp.WriteLine(SvgNest.Config.mutationRate.ToString());
                        break;
                    case (7):
                        SvgNest.Config.populationSize = (int)parameters[i];
                        //Rhino.RhinoApp.WriteLine(SvgNest.Config.populationSize.ToString());
                        break;


                }
            }

            RhinoApp.WriteLine(SvgNest.Config.placementType.ToString());

            this.max_iterations = max_iterations;
            this.max_time_in_seconds = (long)parameters[8];


            SvgNest.Config.clipperScale = 1e5;
            SvgNest.Config.exploreConcave = true;

            SvgNest.Config.clipByHull = false;
            SvgNest.Config.clipByRects = true; //clip by AABB + MinRect


            Background.UseParallel = true;

            ///////////////////////////////////////////////////////////////////////////////////
            //load sheets and geometry
            //SVGnest use geometry with basis point on 0,0,0
            //to_xy is used in load_sample_data that orients input boundaries with holes
            //to_xy also transformation is used in get_results function
            ///////////////////////////////////////////////////////////////////////////////////
            to_xy = new Transform[geometry.boundary_sorted.Count];

            for (int i = 0; i < geometry.boundary_sorted.Count; i++)
            {
                Plane plane = new Plane(geometry.boundary_sorted[i][0].Item2[0], Vector3d.XAxis, Vector3d.YAxis);//nest_lib.rhino_conversions.AverageNormal(geometry.boundary_sorted[i][0].Item2)
                to_xy[i] = Transform.PlaneToPlane(plane, Plane.WorldXY);
            }

            context.partsLocal = new List<NestItem>();
            context.load_sample_data(ref nest_sheets, ref geometry, ref to_xy);

        }

        public void static_solver(ref nest_rhino_lib.nest_geo geometry)
        {
            ///////////////////////////////////////////////////////////////////////////////////
            //compute the nesting
            ///////////////////////////////////////////////////////////////////////////////////
            run(this.max_iterations, this.max_time_in_seconds);

            ///////////////////////////////////////////////////////////////////////////////////
            //get output (transform and sheets)
            ///////////////////////////////////////////////////////////////////////////////////
            get_results(ref geometry, ref this.to_xy, this.max_iterations);
        }



        private static volatile bool stopLoop = false;
        private static Eto.Forms.UITimer keyMonitorTimer;

        private void run(int max_iterations, long max_time_in_seconds=0)
        {
            // C++ DLL backend (default): solve via nfp_nest.dll and write placement back into
            // context.Polygons so the existing get_results / output assembly is reused unchanged.
            if (string.Equals(Engine, "cpp", StringComparison.OrdinalIgnoreCase))
            {
                run_cpp();
                return;
            }

            // DIAGNOSTIC (managed C# engine): how many part-holes actually reached the solver as
            // NFP.children. The full DeepNest hole-fill chain (thenIterate inner-fit -> placeParts
            // void) is present, so if parts fail to nest into element holes the cause is almost
            // always upstream: the surface's inner loop wasn't captured or identify_groups didn't
            // pair it, so parts arrive with 0 children and there is nothing to fill.
            //   part_holes>0  => holes reached the engine; the C# void-fill should place parts inside.
            //   part_holes==0 => holes lost in geometry intake/pairing (fix the Geometry component,
            //                    NOT the solver).
            try
            {
                int _ph = 0;
                foreach (var p in context.Polygons) _ph += (p.children != null ? p.children.Count : 0);
                Rhino.RhinoApp.WriteLine($"[nest C#] engine=managed parts={context.Polygons.Count} part_holes={_ph} UseHoles={UseHoles}");
            }
            catch { }

            //RhinoApp.WriteLine("NestCounter: " + nest_counter.ToString());
            //Rhino.RhinoApp.WriteLine("Settings updated.. \nStart nesting.. \n" + "Parts: " + context.Polygons.Count() + "\nSheets: " + context.Sheets.Count());

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (nest_counter == 0)
            {
                context.StartNest();
                Rhino.RhinoApp.WriteLine("Nesting context created");
            }

            nest_counter++;




            long double_check_max_time_in_seconds = max_time_in_seconds;
            if (max_time_in_seconds > 0)
            {
                var dialog = new NumberInputForm();
                dialog.PredefineNumber(max_time_in_seconds);
                var result = dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);

                
                if (result == DialogResult.Ok)
                {
                    //RhinoApp.WriteLine($"Are you sure you will run the solver for {max_time_in_seconds}: {dialog.UserInput}");
                    double_check_max_time_in_seconds = (long)dialog.UserInput;
                }
                else { return; }


                long ElapsedMilliseconds = 0;


                // Reset the elapsed milliseconds and stop flag for each run
                stopLoop = false;

                do
                {
                    var seconds_counter = Stopwatch.StartNew();
                    // Replace this with your actual context and iteration call
                    context.NestIterate(1);
                    seconds_counter.Stop();
                    ElapsedMilliseconds += seconds_counter.ElapsedMilliseconds;
                    RhinoApp.WriteLine("Time: " + ElapsedMilliseconds + "ms" + " Elapsed: " + (ElapsedMilliseconds/1000).ToString() + "s Max Time: " + double_check_max_time_in_seconds.ToString() + "s Iteration: " + context.Iterations + "; fitness: " + context.Current.fitness + "; nesting time: " + sw.ElapsedMilliseconds + "ms");

                } while (!(ElapsedMilliseconds >= double_check_max_time_in_seconds * 1000) && !stopLoop);

                // Output the result
                RhinoApp.WriteLine("Loop completed or stopped by user");
            }
            else
            {

                //RhinoApp.WriteLine("Max Iterations: " + max_iterations.ToString());



                if (max_iterations == 0)
                {
                    sw = System.Diagnostics.Stopwatch.StartNew();
                  
                    context.NestIterate(max_iterations);
                    sw.Stop();
                    Rhino.RhinoApp.WriteLine("Iteration: " + context.Iterations + "; preparation for nesting time: " + sw.ElapsedMilliseconds + "ms");
                }
                else
                {
                    // Iterations == GENERATIONS, but drive ONE CANDIDATE at a time (NestIterate) and publish
                    // a live snapshot after each. This shows the FIRST result as soon as the first candidate
                    // is placed (~1/population of a generation) instead of waiting for the whole first
                    // generation — important because generation 1 also builds the cold NFP cache (the bulk
                    // of the startup cost). The GA still evolves internally once each population is fully
                    // scored; the best-so-far only improves (single elitism), so more generations = better.
                    int pop = (SvgNest.Config != null && SvgNest.Config.populationSize > 0) ? SvgNest.Config.populationSize : 10;
                    long totalSteps = (long)max_iterations * pop;
                    for (long step = 1; step <= totalSteps && !StopRequested; step++)
                    {
                        sw = System.Diagnostics.Stopwatch.StartNew();
                        context.NestIterate(1);   // score one candidate (inits once; evolves when the pool fills)
                        sw.Stop();

                        CurrentGeneration = (int)((step - 1) / pop) + 1;   // 1-based generation currently being built
                        CurrentFitness = (context.Current != null && context.Current.fitness.HasValue) ? context.Current.fitness.Value : 0.0;
                        if (LiveSheets.Count == 0)
                        {
                            try { var ls = new List<Polyline>(); foreach (var s in context.Sheets) ls.AddRange(s.ToPolylines()); LiveSheets = ls; } catch { }
                        }
                        _liveBorders = BuildLayoutBorders();   // swap a fresh snapshot for the UI preview (fast first preview)

                        if (step % pop == 0)
                            Rhino.RhinoApp.WriteLine("Generation: " + (step / pop) + "/" + max_iterations + "; fitness: " + (context.Current != null ? context.Current.fitness : null) + "; ~ms/cand: " + sw.ElapsedMilliseconds);
                    }
                }

            }


        }

        // Placed part outlines in the LAYOUT frame (same frame as context.Sheets / LiveSheets), built from
        // the current best placement. Pure geometry (Polyline/Transform) — safe off the UI thread. Called
        // ONLY from the run loop (background thread, between generations), so context is not read concurrently.
        private List<Polyline> BuildLayoutBorders()
        {
            var borders = new List<Polyline>();
            try
            {
                for (int i = 0; i < context.Polygons.Count; i++)
                {
                    if (!context.Polygons[i].fitted) continue;
                    // Draw the ORIGINAL (un-offset) outline that was actually nested, placed at its result
                    // transform. context.partsLocal is dilated by spacing/2 (the solver's kerf) and would
                    // render touching parts as fake overlaps — the NFP nesting itself never overlaps.
                    // NFP.ToPolyline() builds from Points and applies the part's x/y/rotation placement.
                    if (context.Polygons[i].Points == null || context.Polygons[i].Points.Length < 3) continue;
                    borders.Add(context.Polygons[i].ToPolyline());
                }
            }
            catch { }
            return borders;
        }

        // Build a CLOSED Rhino polyline from raw NFP points (nesting frame, NO placement applied), so the
        // outline preview shows the exact un-offset geometry that was nested rather than the dilated
        // partsLocal display copy.
        private static Polyline RawClosedPolyline(SvgPoint[] pts)
        {
            var pl = new Polyline();
            if (pts == null) return pl;
            foreach (var p in pts) pl.Add(p.x, p.y, 0);
            if (pl.Count > 0) pl.Add(pl[0]);
            return pl;
        }

        private void get_results(ref nest_rhino_lib.nest_geo nest_geo, ref Transform[] to_xy, int max_iterations)
        {
            ///////////////////////////////////////////////////////////////////////////////////
            //sheets
            ///////////////////////////////////////////////////////////////////////////////////
            this.output_sheets = new List<List<Polyline>>(); //Output Sheets

            if (context.SheetsNotUsed != -1)
            {
                for (int i = 0; i < context.Sheets.Count - context.SheetsNotUsed; i++)
                {
                    List<Polyline> sheet = context.Sheets[i].ToPolylines();
                    this.output_sheets.Add(sheet);
                }
            }

            ///////////////////////////////////////////////////////////////////////////////////
            //geometry
            ///////////////////////////////////////////////////////////////////////////////////

            //temp
            int[] source_ids = context.Polygons.ToSourceArray();
            Transform[] polygons_transforms = context.Polygons.GetTransforms();

            //outputs
            this.output_transforms = new List<List<Transform>>(nest_geo.geometry_sorted.Count); //Output transforms
            this.output_polygon_sheet_ids = new List<List<int>>(nest_geo.geometry_sorted.Count); //Output transforms
            for (int i = 0; i < nest_geo.geometry_sorted.Count; i++)
            {
                output_transforms.Add(new List<Transform>(nest_geo.copies[nest_geo.geometry_sorted[i][0]]));
                output_polygon_sheet_ids.Add(new List<int>(nest_geo.copies[nest_geo.geometry_sorted[i][0]]));
            }


            for (int i = 0; i < context.Polygons.Count; i++)
            {

                //orient original curves, if zero iteration keep them in the original place
                if (max_iterations == 0)
                    output_transforms[(int)context.Polygons[i].source].Add(polygons_transforms[i]);//
                else if (!context.Polygons[i].fitted)
                    output_transforms[(int)context.Polygons[i].source].Add(Transform.Identity);   // unplaced -> keep at original input location (not parked off-sheet)
                else
                    output_transforms[(int)context.Polygons[i].source].Add(polygons_transforms[i] * to_xy[(int)context.Polygons[i].source]);//


                if (context.Polygons[i].fitted)
                    output_polygon_sheet_ids[(int)context.Polygons[i].source].Add(context.Polygons[i].sheet.id);
                else
                    output_polygon_sheet_ids[(int)context.Polygons[i].source].Add(-1);


                // Outline preview must show the ORIGINAL (un-offset) geometry that was actually nested — NOT
                // context.partsLocal, which is dilated by spacing/2 (the solver's kerf) and renders touching
                // parts as fake overlaps. context.Polygons[i].Points/children are the un-offset outline in the
                // nesting frame; apply the same placement the old code used (to_xy_inv for the no-solve input
                // preview, else the per-part placement transform).
                Transform place_xf;
                if (max_iterations == 0 || !context.Polygons[i].fitted)
                    to_xy[(int)context.Polygons[i].source].TryGetInverse(out place_xf);   // original location: no-solve OR unplaced
                else
                    place_xf = polygons_transforms[i];

                Polyline simplified_polyline = RawClosedPolyline(context.Polygons[i].Points);
                simplified_polyline.Transform(place_xf);
                this.simplified_polygons.Add(simplified_polyline);

                if (context.Polygons[i].children != null)
                    foreach (var hole in context.Polygons[i].children)
                    {
                        Polyline hole_polyline = RawClosedPolyline(hole.Points);
                        hole_polyline.Transform(place_xf);
                        this.simplified_polygons.Add(hole_polyline);
                    }

            }

            /////////////////////////////////////////////////////////////////////////////////////
            ////transform input
            /////////////////////////////////////////////////////////////////////////////////////
            nest_geo.xforms = output_transforms;


        }
    }
}
