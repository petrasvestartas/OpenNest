@echo off
REM OpenNest C# example — Windows. Builds the native engines from source, then runs.
if not exist OpenNest git clone --depth 1 https://github.com/petrasvestartas/OpenNest.git || exit /b 1
cmake -S OpenNest/src/opennest_cpp     -B OpenNest/src/opennest_cpp/build     -A x64 && cmake --build OpenNest/src/opennest_cpp/build     --config Release --target nfp_nest     || exit /b 1
cmake -S OpenNest/src/nest_physics_cpp -B OpenNest/src/nest_physics_cpp/build -A x64 && cmake --build OpenNest/src/nest_physics_cpp/build --config Release --target nest_physics || exit /b 1
dotnet build -c Release || exit /b 1
copy /Y OpenNest\src\opennest_cpp\build\Release\nfp_nest.dll bin\Release\net8.0\ >nul
copy /Y OpenNest\src\nest_physics_cpp\build\Release\nest_physics.dll bin\Release\net8.0\ >nul
dotnet bin\Release\net8.0\example.dll
