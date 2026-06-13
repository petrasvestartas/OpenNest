using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Rhino.Geometry;

namespace nest_lib
{
    public partial class NestingContext
    {

        public void load_sample_data(ref nest_rhino_lib.nest_sheets nest_sheets, ref nest_rhino_lib.nest_geo nest_geo, ref Transform[] xforms_geometry)
        {

            for (int i = 0; i < nest_sheets.sheets.Length; i++)
            {
                NFP polygon = new NFP() { source = i, Points = new SvgPoint[] { }, children = new List<NFP>() };

                for (int k = 0; k < nest_sheets.sheets[i][0].Count - 1; k++)
                    polygon.AddPoint(new SvgPoint(nest_sheets.sheets[i][0][k].X, nest_sheets.sheets[i][0][k].Y));

 

                //Add holes
                for (int j = 1; j < nest_sheets.sheets[i].Length; j++)
                {

                    NFP polygon_hole = new NFP();
                    for (int k = 0; k < nest_sheets.sheets[i][j].Count - 1; k++)
                        polygon_hole.AddPoint(new SvgPoint(nest_sheets.sheets[i][j][k].X, nest_sheets.sheets[i][j][k].Y));

                    polygon.children.Add(polygon_hole);
                }

                // Add to sheets list
                Sheets.Add(polygon);
            }

            for (int i = 0; i < nest_geo.boundary_sorted.Count; i++)
                {
                // Per-part rotation override travels with each instance (0 = inherit; same
                // per-geometry-item indexing as copies).
                int rotationOverride = 0;
                {
                    int gi = nest_geo.geometry_sorted[i][0];
                    if (nest_geo.rotations != null && gi >= 0 && gi < nest_geo.rotations.Count)
                        rotationOverride = Math.Max(0, nest_geo.rotations[gi]);
                }
                for (int l = 0; l < nest_geo.copies[nest_geo.geometry_sorted[i][0]]; l++)
                    {
                    //Rhino.RhinoApp.WriteLine("11");
                    if (nest_geo.boundary_sorted[i][0].Item2.Count > 2)
                        {

                            NFP polygon = new NFP() { source = i, Points = new SvgPoint[] { }, children = new List<NFP>(), rotationCount = rotationOverride };

                        //NFP polygon is closed by default, no need to add end point
                        //Add boundary
                        Polyline boundary = new Polyline(nest_geo.boundary_sorted[i][0].Item2);
                        boundary.Transform(xforms_geometry[i]);


                        for (int j = 0; j < nest_geo.boundary_sorted[i][0].Item2.Count - 1; j++)
                                polygon.AddPoint(new SvgPoint(boundary[j].X, boundary[j].Y));

                        //Add holes
                        for (int j = 1; j < nest_geo.boundary_sorted[i].Count; j++)
                        {
                           
                            Polyline hole = new Polyline(nest_geo.boundary_sorted[i][j].Item2);

                            hole.Transform(xforms_geometry[i]);

                            NFP polygon_hole = new NFP();
                            for (int k = 0; k < hole.Count - 1; k++)
                                polygon_hole.AddPoint(new SvgPoint(hole[k].X, hole[k].Y));
                            polygon.children.Add(polygon_hole);
                        }

                        Polygons.Add(polygon);
                        }
                    }
                }

 


        }

    }
}
