# opennest_job — headless OpenNest worker: one JSON job in, one JSON result out (see README.md).
# Compiles the nest_physics C ABI (../nest_physics_cpp/nest_physics_capi.cpp) straight into the executable,
# so no shared library has to be shipped next to it.
#
#   Linux / macOS:  cmake -S src/opennest_job -B build/opennest_job -DCMAKE_BUILD_TYPE=Release
#                   cmake --build build/opennest_job -j
#   Windows (MSVC): cmake -S src/opennest_job -B build/opennest_job -A x64
#                   cmake --build build/opennest_job --config Release
cmake_minimum_required(VERSION 3.16)
cmake_policy(SET CMP0091 NEW)
project(opennest_job LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release)
endif()

set(OPENNEST_JOB_VERSION "0.1.0" CACHE STRING "Version stamp reported by opennest_job --version")
set(NEST_PHYSICS_DIR ${CMAKE_CURRENT_SOURCE_DIR}/../nest_physics_cpp)
# Clipper2 (Boost licence) vendored with the NFP engine; used for the spacing / margin offsets.
set(CLIPPER2_ROOT ${CMAKE_CURRENT_SOURCE_DIR}/../opennest_cpp/src)
set(CLIPPER2_SOURCES ${CLIPPER2_ROOT}/clipper2/clipper.engine.cpp ${CLIPPER2_ROOT}/clipper2/clipper.offset.cpp
                     ${CLIPPER2_ROOT}/clipper2/clipper.rectclip.cpp)

# Fully static Linux binary (runs in a scratch/distroless container, no libstdc++ version coupling).
option(OPENNEST_JOB_STATIC "Link opennest_job statically (GCC/Clang)" OFF)

add_executable(opennest_job opennest_job.cpp ${NEST_PHYSICS_DIR}/nest_physics_capi.cpp ${CLIPPER2_SOURCES})
target_include_directories(opennest_job PRIVATE ${CMAKE_CURRENT_SOURCE_DIR} ${NEST_PHYSICS_DIR} ${CLIPPER2_ROOT})
target_compile_definitions(opennest_job PRIVATE OPENNEST_JOB_VERSION="${OPENNEST_JOB_VERSION}")

# Same optimisation as the shipped DLL: -O3 / /O2, NO fast-math and NO -march=native, so a binary built on
# one machine runs on any x86-64 server and iteration-count results stay reproducible.
if(MSVC)
    target_compile_options(opennest_job PRIVATE /O2 /utf-8)
else()
    target_compile_options(opennest_job PRIVATE -O3)
    find_package(Threads REQUIRED)
    target_link_libraries(opennest_job PRIVATE Threads::Threads)
    if(OPENNEST_JOB_STATIC)
        target_link_options(opennest_job PRIVATE -static)
    endif()
endif()
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
    target_link_options(opennest_job PRIVATE -static -static-libgcc -static-libstdc++)
endif()
