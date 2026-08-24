#!/usr/bin/env bash
# macOS/Linux sibling of run.bat: runs the Perch tray app from source.
#
#   ./run.sh                 # launch the tray app
#   ./run.sh render out      # headless render of every owner-drawn surface into out/
#   ./run.sh render out nord # ...for a specific theme id
#
# Any arguments are passed straight through to the app. On this host the head has only the plain
# net10.0 TFM (the Windows TFM is dropped off-Windows), so no -f is needed - unlike run.bat.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$repo_root"

exec dotnet run --project src/Perch.App -- "$@"
