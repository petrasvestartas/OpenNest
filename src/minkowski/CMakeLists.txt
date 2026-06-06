cmake_minimum_required(VERSION 3.24.2)
project(minkowski LANGUAGES C CXX)

# Set C++ standard and compiler options
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Self-contained CRT: static-link the MSVC runtime (/MT) so minkowski.dll needs no VC++ redistributable.
if(POLICY CMP0091)
    cmake_policy(SET CMP0091 NEW)
endif()
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

# Ensure position-independent code is enabled
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Set architectures for macOS
if (APPLE)
    set(CMAKE_OSX_ARCHITECTURES "arm64;x86_64" CACHE STRING "Build architectures for macOS" FORCE)
endif()

# ExternalProject module is used for downloading and managing dependencies
include(ExternalProject)

# Check if we want to download and use dependencies
option(GET_LIBS "Download dependencies (Boost and Eigen)" OFF)
option(COMPILE_LIBS "Compile dependencies (Boost and Eigen)" OFF)

# Header-only deps (boost/polygon + Eigen). Prefer include dirs passed on the command line
# (-DBOOST_INCLUDE_DIR=... -DEIGEN_INCLUDE_DIR=..., e.g. from vcpkg in CI). GET_LIBS=ON instead
# downloads Boost 1.78 + Eigen 3.4 at build time and overrides these.
set(BOOST_INCLUDE_DIR "" CACHE PATH "Boost include directory (the folder that contains boost/)")
set(EIGEN_INCLUDE_DIR "" CACHE PATH "Eigen include directory (the folder that contains Eigen/)")

########################################################################
# External Dependencies (Boost and Eigen)
########################################################################

# Download and configure Eigen if GET_LIBS is enabled
if (GET_LIBS)
    ExternalProject_Add(eigen
        URL https://gitlab.com/libeigen/eigen/-/archive/3.4.0/eigen-3.4.0.tar.gz
        PREFIX eigen
        CONFIGURE_COMMAND ""
        BUILD_COMMAND ""
        INSTALL_COMMAND ""
    )
    
    ExternalProject_Get_Property(eigen SOURCE_DIR)
    set(EIGEN_INCLUDE_DIR ${SOURCE_DIR})
endif()

# Download and configure Boost if GET_LIBS is enabled
if (GET_LIBS)
    ExternalProject_Add(boost
        URL https://archives.boost.io/release/1.78.0/source/boost_1_78_0.tar.gz
        PREFIX boost
        CONFIGURE_COMMAND ""
        BUILD_COMMAND ""
        INSTALL_COMMAND ""
    )
    
    ExternalProject_Get_Property(boost SOURCE_DIR)
    set(BOOST_INCLUDE_DIR ${SOURCE_DIR})
endif()

########################################################################
# Main Project Build
########################################################################

# Add your source files
set(LIBRARY_SRC_FILES
    src/minkowski.cc
    src/boundingbox.cc
)

# Define the header files
set(LIBRARY_HEADER_FILES
    src/minkowski.h
    src/boundingbox.h
    # Add other header files here
)

# Combine source and header files
set(LIBRARY_FILES ${LIBRARY_SRC_FILES} ${LIBRARY_HEADER_FILES})

# Define the library target
add_library(minkowski SHARED ${LIBRARY_FILES})

# Set output name of the shared library to minkowski and remove the 'lib' prefix
set_target_properties(minkowski PROPERTIES
    OUTPUT_NAME "minkowski"
    PREFIX ""
)

# Compiler-specific options
if (WIN32)
    target_compile_options(minkowski PRIVATE
        /Ob2 /Oi /Ot /Oy- /GL /GF /Gm- /GS- /arch:AVX2 /fp:fast
    )   # CRT (/MT) comes from CMAKE_MSVC_RUNTIME_LIBRARY above — do NOT add /MD/.MT here (it would conflict)
elseif(APPLE)
    # Add specific options for macOS if needed
endif()

# Include directories
target_include_directories(${PROJECT_NAME}
    PUBLIC
        ${CMAKE_SOURCE_DIR}/src
    PRIVATE
        ${BOOST_INCLUDE_DIR}
        ${EIGEN_INCLUDE_DIR}
)

########################################################################
# Console Application Build
########################################################################

# Add your source files
set(APP_SRC_FILES
    src/main.cc
    src/minkowski.cc
    src/boundingbox.cc
)

# Define the header files
set(APP_HEADER_FILES
    src/minkowski.h
    src/boundingbox.h
    # Add other header files here
)

# Combine source and header files
set(APP_FILES ${APP_SRC_FILES} ${APP_HEADER_FILES})

# Define the executable target
add_executable(${PROJECT_NAME}_app ${APP_FILES})
message(${APP_SRC_FILES})

# Link the console application with the library
target_link_libraries(${PROJECT_NAME}_app PRIVATE ${PROJECT_NAME})

# Include directories for the console application
target_include_directories(${PROJECT_NAME}_app
    PRIVATE
        ${CMAKE_SOURCE_DIR}/src
        ${BOOST_INCLUDE_DIR}
        ${EIGEN_INCLUDE_DIR}
)

# When GET_LIBS downloads Boost + Eigen at build time, make the targets wait for those
# ExternalProject download steps so the headers exist before anything tries to compile.
if (GET_LIBS)
    add_dependencies(minkowski eigen boost)
    add_dependencies(${PROJECT_NAME}_app eigen boost)
endif()

# Self-contain GCC/MinGW builds on Windows (CI uses MSVC; defensive for local MinGW builds so the produced
# DLL imports only OS libraries and loads in Rhino with no runtime DLLs to ship).
if(MINGW OR (CMAKE_CXX_COMPILER_ID STREQUAL "GNU" AND WIN32))
    target_link_options(minkowski PRIVATE -static -static-libgcc -static-libstdc++)
    target_link_options(${PROJECT_NAME}_app PRIVATE -static -static-libgcc -static-libstdc++)
endif()