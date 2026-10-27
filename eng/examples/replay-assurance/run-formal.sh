#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
if [ "$#" -gt 1 ]; then echo 'Usage: run-formal.sh [new-output-directory]' >&2; exit 2; fi
output="${1:-$repo_root/eng/examples/replay-assurance/artifacts/tlc-$(date +%Y%m%d-%H%M%S)-$$}"
# Avoid mixing retained evidence from different runs.
if [ -e "$output" ]; then echo 'Output directory must not exist.' >&2; exit 2; fi
mkdir -p "$output"
output="$(cd "$output" && pwd)"
jar="$output/tla2tools-1.7.4.jar"
curl --fail --location --silent --show-error \
    https://github.com/tlaplus/tlaplus/releases/download/v1.7.4/tla2tools.jar -o "$jar"
# check.py verifies the pinned SHA-256 before executing the downloaded tool.
dotnet build eng/examples/replay-assurance -c Release --nologo --disable-build-servers -m:1
dotnet run --project eng/examples/replay-assurance -c Release --no-build -- --export-tlc "$output"
cp eng/examples/replay-assurance/formal/README.md "$output/correspondence.md"
python3 eng/examples/replay-assurance/formal/check.py "$output" "$jar"
echo "Retained bounded model evidence: $output"
