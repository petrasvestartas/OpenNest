using Rhino.Collections;
using Rhino.Geometry;
using Rhino.Runtime;
using System;
using System.Collections.Generic;

namespace nest_rhino_lib
{
    public static class curve_explode
    {
        public static Curve[] GetSegments(Curve crv, bool recursive = true, int types_1_or_2 = 1)
        {
            List<Curve> curves = new List<Curve>();
            if (curve_explode.MakeCurveSegments(ref curves, crv, recursive))
            {
            }
            return curves.ToArray();
        }

        private static bool MakeCurveSegments(ref List<Curve> cList, Curve crv, bool recursive)
        {
            Polyline polyline = null;
            double num = 0;
            bool flag;
            PolyCurve polyCurve = crv as PolyCurve;
            if (polyCurve == null)
            {
                PolylineCurve polylineCurve = crv as PolylineCurve;
                if (polylineCurve != null)
                {
                    if (!recursive)
                    {
                        cList.Add(polylineCurve.DuplicateCurve());
                    }
                    else
                    {
                        for (int i = 0; i < polylineCurve.PointCount - 1; i++)
                        {
                            cList.Add(new LineCurve(polylineCurve.Point(i), polylineCurve.Point(i + 1)));
                        }
                    }
                    flag = true;
                }
                else if (!crv.TryGetPolyline(out polyline))
                {
                    LineCurve lineCurve = crv as LineCurve;
                    if (lineCurve == null)
                    {
                        ArcCurve arcCurve = crv as ArcCurve;
                        if (arcCurve == null)
                        {
                            NurbsCurve nurbsCurve = crv.ToNurbsCurve();
                            if (nurbsCurve != null)
                            {
                                double min = nurbsCurve.Domain.Min;
                                double max = nurbsCurve.Domain.Max;
                                int count = cList.Count;
                                
                                while (nurbsCurve.GetNextDiscontinuity(Continuity.C1_locus_continuous, min, max, out num))
                                {
                                    Interval interval = new Interval(min, num);
                                    if (interval.Length >= 1E-10)
                                    {
                                        Curve curve = nurbsCurve.DuplicateCurve();
                                        curve = curve.Trim(interval);
                                        if (curve.IsValid)
                                        {
                                            cList.Add(curve);
                                        }
                                        min = num;
                                    }
                                    else
                                    {
                                        min = num;
                                    }
                                }
                                if (cList.Count == count)
                                {
                                    cList.Add(nurbsCurve);
                                }
                                flag = true;
                            }
                            else
                            {
                                flag = false;
                            }
                        }
                        else
                        {
                            cList.Add(arcCurve.DuplicateCurve());
                            flag = true;
                        }
                    }
                    else
                    {
                        cList.Add(lineCurve.DuplicateCurve());
                        flag = true;
                    }
                }
                else
                {
                    if (!recursive)
                    {
                        cList.Add(new PolylineCurve(polyline));
                    }
                    else
                    {
                        for (int j = 0; j < polyline.Count - 1; j++)
                        {
                            cList.Add(new LineCurve(polyline[j], polyline[j + 1]));
                        }
                    }
                    flag = true;
                }
            }
            else
            {
                if (recursive)
                {
                    polyCurve.RemoveNesting();
                }
                Curve[] curveArray = polyCurve.Explode();
                if ((curveArray == null ? false : curveArray.Length != 0))
                {
                    if (!recursive)
                    {
                        Curve[] curveArray1 = curveArray;
                        for (int k = 0; k < (int)curveArray1.Length; k++)
                        {
                            cList.Add(curveArray1[k].DuplicateShallow() as Curve);
                        }
                    }
                    else
                    {
                        Curve[] curveArray2 = curveArray;
                        for (int l = 0; l < (int)curveArray2.Length; l++)
                        {
                            curve_explode.MakeCurveSegments(ref cList, curveArray2[l], recursive);
                        }
                    }
                    flag = true;
                }
                else
                {
                    flag = false;
                }
            }
            return flag;
        }
    }
}