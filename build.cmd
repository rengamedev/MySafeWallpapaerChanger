@echo off
setlocal
pushd "%~dp0"

rem A running copy locks the EXE and publish fails at the very end, so check it up front.
set "EXE=outputs\WallpaperRotator-win-x64\WallpaperRotator.exe"
if exist "%EXE%" (
  2>nul (>>"%EXE%" call ) || (
    echo %EXE% is in use, most likely Wallpaper Rotator is running.
    echo Exit it from the tray icon and run build.cmd again.
    goto :failed
  )
)

echo Restoring dependencies...
dotnet restore WallpaperRotator.sln
if errorlevel 1 goto :failed

echo.
echo Running tests...
dotnet test WallpaperRotator.sln -c Release --no-restore
if errorlevel 1 goto :failed

echo.
echo Building WallpaperRotator...
rem ContinuousIntegrationBuild makes the EXE independent of the checkout folder, so its hash can match the CI build.
dotnet publish src\WallpaperRotator\WallpaperRotator.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:ContinuousIntegrationBuild=true ^
  -o outputs\WallpaperRotator-win-x64
if errorlevel 1 goto :failed

echo.
echo Build completed successfully.
echo EXE: %CD%\outputs\WallpaperRotator-win-x64\WallpaperRotator.exe
rem PSModulePath inherited from PowerShell 7 hides Get-FileHash from Windows PowerShell.
set "PSModulePath="
powershell -NoProfile -Command "(Get-FileHash '%EXE%' -Algorithm SHA256).Hash.ToLowerInvariant()"
goto :finish

:failed
echo.
echo Build failed. See the errors above.

:finish
echo.
pause
popd
endlocal
