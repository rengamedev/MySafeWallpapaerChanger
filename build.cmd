@echo off
setlocal
pushd "%~dp0"

echo Restoring dependencies...
dotnet restore WallpaperRotator.sln
if errorlevel 1 goto :failed

echo.
echo Building WallpaperRotator...
dotnet publish src\WallpaperRotator\WallpaperRotator.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -o outputs\WallpaperRotator-win-x64
if errorlevel 1 goto :failed

echo.
echo Build completed successfully.
echo EXE: %CD%\outputs\WallpaperRotator-win-x64\WallpaperRotator.exe
goto :finish

:failed
echo.
echo Build failed. See the errors above.

:finish
echo.
pause
popd
endlocal
