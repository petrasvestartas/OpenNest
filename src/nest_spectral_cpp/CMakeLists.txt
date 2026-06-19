# nest_spectral — 3D mesh-mesh nesting (spectral/FFT packing, CPU). Voxelize parts, find collision-free
# placements by FFT cross-correlation + a distance-field proximity score, greedy largest-first. This is
# the MIT/Inkbit Spectral Packing method ported off CUDA to pocketfft, so it runs anywhere the other
# OpenNest engines do (no GPU). The C-API TU pulls in the header-only core and exports a cdecl interface
# (see nest_spectral_capi.h) that the OpenNest plugin P/Invokes.
#
# Build the shared library (the only thing the plugin needs):
#     cmake -S . -B build -A x64
#     cmake --build build --config Release
#   -> build/Release/nest_spectral.dll (Windows) | build/libnest_spectral.dylib (Unix)
#
# Portable: C++ standard library + threads + the vendored header-only pocketfft (third_party/, BSD-3).
# No Boost / Eigen / CGAL / CUDA. MinGW dev one-liner (matches what CMake produces):
#   g++ -std=c++17 -O3 -pthread -static -static-libgcc -static-libstdc++ -shared \
#       -I. -o nest_spectral.dll nest_spectral_capi.cpp
cmake_minimum_required(VERSION 3.15)
cmake_policy(SET CMP0091 NEW)                 # make CMAKE_MSVC_RUNTIME_LIBRARY authoritative
project(nest_spectral LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")   # /MT — static MSVC CRT
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release)
endif()

# Optional cuFFT GPU backend. OFF by default so the engine builds anywhere with no CUDA. When ON, the
# CUDA toolkit is needed to BUILD, but NOT to RUN: cudart is static-linked and cufft is delay-loaded, so
# the DLL still loads on machines with no CUDA and transparently falls back to the CPU pocketfft path.
option(NEST_SPECTRAL_CUDA "Build the cuFFT GPU backend (uses the GPU at runtime when available)" OFF)
if(NEST_SPECTRAL_CUDA)
    enable_language(CUDA)
    find_package(CUDAToolkit REQUIRED)
endif()

function(nest_spectral_add_cuda tgt)
    target_sources(${tgt} PRIVATE spectral/fft_cuda.cu spectral/cuda_probe.cpp)
    # Real-arch cubins for common modern NVIDIA GPUs (Ampere/Ada). Other archs fall back to CPU via the
    # error-checked conv3_cuda. Override with -DCMAKE_CUDA_ARCHITECTURES=... for a different target GPU.
    if(NOT DEFINED CMAKE_CUDA_ARCHITECTURES)
        set_target_properties(${tgt} PROPERTIES CUDA_ARCHITECTURES "86;89")
    endif()
    set_target_properties(${tgt} PROPERTIES CUDA_RUNTIME_LIBRARY Static)
    target_compile_definitions(${tgt} PRIVATE NSP_CUDA)
    target_include_directories(${tgt} PRIVATE ${CUDAToolkit_INCLUDE_DIRS})
    target_link_libraries(${tgt} PRIVATE CUDA::cufft)
    if(MSVC)
        # Delay-load cufft so a missing cufft64_*.dll doesn't stop the DLL loading (caught in cuda_probe.cpp).
        target_link_options(${tgt} PRIVATE /DELAYLOAD:cufft64_12.dll delayimp.lib)
    endif()
endfunction()

add_library(nest_spectral SHARED nest_spectral_capi.cpp)
target_include_directories(nest_spectral PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
# Emit exactly `nest_spectral.dll` / `nest_spectral.dylib` (no "lib" prefix) to match the P/Invoke name.
set_target_properties(nest_spectral PROPERTIES PREFIX "" OUTPUT_NAME "nest_spectral")

if(MSVC)
    # CXX-only: a raw /O2 passed to nvcc (CUDA sources) is treated as an input file and fails the build.
    target_compile_options(nest_spectral PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/O2>)
else()
    target_compile_options(nest_spectral PRIVATE $<$<COMPILE_LANGUAGE:CXX>:-O3>)
    find_package(Threads REQUIRED)
    target_link_libraries(nest_spectral PRIVATE Threads::Threads)   # pocketfft uses std::thread
endif()

# Self-contain GCC/MinGW Windows builds: bake libgcc/libstdc++/libwinpthread into the DLL so it imports
# only OS libraries and loads in Rhino with nothing to ship alongside.
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
    target_link_options(nest_spectral PRIVATE -static -static-libgcc -static-libstdc++)
endif()

if(NEST_SPECTRAL_CUDA)
    nest_spectral_add_cuda(nest_spectral)
endif()

# Optional standalone CLI self-test (synthetic shapes + mesh voxelizer); off by default.
option(NEST_SPECTRAL_BUILD_CLI "Build the nest_spectral command-line self-test" OFF)
if(NEST_SPECTRAL_BUILD_CLI)
    add_executable(nest_spectral_cli nest_spectral.cpp)
    target_include_directories(nest_spectral_cli PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})
    set_target_properties(nest_spectral_cli PROPERTIES OUTPUT_NAME "nest_spectral")
    if(NOT MSVC)
        find_package(Threads REQUIRED)
        target_link_libraries(nest_spectral_cli PRIVATE Threads::Threads)
    endif()
    if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
        target_link_options(nest_spectral_cli PRIVATE -static -static-libgcc -static-libstdc++)
    endif()
    if(NEST_SPECTRAL_CUDA)
        nest_spectral_add_cuda(nest_spectral_cli)
    endif()
endif()
