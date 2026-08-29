@echo off
setlocal

rem Publishes the self-contained, single-file Release build.
rem Works from anywhere: it runs out of its own folder.

cd /d "%~dp0"

echo Publishing Release (win-x64, self-contained, single file)
echo.

dotnet publish -c Release -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:DebugType=none ^
    -o ".\publish-single"

if errorlevel 1 (
    echo.
    echo Publish FAILED.
    echo.
    pause
    exit /b 1
)

rem Skia and HarfBuzz bring their own .pdb files, and DebugType=none only covers
rem the managed side, so those land beside the executable and add around 100 MB
rem that a release has no use for.
if exist ".\publish-single\*.pdb" del /q ".\publish-single\*.pdb"

echo.
powershell -NoProfile -Command "$f = Get-Item '.\publish-single\Claucraft.exe'; Write-Host ('  ' + $f.FullName); Write-Host ('  {0:N1} MB    version {1}' -f ($f.Length / 1MB), $f.VersionInfo.FileVersion)"
echo.
echo Done.
echo.
pause
