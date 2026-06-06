using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Rhino.Geometry;
using System.Linq;

namespace opennest_2
{
    public class Unroll : GH_Component {
        public override Guid ComponentGuid {
            get { return new Guid("3718d679-f4f1-10ee-4c0f-84ad2b4e7811"); }
        }
        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override System.Drawing.Bitmap Icon {
            get {

                return Properties.Resources.unroll;
            }
        }

        public Unroll() : base("Unroll", "Unroll", "Unrolls a developable Brep or mesh to flat, carrying along its curves, points and text.", "Params", "OpenNest2") {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddGenericParameter("Mesh or Brep", "M or B", "Mesh or Brep to unroll.", 0);
            pManager.AddCurveParameter("Curves", "C", "Curves to unroll along with the surface", GH_ParamAccess.list);
            pManager.AddPointParameter("Points", "P", "Points to unroll along with the surface", GH_ParamAccess.list);
            pManager.AddTextParameter("Text", "TXT", "Text labels to unroll along with the surface", GH_ParamAccess.list);
            pManager.AddPointParameter("Text point", "TP", "Anchor points for the text labels", GH_ParamAccess.list);
            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddBrepParameter("Brep", "B", "Unrolled Brep", 0);
            pManager.AddCurveParameter("Curves", "C", "Unrolled curves", GH_ParamAccess.list);
            pManager.AddPointParameter("Points", "P", "Unrolled points", GH_ParamAccess.list);
            pManager.AddGenericParameter("TextDotsLocation", "TXT L", "Unrolled text label locations", GH_ParamAccess.list);
            pManager.AddGenericParameter("TextDots", "TXT", "Unrolled text labels", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA) {

            Object obj = new object();
            bool flag = !DA.GetData<object>(0, ref obj);




            checked {
                if (!flag) {
                    Brep brep = new Brep();
                    Mesh mesh = new Mesh();
                    if (obj.GetType() == typeof(Grasshopper.Kernel.Types.GH_Mesh)) {
                        mesh = ((Grasshopper.Kernel.Types.GH_Mesh)obj).Value;

                        //Mesh to brep
                        List<Brep> faceBreps = new List<Brep>();
                        foreach (MeshFace mf in mesh.Faces) {
                            if (mf.IsQuad) {
                                faceBreps.Add(
                                    NurbsSurface.CreateFromCorners(mesh.Vertices[mf.A], mesh.Vertices[mf.B],
                                        mesh.Vertices[mf.C], mesh.Vertices[mf.D]).ToBrep());
                            } else {
                                faceBreps.Add(
                                    NurbsSurface.CreateFromCorners(mesh.Vertices[mf.A], mesh.Vertices[mf.B],
                                        mesh.Vertices[mf.C]).ToBrep());

                            }
                        }
                        brep = Brep.JoinBreps(faceBreps, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance)[0];
                    } else
                        brep = ((Grasshopper.Kernel.Types.GH_Brep)obj).Value; ;


                    flag = !brep.IsValid;
                    if (!flag) {
                        List<Curve> list = new List<Curve>();
                        DA.GetDataList<Curve>(1, list);
                        List<Point3d> list2 = new List<Point3d>();
                        DA.GetDataList<Point3d>(2, list2);
                        List<String> textDot = new List<String>();
                        DA.GetDataList<String>(3, textDot);
                        List<Point3d> textLoc = new List<Point3d>();
                        DA.GetDataList<Point3d>(4, textLoc);
                        Unroller unroller = new Unroller(brep);
                        //unroller.AddFollowingGeometry(dotLocations, dotText);
                    
                        Curve[] array = null;
                        Point3d[] array2 = null;
                        //TextDot[] arrayText = null;
                        flag = (list.Count > 0);
                        if (flag) {
                            unroller.AddFollowingGeometry(list);
                        }
                        flag = (list2.Count > 0);
                        if (flag) {
                            unroller.AddFollowingGeometry(list2);
                        }

                        if (flag) {
                            for (int i = 0; i < textDot.Count; i++) {
                                unroller.AddFollowingGeometry(textLoc[i], textDot[i]);
                            }

                        }
                        Unroller arg_9E_0 = unroller;
                        TextDot[] array3 = null;
                        Brep[] array4 = arg_9E_0.PerformUnroll(out array, out array2, out array3);

                        Transform transform = this.transformThem(array4);
                        Brep[] array5 = array4;

                        for (int i = 0; i < array5.Length; i++) {
                            Brep brep2 = array5[i];
                            brep2.Transform(transform);
                        }


                        Curve[] array6 = array;

                        for (int j = 0; j < array6.Length; j++) {
                            Curve curve = array6[j];
                            curve.Transform(transform);
                        }

                        int arg_115_0 = 0;
                        int num = array2.Count<Point3d>() - 1;
                        int num2 = arg_115_0;
                        while (true) {
                            int arg_158_0 = num2;
                            int num3 = num;
                            if (arg_158_0 > num3) {
                                break;
                            }
                            Point3d point3d = new Point3d(array2[num2]);
                            point3d.Transform(transform);
                            array2[num2] = point3d;
                            num2++;
                        }

                        //Text
                        int arg_115_1 = 0;
                        int numA = array3.Count<TextDot>() - 1;
                        int numB = arg_115_1;

                        while (true) {
                            int arg_158_0 = numB;
                            int num3 = numA;
                            if (arg_158_0 > num3) {
                                break;
                            }
                            TextDot text = array3[numB];
                            text.Transform(transform);
                            array3[numB] = text;
                            numB++;
                        }

                        //Dirty way of text dost
                        List<string> txt = new List<string>();
                        List<Point3d> pts = new List<Point3d>();

                        foreach (var t in array3) {
                            txt.Add(t.Text);
                            pts.Add(t.Point);

                        }

                        Brep[] unrollBrep = Brep.JoinBreps(array4, Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);


                        DA.SetDataList(0, unrollBrep.ToList<Brep>());
                        DA.SetDataList(1, array.ToList<Curve>());
                        DA.SetDataList(2, array2.ToList<Point3d>());
                        DA.SetDataList(3, pts);
                        DA.SetDataList(4, txt);

                    }
                }
            }
        }

        public Transform transformThem(Brep[] breps) {
            double[] array = new double[11];
            Plane[] array2 = new Plane[11];
            int num = 0;
            int arg_1E5_0;
            int num3;
            do {
                System.Collections.Generic.List<Point3d> list = new System.Collections.Generic.List<Point3d>();
                double num2 = (double)num / 10.0 * 3.1415926535897931;
                Plane plane = new Plane(Plane.WorldXY);
                Transform transform = Transform.Rotation(num2, Point3d.Origin);
                plane.Transform(transform);
                checked {
                    for (int i = 0; i < breps.Length; i++) {
                        Brep brep = breps[i];
                        Box box = new Box(plane, brep);
                        list.AddRange(box.GetCorners());
                    }
                    Box box2 = new Box(plane, list);
                    array[num] = unchecked(box2.GetCorners()[0].DistanceTo(box2.GetCorners()[1]) * box2.GetCorners()[0].DistanceTo(box2.GetCorners()[3]));
                    Plane plane2 = default(Plane);
                    bool flag = box2.GetCorners()[0].DistanceTo(box2.GetCorners()[1]) < box2.GetCorners()[0].DistanceTo(box2.GetCorners()[3]);
                    if (flag) {
                        plane2 = new Plane(box2.GetCorners()[0], box2.GetCorners()[1], box2.GetCorners()[3]);
                    } else {
                        plane2 = new Plane(box2.GetCorners()[0], box2.GetCorners()[3], box2.GetCorners()[1]);
                    }
                    array2[num] = plane2;
                    num++;
                    arg_1E5_0 = num;
                    num3 = 10;
                }
            }
            while (arg_1E5_0 <= num3);
            System.Array.Sort<double, Plane>(array, array2);
            return Transform.PlaneToPlane(array2[0], Plane.WorldXY);
        }
    }
}