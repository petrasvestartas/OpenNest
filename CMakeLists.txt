# OpenNest — one-shot build of the native engines the Grasshopper plugin loads.
#
#   nfp_nest.dll      (src/opennest_cpp)      NFP / SVGnest GA — self-contained (vendored Clipper2 + boost_min)
#   nest_physics.dll  (src/nest_physics_cpp)  physics / overlap solver — self-contained (header-only)
#
# Build everything:
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#
# Each engine builds into its own src/<engine>/build/Release/ folder (where the .gha's PostBuild and the CI
# expect the DLLs). Sub-projects build via ExternalProject, so their existing standalone CMakeLists are used
# unchanged — you can still build either one on its own. Both engines are fully self-contained: no external
# dependencies (Clipper2 and a minimal Boost.Polygon subset are vendored in-repo).
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
