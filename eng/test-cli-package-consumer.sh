#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
version="${1:?Usage: test-cli-package-consumer.sh <package-version>}"
project="$repo_root/eng/package-smoke/Cohesive.Cli.Consumer/Cohesive.Cli.Consumer.csproj"
assets="$repo_root/eng/package-smoke/Cohesive.Cli.Consumer/obj/project.assets.json"
package="$feed/Cohesive.Cli.$version.nupkg"

if [[ ! -f "$package" ]]; then
  echo "Cohesive.Cli package not found at '$package'." >&2
  exit 1
fi

dotnet run \
  --project "$project" \
  --configuration Release \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed"

for excluded_package in Cohesive.Host Cohesive.Relations Cohesive.Transitions Cohesive.Processes Cohesive.Storage; do
  if grep --fixed-strings --quiet "\"$excluded_package/" "$assets"; then
    echo "Cohesive.Cli package consumer unexpectedly acquired $excluded_package." >&2
    exit 1
  fi
done

echo "Cohesive.Cli $version package consumer executed without host or domain-runtime packages."
