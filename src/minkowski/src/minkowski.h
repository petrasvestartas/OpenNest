#include <iostream>
#include <boost/polygon/polygon.hpp>

// Define EXPORT macro to handle cross-platform export
#if defined(_WIN32) || defined(_WIN64)
  #define EXPORT __declspec(dllexport)
#else
  #define EXPORT
#endif

extern "C" {
    EXPORT void setData(int cntA, double* pntsA, int holesCnt, int* holesSizes, double* holesPoints, int cntB, double* pntsB);
    EXPORT void getSizes1(int* sizes);
    EXPORT void getSizes2(int* sizes1, int* sizes2);
    EXPORT void getResults(double* data, double* holesData);
    EXPORT void calculateNFP();
}
