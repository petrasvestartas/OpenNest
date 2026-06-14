@echo off
REM OpenNest C++ example — Windows. Builds the engine from source (first run downloads it) and runs.
cmake -S . -B build -A x64 || exit /b 1
cmake --build build --config Release || exit /b 1
build\Release\example.exe
