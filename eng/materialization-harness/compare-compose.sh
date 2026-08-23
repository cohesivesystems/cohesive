#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
temporary_dir="$(mktemp -d)"
trap 'rm -rf "$temporary_dir"' EXIT

export COHESIVE_HARNESS_POSTGRES_PASSWORD="compose-parity-only"
export COHESIVE_HARNESS_PGADMIN_PASSWORD="compose-parity-only"

normalize() {
  jq --sort-keys '
    del(.networks)
    | .configs |= with_entries(.value.content |= fromjson)
    | .services |= with_entries(
        .value |= (
          del(.healthcheck.test)
          | if .ports then .ports |= sort_by(.target, .published) else . end
          | if .volumes then .volumes |= sort_by(.target, .source) else . end
          | if .configs then .configs |= sort_by(.target, .source) else . end
        )
      )
  ' "$1"
}

docker compose \
  --project-name cohesive-materialization-local \
  --file "$script_dir/compose.yaml" \
  config --format json > "$temporary_dir/oracle.json"
docker compose \
  --project-name cohesive-materialization-local \
  --file "$script_dir/compose.generated.yaml" \
  config --format json > "$temporary_dir/generated.json"

normalize "$temporary_dir/oracle.json" > "$temporary_dir/oracle.normalized.json"
normalize "$temporary_dir/generated.json" > "$temporary_dir/generated.normalized.json"
diff --unified \
  "$temporary_dir/oracle.normalized.json" \
  "$temporary_dir/generated.normalized.json"
printf 'compose-parity=equivalent\n'
