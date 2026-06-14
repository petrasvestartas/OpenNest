# nest_spectral — 3D mesh-mesh nesting (spectral / FFT packing, CPU)

A native OpenNest engine for packing **3D triangle meshes** into a container, using the MIT/Inkbit
**Spectral Packing** method (SIGGRAPH 2023): voxelize each part, find collision-free placements by
**FFT cross-correlation**, score them by proximity to existing geometry plus a height penalty, and place
greedily largest-first. It is a CPU port of [Vrroom/psacking](https://github.com/Vrroom/psacking) (MIT) —
the cuFFT core is replaced by header-only [pocketfft](https://github.com/mreineck/pocketfft) (BSD-3), so
`nest_spectral` runs anywhere the other OpenNest engines do, **with no GPU/CUDA requirement**.

## How it works

1. **Voxelize** (`spectral/voxelize.hpp`) — each mesh → a binary voxel grid at a shared pitch
   (`pitch = max(container) / voxel_resolution`), via z-axis scanline parity fill.
2. **Distance field** (`spectral/distance.hpp`) — L1 distance from every empty tray cell to the nearest
   occupied cell (separable min-sweeps); correlating it with a part rewards snug placements.
3. **Spectral search** (`spectral/fft.hpp`, `spectral/packer.hpp`) — for each part, try a set of 90°
   cube orientations; the **collision count** at every candidate offset is the cross-correlation of the
   tray with the part (one FFT pair), and the **proximity score** is the cross-correlation of the
   distance field with the part. The lowest-scoring collision-free cell wins; score adds `P·(z/Lz)³`
   so parts settle toward the floor.
4. **Place** and repeat (greedy, biggest part first).

The placement (voxel position + cube orientation) is mapped back to a rigid world pose `world = R·p + t`.

## Build

```
cmake -S . -B build -A x64
cmake --build build --config Release
# -> build/Release/nest_spectral.dll   (Windows)
#    build/libnest_spectral.dylib       (Unix)
```

MinGW one-liner (what the plugin needs, self-contained):

```
g++ -std=c++17 -O3 -pthread -static -static-libgcc -static-libstdc++ -shared \
    -I. -o nest_spectral.dll nest_spectral_capi.cpp
```

Optional self-tests (`-DNEST_SPECTRAL_BUILD_CLI=ON` builds the CLI):

```
nest_spectral --meshtest                       # voxelizer + mesh→pack sanity
nest_spectral --tray 32 --items 24 --orient 24 # synthetic packing + density
```

## C ABI

One stateless entry point, `nest_spectral(...)` (see `nest_spectral_capi.h`), P/Invokable exactly like
`np_nest`. Parts cross as flat `double[]` vertices + `int[]` triangle indices; outputs are, per instance,
a 3×3 rotation `R`, a translation `(tx,ty,tz)`, and a container id (`-1` = did not fit). Apply as
`world = R·p + (tx,ty,tz)` in the container-local frame.

## Status & limitations

- **Working + validated**: mesh → voxelize → spectral pack → world pose, verified zero-overlap and
  in-container on box instances (`tools/abi_smoke.cpp`).
- **Voxel-approximate**: collisions are at voxel resolution (staircasing), like all spectral packers.
- **Single container** in v1; parts that don't fit are reported unplaced.
- **CPU cost**: the FFTs dominate; runtime grows with `voxel_resolution³`, part count, and orientations.
  Future speedups: cache the tray FFT across a part's orientations (psacking's `GPUTrayContext` trick),
  real-to-complex transforms, and parallelizing the orientation search.

Provenance: algorithm from *Dense, Interlocking-Free and Scalable Spectral Packing of Generic 3D Objects*
(Cui, Rong, Chen, Matusik; MIT/Inkbit). Reference implementation Vrroom/psacking (MIT). FFT by pocketfft
(Reinecke, BSD-3, vendored in `third_party/`).
