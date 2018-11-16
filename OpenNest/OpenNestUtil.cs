using DeepNestLib;
using Rhino;
using Rhino.Geometry;
using System;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace OpenNest {
    public static class OpenNestUtil {

       public static Transform[] MergeTransfoms(this Transform[] t0, Transform[] t1) {

            Transform[] t2 = new Transform[t0.Length];

            for(int i = 0; i < t0.Length; i++) {
                t2[i] = t0[i] * t1[i];
            }

            return t2;

        }

        public static Polyline TransformPolyline(this Polyline polyline, Transform transform) {
            Polyline newPolyline = new Polyline(polyline);
            newPolyline.Transform(transform);
            return newPolyline;
        }

        public static Polyline[] TransformPolylines(this List<Polyline> polylines, Transform[] transform) {

            Polyline[] polylinesNew = new Polyline[polylines.Count];

            for (int i = 0; i < polylines.Count; i++) {
                polylinesNew[i] = polylines[i].TransformPolyline(transform[i]);
            }

            return polylinesNew;

        }

        public static Polyline[] TransformPolylines(this Polyline[] polylines, Transform[] transform) {

            Polyline[] polylinesNew = new Polyline[polylines.Length];

            for (int i = 0; i < polylines.Length; i++) {
                polylinesNew[i] = polylines[i].TransformPolyline(transform[i]);
            }

            return polylinesNew;

        }

        public static Transform GetTransform(this NFP nfp) {

            //Transformation matrix from translation and rotation
            Transform translate = Transform.Translation(new Vector3d(nfp.x, nfp.y, 0));
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            translate *= rotation;

            return translate;
        }


        public static Transform[] GetTransforms(this List<NFP> nfps) {
            Transform[] transforms = new Transform[nfps.Count];
            for(int i = 0; i < nfps.Count; i++) {
                transforms[i] = nfps[i].GetTransform();
            }
            return transforms;
        }

        public static Transform GetRotation(this NFP nfp) {
            //Transformation matrix from translation and rotation
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation), new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            return rotation;
        }


        public static Transform[] GetRotations(this List<NFP> nfps) {
            Transform[] transforms = new Transform[nfps.Count];
            for (int i = 0; i < nfps.Count; i++) {
                transforms[i] = nfps[i].GetRotation();
            }
            return transforms;
        }




        public static Polyline ToPolyline(this NFP nfp) {

            Polyline polyline = new Polyline();


            foreach (SvgPoint p in nfp.Points) {
                polyline.Add(p.x, p.y, 0);
            }

            polyline.Add(polyline[0]);

            //Transformation
            Transform translate = Transform.Translation(new Vector3d(nfp.x, nfp.y, 0));
            Transform rotation = Transform.Rotation(Rhino.RhinoMath.ToRadians(nfp.rotation),new Point3d(nfp.Points[0].x, nfp.Points[0].y, 0));
            translate *= rotation ;

            polyline.Transform(translate);

            return polyline;

        }

        public static Polyline[] ToPolylines(this List<NFP> nfps) {

            Polyline[] polylines = new Polyline[nfps.Count];

            for (int i = 0; i < nfps.Count; i++) {
                polylines[i] = nfps[i].ToPolyline();
            }


            return polylines;

        }


        public static int[] ToIDArray(this List<NFP> nfps) {

            int[] id = new int[nfps.Count];

            for (int i = 0; i < nfps.Count; i++)
                id[i] = nfps[i].id;

            return id;

        }

    }
}
