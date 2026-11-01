#pragma once
#include <stdint.h>
#ifdef _WIN32
# ifdef NC_BUILD
#  define NC_API __declspec(dllexport)
# else
#  define NC_API __declspec(dllimport)
# endif
#else
# define NC_API __attribute__((visibility("default")))
#endif
#ifdef __cplusplus
extern "C" {
#endif
/* Versioned, cdecl ABI. Geometry is pre-offset solve space: no implicit spacing.
   Coordinates use x,y pairs. Ring part=-1 is a stock keep-out; other indices
   address quantity/angle arrays. Each part has exactly one outer and any holes.
   Angles are radians. Outputs index input parts; each copy appears at most once.
   Missing copies are returned with sheet=-1. Reflection is local x -> -x,
   followed by rotation about origin and translation. */
typedef struct NcParams {
    int struct_size,max_sheets,beam_width,branch_parts,position_limit;
    int restarts,workers,seed,rotations,flags; /* bit 0: implicit smart angles; bit 1: conservative NFP proxies */
    double time_limit,scan_step,scale;
} NcParams;
typedef struct NcPlacement {
    int part,copy,sheet,mirrored;
    double tx,ty,angle;
} NcPlacement;
typedef struct NcStats {
    int struct_size,placed,requested,sheets,iterations,cancelled;
    int64_t nfp_hits,nfp_misses;
    double elapsed;
} NcStats;
NC_API const char* nc_version(void);
NC_API const char* nc_last_error(void);
NC_API void nc_cancel(void);
/* 0 complete, 1 valid partial/budget exhaustion, -1 invalid input/error,
   -2 insufficient output capacity, -3 another solve is active. */
NC_API int nc_solve(const double* xy,int point_count,
    const int* ring_starts,const int* ring_sizes,const int* ring_parts,const int* ring_holes,int ring_count,
    const int* quantities,const int* mirror_flags,int part_count,
    const double* angles,const int* angle_starts,const int* angle_counts,int angle_count,
    double width,double height,const NcParams* params,
    NcPlacement* output,int capacity,NcStats* stats);
#ifdef __cplusplus
}
#endif
