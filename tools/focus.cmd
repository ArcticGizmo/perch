@echo off
rem Convenience wrapper: tools\focus.cmd <pid> [-Project name] [-NoFocus]
rem Tests whether Perch can focus the terminal hosting a Claude session (see focus.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0focus.ps1" %*
