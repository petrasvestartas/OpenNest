cmake_minimum_required(VERSION 3.20)
project(opennest_cpp_example LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# ENGINE_ROOT is passed by the superbuild (examples/CMakeLists.txt); it is the repo root.
if(NOT DEFINED ENGINE_ROOT)
    get_filename_component(ENGINE_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../.." ABSOLUTE)
endif()

add_executable(nest_demo main.cpp)

# Runtime dynamic loading needs libdl on Linux; std::thread (live-animation demo) needs pthread.
find_package(Threads REQUIRED)
target_link_libraries(nest_demo Threads::Threads)
if(UNIX AND NOT APPLE)
    target_link_libraries(nest_demo ${CMAKE_DL_LIBS})
endif()

# Copy the engine runtime libraries next to the executable so the runtime LoadLibrary/dlopen finds
# them. Covers MSVC (build/Release/), single-config generators (build/), and Unix (.dylib/.so).
file(GLOB _engine_libs
    "${ENGINE_ROOT}/src/opennest_cpp/build/Release/nfp_nest.dll"
    "${ENGINE_ROOT}/src/opennest_cpp/build/nfp_nest.dll"
    "${ENGINE_ROOT}/src/opennest_cpp/build/nfp_nest.dylib"
    "${ENGINE_ROOT}/src/opennest_cpp/build/nfp_nest.so"
    "${ENGINE_ROOT}/src/nest_physics_cpp/build/Release/nest_physics.dll"
    "${ENGINE_ROOT}/src/nest_physics_cpp/build/nest_physics.dll"
    "${ENGINE_ROOT}/src/nest_physics_cpp/build/nest_physics.dylib"
    "${ENGINE_ROOT}/src/nest_physics_cpp/build/nest_physics.so")

foreach(_lib ${_engine_libs})
    add_custom_command(TARGET nest_demo POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different "${_lib}" "$<TARGET_FILE_DIR:nest_demo>")
endforeach()
