#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
registry="${COHESIVE_NPM_REGISTRY:-http://localhost:4873/}"
base_version="${COHESIVE_VERSION_PREFIX:-0.1.0}"
stamp="$(date -u +%Y%m%d%H%M%S)"
version="${1:-"$base_version-dev.$stamp"}"

cd "$repo_root"
node ./eng/set-version.mjs "$version" --ts-only
corepack pnpm install
corepack pnpm frontend:build
corepack pnpm -r --filter '@cohesive/*' publish --registry "$registry" --tag dev --no-git-checks

echo "$version"
