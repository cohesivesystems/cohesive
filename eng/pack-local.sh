#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed="${COHESIVE_NUGET_LOCAL_FEED:-"$repo_root/../.feeds/nuget/cohesive-local"}"
base_version="${COHESIVE_VERSION_PREFIX:-0.1.0}"
stamp="$(date -u +%Y%m%d%H%M%S)"
version="${1:-"$base_version-dev.$stamp"}"

mkdir -p "$feed"

dotnet pack "$repo_root/Cohesive.sln" \
  --configuration Release \
  --output "$feed" \
  /p:PackageVersion="$version"

echo "$version"
