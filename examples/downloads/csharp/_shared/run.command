#!/bin/bash
# OpenNest C# example — macOS/Linux. Builds the native engines from source, then runs.
cd "$(dirname "$0")" || exit 1
[ -d OpenNest ] || git clone --depth 1 https://github.com/petrasvestartas/OpenNest.git || exit 1
cmake -S OpenNest/src/opennest_cpp     -B OpenNest/src/opennest_cpp/build     -DCMAKE_BUILD_TYPE=Release && cmake --build OpenNest/src/opennest_cpp/build     --target nfp_nest     || exit 1
cmake -S OpenNest/src/nest_physics_cpp -B OpenNest/src/nest_physics_cpp/build -DCMAKE_BUILD_TYPE=Release && cmake --build OpenNest/src/nest_physics_cpp/build --target nest_physics || exit 1
dotnet build -c Release || exit 1
cp OpenNest/src/opennest_cpp/build/nfp_nest.*     bin/Release/net8.0/ 2>/dev/null
cp OpenNest/src/nest_physics_cpp/build/nest_physics.* bin/Release/net8.0/ 2>/dev/null
dotnet bin/Release/net8.0/example.dll
