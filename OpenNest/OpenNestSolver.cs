using DeepNestLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Rhino;
using Rhino.Geometry;
using OpenNest;

namespace OpenNest {

    public class OpenNestSolver {

        List<NFP> polygons = new List<NFP>();
        List<NFP> sheets = new List<NFP>();

        public void AddSheet(int w, int h) {
            var tt = new RectanglePolygonSheet();
            tt.Name = "sheet" + (sheets.Count + 1);
            sheets.Add(tt);
            var p = sheets.Last();
            tt.Height = h;
            tt.Width = w;
            tt.Rebuild();
            ReorderSheets();
        }

        Random r = new Random();

        public int GetNextSource() {
            if (polygons.Any()) {
                return polygons.Max(z => z.source.Value) + 1;
            }
            return 0;
        }

        public void AddRectanglePart(int src, int ww = 50, int hh = 80) {
            int xx = 0;
            int yy = 0;
            NFP pl = new NFP();

            polygons.Add(pl);
            pl.source = src;
            pl.Points = new SvgPoint[] { };
            pl.AddPoint(new SvgPoint(xx, yy));
            pl.AddPoint(new SvgPoint(xx + ww, yy));
            pl.AddPoint(new SvgPoint(xx + ww, yy + hh));
            pl.AddPoint(new SvgPoint(xx, yy + hh));
        }




        public bool IsFinished(int iterations, int _iterations= 10) {
            //insert you code here
            //return current.fitness < 12e6;
       
            return (iterations >= _iterations) || (iterations >= 1000);
        }

        public void LoadDataFromXml(string v) {
            var d = XDocument.Load(v);
            var f = d.Descendants().First();
            var gap = int.Parse(f.Attribute("gap").Value);
            SvgNest.Config.spacing = gap;

            foreach (var item in d.Descendants("sheet")) {
                var cnt = int.Parse(item.Attribute("count").Value);
                var ww = int.Parse(item.Attribute("width").Value);
                var hh = int.Parse(item.Attribute("height").Value);

                for (int i = 0; i < cnt; i++) {
                    AddSheet(ww, hh);
                }
            }
            foreach (var item in d.Descendants("part")) {
                var cnt = int.Parse(item.Attribute("count").Value);
                var path = item.Attribute("path").Value;
                var r = SvgParser.LoadSvg(path);
                var src = GetNextSource();

                for (int i = 0; i < cnt; i++) {
                    ImportFromRawDetail(r, src);
                }
            }
        }

        public void StartNest() {
            current = null;
            nest = new SvgNest();
            Background.cacheProcess2 = new Dictionary<string, NFP[]>();

            Background.window = new windowUnk();
            Background.callCounter = 0;
        }

        public void DeepNestIterate() {


            List<NFP> lsheets = new List<NFP>();
            List<NFP> lpoly = new List<NFP>();
            for (int i = 0; i < polygons.Count; i++) {
                polygons[i].id = i;
            }
            for (int i = 0; i < sheets.Count; i++) {
                sheets[i].id = i;
            }
            foreach (var item in polygons) {
                NFP clone = new NFP();
                clone.id = item.id;
                clone.source = item.source;
                clone.Points = item.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();

                lpoly.Add(clone);
            }


            foreach (var item in sheets) {
                RectanglePolygonSheet clone = new RectanglePolygonSheet();
                clone.id = item.id;
                clone.source = item.source;
                clone.Points = item.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();

                lsheets.Add(clone);
            }


            var grps = lpoly.GroupBy(z => z.source).ToArray();
            if (Background.UseParallel) {
                Parallel.ForEach(grps, (item) => {
                    SvgNest.offsetTree(item.First(),  1.0*SvgNest.Config.spacing, SvgNest.Config);
                    foreach (var zitem in item) {
                        zitem.Points = item.First().Points.ToArray();
                    }

                });

            } else {

                foreach (var item in grps) {
                    SvgNest.offsetTree(item.First(), 1.0 * SvgNest.Config.spacing, SvgNest.Config);
                    foreach (var zitem in item) {
                        zitem.Points = item.First().Points.ToArray();
                    }
                }
            }
            foreach (var item in lsheets) {
                SvgNest.offsetTree(item, -1.0 * SvgNest.Config.spacing, SvgNest.Config, true);
            }



            List<NestItem> partsLocal = new List<NestItem>();
            var p1 = lpoly.GroupBy(z => z.source).Select(z => new NestItem() {
                Polygon = z.First(),
                IsSheet = false,
                Quanity = z.Count()
            });

            var p2 = lsheets.GroupBy(z => z.source).Select(z => new NestItem() {
                Polygon = z.First(),
                IsSheet = true,
                Quanity = z.Count()
            });


            partsLocal.AddRange(p1);
            partsLocal.AddRange(p2);
            int srcc = 0;
            foreach (var item in partsLocal) {
                item.Polygon.source = srcc++;
            }


            nest.launchWorkers(partsLocal.ToArray());
            var plcpr = nest.nests.First();


            if (current == null || plcpr.fitness < current.fitness) {
                AssignPlacement(plcpr);
            }
        }

