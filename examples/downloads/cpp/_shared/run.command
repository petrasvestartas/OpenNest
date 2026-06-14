#!/bin/bash
# OpenNest C++ example — macOS/Linux. Builds the engine from source (first run downloads it) and runs.
cd "$(dirname "$0")" || exit 1
cmake -S . -B build -DCMAKE_BUILD_TYPE=Release || exit 1
cmake --build build --config Release || exit 1
./build/example
