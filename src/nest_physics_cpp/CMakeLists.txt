# nest_physics — 2D irregular nesting solver (physics/overlap-relaxation, sparrow-style).
# Header-only solver (geometry/*.hpp + solver/*.hpp); the C-API translation unit pulls it all in and
# exports a small cdecl C interface (see nest_physics_capi.h) that the OpenNest plugin P/Invokes.
#
# Build the shared library (the only thing the plugin needs):
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#   -> build/Release/nest_physics.dll   (Windows)   |   build/libnest_physics.dylib -> nest_physics.dylib (Unix)
#
# Portable: only the C++ standard library + threads (no Boost / Eigen / CGAL).
#
# SELF-CONTAINED by default: MSVC static-links the CRT (/MT); MinGW/GCC static-links libgcc/libstdc++/
# libwinpthread (GNU block below) so the produced DLL imports ONLY OS libraries (KERNEL32 + msvcrt) and loads
# in Rhino with nothing to ship alongside. Verify: objdump -p nest_physics.dll | grep "DLL Name".
# Canonical MinGW dev one-liner (matches what CMake produces):
#   g++ -std=c++20 -O3 -march=native -ffp-contract=off -pthread -fopenmp-simd \
#       -static -static-libgcc -static-libstdc++ -shared -o nest_physics.dll nest_physics_capi.cpp
cmake_minimum_required(VERSION 3.15)
cmake_policy(SET CMP0091 NEW)                 # make CMAKE_MSVC_RUNTIME_LIBRARY authoritative
project(nest_physics LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # /MT — static MSVC CRT (no vc-runtime DLLs)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

add_library(nest_physics SHARED nest_physics_capi.cpp)
target_include_directories(nest_physics PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
# Emit exactly `nest_physics.dll` / `nest_physics.dylib` (no "lib" prefix) to match the P/Invoke name.
set_target_properties(nest_physics PROPERTIES PREFIX "" OUTPUT_NAME "nest_physics")

# Optimization matches the original build (-O3, NO fast-math, to keep iteration-count results reproducible).
if(MSVC)
    target_compile_options(nest_physics PRIVATE /O2)
else()
    target_compile_options(nest_physics PRIVATE -O3)
    find_package(Threads REQUIRED)
    target_link_libraries(nest_physics PRIVATE Threads::Threads)
endif()

# Self-contain GCC/MinGW builds on Windows: bake libgcc/libstdc++/libwinpthread into the DLL so it imports only
# OS libraries and loads in Rhino with no runtime DLLs to ship. (This is what tonight's bug needed.)
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
    target_link_options(nest_physics PRIVATE -static -static-libgcc -static-libstdc++)
endif()

# Optional standalone CLI (the original `g++ nest_physics.cpp -o nest_physics`); off by default.
option(NEST_PHYSICS_BUILD_CLI "Build the nest_physics command-line tool" OFF)
if(NEST_PHYSICS_BUILD_CLI)
    add_executable(nest_physics_cli nest_physics.cpp)
    target_include_directories(nest_physics_cli PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    set_target_properties(nest_physics_cli PROPERTIES OUTPUT_NAME "nest_physics")
    if(NOT MSVC)
        find_package(Threads REQUIRED)
        target_link_libraries(nest_physics_cli PRIVATE Threads::Threads)
    endif()
    if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
        target_link_options(nest_physics_cli PRIVATE -static -static-libgcc -static-libstdc++)
    endif()
endif()
