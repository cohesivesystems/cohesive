#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
feed_root="${COHESIVE_NPM_FEED_ROOT:-"$repo_root/../.feeds/npm/verdaccio"}"
listen="${COHESIVE_NPM_LISTEN:-127.0.0.1:4873}"
config_path="$feed_root/config.yaml"

mkdir -p "$feed_root/storage"

cat > "$config_path" <<YAML
storage: $feed_root/storage

auth:
  htpasswd:
    file: $feed_root/htpasswd

uplinks:
  npmjs:
    url: https://registry.npmjs.org/

packages:
  '@cohesivesystems/*':
    access: \$all
    publish: \$all
    unpublish: \$all

  '**':
    access: \$all
    publish: \$authenticated
    unpublish: \$authenticated
    proxy: npmjs

log:
  type: stdout
  format: pretty
  level: http
YAML

corepack pnpm dlx verdaccio --config "$config_path" --listen "$listen"
