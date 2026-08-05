@echo off
REM One-click runner for the Perch calendar probe.
REM Installs deps into your current Python, then runs the probe.
REM Pass extra args through, e.g.:  run.cmd --schema-only
setlocal

REM dfindexeddb pins python-snappy==0.6.1 (no Windows wheel on Python 3.11+), so install
REM it WITHOUT its deps, then install a compiler-free snappy/zstd ourselves.
py -3 -m pip install -q --no-deps dfindexeddb || goto :pipfail
py -3 -m pip install -q -r "%~dp0requirements.txt" || goto :pipfail

py -3 "%~dp0probe.py" %*
goto :eof

:pipfail
echo.
echo pip install failed. Make sure Python 3.10+ is installed and on PATH ("py -3 --version").
exit /b 1
