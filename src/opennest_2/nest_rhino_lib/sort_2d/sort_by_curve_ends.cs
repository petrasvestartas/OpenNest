using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;


namespace nest_rhino_lib.sort_2d
{
    public static class sort_by_curve_ends
    {


        private struct CurveNeighbour
        {
            public int curveId;
            public double startPtClosestDistance;
            public double endPtClosestDistance;
            public double closestDistance; //smaller values btw prevoius two
            public int startPtClosestId;
            public int endPtClosestId;
            public int startPtClosestCrvPointType; //1=start 2=end
            public int endPtClosestCrvPointType; //1=start 2=end
            public CurveNeighbour(int id)
            {
                curveId = id;
                startPtClosestDistance = double.MaxValue;
                startPtClosestId = -1;
                startPtClosestCrvPointType = -1;
                endPtClosestDistance = double.MaxValue;
                endPtClosestId = -1;
                endPtClosestCrvPointType = -1;
                closestDistance = double.MaxValue;
            }
            public void ResetStartValues()
            {
                startPtClosestDistance = double.MaxValue;
                startPtClosestId = -1;
                startPtClosestCrvPointType = -1;
                this.DetermineClosestDistance();
            }
            public void ResetEndValues()
            {
                endPtClosestDistance = double.MaxValue;
                endPtClosestId = -1;
                endPtClosestCrvPointType = -1;
                this.DetermineClosestDistance();
            }
            public void DetermineClosestDistance()
            {
                if (startPtClosestDistance < closestDistance || endPtClosestDistance < closestDistance)
                {
                    closestDistance = Math.Min(startPtClosestDistance, endPtClosestDistance);
                }
            }
            public void CompareCurve(Curve thisCurve, Curve curveToCompare, int curveToCompareId)
            {
                if (curveToCompareId == curveId) return;
                // thisCurve.PointAtStart
                var tmpDistance = thisCurve.PointAtStart.DistanceTo(curveToCompare.PointAtStart);
                if (tmpDistance < startPtClosestDistance)
                {
                    startPtClosestDistance = tmpDistance;
                    startPtClosestId = curveToCompareId;
                    startPtClosestCrvPointType = 1; //1=start
                }
                tmpDistance = thisCurve.PointAtStart.DistanceTo(curveToCompare.PointAtEnd);
                if (tmpDistance < startPtClosestDistance)
                {
                    startPtClosestDistance = tmpDistance;
                    startPtClosestId = curveToCompareId;
                    startPtClosestCrvPointType = 2; // 2=end
                }
                //thisCurve.PointAtEnd
                tmpDistance = thisCurve.PointAtEnd.DistanceTo(curveToCompare.PointAtStart);
                if (tmpDistance < endPtClosestDistance)
                {
                    endPtClosestDistance = tmpDistance;
                    endPtClosestId = curveToCompareId;
                    endPtClosestCrvPointType = 1; //1=start
                }
                tmpDistance = thisCurve.PointAtEnd.DistanceTo(curveToCompare.PointAtEnd);
                if (tmpDistance < endPtClosestDistance)
                {
                    endPtClosestDistance = tmpDistance;
                    endPtClosestId = curveToCompareId;
                    endPtClosestCrvPointType = 2; // 2=end
                }
                DetermineClosestDistance();
            }
        }


        public static List<Curve> BlendCurvesC(List<Curve> curvesToBlend, bool closeBlend)
        {
            if (curvesToBlend == null) return null;
            List<Curve> curves = new List<Curve>(curvesToBlend);
            List<Curve> ClosedCurves = new List<Curve>();
            List<Curve> blendCurves = new List<Curve>();
            List<Curve> blendSegments = new List<Curve>();
            while (true)
            {
                for (int i = curves.Count - 1; i > -1; i--)
                {
                    if (curves[i].IsClosed)
                    {
                        ClosedCurves.Add(curves[i]);
                        curves.RemoveAt(i);
                    }
                }
                blendCurves = new List<Curve>();
                if (curves.Count == 1 && closeBlend)
                {
                    Curve curveA = curves[0].DuplicateCurve();
                    Curve curveB = curves[0].DuplicateCurve();
                    var blendCurve = Curve.CreateBlendCurve(curveA, curveB, BlendContinuity.Position, 0.5, 0.5);
                    blendCurves.Add(blendCurve);
                    blendSegments.Add(blendCurve);
                }
                //we find curve with shortest distance to neighbour curve (distance btwn end points of curves)
                CurveNeighbour closestCurveNeighbour = new CurveNeighbour(-1);
                for (int i = 0; i < curves.Count - 1; i++)
                {
                    //we track curve with closest-distance to its curve-neighbour
                    CurveNeighbour curveNeighbour = new CurveNeighbour(i);
                    for (int j = i + 1; j < curves.Count; j++)
                    {
                        curveNeighbour.CompareCurve(curves[i], curves[j], j);
                    }
                    //if start POINT and end POINT points to the same start/end point of the same  closest curve
                    //  we will sert for blend/connect closest ends of curves
                    if (curveNeighbour.startPtClosestId == curveNeighbour.endPtClosestId && curveNeighbour.endPtClosestId > -1)
                    {
                        if (curveNeighbour.endPtClosestDistance > curveNeighbour.startPtClosestDistance)
                            curveNeighbour.ResetEndValues();
                        else
                            curveNeighbour.ResetStartValues();
                    }
                    if (curveNeighbour.closestDistance < closestCurveNeighbour.closestDistance)
                    {
                        closestCurveNeighbour = curveNeighbour;
                    }
                }
                //   ... we blen closest 2 curves (closest start/end points)
                if (closestCurveNeighbour.curveId > -1)
                {
                    Curve blendCurve;
                    if (closestCurveNeighbour.startPtClosestDistance < closestCurveNeighbour.endPtClosestDistance)
                    {
                        //blend from start
                        Curve curveA = curves[closestCurveNeighbour.curveId].DuplicateCurve();
                        curveA.Reverse();
                        Curve curveB = curves[closestCurveNeighbour.startPtClosestId].DuplicateCurve();
                        if (closestCurveNeighbour.startPtClosestCrvPointType == 2)
                            curveB.Reverse();
                        blendCurve = Curve.CreateBlendCurve(curveA, curveB, BlendContinuity.Position, 0.5, 0.5);
                        blendCurves.Add(blendCurve);
                    }
                    else
                    {
                        //blend from end
                        Curve curveA = curves[closestCurveNeighbour.curveId].DuplicateCurve();
                        Curve curveB = curves[closestCurveNeighbour.endPtClosestId].DuplicateCurve();
                        if (closestCurveNeighbour.endPtClosestCrvPointType == 2)
                            curveB.Reverse();
                        blendCurve = Curve.CreateBlendCurve(curveA, curveB, BlendContinuity.Position, 0.5, 0.5);
                        blendCurves.Add(blendCurve);
                    }
                }

                if (blendCurves.Count > 0)
                {
                    blendSegments.AddRange(blendCurves);
                    blendCurves.AddRange(curves);
                    curves = Curve.JoinCurves(blendCurves).ToList();
                }
                else
                {
                    break;
                }
            }
            curves.AddRange(ClosedCurves);
            //return curves;
            return blendSegments;
        }


    }
}
