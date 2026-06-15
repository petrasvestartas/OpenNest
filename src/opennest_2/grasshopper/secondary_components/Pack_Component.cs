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
    public class Pack_Component : GH_Component {

        public Pack_Component()
          : base("Pack Objects", "Pack",
              "Lays objects out in a row, orienting each from 3D to 2D with a chosen spacing.",
              "Params", "OpenNest2") {
        }
        public override GH_Exposure Exposure => GH_Exposure.quinary;   // packing row, right after Sheets

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddGeometryParameter("Geo", "G", "Objects to pack", GH_ParamAccess.tree);
            pManager.AddPlaneParameter("Plane", "P", "Base plane to orient from 3D to 2D", GH_ParamAccess.tree);
            pManager.AddNumberParameter("X", "X", "Offset Distance, negative number will not include bbox", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("Y", "Y", "Offset Distance, negative number will not include bbox", GH_ParamAccess.item, 1);
            pManager.AddNumberParameter("tolerance", "t", "Rotates objects by t in radians until Math.Pi and takes one with min bounding box", GH_ParamAccess.item, 0);
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddGeometryParameter("Geo", "G", "Packed objects", GH_ParamAccess.tree);
            pManager.AddTransformParameter("Transformation","T","Transform applied to each object", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA) {

            GH_Structure<IGH_GeometricGoo> g = new GH_Structure<IGH_GeometricGoo>();
            GH_Structure<IGH_GeometricGoo> gT = new GH_Structure<IGH_GeometricGoo>();

            GH_Structure<GH_Plane> p = new GH_Structure<GH_Plane>();
            GH_Structure<Grasshopper.Kernel.Types.GH_Transform> transforms = new GH_Structure<Grasshopper.Kernel.Types.GH_Transform>();



            double x = 1;
            double y = 1;
            double max = 3.14;
            double t = 3.14;

           



            DA.GetDataTree<IGH_GeometricGoo>(0, out g);
            DA.GetDataTree<GH_Plane>(1, out p);
            DA.GetData(2, ref x);
            DA.GetData(3, ref y);
            DA.GetData(4, ref t);
            bool noRotation = (t == 0);
            t = 3.14;
           t = Math.Max(t, 0.01);

          

            base.Message = t.ToString();

            double tempDist2 = 0;


            for (int i = 0; i < g.PathCount; i++) {
                if (g.Branches[i].Count == p.Branches[i].Count) {
                    base.Message = null;
                    double massAddition = 0;
                    double massAdditionY = 0;
                    double tempDist = 0;

                    Transform allTransforms;

                    for (int j = 0; j < g.Branches[i].Count; j++) {

                        //Orient to xy plane
                        ITransform orientation = new Orientation(p.Branches[i][j].Value, Plane.WorldXY);
                        IGH_GeometricGoo g1 = g.Branches[i][j].DuplicateGeometry().Transform(orientation.ToMatrix());
                        IGH_GeometricGoo g1_ = g1;


                        //Rhino.RhinoApp.WriteLine(g1.Boundingbox.Area.ToString());
                        //Rhino.RhinoDoc.ActiveDoc.Objects.AddBrep(g1.Boundingbox.ToBrep());

                        allTransforms = orientation.ToMatrix();

            

                        //Move to origin
                        BoundingBox bbox = g1.Boundingbox;
                        double vol = 0.0;
                        Transform rotation_ =new Transform();
                        for (double k = 0.0; k < max; k += t) {

                            Transform rotation = (noRotation) ? Transform.Rotation(0, bbox.Center) : Transform.Rotation(k, bbox.Center);



                            IGH_GeometricGoo g1Temp = g1.DuplicateGeometry().Transform(rotation);
                            //transform *= rotation;
                            BoundingBox tempBox = g1Temp.Boundingbox;
                            Box bb;
                        
                            double tempVol = new Box(tempBox).Volume;
                            //Line[] edges = tempBox.GetEdges();

                            if (k == 0.0) {
                                vol = tempVol;
                                g1_ = g1Temp;
                                rotation_ = rotation;
                                bbox = tempBox;
                            } else {
                                if (vol > tempVol) {
                                    vol = tempVol;
                                    g1_ = g1Temp;
                                    rotation_ = rotation;
                                    bbox = tempBox;
                                }
                            }
                       }


                        allTransforms = rotation_ * allTransforms;
                        Point3d[] corners = bbox.GetCorners();


                        if (corners[1].DistanceTo(corners[0]) < corners[1].DistanceTo(corners[2])) {


                            Transform xform = (noRotation) ? Transform.Rotation(0, bbox.Center) :  Transform.Rotation(Math.PI * 0.5, bbox.Center);

                            allTransforms = xform * allTransforms;

                            g1_.Transform(xform);
                            bbox = g1_.Boundingbox;
                        }

                        corners = bbox.GetCorners();



                        Vector3d vec = Vector3d.Subtract((Vector3d)Point3d.Origin, (Vector3d)corners[3]);
                        Transform translation0 = Transform.Translation(vec);
                        g1_.Transform(translation0);
                        //transform *= translation0;
                        allTransforms = translation0 * allTransforms;


                        //g1_.Transform(Transform.Translation(new Vector3d(0, massAdditionY, 0)));

                        //Move by bbox edge length using mass addition below
                        Transform translation1 = Transform.Translation(new Vector3d(massAddition, 0, 0));
                        g1_.Transform(translation1);
                        //transform *= translation1;
                        allTransforms = translation1 * allTransforms;



                        double dist = corners[0].DistanceTo(corners[1]) + x;
                        if (x < 0) dist = -x;
                        massAddition += dist;

                        //Move for dataTreeDisplay
                        double dist2 = corners[1].DistanceTo(corners[2])  ;
                        if (y < 0) dist2 = -y;
                        //massAdditionY += dist2;

                        //tempDist = Math.Max(tempDist, dist2);
                        Transform translation2 = Transform.Translation(new Vector3d(0, -tempDist2, 0));
                        g1_.Transform(translation2);
                        //allTransforms *= translation2;
                        allTransforms = translation2 * allTransforms;


                        //Output
                        gT.Append(g1_, new GH_Path(i));
                        transforms.Append(new GH_Transform(allTransforms), new GH_Path(i));


                    }




                    tempDist2 += tempDist + y;

                } else
                   base.Message = "Number of planes \n is not equal to \n Number of Geometry";


            }
            DA.SetDataTree(0, gT);
            DA.SetDataTree(1, transforms);

        }




        protected override System.Drawing.Bitmap Icon {
            get {
                return Properties.Resources.pack_objects;
            }
        }


        public override Guid ComponentGuid {
            get { return new Guid("{3278ccf2-7220-1478-ae14-1b111e786bbd}"); }
        }


    }
}
