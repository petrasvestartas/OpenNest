using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace opennest_2
{
    public class Component_RegionSlits : GH_Component
    {
        public override Guid ComponentGuid
        {
            get
            {
                return new Guid("{0FEEEACA-8F1F-4D7C-A24A-8E7DD21334A2}");
            }
        }


        protected override Bitmap Icon
        {
            get
            {
                return Properties.Resources.region_slits;
            }
        }

        public override GH_Exposure Exposure => GH_Exposure.octonary;

        public Component_RegionSlits() : base("Region Slits", "RSlits", "Cuts interlocking slits where planar regions meet (a fixed version of the native component).", "Params", "OpenNest2")
        {
        }

        protected override void RegisterInputParams(GH_Component.GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter("Regions", "R", "Planar regions to intersect", GH_ParamAccess.list);
            pManager.AddNumberParameter("Width", "W", "Width of slits", 0);
            pManager.AddNumberParameter("Gap", "G", "Additional gap size at slit meeting points", 0, 0);
        }

        protected override void RegisterOutputParams(GH_Component.GH_OutputParamManager pManager)
        {
            pManager.AddSurfaceParameter("Regions", "R", "Regions with slits", GH_ParamAccess.tree);
            pManager.AddIntegerParameter("Topology", "T", "Slit topology", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Line line;
            Line line1;
            List<Curve> curves = new List<Curve>();
            double num = 0;
            double num1 = 0;
            if (!DA.GetDataList<Curve>(0, curves))
            {
                return;
            }
            if (!DA.GetData<double>(1, ref num))
            {
                return;
            }
            if (!DA.GetData<double>(2, ref num1))
            {
                return;
            }
            List<Component_RegionSlits.Slab> slabs = new List<Component_RegionSlits.Slab>();
            int count = checked(curves.Count - 1);
            for (int i = 0; i <= count; i++)
            {
                if (curves[i] != null)
                {
                    try
                    {
                        slabs.Add(new Component_RegionSlits.Slab(i, curves[i]));
                    }
                    catch (Exception exception1)
                    {
                        //ProjectData.SetProjectError(exception1);
                        Exception exception = exception1;
                        slabs.Add(null);
                        this.AddRuntimeMessage(GH_RuntimeMessageLevel.Error, exception.Message);
                        
                        //ProjectData.ClearProjectError();
                    }
                }
                else
                {
                    slabs.Add(null);
                }
            }
            GH_Structure<GH_Surface> gHStructure = new GH_Structure<GH_Surface>();
            GH_Structure<GH_Integer> gHStructure1 = new GH_Structure<GH_Integer>();
            GH_Path gHPath = DA.ParameterTargetPath(0);
            GH_Path gHPath1 = DA.ParameterTargetPath(1);
            int count1 = checked(slabs.Count - 2);
            for (int j = 0; j <= count1; j++)
            {
                int num2 = checked(j + 1);
                int count2 = checked(slabs.Count - 1);
                for (int k = num2; k <= count2; k++)
                {
                    Component_RegionSlits.Slab item = slabs[j];
                    Component_RegionSlits.Slab slab = slabs[k];
                    if (item != null)
                    {
                        if (slab != null)
                        {
                            if (Component_RegionSlits.Slab.Intersect(item, slab, out line, out line1))
                            {
                                if (line.IsValid)
                                {
                                    item.Cutters.Add(line);
                                }
                                if (line1.IsValid)
                                {
                                    slab.Cutters.Add(line1);
                                }
                            }
                        }
                    }
                }
            }
            int count3 = checked(slabs.Count - 1);
            for (int l = 0; l <= count3; l++)
            {
                GH_Path gHPath2 = gHPath.AppendElement(l);
                GH_Path gHPath3 = gHPath1.AppendElement(l);
                gHStructure.EnsurePath(gHPath2);
                gHStructure1.EnsurePath(gHPath3);
                Component_RegionSlits.Slab item1 = slabs[l];
                if (item1 != null)
                {
                    Brep[] brep = item1.ToBrep(num, num1);
                    List<GH_Surface> gHSurfaces = new List<GH_Surface>();
                    if (brep != null && checked((int)brep.Length) > 0)
                    {
                        Brep[] brepArray = brep;
                        for (int m = 0; m < checked((int)brepArray.Length); m++)
                        {
                            Brep brep1 = brepArray[m];
                            if (brep1 != null)
                            {
                                gHSurfaces.Add(new GH_Surface(brep1));
                            }
                            else
                            {
                                gHSurfaces.Add(null);
                            }
                        }
                    }
                    gHStructure.AppendRange(gHSurfaces, gHPath2);
                    gHStructure1.Append(null, gHPath3);
                }
            }
            DA.SetDataTree(0, gHStructure);
            DA.SetDataTree(1, gHStructure1);
        }

        public class Slab
        {
            public readonly int Index;

            public readonly Curve Region;

            public readonly Plane Plane;

            public readonly BoundingBox Bounds;

            public readonly List<Line> Cutters;

            public double Diagonal
            {
                get
                {
                    return this.Bounds.Diagonal.Length;
                }
            }

            public Slab(int index, Curve shape)
            {

                Point3d[] divisionPoints;
                shape.DivideByCount(10, false, out divisionPoints);

                Plane planeTemp;
                Plane.FitPlaneToPoints(divisionPoints.ToList(), out planeTemp);

                shape = Curve.ProjectToPlane(shape, planeTemp);

                this.Cutters = new List<Line>();
                this.Index = index;
                this.Region = shape;
                this.Bounds = shape.GetBoundingBox(true);
                this.Plane = planeTemp;
                
                //if (flag)
                  //   throw new ArgumentException("try get polyline");

                if (!this.Plane.IsValid)
                    throw new ArgumentException("shape must be a planar region");
            }

            public static bool Intersect(Component_RegionSlits.Slab P, Component_RegionSlits.Slab Q, out Line cutP, out Line cutQ)
            {
                Line line = new Line();
                IEnumerator<IntersectionEvent> enumerator = null;
                Interval overlapA;
                IEnumerator<IntersectionEvent> enumerator1 = null;
                cutP = Line.Unset;
                cutQ = Line.Unset;
                if (P == null)
                {
                    return false;
                }
                if (Q == null)
                {
                    return false;
                }
                if (P.Index == Q.Index)
                {
                    return false;
                }
                if (!BoundingBox.Intersection(P.Bounds, Q.Bounds).IsValid)
                {
                    return false;
                }
                if (!Intersection.PlanePlane(P.Plane, Q.Plane, out line))
                {
                    return false;
                }
                BoundingBox boundingBox = BoundingBox.Union(P.Bounds, Q.Bounds);
                boundingBox.Inflate(1);
                if (!line.ExtendThroughBox(boundingBox))
                {
                    return false;
                }
                LineCurve lineCurve = new LineCurve(line);
                double num = 1E-06;
                CurveIntersections curveIntersection = Intersection.CurveCurve(lineCurve, P.Region, num, 2 * num);
                CurveIntersections curveIntersection1 = Intersection.CurveCurve(lineCurve, Q.Region, num, 2 * num);
                if (curveIntersection == null || curveIntersection1 == null)
                {
                    return false;
                }
                if (curveIntersection.Count == 0 || curveIntersection1.Count == 0)
                {
                    return false;
                }
                double num1 = double.MaxValue;
                double num2 = double.MinValue;
                double num3 = double.MaxValue;
                //using (double num4 = double.MinValue)
                // {
                double num4 = double.MinValue;
                    enumerator = curveIntersection.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        IntersectionEvent current = enumerator.Current;
                        Interval interval = current.OverlapA;
                        num1 = Math.Min(num1, interval.Min);
                        overlapA = current.OverlapA;
                        num2 = Math.Max(num2, overlapA.Max);
                    }
               // }
                using (enumerator1)
                {
                    enumerator1 = curveIntersection1.GetEnumerator();
                    while (enumerator1.MoveNext())
                    {
                        IntersectionEvent intersectionEvent = enumerator1.Current;
                        overlapA = intersectionEvent.OverlapA;
                        num3 = Math.Min(num3, overlapA.Min);
                        overlapA = intersectionEvent.OverlapA;
                        num4 = Math.Max(num4, overlapA.Max);
                    }
                }
                if (num1 > num4)
                {
                    return false;
                }
                if (num3 > num2)
                {
                    return false;
                }
                line = new Line(lineCurve.PointAt(Math.Max(num1, num3)), lineCurve.PointAt(Math.Min(num2, num4)));
                line.Flip();
                Point3d from = line.From;
                Point3d to = line.To;
                Point3d point3d = line.PointAt(0.5);
                Vector3d unitTangent = line.UnitTangent;
                double diagonal = P.Diagonal;
                double diagonal1 = Q.Diagonal;
                bool flag = num1 <= num3;
                bool flag1 = num3 <= num1;
                bool flag2 = num2 >= num4;
                bool flag3 = num4 >= num2;
                if (flag && flag2 && flag1 && flag3)
                {
                    cutP = new Line(point3d, point3d + (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d - (diagonal1 * unitTangent));
                    return true;
                }
                if (flag && flag2 && flag1)
                {
                    cutP = new Line(point3d, point3d + (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d - (diagonal1 * unitTangent));
                    return true;
                }
                if (flag && flag2 && flag3)
                {
                    cutP = new Line(point3d, point3d - (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d + (diagonal1 * unitTangent));
                    return true;
                }
                if (flag1 && flag3 && flag)
                {
                    cutP = new Line(point3d, point3d - (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d + (diagonal1 * unitTangent));
                    return true;
                }
                if (flag1 && flag3 && flag2)
                {
                    cutP = new Line(point3d, point3d + (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d - (diagonal1 * unitTangent));
                    return true;
                }
                if (flag1 && flag2)
                {
                    cutP = new Line(point3d, point3d + (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d - (diagonal1 * unitTangent));
                    return true;
                }
                if (flag && flag3)
                {
                    cutP = new Line(point3d, point3d - (diagonal * unitTangent));
                    cutQ = new Line(point3d, point3d + (diagonal1 * unitTangent));
                    return true;
                }
                if (flag && flag2)
                {
                    cutP = new Line(from, to);
                    cutQ = Line.Unset;
                    return true;
                }
                if (!flag1 || !flag3)
                {
                    return false;
                }
                cutP = Line.Unset;
                cutQ = new Line(from, to);
                return true;
            }

            public Brep[] ToBrep(double slitWidth, double additionalTolerance)
            {
                List<Line>.Enumerator enumerator = new List<Line>.Enumerator();
                List<Curve>.Enumerator enumerator1 = new List<Curve>.Enumerator();
                List<Curve> curves = new List<Curve>()
                {
                    this.Region
                };
                try
                {
                    enumerator = this.Cutters.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        Line current = enumerator.Current;
                        Point3d from = current.From;
                        Vector3d unitTangent = current.UnitTangent;
                        Vector3d zAxis = this.Plane.ZAxis;
                        Vector3d vector3d = Vector3d.CrossProduct(unitTangent, zAxis);
                        Plane plane = new Plane(from, unitTangent, vector3d);
                        Interval interval = new Interval(0 - additionalTolerance, current.Length + additionalTolerance);
                        Interval interval1 = new Interval(-0.5 * slitWidth, 0.5 * slitWidth);
                        Rectangle3d rectangle3d = new Rectangle3d(plane, interval, interval1);
                        PolylineCurve polylineCurve = new PolylineCurve(rectangle3d.ToPolyline());
                        List<Curve> curves1 = new List<Curve>();
                        try
                        {
                            enumerator1 = curves.GetEnumerator();
                            while (enumerator1.MoveNext())
                            {
                                curves1.AddRange(Curve.CreateBooleanDifference(enumerator1.Current, polylineCurve));
                            }
                        }
                        finally
                        {
                            ((IDisposable)enumerator1).Dispose();
                        }
                        curves = curves1;
                    }
                }
                finally
                {
                    ((IDisposable)enumerator).Dispose();
                }
                return Brep.CreatePlanarBreps(curves);
            }
        }
    }
}