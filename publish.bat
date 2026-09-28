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

rem Explorer caches an exe's icon by path, so a rebuilt Snipyard.exe keeps showing
rem the old one after the icon changes. Tell the shell this file and the icon
rem associations changed so it draws the new one.
powershell -NoProfile -Command "Add-Type -Namespace W -Name S -MemberDefinition '[DllImport(\"shell32.dll\", CharSet = CharSet.Unicode)] public static extern void SHChangeNotify(int e, uint f, string a, IntPtr b);'; [W.S]::SHChangeNotify(0x2000, 5, (Resolve-Path '.\publish-single\Snipyard.exe').Path, [IntPtr]::Zero); [W.S]::SHChangeNotify(0x8000000, 0, $null, [IntPtr]::Zero)"
ie4uinit.exe -show >nul 2>&1

echo.
powershell -NoProfile -Command "$f = Get-Item '.\publish-single\Snipyard.exe'; Write-Host ('  ' + $f.FullName); Write-Host ('  {0:N1} MB    version {1}' -f ($f.Length / 1MB), $f.VersionInfo.FileVersion)"
echo.
echo Done.
echo.
pause