        SheetPlacement current = null;
        SvgNest nest;
        public void AssignPlacement(SheetPlacement plcpr) {
            current = plcpr;
            double totalSheetsArea = 0;
            double totalPartsArea = 0;

            List<Polygon> placed = new List<Polygon>();
            foreach (var item in polygons) {
                item.fitted = false;
            }
            foreach (var item in plcpr.placements) {
                foreach (var zitem in item) {
                    var sheetid = zitem.sheetId;
                    var sheet = sheets.First(z => z.id == sheetid);
                    totalSheetsArea += GeometryUtil.polygonArea(sheet);

                    foreach (var ssitem in zitem.sheetplacements) {

                        var poly = polygons.First(z => z.id == ssitem.id);
                        totalPartsArea += GeometryUtil.polygonArea(poly);
                        placed.Add(poly);
                        poly.fitted = true;
                        poly.x = ssitem.x + sheet.x;
                        poly.y = ssitem.y + sheet.y;
                        poly.rotation = ssitem.rotation;
                    }
                }
            }

            var ppps = polygons.Where(z => !placed.Contains(z));
            foreach (var item in ppps) {
                item.x = -500;
                item.y = 0;
            }
        }
        public void ReorderSheets() {
            double x = 0;
            double y = 0;
            for (int i = 0; i < sheets.Count; i++) {
                sheets[i].x = x;
                sheets[i].y = y;
                var r = sheets[i] as RectanglePolygonSheet;
                x += r.Width + 10;
            }
        }


        public void LoadSampleData(List<Rectangle3d> sheets, List<Polyline> outlines) {
            Console.WriteLine("Adding sheets..");
            //add sheets
            for (int i = 0; i < sheets.Count; i++) {
                AddSheet((int)sheets[i].Width, (int)sheets[i].Height);
            }

            Console.WriteLine("Adding parts..");
            //add parts
            int src1 = GetNextSource();
            for (int i = 0; i < outlines.Count; i++) {
                AddPolygon(outlines[i], i);
                //AddRectanglePart(src1, 250, 220);
                //AddTrianglePart(src1, 250, 220);
            }

        }

        public void AddTrianglePart(int src, int ww = 50, int hh = 80) {
            int xx = 0;
            int yy = 0;
            NFP pl = new NFP();

            polygons.Add(pl);
            pl.source = src;
            pl.Points = new SvgPoint[] { };
            pl.AddPoint(new SvgPoint(xx, yy));
            pl.AddPoint(new SvgPoint(xx + ww, yy));
            pl.AddPoint(new SvgPoint(xx + ww, yy + hh));
            //pl.AddPoint(new SvgPoint(xx, yy + hh));
        }

        public void AddPolygon(Polyline polyline, int id) {



            if (polyline.IsValid) {
                if (polyline.Count > 2) {
                    NFP pl = new NFP();
                    pl.source = id;

                    pl.Points = new SvgPoint[] { };
                    int last = (polyline.IsClosed) ? 1 : 0;
                    for (int i = 0; i < polyline.Count - last; i++)
                        pl.AddPoint(new SvgPoint(polyline[i].X, polyline[i].Y));

                    polygons.Add(pl);
                }
            }

            
        }

        //--------------------->First function points here
        public void LoadInputData(string path, int count) {
            var dir = new DirectoryInfo(path);
            foreach (var item in dir.GetFiles("*.svg")) {
                try {
                    var src = GetNextSource();
                    for (int i = 0; i < count; i++) {
                        ImportFromRawDetail(SvgParser.LoadSvg(item.FullName), src);
                    }
                } catch (Exception ex) {
                    Console.WriteLine("Error loading " + item.FullName + ". skip");
                }
            }
        }


        //--------------------->Second function points here
        public void ImportFromRawDetail(RawDetail raw, int src) {
            NFP po = new NFP();

            po.Name = raw.Name;//take name

            po.Points = new SvgPoint[] { };
            //if (raw.Outers.Any())
            {
                var tt = raw.Outers.Union(raw.Holes).OrderByDescending(z => z.Len).First();
                foreach (var item in tt.Points) {
                    po.AddPoint(new SvgPoint(item.X, item.Y));
                }

                po.source = src;
                polygons.Add(po);
            }
        }
        int iterations = 0;


        public Tuple<Polyline[], int[], Transform[]> Run(double spacing = 1, int _iterations = 1) {


            Background.UseParallel = true;
            SvgNest.Config.placementType = PlacementTypeEnum.gravity;
            SvgNest.Config.spacing = spacing;
            Rhino.RhinoApp.WriteLine("Settings updated..");

            Rhino.RhinoApp.WriteLine("Start nesting..");
            Rhino.RhinoApp.WriteLine("Parts: " + polygons.Count());
            Rhino.RhinoApp.WriteLine("Sheets: " + sheets.Count());
            StartNest();
            iterations = 0;

            do {
                var sw = Stopwatch.StartNew();
                DeepNestIterate();
                sw.Stop();
                Rhino.RhinoApp.WriteLine("Iteration: " + iterations + "; fitness: " + current.fitness + "; nesting time: " + sw.ElapsedMilliseconds + "ms");
                iterations++;
            } while (!IsFinished(iterations,_iterations));

            #region convert results

            //polygons[i].Points
            Polyline[] sheetsRhino = sheets.ToPolylines();
            //Polyline[] polygonsRhino = polygons.ToPolylines();
            Transform[] polygonsTransforms = polygons.GetTransforms();
            //Transform[] polygonsRotations = polygons.GetRotations();
            //Transform[] polygonsRotations = polygons.GetRotations();
            int[] id = polygons.ToIDArray();

            return new  Tuple<Polyline[],int[], Transform[]>(sheetsRhino,  id, polygonsTransforms);

            //SvgParser.Export("temp.svg", polygons, sheets);
            //Console.WriteLine("Results exported in: temp.svg");
            #endregion
        }




    }

    
}

