using System.Runtime.InteropServices;

namespace Minkowski
{
    public class MinkowskiWrapper
    {
#if WINDOWS
        private const string LIBRARY_NAME = "minkowski.dll";
#elif MAC
        private const string LIBRARY_NAME = "minkowski.dylib";
#else
        private const string LIBRARY_NAME = "minkowski";
#endif

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void setData(int cntA, double[] pntsA, int holesCnt, int[] holesSizes, double[] holesPoints, int cntB, double[] pntsB);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void calculateNFP();

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void getSizes1(int[] sizes);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void getSizes2(int[] sizes1, int[] sizes2);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void getResults(double[] data, double[] holesData);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void compute_pca(double[] points, int numPoints, double[] output);

        [DllImport(LIBRARY_NAME, CallingConvention = CallingConvention.Cdecl)]
        public static extern void pinvoke_compute_oriented_box(int pointCount, double[] points, double[] orientedBoxPoints);


    }
}
