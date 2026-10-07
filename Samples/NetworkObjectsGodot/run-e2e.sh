#!/usr/bin/env bash
set -euo pipefail
sample="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# GODOT_HOME points at the .NET distribution containing GodotSharp/Tools/nupkgs.
dotnet build "$sample/Godot/NetworkObjectsGodot.csproj" --nologo -m:1 -nodeReuse:false -p:UseSharedCompilation=false -p:NuGetAudit=false
python3 "$sample/run-e2e.py" "$@"
