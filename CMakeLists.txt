# OpenNest — one-shot build of all three native engines the Grasshopper plugin loads.
#
#   nfp_nest.dll      (src/opennest_cpp)      NFP / SVGnest GA — self-contained (vendored Clipper2 + boost_min)
#   nest_physics.dll  (src/nest_physics_cpp)  physics / overlap solver — self-contained (header-only)
#   minkowski.dll     (src/minkowski)         Minkowski / NFP helper — needs Boost.Polygon + Eigen headers
#
# Build everything:
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#
# Each engine builds into its own src/<engine>/build/Release/ folder (where the .gha's PostBuild and the
# CI expect the DLLs). Sub-projects build via ExternalProject, so their existing standalone CMakeLists are
# used unchanged — you can still build any one of them on its own.
#
# minkowski needs Boost.Polygon + Eigen headers. Provide them one of three ways, or skip it:
#     -DBOOST_INCLUDE_DIR=<dir> -DEIGEN_INCLUDE_DIR=<dir>   (e.g. a vcpkg install/x64-windows/include)
#     -DGET_LIBS=ON                                         (download Boost 1.78 + Eigen 3.4 at build time)
#     -DBUILD_MINKOWSKI=OFF                                 (don't build it)
cmake_minimum_required(VERSION 3.20)
project(opennest NONE)

include(ExternalProject)

option(BUILD_MINKOWSKI "Build minkowski.dll (needs Boost.Polygon + Eigen headers)" ON)
set(BOOST_INCLUDE_DIR "" CACHE PATH "Boost include dir for minkowski (folder containing boost/)")
set(EIGEN_INCLUDE_DIR "" CACHE PATH "Eigen include dir for minkowski (folder containing Eigen/)")
option(GET_LIBS "Let minkowski download Boost + Eigen at build time" OFF)

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

if(BUILD_MINKOWSKI)
  ExternalProject_Add(minkowski
    SOURCE_DIR  ${_root}/src/minkowski
    BINARY_DIR  ${_root}/src/minkowski/build
    CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
                -DBOOST_INCLUDE_DIR=${BOOST_INCLUDE_DIR}
                -DEIGEN_INCLUDE_DIR=${EIGEN_INCLUDE_DIR}
                -DGET_LIBS=${GET_LIBS}
    BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target minkowski
    INSTALL_COMMAND ""
    BUILD_ALWAYS 1
  )
endif()
