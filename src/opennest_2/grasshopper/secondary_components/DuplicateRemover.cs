using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace opennest_2 {



    public class DuplicateRemover : IDisposable {

        public static int[] CreateArray(int Count, int Value) {
            int[] value = new int[checked(checked(Count - 1) + 1)];
            int count = checked(Count - 1);
            for (int i = 0; i <= count; i = checked(i + 1)) {
                value[i] = Value;
            }
            return value;
        }


        public static int[] CreateSeries(int Count) {
            int[] numArray = new int[checked(checked(Count - 1) + 1)];
            int count = checked(Count - 1);
            for (int i = 0; i <= count; i = checked(i + 1)) {
                numArray[i] = i;
            }
            return numArray;
        }

        private Point3d[] _Points;

        private int[] _ChildA;

        private int[] _ChildB;

        private int[] _Parent;

        private int[] _Map;

        private double _Tolerance;

        private int[] _SplitAxis;

        private int _RootNode;

        private double _ToleranceSq;

        private Transform _RandomTransform;

        private int _Seed;

        private Point3d[] _PointsOut;

        private bool disposedValue;

        public int[] Map {
            get {
                return this._Map;
            }
        }

        public Point3d[] UniquePoints {
            get {
                return this._PointsOut;
            }
        }

        private DuplicateRemover(int Count) {
            this._Points = null;
            this._ChildA = null;
            this._ChildB = null;
            this._Parent = null;
            this._Map = null;
            this._Tolerance = 0;
            this._SplitAxis = null;
            this._RootNode = 0;
            this._ToleranceSq = 0;
            this._RandomTransform = Transform.Identity;
            this._Seed = 123;
            this._PointsOut = null;
            this._Points = new Point3d[checked(checked(Count - 1) + 1)];
            this._ChildA = new int[checked(checked(Count - 1) + 1)];
            this._ChildB = new int[checked(checked(Count - 1) + 1)];
            this._Parent = new int[checked(checked(Count - 1) + 1)];
            this._Map = new int[checked(checked(Count - 1) + 1)];
            this._SplitAxis = new int[checked(checked(Count - 1) + 1)];
        }

        public DuplicateRemover(IEnumerable<Point3d> Points, double Tolerance = 0, int Seed = 123) {
            this._Points = null;
            this._ChildA = null;
            this._ChildB = null;
            this._Parent = null;
            this._Map = null;
            this._Tolerance = 0;
            this._SplitAxis = null;
            this._RootNode = 0;
            this._ToleranceSq = 0;
            this._RandomTransform = Transform.Identity;
            this._Seed = 123;
            this._PointsOut = null;
            this._Points = Points.ToArray<Point3d>();
            this._Tolerance = Tolerance;
            this._ToleranceSq = this._Tolerance * this._Tolerance;
            this._Seed = Seed;
            this._ChildA = new int[checked(checked(Points.Count<Point3d>() - 1) + 1)];
            this._ChildB = new int[checked(checked(Points.Count<Point3d>() - 1) + 1)];
            this._Parent = new int[checked(checked(Points.Count<Point3d>() - 1) + 1)];
            this._Map = new int[checked(checked(Points.Count<Point3d>() - 1) + 1)];
            this._SplitAxis = new int[checked(checked(Points.Count<Point3d>() - 1) + 1)];
            Random random = new Random(Seed);
            Plane plane = new Plane(Point3d.Origin, new Vector3d(random.NextDouble() - 0.5, random.NextDouble() - 0.5, random.NextDouble() - 0.5));
            this._RandomTransform = Transform.PlaneToPlane(Plane.WorldXY, plane);
            int length = checked((int)this._Points.Length - 1);
            for (int i = 0; i <= length; i = checked(i + 1)) {
                Point3d point3d = this._Points[i];
                point3d.Transform(this._RandomTransform);
                this._Points[i] = point3d;
            }
            this._ChildA = CreateArray(Points.Count<Point3d>(), -1);
            this._Map = CreateSeries(Points.Count<Point3d>());
            this._ChildA.CopyTo(this._ChildB, 0);
            this._ChildA.CopyTo(this._Parent, 0);
        }

        private bool AreSame(Point3d Point1, Point3d Point2) {
            bool flag;
            if (Math.Abs(Point1.X - Point2.X) > this._Tolerance) {
                flag = false;
            } else if (Math.Abs(Point1.Y - Point2.Y) > this._Tolerance) {
                flag = false;
            } else if (Math.Abs(Point1.Z - Point2.Z) <= this._Tolerance) {
                flag = (this.FastDistance(Point1, Point2) <= this._ToleranceSq ? true : false);
            } else {
                flag = false;
            }
            return flag;
        }

        private bool AreSame(int Id1, int Id2) {
            return this.AreSame(this._Points[Id1], this._Points[Id2]);
        }

        private void ConstructPoints() {
            Transform identity = new Transform();
            int num = 0;
            int length = checked((int)this.Map.Length - 1);
            for (int i = 0; i <= length; i = checked(i + 1)) {
                if (this.Map[i] == i) {
                    num = checked(num + 1);
                }
            }
            this._PointsOut = null;
            this._PointsOut = new Point3d[checked(checked(num - 1) + 1)];
            if (!this._RandomTransform.TryGetInverse(out identity)) {
                identity = Transform.Identity;
            }
            num = 0;
            int[] numArray = new int[checked(checked((int)this._Points.Length - 1) + 1)];
            int[] numArray1 = new int[checked(checked((int)this._Points.Length - 1) + 1)];
            int length1 = checked((int)this.Map.Length - 1);
            for (int j = 0; j <= length1; j = checked(j + 1)) {
                if (this.Map[j] == j) {
                    this._PointsOut[num] = this._Points[this.Map[j]];
                    this._PointsOut[num].Transform(identity);
                    numArray1[this.Map[j]] = num;
                    num = checked(num + 1);
                }
            }
            int num1 = checked((int)numArray.Length - 1);
            for (int k = 0; k <= num1; k = checked(k + 1)) {
                numArray[k] = numArray1[this.Map[k]];
            }
            this._Map = null;
            this._Map = numArray;
        }

        protected virtual void Dispose(bool disposing) {
            if (!this.disposedValue) {
                this._Points = null;
                this._ChildA = null;
                this._ChildB = null;
                this._Parent = null;
                this._Map = null;
                this._SplitAxis = null;
                this._PointsOut = null;
            }
            this.disposedValue = true;
        }

        public void Dispose() {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }

        public DuplicateRemover Duplicate() {
            DuplicateRemover duplicateRemover = new DuplicateRemover((int)this._Points.Length);
            this._ChildA.CopyTo(duplicateRemover._ChildA, 0);
            this._ChildB.CopyTo(duplicateRemover._ChildB, 0);
            this._Map.CopyTo(duplicateRemover._Map, 0);
            this._Parent.CopyTo(duplicateRemover._Parent, 0);
            this._Points.CopyTo(duplicateRemover._Points, 0);
            this._SplitAxis.CopyTo(duplicateRemover._SplitAxis, 0);
            duplicateRemover._Tolerance = this._Tolerance;
            return duplicateRemover;
        }

        private double FastDistance(Point3d p1, Point3d p2) {
            return (p2.X - p1.X) * (p2.X - p1.X) + (p2.Y - p1.Y) * (p2.Y - p1.Y) + (p2.Z - p1.Z) * (p2.Z - p1.Z);
        }

        private int IncrementAxis(int Axis) {
            return (checked(Axis + 1)) % 3;
        }

        private bool InsertNode(int NodeId, int ParentId, bool AsNodeA) {
            bool asNodeA = AsNodeA;
            if (!asNodeA) {
                this._ChildB[ParentId] = NodeId;
            } else if (asNodeA) {
                this._ChildA[ParentId] = NodeId;
            }
            this._Parent[NodeId] = ParentId;
            this._SplitAxis[NodeId] = this.IncrementAxis(this._SplitAxis[ParentId]);
            return true;
        }

        private bool Merge(int NodeId, int ParentId) {
            this._Map[NodeId] = ParentId;
            return true;
        }

        public void Solve() {
            Random random = new Random(this._Seed);
            double[] numArray = new double[checked(checked((int)this._Points.Length - 1) + 1)];
            int[] numArray1 = CreateSeries((int)numArray.Length);
            int length = checked((int)numArray.Length - 1);
            for (int i = 0; i <= length; i = checked(i + 1)) {
                numArray[i] = random.NextDouble();
            }
            Array.Sort<double, int>(numArray, numArray1);
            int num = checked((int)numArray1.Length - 1);
            for (int j = 0; j <= num; j = checked(j + 1)) {
                if (numArray1[j] != this._RootNode) {
                    this.SolveOne(numArray1[j], this._RootNode, true);
                }
            }
            this.ConstructPoints();
        }

        private DuplicateRemover.Result SolveOne(int NodeId, int ParentId, bool Insert = true) {
            DuplicateRemover.Result result;
            double x = 0;
            switch (this._SplitAxis[ParentId]) {
                case 0: {
                        x = this._Points[NodeId].X - this._Points[ParentId].X;
                        break;
                    }
                case 1: {
                        x = this._Points[NodeId].Y - this._Points[ParentId].Y;
                        break;
                    }
                case 2: {
                        x = this._Points[NodeId].Z - this._Points[ParentId].Z;
                        break;
                    }
            }
            double num = Math.Abs(x);
            if (num <= this._Tolerance) {
                if (!this.AreSame(NodeId, ParentId)) {
                    DuplicateRemover.Result result1 = DuplicateRemover.Result.EmptySpace;
                    DuplicateRemover.Result result2 = DuplicateRemover.Result.EmptySpace;
                    if (this._ChildA[ParentId] != -1) {
                        result1 = this.SolveOne(NodeId, this._ChildA[ParentId], false);
                    }
                    if (this._ChildB[ParentId] != -1) {
                        result2 = this.SolveOne(NodeId, this._ChildB[ParentId], false);
                    }
                    if (result1 == DuplicateRemover.Result.FoundSame | result2 == DuplicateRemover.Result.FoundSame) {
                        result = DuplicateRemover.Result.FoundSame;
                    } else if (x <= 0) {
                        if (result1 == DuplicateRemover.Result.EmptySpace) {
                            if (this._ChildA[ParentId] == -1) {
                                if (Insert) {
                                    this.InsertNode(NodeId, ParentId, true);
                                }
                                result = DuplicateRemover.Result.CanInsert;
                            } else {
                                result = this.SolveOne(NodeId, this._ChildA[ParentId], Insert);
                            }
                        } else if (result2 == DuplicateRemover.Result.EmptySpace) {
                            if (this._ChildB[ParentId] == -1) {
                                if (Insert) {
                                    this.InsertNode(NodeId, ParentId, false);
                                }
                                result = DuplicateRemover.Result.CanInsert;
                            } else {
                                result = this.SolveOne(NodeId, this._ChildB[ParentId], Insert);
                            }
                        } else if (result1 != DuplicateRemover.Result.CanInsert) {
                            if (result2 != DuplicateRemover.Result.CanInsert) {
                                result = DuplicateRemover.Result.Failed;
                                return result;
                            }
                            if (this._ChildB[ParentId] == -1) {
                                if (Insert) {
                                    this.InsertNode(NodeId, ParentId, false);
                                }
                                result = DuplicateRemover.Result.CanInsert;
                            } else {
                                result = this.SolveOne(NodeId, this._ChildB[ParentId], Insert);
                            }
                        } else if (this._ChildA[ParentId] == -1) {
                            if (Insert) {
                                this.InsertNode(NodeId, ParentId, true);
                            }
                            result = DuplicateRemover.Result.CanInsert;
                        } else {
                            result = this.SolveOne(NodeId, this._ChildA[ParentId], Insert);
                        }
                    } else if (result2 == DuplicateRemover.Result.EmptySpace) {
                        if (this._ChildB[ParentId] == -1) {
                            if (Insert) {
                                this.InsertNode(NodeId, ParentId, false);
                            }
                            result = DuplicateRemover.Result.CanInsert;
                        } else {
                            result = this.SolveOne(NodeId, this._ChildB[ParentId], Insert);
                        }
                    } else if (result1 == DuplicateRemover.Result.EmptySpace) {
                        if (this._ChildA[ParentId] == -1) {
                            if (Insert) {
                                this.InsertNode(NodeId, ParentId, true);
                            }
                            result = DuplicateRemover.Result.CanInsert;
                        } else {
                            result = this.SolveOne(NodeId, this._ChildA[ParentId], Insert);
                        }
                    } else if (result2 != DuplicateRemover.Result.CanInsert) {
                        if (result1 != DuplicateRemover.Result.CanInsert) {
                            result = DuplicateRemover.Result.Failed;
                            return result;
                        }
                        if (this._ChildA[ParentId] == -1) {
                            if (Insert) {
                                this.InsertNode(NodeId, ParentId, true);
                            }
                            result = DuplicateRemover.Result.CanInsert;
                        } else {
                            result = this.SolveOne(NodeId, this._ChildA[ParentId], Insert);
                        }
                    } else if (this._ChildB[ParentId] == -1) {
                        if (Insert) {
                            this.InsertNode(NodeId, ParentId, false);
                        }
                        result = DuplicateRemover.Result.CanInsert;
                    } else {
                        result = this.SolveOne(NodeId, this._ChildB[ParentId], Insert);
                    }
                } else {
                    this.Merge(NodeId, ParentId);
                    result = DuplicateRemover.Result.FoundSame;
                }
            } else if (num > this._Tolerance) {
                double num1 = x;
                if (num1 > 0) {
                    if (num1 <= 0) {
                        result = DuplicateRemover.Result.Failed;
                        return result;
                    }
                    if (this._ChildB[ParentId] != -1) {
                        result = this.SolveOne(NodeId, this._ChildB[ParentId], Insert);
                    } else {
                        if (Insert) {
                            this.InsertNode(NodeId, ParentId, false);
                        }
                        result = DuplicateRemover.Result.CanInsert;
                    }
                } else if (this._ChildA[ParentId] != -1) {
                    result = this.SolveOne(NodeId, this._ChildA[ParentId], Insert);
                } else {
                    if (Insert) {
                        this.InsertNode(NodeId, ParentId, true);
                    }
                    result = DuplicateRemover.Result.CanInsert;
                }
            } else {
                result = DuplicateRemover.Result.Failed;
                return result;
            }
            return result;
        }

        private enum Result {
            Failed = -1,
            EmptySpace = 0,
            CanInsert = 1,
            FoundSame = 2
        }
    }
}
