cmake_minimum_required(VERSION 3.20)
project(opennest_cpp LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Static-link the MSVC runtime across ALL targets so nfp_nest.dll is self-contained
# (the Rhino/.gha host then needs no extra VC++ redistributable). CMP0091 (NEW by
# default on CMake >= 3.15) makes this property authoritative.
if(POLICY CMP0091)
    cmake_policy(SET CMP0091 NEW)
endif()
if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

# --- External dependencies ---

# Clipper2 (vendored in-tree)
set(CLIPPER2_SOURCES
    src/clipper2/clipper.engine.cpp
    src/clipper2/clipper.offset.cpp
    src/clipper2/clipper.rectclip.cpp
)
add_library(Clipper2 STATIC ${CLIPPER2_SOURCES})
target_include_directories(Clipper2 PUBLIC ${CMAKE_SOURCE_DIR}/src)

# --- Source files (the core nesting library) ---
set(NEST_SOURCES
    src/Point.h
    src/NestConfig.h
    src/HelperTypes.h
    src/NFP.h
    src/GeometryUtil.h
    src/GeometryUtil.cpp
    src/Simplify.h
    src/D3.h
    src/ClipperUtil.h
    src/MinkowskiConvolution.h
    src/MinkowskiConvolution.cpp
    src/NfpWorker.h
    src/NfpWorker.cpp
    src/Random.h
    src/GeneticAlgorithm.h
    src/GeneticAlgorithm.cpp
    src/NestingEngine.h
    src/NestingEngine.cpp
    src/NestingContext.h
    src/NestingContext.cpp
)

# --- Boost.Polygon (minimal vendored subset: polygon + config, ~2 MB) ---
# Backs MinkowskiConvolution (NFP engine), matching deepnest-next / minkowski.dll.
# Override with -DBOOST_MIN_INCLUDE_DIR=<path> to point at a full Boost install.
set(BOOST_MIN_INCLUDE_DIR "${CMAKE_SOURCE_DIR}/third_party/boost_min"
    CACHE PATH "Boost.Polygon include root")

# --- Nest static library ---
add_library(nest_lib STATIC ${NEST_SOURCES})
target_include_directories(nest_lib
    PUBLIC
        ${CMAKE_SOURCE_DIR}/src
    PRIVATE
        ${BOOST_MIN_INCLUDE_DIR}
)
target_link_libraries(nest_lib PUBLIC Clipper2)

# MSVC needs _USE_MATH_DEFINES for M_PI
if(MSVC)
    target_compile_definitions(nest_lib PUBLIC _USE_MATH_DEFINES)
    target_compile_options(nest_lib PRIVATE /W3 /wd4244 /wd4267 /wd4305)
else()
    target_compile_options(nest_lib PRIVATE -Wall -Wextra -Wno-unused-parameter)
endif()

# --- Shared C-API DLL (nfp_nest.dll) bound to C# via P/Invoke ---
# This is the only product the OpenNest plugin needs from this project.
add_library(nfp_nest SHARED src/capi/nfp_nest_capi.h src/capi/nfp_nest_capi.cpp)
target_link_libraries(nfp_nest PRIVATE nest_lib)
target_include_directories(nfp_nest PRIVATE ${CMAKE_SOURCE_DIR}/src)
# Emit exactly nfp_nest.dll / nfp_nest.dylib (no "lib" prefix on macOS) to match the P/Invoke name.
set_target_properties(nfp_nest PROPERTIES PREFIX "")
if(MSVC)
    target_compile_definitions(nfp_nest PRIVATE _USE_MATH_DEFINES)
    target_compile_options(nfp_nest PRIVATE /W3 /wd4244 /wd4267 /wd4305)
endif()
# Self-contain GCC/MinGW builds on Windows: bake libgcc/libstdc++/libwinpthread into the DLL so it imports
# only OS libraries and loads in Rhino with no runtime DLLs to ship.
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
    target_link_options(nfp_nest PRIVATE -static -static-libgcc -static-libstdc++)
endif()
