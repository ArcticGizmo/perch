@echo off
setlocal

:: Read version from the Avalonia csproj if not passed as argument
if not "%~1"=="" (
    set VERSION=%~1
) else (
    for /f "tokens=*" %%i in ('powershell -NoProfile -Command "(Select-Xml -Path src\Perch.App\Perch.App.csproj -XPath \"//Version\").Node.InnerText"') do set VERSION=%%i
)

if "%VERSION%"=="" (
    echo Error: Could not determine version. Pass as argument: publish.bat 1.2.3
    exit /b 1
)

echo Building Perch v%VERSION%...

dotnet publish src\Perch.App\Perch.App.csproj -c Release -f net10.0-windows10.0.19041.0 -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=embedded ^
    -o publish\

if %ERRORLEVEL% neq 0 (
    echo Build failed.
    exit /b %ERRORLEVEL%
)

echo Publishing perch-hook (NativeAOT) ...

:: perch-hook is the self-managed Claude Code hook binary. Publish it into the SAME dir as perch.exe
:: so Velopack packs the two together; the app copies it to a stable per-user path on launch. NativeAOT
:: gives the best hook cold-start (it fires on every tool call), but needs the Visual Studio "Desktop
:: development with C++" workload for the native linker. When that's missing (common on a fresh dev box)
:: the AOT publish can't link, so fall back to a self-contained single-file build below so LOCAL packaging
:: still works. CI releases (release.yml, on a runner that has the workload) stay AOT.
dotnet publish src\Perch.Hook\Perch.Hook.csproj -c Release -r win-x64 -o publish\

if %ERRORLEVEL% neq 0 (
    echo.
    echo NativeAOT publish failed - falling back to a self-contained single-file perch-hook so local
    echo packaging can proceed. This local build has a slower hook cold-start than a CI/AOT release;
    echo install the C++ workload from https://aka.ms/nativeaot-prerequisites for an AOT-equivalent build.
    echo.
    dotnet publish src\Perch.Hook\Perch.Hook.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishAot=false -p:EnableCompressionInSingleFile=true -o publish\
)

if %ERRORLEVEL% neq 0 (
    echo perch-hook publish failed.
    exit /b %ERRORLEVEL%
)

echo Packaging ...

dnx vpk pack --packId Perch --packTitle "Perch" --packVersion %VERSION% --packDir publish\ --mainExe perch.exe --outputDir releases\

if %ERRORLEVEL% neq 0 (
    echo Pack failed. Is the vpk CLI installed? Run: dotnet tool install -g vpk
    exit /b %ERRORLEVEL%
)

echo.
echo Release artifacts ready in: releases\
echo Upload to: https://github.com/ArcticGizmo/perch/releases/new?tag=v%VERSION%
