using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace nest_lib
{
    public partial class NestingContext
    {
        public List<NFP> Polygons { get; private set; } = new List<NFP>();
        public List<NFP> Sheets { get; private set; } = new List<NFP>();

        public int SheetsNotUsed = -1;





        bool offsetTreePhase = true;

        public List<NestItem> partsLocal = new List<NestItem>();


        public void init()
        {
            partsLocal = new List<NestItem>();
            List<NFP> lsheets = new List<NFP>();
            List<NFP> lpoly = new List<NFP>();

            for (int i = 0; i < Polygons.Count; i++)
                Polygons[i].id = i;

            for (int i = 0; i < Sheets.Count; i++)
                Sheets[i].id = i;

            foreach (var item in Polygons)
            {
                NFP clone = new NFP();
                clone.id = item.id;
                clone.source = item.source;
                clone.Points = item.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();
                if (item.children != null)
                {
                    clone.children = new List<NFP>();
                    foreach (var citem in item.children)
                    {
                        clone.children.Add(new NFP());
                        var l = clone.children.Last();
                        l.id = citem.id;
                        l.source = citem.source;
                        l.Points = citem.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();
                    }
                }
                lpoly.Add(clone);
            }


            foreach (var item in Sheets)
            {
                NFP clone = new NFP();
                clone.id = item.id;
                clone.source = item.source;
                clone.Points = item.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();
                if (item.children != null)
                {
                    clone.children = new List<NFP>();
                    foreach (var citem in item.children)
                    {
                        clone.children.Add(new NFP());
                        var l = clone.children.Last();
                        l.id = citem.id;
                        l.source = citem.source;
                        l.Points = citem.Points.Select(z => new SvgPoint(z.x, z.y) { exact = z.exact }).ToArray();
                    }
                }
                lsheets.Add(clone);
            }


            if (offsetTreePhase)
            {
                var grps = lpoly.GroupBy(z => z.source).ToArray();
                if (Background.UseParallel)
                {
                    Parallel.ForEach(grps, (item) =>
                    {
                        SvgNest.offsetTree(item.First(), 0.5 * SvgNest.Config.spacing, SvgNest.Config);
                        foreach (var zitem in item)
                        {
                            zitem.Points = item.First().Points.ToArray();
                        }

                    });

                }
                else
                {
                    foreach (var item in grps)
                    {
                        SvgNest.offsetTree(item.First(), 0.5 * SvgNest.Config.spacing, SvgNest.Config);
                        foreach (var zitem in item)
                        {
                            zitem.Points = item.First().Points.ToArray();
                        }
                    }
                }

                foreach (var item in lsheets)
                {
                    var gap = SvgNest.Config.sheetSpacing - SvgNest.Config.spacing / 2;
                    SvgNest.offsetTree(item, -gap, SvgNest.Config, true);
                }
            }



            var p1 = lpoly.GroupBy(z => z.source).Select(z => new NestItem()
            {
                Polygon = z.First(),
                IsSheet = false,
                Quanity = z.Count()
            });

            var p2 = lsheets.GroupBy(z => z.source).Select(z => new NestItem()
            {
                Polygon = z.First(),
                IsSheet = true,
                Quanity = z.Count()
            });


            partsLocal.AddRange(p1);
            partsLocal.AddRange(p2);
            int srcc = 0;
            foreach (var item in partsLocal)
                item.Polygon.source = srcc++;
        }


    }
}
