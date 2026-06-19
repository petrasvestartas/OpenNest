# 3D mesh–mesh nesting — open-source landscape & a `nest_spectral` plan

Research for adding a **3D nesting** engine to OpenNest, in the spirit of the MIT/Inkbit
**Spectral Packing** method (voxelize → FFT-correlation collision → FFT cost → greedy placement).

- Article: <https://www.voxelmatters.com/mit-and-inkbit-present-spectral-packing-method-for-packing-3d-objects/>
- Paper: *Dense, Interlocking-Free and Scalable Spectral Packing of Generic 3D Objects* —
  Cui, Rong, Chen, **Matusik** (MIT + Inkbit), SIGGRAPH/TOG 2023.
  PDF: <https://inkbit3d.com/wp-content/uploads/2023/06/spectralPacking_optimized.pdf> ·
  <https://dl.acm.org/doi/10.1145/3592126>

## The method in one paragraph

Each object is rasterised into a **binary voxel grid**. A collision between two objects at a
candidate offset is exactly a **correlation of their occupancy functions**, which is computed for
*all* offsets at once with an **FFT** (multiply in the frequency domain, inverse-transform). A
second FFT evaluates a **placement cost** (proximity + height penalty), so the best collision-free
pose is read straight off the transformed grid. Objects are placed greedily, largest-volume first;
the same correlation framework detects and prevents **interlocking**. Reported: ~670 objects in 40 s
at ~36% density. **NP-hard 3D irregular bin packing, made cheap by doing the geometry in Fourier space.**

## Key finding on official code

**The paper authors released NO open-source code.** Inkbit ships it as a commercial product
("Pack Studio"/SSP) and holds a patent (US 11,897,203, *Frequency domain spatial packing for 3D
fabrication*). So any open base is a **third-party reimplementation** or an **adjacent algorithm**.

## The one direct match — `Vrroom/psacking`

<https://github.com/Vrroom/psacking> — an independent reimplementation of *this exact paper*.

