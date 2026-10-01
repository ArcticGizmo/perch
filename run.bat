@echo off
rem Runs the dev (Debug) build. With no dev tray running it starts the tray; with one running it acts as the
rem `perch` CLI against it:
rem
rem   run                    start the dev tray
rem   run -c                 continue this folder's most recent session
rem   run --resume ID        resume a specific session
rem   run --resume           open the resume picker
rem   run .                  new session in this folder
rem
rem Works from any folder (the project path is relative to this script, and the app runs in the caller's
rem folder). The "--" keeps Perch's arguments away from `dotnet run`, whose own -c/-r would swallow them.
dotnet run --project "%~dp0src\Perch.App" -f net10.0-windows10.0.19041.0 -- %*
