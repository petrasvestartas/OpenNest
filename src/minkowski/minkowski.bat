@echo off

REM Define project and build directory
set "PROJECT_DIR=%cd%"
set "BUILD_DIR=%PROJECT_DIR%\build"

REM Create build directory if it doesn't exist
mkdir "%BUILD_DIR%" 2>nul

REM Navigate to the build directory
cd "%BUILD_DIR%"

REM Run CMake to configure the build (assuming you want both compile and get libs)
cmake -DCOMPILE_LIBS=ON -DGET_LIBS=ON -DCMAKE_BUILD_TYPE=Release "%PROJECT_DIR%"

REM Compile the project in release mode
cmake --build . --config Release

REM Run the application
"%BUILD_DIR%\Release\minkowski_app.exe"

REM Navigate back to the project directory
cd "%PROJECT_DIR%"