| Aspect | Finding |
| --- | --- |
| License | **MIT** (compatible with OpenNest's MIT) |
| Pipeline | voxelize (VoxSurf) → **FFT-correlation collision** → proximity+height cost → greedy by descending volume |
| Representation | **voxel** binary grid (default resolution **128**) |
| FFT/spectral | **Yes** — this is the core; the literal spectral-packing method |
| Deps | **CUDA 11+ and an NVIDIA GPU are REQUIRED** (no CPU fallback), FFTW3, pybind11, VoxSurf, CMake≥3.18, scikit-build, numpy, trimesh |
| Build / OS | **Linux (Ubuntu) only**; Windows not supported; `pip install -e .` |
| API | **Python module only** (pybind11): `BinPacker.pack_files/pack_voxels/pack_single` → `PackingResult{placements, density, …}`, `Voxelizer`. No C++ headers, no CLI exposed |
| Maturity | research prototype: 28 commits, 143★, 0 releases, 0 open issues |
| Result | 60.8% density on 348 Thingi10K parts in a 240×123×100 mm tray |

### Can it be used as OpenNest's `nest_spectral`? — honest assessment

**Yes as the algorithm + reference, and the MIT license is clean.** But it does not drop into
OpenNest's deployment model as-is. OpenNest ships **CPU C-ABI DLLs** (`nfp_nest`, `nest_physics`)
that P/Invoke from C#/Grasshopper on *arbitrary* Windows machines. psacking is the opposite on three axes:

1. **GPU-locked.** It *requires* CUDA + an NVIDIA GPU with no CPU path. Most Rhino/Grasshopper users
   don't have CUDA. Options: (a) make 3D nesting a CUDA-only feature (narrow audience), or
   (b) **port the FFT correlation to a CPU FFT** (FFTW/pocketfft) — feasible because the math is
   FFT-based, but real work and much slower per solve.
2. **Linux-only, Python-only surface.** To match OpenNest you'd extract the C++/CUDA core, add a
   **Windows CMake build**, and write a **C-ABI wrapper** (`np_nest`-style) — there are no C++ headers
   or CLI to call today, only a pybind11 module.
3. **Voxel-approximate, not exact-geometry.** Fine for print-density packing; note it's not the exact
   mesh-mesh contact OpenNest's 2D engines do. Output is a voxel grid + placements; you'd extract a
   per-part transform (translation + discrete rotation) and emit it like `np_nest`.

So: **adopt psacking as the algorithmic blueprint** (and possibly vendor its FFT-correlation core),
but budget for a CPU port + Windows C-ABI wrapper if `nest_spectral` must deploy like the other engines.

## The other realistic base — the JonasTollenaere ecosystem (CPU, mesh-native)

If deployment fit (CPU, Windows, exact mesh) outweighs matching the exact paper:

- **`JonasTollenaere/MeshCore`** <https://github.com/JonasTollenaere/MeshCore> — C++ LGPL-3.0, active
  (KU Leuven). Triangle-mesh I/O + **collision detection** kernel. The geometry backend.
- **`JonasTollenaere/sparrow-3d`** <https://github.com/JonasTollenaere/sparrow-3d> — C++ LGPL-3.0,
  the **3D port of the SOTA 2D "sparrow" nester**; collision-driven overlap-minimisation heuristic,
  continuous placement, strip-height minimisation. **Closest existing open analog to a 3D deepnest.**
- Exact baselines from the same group: `strip-milp-3d` (convex, MILP), `svmp-heuristic`,
  `svmp-quaternion-qcp`.

Trade-off vs psacking: **CPU + mesh-exact + LGPL** (not MIT), but a *different algorithm*
(collision-heuristic, not FFT-spectral).

## Other candidates (lower fit)

- **`alexfrom0815/IR-BPP`** <https://github.com/alexfrom0815/IR-BPP> — Deep-RL irregular 3D packing,
  Python, **MIT**, 316★. Strongest ML option + good benchmark data (uses VHACD). Different paradigm.
- **`ahmedmdl/3d-binary-packing-`** — octree/voxel mesh packing, Python, tiny/unmaintained. A compact
  voxel-collision reference.
- **`MbBrainz/irregular-object-packing`** (IROP) — hybrid-optimization (Ma et al. 2018), Python, BSD,
  ~10 items practical.
- Slicers do **not** help: PrusaSlicer (`tamasmeszaros/libnest2d`) and Cura (`pynest2d`) are **2D
  footprint** arrange only; no mainline open slicer does true 3D build-volume packing.
- **Dapper** (SIGGRAPH Asia 2015 decompose-and-pack): **no public code**.

## FFT building blocks (for a CPU port of the spectral method)

- `kwsp/fftconv` — FFTW-based FFT convolution (the core primitive), C++/MIT.
- `marian42/mesh_to_sdf`, `kmammou/v-hacd` (VHACD) — voxelize / decompose to feed the packer.

## Proposed `nest_spectral` shape (if we go FFT/voxel)

Mirror the existing engines so the binding/Grasshopper side is unchanged in spirit:

```
nest_spectral(
    parts[]      : triangle meshes (+ quantities, allowed discrete rotations)
    container    : box (W×D×H) or mesh build volume
    voxel_res    : e.g. 128
    cost params  : proximity weight, height penalty
) -> per instance: translation (tx,ty,tz), rotation (index/quaternion), container_id (-1 = unplaced)
```

Core loop = voxelize each part → for each part (descending volume) FFT-correlate its grid against the
current container occupancy → mask collisions → FFT cost → place at best cell → OR the part into the
container grid. CPU FFT (FFTW/pocketfft) for portability; optional CUDA fast path.

## Recommendation

1. **`Vrroom/psacking` is the right reference** and the only open implementation of *this* method;
   MIT means we can study/vendor it freely.
2. Decision point: **(A)** require CUDA and wrap psacking's core into a `nest_spectral` DLL, vs
   **(B)** CPU-port the FFT-correlation core for portable deployment, vs **(C)** base 3D nesting on
   **sparrow-3d/MeshCore** (CPU, mesh-exact, but a non-spectral algorithm and LGPL).
3. For OpenNest's "runs anywhere inside Rhino" model, **(B)** or **(C)** fit the deployment story
   better; **(A)** is the fastest to a working prototype if a CUDA requirement is acceptable.

## Update — option (B) built: `nest_spectral` (CPU port)

Decision taken: **(B)**. A working CPU port of psacking's spectral method now lives in
[`src/nest_spectral_cpp/`](src/nest_spectral_cpp/) on this branch — the cuFFT core swapped for
header-only **pocketfft** (BSD-3), so it has **no GPU/CUDA dependency** and builds like the other
OpenNest engines. Self-contained header-only core (`spectral/grid,fft,distance,voxelize,packer.hpp`),
a C ABI (`nest_spectral_capi.h`, P/Invokable like `np_nest`, returns `world = R·p + t` per instance),
and a CMake target emitting `nest_spectral.dll`.

Validated (MinGW g++ 15.2 **and** MSVC via `cmake -A x64`):
- CPU FFT-correlation pipeline reproduces the method — 24 synthetic parts into a 32³ tray, **zero
  overlap** at every orientation count, density 50/48/57 % for 1/6/24 orientations.
- Voxelizer: a 10³ cube fills exactly 1000/1000 voxels (solid).
- ABI: 10 box instances packed, and transforming each by the returned `R·p + t` confirms **all inside
  the container, no overlap** (`tools/abi_smoke.cpp`).

Remaining for production: wire it into the Grasshopper plugin (a 3D `nest_Geo`/P-Invoke), and the
CPU-speed work noted in the project README (cache the tray FFT across a part's orientations, r2c
transforms, parallel orientation search).
