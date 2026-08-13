# OpenNest — one-shot build of the THREE native engines the Grasshopper plugin loads.
#
#   nfp_nest.dll      (src/opennest_cpp)       NFP / SVGnest GA + rectangle fast path — self-contained
#                                              (vendored Clipper2 + boost_min)
#   nest_physics.dll  (src/nest_physics_cpp)   physics / overlap-relaxation solver — self-contained (header-only)
#   nest_spectral.dll (src/nest_spectral_cpp)  3D mesh packing (spectral/FFT) — self-contained (vendored
#                                              header-only pocketfft; the optional cuFFT backend is OFF here)
#
# Build everything:
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#
# Each engine builds into its own src/<engine>/build/Release/ folder (where the .gha's PostBuild and the CI
# expect the DLLs). Sub-projects build via ExternalProject, so their existing standalone CMakeLists are used
# unchanged — you can still build any one of them on its own. All three engines are fully self-contained: no
# external dependencies (Clipper2, a minimal Boost.Polygon subset and pocketfft are vendored in-repo).
#
# All three are built here on purpose: src/opennest_2/opennest_2.csproj emits
# "nest_spectral engine not built - OpenNest3D will fail at runtime" when
# src/nest_spectral_cpp/build/Release/nest_spectral.dll is missing, so a superbuild that skipped it would
# leave the documented "superbuild, then dotnet build" flow warning on a fresh clone.
cmake_minimum_required(VERSION 3.20)
project(opennest NONE)

include(ExternalProject)

set(_root ${CMAKE_CURRENT_SOURCE_DIR})

ExternalProject_Add(nfp_nest
  SOURCE_DIR  ${_root}/src/opennest_cpp
  BINARY_DIR  ${_root}/src/opennest_cpp/build
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target nfp_nest
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1
)

ExternalProject_Add(nest_physics
  SOURCE_DIR  ${_root}/src/nest_physics_cpp
  BINARY_DIR  ${_root}/src/nest_physics_cpp/build
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target nest_physics
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1
)

# 3D engine. NEST_SPECTRAL_CUDA stays OFF (its default) so this needs no CUDA toolkit; the CPU pocketfft
# path is what ships.
ExternalProject_Add(nest_spectral
  SOURCE_DIR  ${_root}/src/nest_spectral_cpp
  BINARY_DIR  ${_root}/src/nest_spectral_cpp/build
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target nest_spectral
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1
)
