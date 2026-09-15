#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
version="${1:?Usage: test-pulumi-azure-package-consumer.sh <package-version>}"
package="$feed/Cohesive.Adapters.Pulumi.Azure.$version.nupkg"
if [[ ! -f "$package" ]]; then
  echo "Cohesive.Adapters.Pulumi.Azure package not found at '$package'." >&2
  exit 1
fi

# Reuse semantic tests against packages only: no source-project references or Azure credentials.
dotnet test "$repo_root/eng/package-smoke/Cohesive.Adapters.Pulumi.Azure.Consumer/Cohesive.Adapters.Pulumi.Azure.Consumer.csproj" \
  --configuration Release -m:1 -nodeReuse:false \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed"
