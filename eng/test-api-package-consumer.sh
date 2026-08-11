#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
version="${1:?Usage: test-api-package-consumer.sh <package-version>}"
project="$repo_root/eng/package-smoke/Cohesive.Api.Consumer/Cohesive.Api.Consumer.csproj"
assets="$repo_root/eng/package-smoke/Cohesive.Api.Consumer/obj/project.assets.json"
package="$feed/Cohesive.Api.$version.nupkg"

if [[ ! -f "$package" ]]; then
  echo "Cohesive.Api package not found at '$package'." >&2
  exit 1
fi

dotnet build "$project" \
  --configuration Release \
  --property:CohesivePackageVersion="$version" \
  --property:CohesivePackageFeed="$feed"

if grep --fixed-strings --quiet 'Microsoft.AspNetCore.App' "$assets"; then
  echo "Cohesive.Api package consumer unexpectedly acquired Microsoft.AspNetCore.App." >&2
  exit 1
fi

echo "Cohesive.Api $version package consumer built without Microsoft.AspNetCore.App."
