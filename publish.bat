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

echo Writing checksums ...

:: Mirrors the SHA256SUMS.txt that release.yml publishes, so install.ps1 can be pointed at a local pack and
:: a hand-uploaded release still ships checksums. Written LF-terminated with lower-case hex in sha256sum's
:: own format, so `sha256sum -c SHA256SUMS.txt` validates it as-is. Note this hashes EVERYTHING currently in
:: releases\ -- a local dir accumulates older versions' nupkgs, unlike CI's clean per-run artifact set.
powershell -NoProfile -Command "$d = Resolve-Path 'releases'; $lines = Get-ChildItem -File -LiteralPath $d -Exclude 'SHA256SUMS.txt' | Sort-Object Name | ForEach-Object { '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name }; [System.IO.File]::WriteAllText((Join-Path $d 'SHA256SUMS.txt'), ($lines -join [char]10) + [char]10); Write-Host ('  ' + @($lines).Count + ' files hashed')"

if %ERRORLEVEL% neq 0 (
    echo Checksum generation failed.
    exit /b %ERRORLEVEL%
)

echo.
echo Release artifacts ready in: releases\
echo Upload to: https://github.com/ArcticGizmo/perch/releases/new?tag=v%VERSION%
echo   Include SHA256SUMS.txt -- install.ps1 refuses to install a release without it.
