cmake_minimum_required(VERSION 3.20)
project(opennest_examples NONE)

# ---------------------------------------------------------------------------------------------------
# Superbuild for the two standalone example apps (a C++ console + a C# console) that drive the native
# OpenNest engines through their plain C ABI. It uses the SAME ExternalProject superbuild pattern as the
# repo-root CMakeLists.txt: each engine is built by its own unchanged standalone CMakeLists, then the
# example apps are built against the produced libraries.
#
#   cmake -S examples -B examples/build        (add -A x64 on Windows / MSVC)
#   cmake --build examples/build --config Release
#   cmake --build examples/build --target run_examples --config Release   # build + run both
# ---------------------------------------------------------------------------------------------------
include(ExternalProject)

get_filename_component(REPO_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)

# --- 1) the two native engines (reuse their standalone CMakeLists, unchanged) ---
ExternalProject_Add(nfp_engine
  SOURCE_DIR  ${REPO_ROOT}/src/opennest_cpp
  BINARY_DIR  ${REPO_ROOT}/src/opennest_cpp/build
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target nfp_nest
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1)

ExternalProject_Add(np_engine
  SOURCE_DIR  ${REPO_ROOT}/src/nest_physics_cpp
  BINARY_DIR  ${REPO_ROOT}/src/nest_physics_cpp/build
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release
  BUILD_COMMAND ${CMAKE_COMMAND} --build <BINARY_DIR> --config Release --target nest_physics
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1)

# --- 2) the C++ console (its own CMakeLists; copies the engine libs next to the exe) ---
ExternalProject_Add(cpp_console
  SOURCE_DIR  ${CMAKE_CURRENT_SOURCE_DIR}/cpp_console
  BINARY_DIR  ${CMAKE_BINARY_DIR}/cpp_console
  CMAKE_ARGS  -DCMAKE_BUILD_TYPE=Release -DENGINE_ROOT=${REPO_ROOT}
  INSTALL_COMMAND ""
  BUILD_ALWAYS 1
  DEPENDS nfp_engine np_engine)

# --- 3) the C# console (built with the dotnet SDK; the .csproj copies the engine libs next to the exe) ---
find_program(DOTNET_EXE dotnet)
if(DOTNET_EXE)
  ExternalProject_Add(csharp_console
    SOURCE_DIR  ${CMAKE_CURRENT_SOURCE_DIR}/csharp_console
    CONFIGURE_COMMAND ""
    BUILD_COMMAND ${DOTNET_EXE} build ${CMAKE_CURRENT_SOURCE_DIR}/csharp_console/csharp_console.csproj -c Release
    INSTALL_COMMAND ""
    BUILD_ALWAYS 1
    DEPENDS nfp_engine np_engine)
else()
  message(WARNING "dotnet not found - skipping the C# example (the C++ example still builds & runs).")
endif()

# --- 4) convenience target: run both examples (each prints the placements it computed) ---
add_custom_target(run_examples
  COMMAND ${CMAKE_COMMAND} -DCPP_BIN=${CMAKE_BINARY_DIR}/cpp_console -P ${CMAKE_CURRENT_SOURCE_DIR}/run_cpp.cmake
  VERBATIM USES_TERMINAL)
add_dependencies(run_examples cpp_console)
if(DOTNET_EXE)
  add_custom_command(TARGET run_examples POST_BUILD
    COMMAND ${DOTNET_EXE} run --project ${CMAKE_CURRENT_SOURCE_DIR}/csharp_console/csharp_console.csproj -c Release)
  add_dependencies(run_examples csharp_console)
endif()
