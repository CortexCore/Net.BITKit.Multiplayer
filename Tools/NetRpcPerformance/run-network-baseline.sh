#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
phase=${1:-baseline}
runner=${2:-Artifacts/bin/NetRpcPerformance/Release/net10.0/NetRpcPerformance.dll}
root="Artifacts/NetworkModuleBaseline/$phase"
mkdir -p "$root"
dotnet --info > "$root/dotnet-info.txt"
if [[ -e .git ]]; then
  git rev-parse HEAD > "$root/base-commit.txt"
  git diff > "$root/tracked-working-tree.patch"
else
  printf '%s\n' 'Source export: use the included source-manifest.json; no live Git revision available.' > "$root/base-commit.txt"
fi
sha256sum "$runner" "$(dirname "$runner")/Net.BITKit.Multiplayer.dll" > "$root/assembly-sha256.txt"
for repetition in 1 2 3; do
  dotnet "$runner" --transport direct --clients 2 --profile all \
    --iterations 600 --warmup 300 --sizes 128 --rate 300 --idle-ms 3000 \
    --timeout-seconds 60 --output "$root/r$repetition" \
    > "$root/r$repetition.log" 2>&1
  cat "$root/r$repetition.log"
done
