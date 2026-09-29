#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
version="${1:?Usage: test-pulumi-package-consumer.sh <package-version>}"
package="$feed/Cohesive.Adapters.Pulumi.$version.nupkg"
if [[ ! -f "$package" ]]; then
  echo "Cohesive.Adapters.Pulumi package not found at '$package'." >&2
  exit 1
fi

# Reuse semantic tests against packages only: no source-project references or Azure credentials.
dotnet test "$repo_root/eng/package-smoke/Cohesive.Adapters.Pulumi.Consumer/Cohesive.Adapters.Pulumi.Consumer.csproj" \
  --configuration Release -m:1 -nodeReuse:false \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed"
