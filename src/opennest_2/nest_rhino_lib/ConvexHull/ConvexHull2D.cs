using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace nest_rhino_lib.ConvexHull
{
    public static class ConvexHull2D {

        public static Polyline Solve(List<Point3d> points) {

            var polyline = new Polyline(points);

            Plane plane; 
            Plane.FitPlaneToPoints(points, out plane);
            Transform t = Transform.PlaneToPlane(plane,Plane.WorldXY);
            polyline.Transform(t);

            ConvexHull<DefaultVertex, DefaultConvexFace<DefaultVertex>> hull = ConvexHull.Create(polyline.ToList(), true);

            Polyline outline = new Polyline();
            foreach (DefaultVertex pt in hull.Points) {
                double[] pos = pt.Position;
                outline.Add(new Point3d(pos[0], pos[1], 0));
            }


            outline.Add(outline[0]);

            Transform inv;
            t.TryGetInverse(out inv);
            outline.Transform(inv);

            return outline;
        }

    }
}
