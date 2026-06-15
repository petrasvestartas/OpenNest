using System;
using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.Geometry;
using Grasshopper.Kernel.Types;
using Sharp3DBinPacking;

namespace opennest_2
{
    public class BinPacking : GH_Component {
 
        public BinPacking()
          : base("Box Packing", "PackBox",
              "Packs 3D boxes into a container.",
              "Params", "OpenNest2") {
        }
        public override GH_Exposure Exposure => GH_Exposure.quinary;   // packing row, right after Sheets

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager) {
            pManager.AddGeometryParameter("Con", "C", "Container to pack into", GH_ParamAccess.list);
            pManager.AddGeometryParameter("Geo", "G", "Geometry to pack", GH_ParamAccess.list);
        }


        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager) {
            pManager.AddGeometryParameter("Geo", "G", "Packed geometry", GH_ParamAccess.list);
            pManager.AddTransformParameter("Tra", "T", "Packing transforms", GH_ParamAccess.list);
        }


        protected override void SolveInstance(IGH_DataAccess DA) {
            try {
                //Input
                var gooContainers = new List<IGH_GeometricGoo>();
                var gooGeometry = new List<IGH_GeometricGoo>();

                DA.GetDataList(0, gooContainers);//Gui to get items
                DA.GetDataList(1, gooGeometry);//Gui to get containers


                bool[] nested = new bool[gooGeometry.Count];
                Transform[] xforms = new Transform[gooGeometry.Count];
                for (int i = 0; i < xforms.Length; i++)
                    xforms[i] = Transform.Identity;
                Box[] b0 = new Box[gooGeometry.Count];

                if (gooContainers.Count > 0 && gooGeometry.Count > 0) {


                    for (int k = 0; k < gooContainers.Count; k++) {
                        //List<Box> b0 = new List<Box>();

                        double[] b0_U = new double[gooGeometry.Count];
                        double[] b0_V = new double[gooGeometry.Count];
                        Point3d[] b0_C = new Point3d[gooGeometry.Count];

                        // Define the size of bin
                        var cuboids = new Cuboid[gooGeometry.Count];
                        decimal[] size = GooToBox(gooContainers[k]);

                        #region geometry to nest
                        for (int i = 0; i < gooGeometry.Count; i++) {
                            if (nested[i]) continue;

                            BoundingBox bbox = gooGeometry[i].Boundingbox;

                            if (bbox.IsDegenerate(0.1) != 0) {
                                bbox.Union(bbox.PointAt(0, 0, 0) + new Point3d(0, 0, 1));
                            }

                            Box box = new Box(bbox);

                            Box boxPlanar = new Box(bbox);
                            boxPlanar.Transform(Rhino.Geometry.Transform.PlanarProjection(Plane.WorldXY));
                            decimal d0 = (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(1, 0, 0));
                            decimal d1 = (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(0, 1, 0));
                         

                            b0[i] = box;

                            if ((d0 > size[3]) || (d1 > size[4]))
                                continue;
                            cuboids[i] = (new Cuboid((decimal)box.X.Length, (decimal)box.Z.Length, (decimal)box.Y.Length, (decimal)box.Volume, i));

                            b0_U[i] = (box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(0, 1, 0)));
                            b0_V[i] = (box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(1, 0, 0)));
                            b0_C[i] = (box.Center);
                        }

                        #endregion

                        var transforms = new List<Transform>(b0.Length);

                        List<Cuboid> cuboidsClean = new List<Cuboid>(cuboids.Length);
                        for (int i = 0; i < cuboids.Length; i++) {
                            if (cuboids[i] != null) {
                                cuboidsClean.Add(cuboids[i]);
                            }
                        }

                        // Define the cuboids to pack
                        var parameter = new BinPackParameter(size[0], size[1], size[2], (decimal)1000000000, false, cuboidsClean);
                        var binPacker = BinPacker.GetDefault(BinPackerVerifyOption.BestOnly);

                        var result = binPacker.Pack(parameter);
                        int counter = 0;
                        double dist = 0;

                        foreach (IList<Cuboid> bins in result.BestResult) {
                            foreach (Cuboid cuboid in bins) {
                                b0[(int)cuboid.Tag] = (new Box(new Plane(new Point3d((double)cuboid.X + dist, (double)cuboid.Z, (double)cuboid.Y), Vector3d.ZAxis), new Interval(0, (double)cuboid.Width),
                               new Interval(0, (double)cuboid.Depth),
                               new Interval(0, (double)cuboid.Height)
                               ));
                                nested[(int)cuboid.Tag] = true;
                            }
                            //dist += binWidths[counter % binWidths.Count];
                            break;
                            counter++;
                        }




                        for (int i = 0; i < b0.Length; i++) {

                            if (cuboids[i] == null || !nested[i]) continue;

                            Box box = new Box(new BoundingBox(b0[i].GetCorners()));
                            Point3d center = box.Center;
                            double distU = box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(0, 1, 0));
                            double distV = box.PointAt(0, 0, 0).DistanceToSquared(box.PointAt(1, 0, 0));

                            Point3d center_ = b0_C[i];
                            double distU_ = b0_U[i];
                            double distV_ = b0_V[i];

                            Vector3d v = center - center_;

                            xforms[i] = Transform.Identity;
                            xforms[i] *= Transform.Translation(new Vector3d((double)size[5], (double)size[6], (double)size[7]));

                            // box is rotated after packing
                            if (Math.Abs(distU - distU_) > Math.Abs(distV - distU_))
                            {
                        
                                
                                xforms[i] *= (Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, center));
                                xforms[i] *= (Transform.Translation(v));

                                //center = Point3d.Origin;
                                ////
                                //xforms[i] *= (Transform.Rotation(Math.PI * 0.5, Vector3d.ZAxis, center));// * Transform.Translation(v)); 
                                //Vector3d vv = new Vector3d(v);
                                //vv.Transform(Transform.Rotation(-Math.PI * 0.5, Vector3d.ZAxis, center));
                                //xforms[i] *= (Transform.Translation(vv));

                            }
                            // box is not rotated
                            else
                            {

                               
                                xforms[i] *= (Transform.Translation(v));

                            }
                            b0[i].Transform(Transform.Translation(new Vector3d((double)size[5], (double)size[6], (double)size[7])));
                            //xforms[i] *= (Transform.Translation(v));
                            //xforms[i] *= (Transform.Translation(new Vector3d((double)size[5], (double)size[6], (double)size[7])));

                            //Translate elements to zero point

                        }


                    }


                    
                    //Output
                    DA.SetDataList(0, b0);
                    DA.SetDataList(1, xforms);





                }//if there are more than one object
            }catch(Exception e) {
                Rhino.RhinoApp.WriteLine(e.ToString());
            }

        }


        public decimal[] GooToBox(IGH_GeometricGoo gooContainer) {

            BoundingBox bbox = gooContainer.Boundingbox;
            if (bbox.IsDegenerate(0.01) != 0) {
                bbox.Union(bbox.PointAt(0, 0, 0) + new Point3d(0, 0, 1.1));
            }

            Box box = new Box(bbox);
            Box boxPlanar = new Box(bbox);
            boxPlanar.Transform(Rhino.Geometry.Transform.PlanarProjection(Plane.WorldXY));

       

            return new decimal[] { (decimal)box.X.Length, (decimal)box.Z.Length, (decimal)box.Y.Length,
                (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(1, 0, 0)),
                (decimal)boxPlanar.PointAt(0, 0, 0).DistanceTo(boxPlanar.PointAt(0, 1, 0)),
                (decimal)box.PointAt(0,0,0).X ,
                (decimal)box.PointAt(0,0,0).Y ,
                (decimal)box.PointAt(0,0,0).Z 

            };
        }

        protected override System.Drawing.Bitmap Icon {
            get {
                return Properties.Resources.packing_boxes;
            }
        }


        public override Guid ComponentGuid {
            get { return new Guid("9bcf7312-b1fb-4a6e-8b4e-5c213154771c"); }
        }
    }
}