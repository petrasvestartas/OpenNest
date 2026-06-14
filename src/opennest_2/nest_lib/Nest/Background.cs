using ClipperLib;
using System.Collections.Generic;
using System.Linq;

namespace nest_lib
{
    // Static geometry utilities shared by the data/marshalling layer (SvgNest helpers,
    // NestingContext.init). The managed DeepNest placement worker that used to live here
    // (placeParts, NFP caches, minkowski.dll interop) was removed together with the
    // Engine="cs" solver branch - the native nfp_nest.dll engine is the only solver.
    public class Background
    {
        // Parallel preparation toggle for NestingContext.init's offset phase.
        public static bool UseParallel = false;

        public static NFP toNestCoordinates(IntPoint[] polygon, double scale)
        {
            var clone = new List<SvgPoint>();

            for (var i = 0; i < polygon.Count(); i++)
            {
                clone.Add(new SvgPoint(
                     polygon[i].X / scale,
                             polygon[i].Y / scale
                        ));
            }
            return new NFP() { Points = clone.ToArray() };
        }

        public static NFP getHull(NFP polygon)
        {
            double[][] points = new double[polygon.length][];
            for (var i = 0; i < polygon.length; i++)
            {
                points[i] = (new double[] { polygon[i].x, polygon[i].y });
            }

            var hullpoints = D3.polygonHull(points);

            if (hullpoints == null)
            {
                return polygon;
            }

            NFP hull = new NFP();
            for (int i = 0; i < hullpoints.Count(); i++)
            {
                hull.AddPoint(new SvgPoint(hullpoints[i][0], hullpoints[i][1]));
            }
            return hull;
        }

        // returns clipper nfp. Remember that clipper nfp are a list of polygons, not a tree!
        public static IntPoint[][] nfpToClipperCoordinates(NFP nfp, double clipperScale = 10000000)
        {
            List<IntPoint[]> clipperNfp = new List<IntPoint[]>();

            // children first
            if (nfp.children != null && nfp.children.Count > 0)
            {
                for (var j = 0; j < nfp.children.Count; j++)
                {
                    if (GeometryUtil.polygonArea(nfp.children[j]) < 0)
                    {
                        nfp.children[j].reverse();
                    }
                    var childNfp = clipper_util.ScaleUpPaths(nfp.children[j], clipperScale);
                    clipperNfp.Add(childNfp);
                }
            }

            if (GeometryUtil.polygonArea(nfp) > 0)
            {
                nfp.reverse();
            }

            var outerNfp = clipper_util.ScaleUpPaths(nfp, clipperScale);
            clipperNfp.Add(outerNfp);

            return clipperNfp.ToArray();
        }

        // inner nfps can be an array of nfps, outer nfps are always singular
        public static IntPoint[][] innerNfpToClipperCoordinates(NFP[] nfp, SvgNestConfig config)
        {
            List<IntPoint[]> clipperNfp = new List<IntPoint[]>();
            for (var i = 0; i < nfp.Count(); i++)
            {
                var clip = nfpToClipperCoordinates(nfp[i], config.clipperScale);
                clipperNfp.AddRange(clip);
            }

            return clipperNfp.ToArray();
        }
    }
}
