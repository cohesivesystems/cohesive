#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
registry="${COHESIVE_NPM_REGISTRY:-http://localhost:4873/}"
base_version="${COHESIVE_VERSION_PREFIX:-0.1.0}"
stamp="$(date -u +%Y%m%d%H%M%S)"
version="${1:-"$base_version-dev.$stamp"}"
tag="${COHESIVE_NPM_TAG:-dev}"
dry_run="${COHESIVE_NPM_DRY_RUN:-false}"
backup_dir="$(mktemp -d)"
package_jsons=()

while IFS= read -r package_json; do
  package_jsons+=("$package_json")
done < <(cd "$repo_root" && find src/frontend -maxdepth 2 -name package.json | sort)

restore_package_jsons() {
  for package_json in "${package_jsons[@]}"; do
    cp "$backup_dir/$package_json" "$repo_root/$package_json"
  done

  rm -rf "$backup_dir"
}

for package_json in "${package_jsons[@]}"; do
  mkdir -p "$backup_dir/$(dirname "$package_json")"
  cp "$repo_root/$package_json" "$backup_dir/$package_json"
done

trap restore_package_jsons EXIT

cd "$repo_root"
corepack pnpm install --frozen-lockfile
node ./eng/set-version.mjs "$version" --ts-only
corepack pnpm frontend:build

if [[ "$dry_run" == "true" ]]; then
  pack_destination="${COHESIVE_NPM_PACK_OUTPUT:-"$repo_root/tmp/npm-pack-local"}"
  mkdir -p "$pack_destination"
  corepack pnpm -r --filter '@cohesivesystems/*' pack --pack-destination "$pack_destination"
else
  corepack pnpm -r --filter '@cohesivesystems/*' publish --registry "$registry" --tag "$tag" --no-git-checks
fi

echo "$version"
