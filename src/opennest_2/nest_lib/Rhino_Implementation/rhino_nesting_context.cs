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

        ///////////////////////////////////////////////////////////////////////////////////////////////
        //Rhino methods
        ///////////////////////////////////////////////////////////////////////////////////////////////
        public void load_sample_data(List<Rectangle3d> sheets, List<Polyline> outlines)
        {

            for (int i = 0; i < sheets.Count; i++)
                AddSheet((int)sheets[i].Width, (int)sheets[i].Height, i);

            int src1 = GetNextSource();
            for (int i = 0; i < outlines.Count; i++)
                AddPolygon(outlines[i], i);

        }

        public void load_sample_data(List<Rectangle3d> sheets, Polyline[][] outlines)
        {

            for (int i = 0; i < sheets.Count; i++)
                AddSheet((int)sheets[i].Width, (int)sheets[i].Height, i);


            int src1 = GetNextSource();
            for (int i = 0; i < outlines.Length; i++)
                AddPolygon(outlines[i], i);

        }

        public void load_sample_data(List<Polyline> sheets, Polyline[][] outlines)
        {

            for (int i = 0; i < sheets.Count; i++)
                AddSheet(sheets[i], i);

            int src1 = GetNextSource();
            for (int i = 0; i < outlines.Length; i++)
                AddPolygon(outlines[i], i);

        }

        public void load_sample_data(List<Rectangle3d> sheets, Polyline[][] outlines, Polyline[][] holes)
        {

            for (int i = 0; i < sheets.Count; i++)
                AddSheet((int)sheets[i].Width, (int)sheets[i].Height, i);

            int src1 = GetNextSource();
            for (int i = 0; i < outlines.Length; i++)
                AddPolygon(outlines[i][0], holes[i], i);
        }

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
                for (int l = 0; l < nest_geo.copies[nest_geo.geometry_sorted[i][0]]; l++)
                    {
                    //Rhino.RhinoApp.WriteLine("11");
                    if (nest_geo.boundary_sorted[i][0].Item2.Count > 2)
                        {

                            NFP polygon = new NFP() { source = i, Points = new SvgPoint[] { }, children = new List<NFP>() };

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

        public void AddPolygon(Polyline polyline, int id)
        {

            if (polyline.IsValid)
                if (polyline.Count > 2)
                {

                    NFP pl = new NFP() { source = id, Points = new SvgPoint[] { } };

                    for (int i = 0; i < polyline.Count - 1; i++)
                        pl.AddPoint(new SvgPoint(polyline[i].X, polyline[i].Y));

                    Polygons.Add(pl);
                }

        }

        public void AddSheet(Polyline polyline, int id)
        {

            if (polyline.IsValid)
                if (polyline.Count > 2)
                {

                    NFP pl = new NFP() { source = id, Points = new SvgPoint[] { } };

                    for (int i = 0; i < polyline.Count - 1; i++)
                        pl.AddPoint(new SvgPoint(polyline[i].X, polyline[i].Y));

                    Sheets.Add(pl);
                }

        }

        public void AddPolygon(Polyline[] polyline, int id)
        {

            //Polyline is just a list of points which end point is the same

            NFP polygon = new NFP();
            polygon.source = id;

            polygon.Points = new SvgPoint[] { };
            int last = (polyline.Length - 1) % polyline.Length;
            for (int i = 0; i < polyline[last].Count - 1; i++)
                polygon.AddPoint(new SvgPoint(polyline[last][i].X, polyline[last][i].Y));

            if (polyline.Length > 1)
            {//if there are holes

                polygon.children = new List<NFP>();

                for (int i = 0; i < polyline.Length - 1; i++)
                { //dont include last outline
                    NFP hole = new NFP();
                    for (int j = 0; j < polyline[i].Count - 1; j++) //dont include last point
                        hole.AddPoint(new SvgPoint(polyline[i][j].X, polyline[i][j].Y));
                    polygon.children.Add(hole);
                }
            }

            Polygons.Add(polygon);

        }

        public void AddPolygon(Polyline polyline, Polyline[] holes, int id)
        {


            if (polyline.IsValid)
                if (polyline.Count > 2)
                {
                    NFP polygon = new NFP() { source = id, Points = new SvgPoint[] { } };

                    //NFP polygon is closed by default, no need to add end point
                    for (int i = 0; i < polyline.Count - 1; i++)
                        polygon.AddPoint(new SvgPoint(polyline[i].X, polyline[i].Y));


                    polygon.children = new List<NFP>();

                    for (int i = 0; i < holes.Length; i++)
                    {
                        NFP hole = new NFP();
                        for (int j = 0; j < holes[i].Count - 1; j++)
                            hole.AddPoint(new SvgPoint(polyline[i].X, polyline[i].Y));
                        polygon.children.Add(hole);
                    }

                    Polygons.Add(polygon);
                }


        }

    }
}
