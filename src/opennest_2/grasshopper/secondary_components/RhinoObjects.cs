using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Types.Transforms;
using Rhino.Geometry;

namespace opennest_2
{

    public class RhinoObjects : GH_Component {

        public RhinoObjects()
          : base("Rhino Objects", "RhinoObjects",
              "Reads referenced Rhino objects, with attributes such as text, into planar Breps for nesting.",
              "Params", "OpenNest2") {
        }

        public override GH_Exposure Exposure => GH_Exposure.octonary;

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            IGH_Param geometryAsGuid = new Grasshopper.Kernel.Parameters.Param_Guid();
            pManager.AddParameter(geometryAsGuid, "Geometry as guid", "G", "Referenced geometry as guid (can be mesh, brep, curves in one list)", GH_ParamAccess.tree);
            pManager.AddNumberParameter("T", "T", "Tolerance", GH_ParamAccess.item, 0.01);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddBrepParameter("Breps", "B", "Planar Breps", GH_ParamAccess.list);
            IGH_Param geometryAsGuid = new Grasshopper.Kernel.Parameters.Param_Guid();
            pManager.AddParameter(geometryAsGuid, "Geometry as guid", "G", "Referenced geometry as guid (can be mesh, brep, curves in one list)", GH_ParamAccess.tree);
        }

        public Tuple<Brep[], Dictionary<int, List<Guid>>> SortGuidsByPlanarCurves(List<Guid> guids, double tol)
        {
            var TxtInput = new Dictionary<int, Tuple<Point3d, string>>();
            var crvsInput = new Dictionary<int, Curve>();
            var ptsInput = new Dictionary<int, Point3d>();
            var meshesInput = new Dictionary<int, Mesh>();
            var brepsInput = new Dictionary<int, Brep>();

            //Identify object types
            int id = 0;
            foreach (var g in guids)
            {
                GeometryBase rhino_obj = Rhino.RhinoDoc.ActiveDoc.Objects.Find(g).Geometry;
                string objectType = rhino_obj.ObjectType.ToString();

                //Rhino.RhinoApp.WriteLine("Uknown type: " + objectType.ToString());

                switch (objectType)
                {
                    case ("Annotation"):
                        TextEntity textObj = rhino_obj as TextEntity;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textObj.Plane.Origin, textObj.Text));
                        break;

                    case ("TextDot"):
                        TextDot textDotObj = rhino_obj as TextDot;
                        TxtInput.Add(id, new Tuple<Point3d, string>(textDotObj.Point, textDotObj.Text));

                        break;

                    case ("Curve"):
                        Curve curve = rhino_obj as Curve;
                        //if (curve.IsPolyline()) {
                        crvsInput.Add(id, curve);
                        //}
                        break;

                    case ("Point"):
                        Rhino.Geometry.Point p = rhino_obj as Rhino.Geometry.Point;
                        ptsInput.Add(id, p.Location);
                        break;

                    case ("Mesh"):
                        Mesh m = rhino_obj as Mesh;
                        meshesInput.Add(id, m);
                        break;

                    case ("Brep"):
                        Brep b = rhino_obj as Brep;
                        brepsInput.Add(id, b);
                        break;

                    case ("Extrusion"):
                        Extrusion e = rhino_obj as Extrusion;
                        brepsInput.Add(id, e.ToBrep());
                        break;

                    default:
                        Rhino.RhinoApp.WriteLine("Uknown type: " + objectType.ToString());
                        break;
                }

                id++;
            }

            //Joints Curves
            var crvClosed = new List<Curve>();

            foreach (var c in crvsInput)
            {
                c.Value.Domain = new Interval(0, 1);
                if (c.Value.IsClosed)
                {
                    if (c.Value.IsPolyline())
                    {
                        crvClosed.Add(c.Value);
                    }
                }
            }

            //Create planar breps
            Brep[] breps = Brep.CreatePlanarBreps(crvClosed);// Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance);

            HashSet<int> hash = new HashSet<int>();
            Dictionary<int, List<Guid>> guidsTree = new Dictionary<int, List<Guid>>();
            for (int i = 0; i < breps.Length; i++)
            {
                guidsTree.Add(i, new List<Guid>());
            }

            //Check input

            //Crvs
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in crvsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.PointAt(0.5);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Txt
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in TxtInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.Item1;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Points
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in ptsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value;
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Meshes
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in meshesInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        p = c.Value.Vertices[0];


                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            //Breps
            for (int i = 0; i < breps.Length; i++)
            {
                int j = 0;
                foreach (var c in brepsInput)
                {
                    if (!hash.Contains(j))
                    {
                        Point3d p = c.Value.GetBoundingBox(true).PointAt(0.5, 0.5, 0);
                        double dist = breps[i].ClosestPoint(p).DistanceTo(p);
                        if (dist < tol)
                        {
                            hash.Add(j);
                            guidsTree[i].Add(guids[c.Key]);
                        }
                    }
                    j++;
                }//foreach
            }//for

            hash.Clear();

            return new Tuple<Brep[], Dictionary<int, List<Guid>>>(breps, guidsTree);
        }

        public DataTree<T> DictioryToTree<T>(Dictionary<int, List<T>> list, int iteration = 0)
        {
            DataTree<T> tree = new DataTree<T>();

            foreach (var a in list)
            {
                tree.AddRange(a.Value, new GH_Path(a.Key));
            }
            return tree;
        }

        protected override void SolveInstance(IGH_DataAccess DA) {
            try {
                // Input
                double tol = 0.01;
                DA.GetData<double>(1, ref tol);

                GH_Structure<GH_Guid> tree;
                DA.GetDataTree(0, out tree);

                List<Guid> guids = new List<Guid>();
                foreach (GH_Guid goo in tree.AllData(true))
                    guids.Add(goo.Value);

                var data = SortGuidsByPlanarCurves(guids, tol);

                //B = breps;
                //G = guidsTree;
                DA.SetDataList(0, data.Item1);
                DA.SetDataTree(1, DictioryToTree(data.Item2));
            } catch (Exception e) {
                Rhino.RhinoApp.WriteLine(e.ToString());
            }
        }

        protected override System.Drawing.Bitmap Icon {
            get {
                return Properties.Resources.element_rhino;
            }
        }

        public override Guid ComponentGuid {
            get { return new Guid("{3278ccf2-1258-4896-ae14-1b125e122bbd}"); }
        }
    }
}