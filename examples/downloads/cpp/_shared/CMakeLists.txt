cmake_minimum_required(VERSION 3.20)
project(opennest_example LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Fetch OpenNest and build its two native engines from source (works on Windows, macOS, Linux).
include(FetchContent)
FetchContent_Declare(opennest
    GIT_REPOSITORY https://github.com/petrasvestartas/OpenNest.git
    GIT_TAG        main)
FetchContent_GetProperties(opennest)
if(NOT opennest_POPULATED)
    FetchContent_Populate(opennest)
    add_subdirectory(${opennest_SOURCE_DIR}/src/opennest_cpp     ${CMAKE_BINARY_DIR}/nfp_engine)
    add_subdirectory(${opennest_SOURCE_DIR}/src/nest_physics_cpp ${CMAKE_BINARY_DIR}/np_engine)
endif()

add_executable(example main.cpp)
target_include_directories(example PRIVATE
    ${opennest_SOURCE_DIR}/src/opennest_cpp/src
    ${opennest_SOURCE_DIR}/src/nest_physics_cpp)
target_link_libraries(example PRIVATE nfp_nest nest_physics)

# Put the engine .dll/.dylib/.so next to the executable so it runs in place.
add_custom_command(TARGET example POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:nfp_nest>     $<TARGET_FILE_DIR:example>
    COMMAND ${CMAKE_COMMAND} -E copy_if_different $<TARGET_FILE:nest_physics> $<TARGET_FILE_DIR:example>)
