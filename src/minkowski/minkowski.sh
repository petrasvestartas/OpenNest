#!/bin/bash

# Define project and build directory
PROJECT_DIR=$(pwd)
BUILD_DIR="${PROJECT_DIR}/build"

# Create build directory if it doesn't exist
mkdir -p "${BUILD_DIR}"

# Navigate to the build directory
cd "${BUILD_DIR}"

# Step 1: Download and install libraries
cmake -DGET_LIBS=ON "${PROJECT_DIR}"
cmake --build . --target eigen
cmake --build . --target boost

# Step 2: Configure the build with compile options and in release mode
cmake -DCOMPILE_LIBS=ON -DCMAKE_BUILD_TYPE=Release "${PROJECT_DIR}"

# Step 3: Compile the project
cmake --build . --config Release

# Run the application (assuming the executable is named minkowski_app)
"${BUILD_DIR}/minkowski_app"

# Navigate back to the project directory
cd "${PROJECT_DIR}"