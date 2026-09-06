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
  -- emit "$work_directory/world.json"

dotnet tool install Cohesive.Simulation.Cli \
  --version "$version" \
  --tool-path "$tool_directory" \
  --add-source "$feed"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- emit-catalog "$work_directory/identities.catalog.json"

"$tool_directory/cohesive-sim" catalog verify \
  --catalog "$work_directory/identities.catalog.json" \
  > "$work_directory/identities.catalog.verification.json"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- verify-catalog \
  "$work_directory/identities.catalog.json" \
  "$work_directory/identities.catalog.verification.json"

"$tool_directory/cohesive-sim" manifest \
  --world "$work_directory/world.json" \
  --seed 42 \
  --out "$work_directory/world.manifest.json"

"$tool_directory/cohesive-sim" provision \
  --manifest "$work_directory/world.manifest.json" \
  --target package-smoke/cli \
  --out "$work_directory/world.jsonl" \
  --batch-size 1

"$tool_directory/cohesive-sim" verify \
  --manifest "$work_directory/world.manifest.json" \
  --jsonl "$work_directory/world.jsonl" \
  > "$work_directory/world.verification.json"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- verify \
  "$work_directory/world.jsonl" \
  "$work_directory/world.manifest.json" \
  "$work_directory/world.verification.json"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- emit-relationship "$work_directory/relationship.world.json"

"$tool_directory/cohesive-sim" manifest \
  --relationship-world "$work_directory/relationship.world.json" \
  --seed 42 \
  --out "$work_directory/relationship.manifest.json"

"$tool_directory/cohesive-sim" provision \
  --manifest "$work_directory/relationship.manifest.json" \
  --target package-smoke/relationship-cli \
  --out "$work_directory/relationship.jsonl" \
  --batch-size 1

"$tool_directory/cohesive-sim" verify \
  --manifest "$work_directory/relationship.manifest.json" \
  --jsonl "$work_directory/relationship.jsonl" \
  > "$work_directory/relationship.verification.json"

dotnet run \
  --project "$project" \
  --configuration Release \
  --no-restore \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed" \
  -- verify-relationship \
  "$work_directory/relationship.jsonl" \
  "$work_directory/relationship.manifest.json" \
  "$work_directory/relationship.verification.json"

echo "Cohesive.Simulation packages $version installed and verified catalogs, core artifacts, and relationship-world artifacts."
