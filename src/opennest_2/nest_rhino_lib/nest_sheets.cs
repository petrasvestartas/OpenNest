using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Types.Transforms;
using Rhino.Collections;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace nest_rhino_lib
{


    /// <summary>
    /// Holds the sheets (bins) to nest into. Like nest_geo, any spacing/margin is applied HERE, upstream of
    /// the solver — the solver does no offsetting. To keep parts set back from the sheet edge, call
    /// <see cref="offset_sheet_boundary"/> before solving:
    ///
    ///     sheets.offset_sheet_boundary(spacing / 2);   // shrink outer in, grow holes out
    ///
    /// The sheet OUTER boundary shrinks inward by `margin` (parts keep a setback from the edge) and each sheet
    /// HOLE grows by `margin` (keep-out regions gain clearance). Pairing this with
    /// nest_geo.offset_nesting_boundary(spacing/2) yields a full `spacing` gap between every part and the edge.
    /// On failure the original sheet polyline is kept.
    /// </summary>
    public class nest_sheets
    {
        public Polyline[][] sheets = new Polyline[100][];

        public double width = 2000;

        public double height = 100;

        public double gap_x = 300;

        public double gap_y = 300;

        public int array_x = 2;

        public int array_y = 100;

        public string template = "";

        public bool[] overwrite = new bool[6];

        public double[] overwrite_values = new double[] { 2000, 100, 300, 300, 2, 10 };

        // Deep copy. A solver transforms / offsets its sheets IN PLACE (see offset_sheet_boundary and the
        // placement transforms), so two nesting components fed by the SAME Sheets component would corrupt
        // each other's input — one's solve leaves the other reading already-transformed sheets ("OpenNest2
        // doesn't start after OpenNestCollision finished"). Each consumer duplicates first, like nest_geo.
        public nest_sheets duplicate()
        {
            nest_sheets c = (nest_sheets)this.MemberwiseClone();
            if (this.sheets != null)
            {
                c.sheets = new Polyline[this.sheets.Length][];
                for (int i = 0; i < this.sheets.Length; i++)
                {
                    if (this.sheets[i] == null) { c.sheets[i] = null; continue; }
                    c.sheets[i] = new Polyline[this.sheets[i].Length];
                    for (int j = 0; j < this.sheets[i].Length; j++)
                        c.sheets[i][j] = this.sheets[i][j] == null ? null : new Polyline(this.sheets[i][j]);
                }
            }
            c.overwrite = this.overwrite != null ? (bool[])this.overwrite.Clone() : null;
            c.overwrite_values = this.overwrite_values != null ? (double[])this.overwrite_values.Clone() : null;
            return c;
        }

        public nest_sheets(List<List<Polyline>> unsorted_polylines, List<double> gap_xy, List<int> row_count, int copies)
        {

            //Check inputs
            var gap_xy_checked = new List<double>();
            for (int i = 0; i < 2; i++)
                gap_xy_checked.Add(gap_xy[i % gap_xy.Count]);
            if (gap_xy_checked.Count == 0)
                gap_xy_checked = new List<double> { 1, 1 };

            if (row_count.Count == 0)
                row_count = new List<int> { sheets.Length };

            int copies_checked = 100;
            if (copies_checked > 0)
                copies_checked = Math.Max(copies, unsorted_polylines.Count);
            this.sheets = new Polyline[copies_checked][];

            bool transformed = copies > unsorted_polylines.Count;



            double totalWidth = 0;
            List<double> widths = new List<double>();
            List<double> heights = new List<double>();
            Polyline[][] unsorted_polylines_checked = new Polyline[unsorted_polylines.Count][];

            Point3d base_point = Point3d.Unset;
            
            for (int i = 0; i < unsorted_polylines.Count; i++)
            {
                List<Polyline> plines = new List<Polyline>(unsorted_polylines[i].Count);
                List<double> sizes = new List<double>(unsorted_polylines[i].Count);
                for (int j = 0; j < unsorted_polylines[i].Count; j++)
                {
                    plines.Add(unsorted_polylines[i][j]);
                    sizes.Add(unsorted_polylines[i][j].BoundingBox.Diagonal.Length);
                }

                //Sort the polylines by size
                var plines_array = plines.ToArray();
                var sizes_array = sizes.ToArray();
                Array.Sort(sizes_array, plines_array);
                Array.Reverse(plines_array);
                Array.Reverse(sizes_array);

                // Translate all the polylines using the first polyline bounding-box lower left corner as the reference
                Point3d refPoint = plines_array[0].BoundingBox.Corner(false, true, true);
                if (base_point == Point3d.Unset)
                    base_point = plines_array[0].BoundingBox.Corner(true, true, true); 
                Vector3d translation = Point3d.Origin - refPoint;

                for (int j = 0; j < plines_array.Length; j++)
                {
                    Transform transform = Transform.Translation(translation);
                    if (!transformed)
                        transform = Transform.Identity;
                    plines_array[j].Transform(transform);
                }
         

                // Calculate total width of bounding boxes for this set of polylines
                double width = 0;
                foreach (var pline in plines_array)
                {

                    width += pline.BoundingBox.Max.X - pline.BoundingBox.Min.X + gap_xy_checked[0];
   
                        break;
                }
                widths.Add(width);
                double height = plines_array[0].BoundingBox.Max.Y - plines_array[0].BoundingBox.Min.Y + gap_xy_checked[1];

                heights.Add(height);

                // Add this set to the sheets array
                sheets[i] = plines_array;
                unsorted_polylines_checked[i] = plines_array;
                totalWidth = Math.Max(totalWidth, width);
            }


            Vector3d base_vector =  base_point - Point3d.Origin;

             // Initialize a boolean array to track when to move to the next row
             bool[] move_to_next_row = new bool[sheets.Length];
            int last_bool = 0;

            int current_row = 0;
            int row_index = 0;
            for (int i = 0; i < sheets.Length; i++)
            {
                // Adjust the condition to check if it's time to move to the next row
                if (i == (current_row))
                {
                    move_to_next_row[i] = true;
                    last_bool = i;

                    // Ensure the index is correct by adding the appropriate row count
                    current_row += row_count[row_index % row_count.Count];
                    row_index++;

                }
            }




            //foreach (var item in move_to_next_row)
            //{
            //    Rhino.RhinoApp.WriteLine(item.ToString());
            //}

            // Calculate the total height of the rows

            double current_row_height = 0;
            double total_row_height = 0;
            int start = 0;
            double[] row_heights = new double[sheets.Length];

            for (int i = 0; i < move_to_next_row.Length; i++)
            {


                // Get the maximum row height including the gap between rows
                current_row_height = Math.Max(current_row_height, heights[i % heights.Count]);

                if (move_to_next_row[(i+1)%sheets.Length])
                {

                    //Rhino.RhinoApp.WriteLine(start.ToString() + " " + i.ToString());


                    // Assign the total height to all sheets in the current row
                    int end = (i + 1) % sheets.Length;
                    if (start == last_bool)
                    {
                        end = sheets.Length;
                        //Rhino.RhinoApp.WriteLine("Last");
                    }
                    for (int j = start; j < end; j++)
                    {

                            row_heights[j] = total_row_height;
                    }

                    // Add the current row height to the total height
                    total_row_height += current_row_height;

                    start = i+1;  // Move start to the next row's start position
                }

            }

            // Assign for the last row
            //Rhino.RhinoApp.WriteLine(start.ToString() + " " + move_to_next_row.Length.ToString());
            //for (int j = start; j < move_to_next_row.Length; j++)
            //    row_heights[j] = total_row_height;
    

            //foreach (var item in row_heights)
            //{
            //    Rhino.RhinoApp.WriteLine("Y" + item.ToString());
            //}

            // Calculate the total width of the sheets
            double temp_totalWidth = 0;
            double[] row_widths = new double[sheets.Length];

            for (int i = 0; i < sheets.Length; i++)
            {
                // Reset the total width when moving to the next row
                if (move_to_next_row[i])
                {
                    temp_totalWidth = 0;
                }

                // Add the width of the current sheet and the horizontal gap
          
                temp_totalWidth += widths[i % widths.Count] ;

                // Store the width of the current row
                row_widths[i] = temp_totalWidth - gap_xy_checked[0];
            }

            //foreach (var item in row_widths)
            //{
            //    Rhino.RhinoApp.WriteLine("X" + item.ToString());
            //}

            // Duplicate sheets until we have 100 sheets and move to the correct location

            for (int i = 0; i < sheets.Length; i++)
            {
                // Ensure the index doesn't go out of bounds when duplicating polylines
                int unsortedIndex = i % unsorted_polylines_checked.Length;
                Polyline[] new_sheet = new Polyline[unsorted_polylines_checked[unsortedIndex].Length];

                // Copy and transform the sheet
                for (int j = 0; j < unsorted_polylines_checked[unsortedIndex].Length; j++)
                {
                    // Create a new polyline as a copy of the original one
                    new_sheet[j] = new Polyline(unsorted_polylines_checked[unsortedIndex][j]);

                    // Apply translation transformation based on the calculated row and width
                    Vector3d translation = new Vector3d(row_widths[i], -row_heights[i], 0);

                    Transform transform = Transform.Translation(translation);

                    if (!transformed)
                        transform = Transform.Identity;

                    // Apply the transformation to the polyline
                    new_sheet[j].Transform(transform);
                }

                // Store the newly transformed sheet back in the sheets array
                sheets[i] = new_sheet;
            }

            // Translate all sheets by the base vector
            for (int i = 0; i < sheets.Length; i++)
            {
                for (int j = 0; j < sheets[i].Length; j++)
                {
                    Transform transform = Transform.Translation(base_vector);

                    if (!transformed)
                        transform = Transform.Identity;
                    sheets[i][j].Transform(transform);
                }
            }


            




            //this.sheets = new Polyline[0][];
            //this.width = 2000;
            //this.height = 100;
            //this.gap_x = 300;
            //this.gap_y = 300;
            //this.array_x = 2;
            //this.array_y = 100;
        }

        // Inward MARGIN on the NESTING container: each sheet's OUTER boundary shrinks by `margin` (parts keep a
        // setback from the edge), each sheet HOLE grows by `margin` (keep-out regions get clearance). Reuses the
        // same orientation-agnostic, collinear-merged offset as parts. On failure the original is kept.
        public void offset_sheet_boundary(double margin)
        {
            if (System.Math.Abs(margin) < 1e-9) return;
            double tol = Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            for (int s = 0; s < this.sheets.Length; s++)
            {
                if (this.sheets[s] == null) continue;
                for (int j = 0; j < this.sheets[s].Length; j++)
                {
                    if (this.sheets[s][j] == null || this.sheets[s][j].Count < 4) continue;
                    // sheet OUTER (j==0) shrinks (isHole=true); sheet HOLES (j>0) grow (isHole=false)
                    Polyline off = nest_geo.offset_closed_polyline(this.sheets[s][j], margin, j == 0, tol);
                    if (off != null && off.Count >= 4) this.sheets[s][j] = off;
                }
            }
        }

    //    public nest_sheets(double width = 2000, double height = 100, double gap_x = 300, double gap_y = 300, int array_x = 2, int array_y = 10, string template = "")
    //    {
    //        this.sheets = new Polyline[0][];
    //        this.width = width;
    //        this.height = height;
    //        this.gap_x = gap_x;
    //        this.gap_y = gap_y;
    //        this.array_x = array_x;
    //        this.array_y = array_y;
    //        this.template = template;
    //    }

    //    private void get_grid()
    //    {

           
    //        Polyline[][] polylines = new Polyline[this.sheets.Length][];
    //        this.sheets = new Polyline[this.array_x * this.array_y][];
    //        Rectangle3d rectangle3d = new Rectangle3d(Plane.WorldXY, new Point3d(0, 0, 0), new Point3d(this.width, this.height, 0));
    //        Rhino.RhinoApp.WriteLine(this.width.ToString());
            
    //        for (int k = 0; k < this.array_x; k++)
    //        {
    //            for (int l = 0; l < this.array_y; l++)
    //            {
    //                int arrayX = k + this.array_x * l;
    //                this.sheets[arrayX] = new Polyline[] { rectangle3d.ToPolyline() };
    //                this.sheets[arrayX][0].Transform(Transform.Translation(new Vector3d(k * (this.width + this.gap_x),-l * (this.height + this.gap_y), 0)));
    //            }
    //        }
    //    }

    //    public void run()
    //    {
    //        this.template_values();
    //        this.get_grid();
    //    }

    //    public void template_values()
    //    {
    //        this.overwrite_values[0] = this.width;
    //        this.overwrite_values[1] = this.height;
    //        this.overwrite_values[2] = this.gap_x;
    //        this.overwrite_values[3] = this.gap_y;
    //        this.overwrite_values[4] = (double)this.array_x;
    //        this.overwrite_values[5] = (double)this.array_y;
    //        string lower = this.template.ToLower();
    //        string str = Regex.Replace(lower, "\\s+", "");

    //        if (str == "autometrix" || str == "0")
    //        {
    //            this.width = 2000;
    //            this.height = 100;
    //            if ((this.array_x != 2 ? false : this.array_y == 100))
    //            {
    //                this.array_x = 2;
    //                this.array_y = 30;
    //            }
    //        }
    //        else if (str == "shopsabre" || str == "1")
    //        {
    //            this.width = 100;
    //            this.height = 2000;
    //            if ((this.array_x != 2 ? false : this.array_y == 100))
    //            {
    //                this.array_x = 30;
    //                this.array_y = 1;
    //            }
    //        }
    //        for (int i = 0; i < (int)this.overwrite.Length; i++)
    //        {
    //            switch (i)
    //            {
    //                case 0:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.width = this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //                case 1:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.height = this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //                case 2:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.gap_x = this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //                case 3:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.gap_y = this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //                case 4:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.array_x = (int)this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //                case 5:
    //                    {
    //                        if (this.overwrite[i])
    //                        {
    //                            this.array_y = (int)this.overwrite_values[i];
    //                        }
    //                        break;
    //                    }
    //            }
    //        }

           
    //    }
    }
}