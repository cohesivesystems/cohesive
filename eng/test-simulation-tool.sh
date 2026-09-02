#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
version="${1:?Usage: test-simulation-tool.sh <package-version>}"
project="$repo_root/eng/package-smoke/Cohesive.Simulation.Consumer/Cohesive.Simulation.Consumer.csproj"
package="$feed/Cohesive.Simulation.Cli.$version.nupkg"
tool_directory="$(mktemp -d)"
work_directory="$(mktemp -d)"

cleanup() {
  rm -rf "$tool_directory" "$work_directory"
}
trap cleanup EXIT

if [[ ! -f "$package" ]]; then
  echo "Cohesive.Simulation.Cli package not found at '$package'." >&2
  exit 1
fi

dotnet run \
  --project "$project" \
  --configuration Release \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- emit "$work_directory/world.json" "$work_directory/world.manifest.json"

dotnet tool install Cohesive.Simulation.Cli \
  --version "$version" \
  --tool-path "$tool_directory" \
  --add-source "$feed"

"$tool_directory/cohesive-sim" provision \
  --world "$work_directory/world.json" \
  --seed 42 \
  --target package-smoke/cli \
  --out "$work_directory/world.jsonl" \
  --batch-size 1

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- verify "$work_directory/world.jsonl" "$work_directory/world.manifest.json"

echo "Cohesive.Simulation.Cli $version package installed and provisioned a verified portable world artifact."
