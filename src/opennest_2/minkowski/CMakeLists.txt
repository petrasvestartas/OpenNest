########################################################################
#
#  2023_02_27
#  Petras Vestartas
#  Compile dynamic library in linux using Superbuild pattern
#
########################################################################
cmake_minimum_required(VERSION 3.24.2)

project(minkowski LANGUAGES C CXX)

set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

########################################################################
#
# START SUPERBUILD - DOWNLOAD LIBRARIES FROM GITHUB REPOSITORIES
#
########################################################################
#cmake_policy(SET CMP0097 NEW)
cmake_policy(SET CMP0135 NEW)
include(ExternalProject)



SET(GET_LIBS "" CACHE STRING "Set option to download dependencies")
if (GET_LIBS)
  message(AUTHOR_WARNING "GET_LIBS_" ${GET_LIBS})

########################################################################
# BOOST
######################################################################## 
  if (WIN32)
    set(BOOST_BOOTSTRAP_CMD ${CMAKE_CURRENT_BINARY_DIR}/boost-prefix/src/boost/bootstrap.bat)
    set(BOOST_BUILD_CMD ${CMAKE_CURRENT_BINARY_DIR}/boost-prefix/src/boost/b2.exe)
  elseif (UNIX)
    set(BOOST_BOOTSTRAP_CMD ${CMAKE_CURRENT_BINARY_DIR}/boost-prefix/src/boost/bootstrap.sh)
    set(BOOST_BUILD_CMD ${CMAKE_CURRENT_BINARY_DIR}/boost-prefix/src/boost/b2)
  endif ()

  ExternalProject_Add(boost
    GIT_REPOSITORY https://github.com/boostorg/boost.git
    GIT_TAG boost-1.78.0
    CONFIGURE_COMMAND ${BOOST_BOOTSTRAP_CMD}
    BUILD_COMMAND
      ${BOOST_BUILD_CMD}
      --prefix=${CMAKE_BINARY_DIR}/install/boost
      -sZLIB_LIBRARY_PATH=${CMAKE_BINARY_DIR}/install/boost/lib
      --without-python
      address-model=64
      variant=debug,release
      link=shared
      runtime-link=shared
      threading=multi
      install
    BUILD_IN_SOURCE 1
    INSTALL_COMMAND ""
    #SOURCE_DIR "${CMAKE_BINARY_DIR}/install/boost" #install directory is in build/install/eigen
  )

endif ()

########################################################################
#
# COMPILE LIBRARIES TO REDUCE BUILD TIME
#
########################################################################
SET(COMPILE_LIBS "" CACHE STRING "Set option to compile dependencies")
if (COMPILE_LIBS)

  # add library
  add_library(
    "${PROJECT_NAME}" # project name
    SHARED "${CMAKE_SOURCE_DIR}/src/minkowski.cc"
  )

  # include directories for header files
  target_include_directories(
    "${PROJECT_NAME}" # project name
    #source for the no fit polygon using bust
    PUBLIC  "${CMAKE_SOURCE_DIR}/src/"
  )

endif ()

