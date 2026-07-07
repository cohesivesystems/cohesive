#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
pack_destination="${COHESIVE_NPM_PACK_OUTPUT:-"$repo_root/tmp/npm-pack"}"

mkdir -p "$pack_destination"

cd "$repo_root"
corepack pnpm install --frozen-lockfile
corepack pnpm frontend:build
corepack pnpm -r --filter '@cohesivesystems/*' pack --pack-destination "$pack_destination"
