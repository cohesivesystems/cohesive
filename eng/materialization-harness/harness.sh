#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
compose_file="$script_dir/compose.yaml"

if [[ -f "$script_dir/.env" ]]; then
  set -a
  # shellcheck disable=SC1091
  source "$script_dir/.env"
  set +a
fi

worktree_name="$(basename "$repo_root" | tr -cs '[:alnum:]' '-' | sed 's/^-*//;s/-*$//')"
worktree_hash="$(printf '%s' "$repo_root" | cksum | awk '{print $1}')"
export COHESIVE_HARNESS_PROJECT_NAME="${COHESIVE_HARNESS_PROJECT_NAME:-cohesive-materialization-${worktree_name}-${worktree_hash}}"
export COHESIVE_HARNESS_POSTGRES_PORT="${COHESIVE_HARNESS_POSTGRES_PORT:-55432}"
export COHESIVE_HARNESS_POSTGRES_DATABASE="${COHESIVE_HARNESS_POSTGRES_DATABASE:-cohesive_materialization}"
export COHESIVE_HARNESS_POSTGRES_USER="${COHESIVE_HARNESS_POSTGRES_USER:-cohesive}"
export COHESIVE_HARNESS_POSTGRES_PASSWORD="${COHESIVE_HARNESS_POSTGRES_PASSWORD:-cohesive-local-only}"
export COHESIVE_HARNESS_COSMOS_PORT="${COHESIVE_HARNESS_COSMOS_PORT:-58081}"
export COHESIVE_HARNESS_COSMOS_HEALTH_PORT="${COHESIVE_HARNESS_COSMOS_HEALTH_PORT:-58080}"
export COHESIVE_HARNESS_COSMOS_EXPLORER_PORT="${COHESIVE_HARNESS_COSMOS_EXPLORER_PORT:-58082}"
export COHESIVE_HARNESS_ELASTIC_PORT="${COHESIVE_HARNESS_ELASTIC_PORT:-59200}"
export COHESIVE_HARNESS_ELASTIC_JAVA_OPTS="${COHESIVE_HARNESS_ELASTIC_JAVA_OPTS:--Xms512m -Xmx512m}"
export COHESIVE_HARNESS_KIBANA_PORT="${COHESIVE_HARNESS_KIBANA_PORT:-55601}"
export COHESIVE_HARNESS_PGADMIN_PORT="${COHESIVE_HARNESS_PGADMIN_PORT:-55050}"
export COHESIVE_HARNESS_PGADMIN_EMAIL="${COHESIVE_HARNESS_PGADMIN_EMAIL:-harness@cohesivesystems.com}"
export COHESIVE_HARNESS_PGADMIN_PASSWORD="${COHESIVE_HARNESS_PGADMIN_PASSWORD:-cohesive-local-only}"
export COHESIVE_HARNESS_HOST_PORT="${COHESIVE_HARNESS_HOST_PORT:-59399}"

compose() {
  docker compose \
    --project-name "$COHESIVE_HARNESS_PROJECT_NAME" \
    --file "$compose_file" \
    "$@"
}

configure_runtime() {
  export COHESIVE_MATERIALIZATION_SCENARIO_PATH="$script_dir/scenarios/freight-baseline.json"
  export COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING="Host=localhost;Port=${COHESIVE_HARNESS_POSTGRES_PORT};Database=${COHESIVE_HARNESS_POSTGRES_DATABASE};Username=${COHESIVE_HARNESS_POSTGRES_USER};Password=${COHESIVE_HARNESS_POSTGRES_PASSWORD};Pooling=false"
  export COHESIVE_MATERIALIZATION_COSMOS_CONNECTION_STRING="AccountEndpoint=https://localhost:${COHESIVE_HARNESS_COSMOS_PORT}/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==;"
  export COHESIVE_MATERIALIZATION_COSMOS_DATABASE="cohesive-freight-harness"
  export COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT="http://localhost:${COHESIVE_HARNESS_ELASTIC_PORT}"
  export COHESIVE_MATERIALIZATION_HOST_URL="http://localhost:${COHESIVE_HARNESS_HOST_PORT}"
  export COHESIVE_POSTGRES_TEST_CONNECTION_STRING="$COHESIVE_MATERIALIZATION_POSTGRES_CONNECTION_STRING"
}

up() {
  compose up --detach --wait --wait-timeout 240
}

seed() {
  configure_runtime
  local seed_mode="${1:---cohesive}"
  dotnet run \
    --project "$script_dir/seed/Cohesive.MaterializationHarness.Seed.csproj" \
    --configuration Release \
    -- \
    "$seed_mode"
}

validate() {
  export COHESIVE_MATERIALIZATION_SCENARIO_PATH="$script_dir/scenarios/freight-baseline.json"
  dotnet run \
    --project "$script_dir/seed/Cohesive.MaterializationHarness.Seed.csproj" \
    --configuration Release \
    -- \
    --validate-only
}

verify() {
  configure_runtime
  dotnet run \
    --project "$script_dir/seed/Cohesive.MaterializationHarness.Seed.csproj" \
    --configuration Release \
    -- \
    --verify-only
}

materialize() {
  configure_runtime
  dotnet run \
    --project "$script_dir/materialize/Cohesive.MaterializationHarness.Materialize.csproj" \
    --configuration Release
}

process_host() {
  configure_runtime
  dotnet run \
    --project "$script_dir/host/Cohesive.MaterializationHarness.Host.csproj" \
    --configuration Release
}

process_command() {
  configure_runtime
  local command="$1"
  shift
  dotnet run \
    --project "$script_dir/host/Cohesive.MaterializationHarness.Host.csproj" \
    --configuration Release \
    -- \
    "$command" "$@"
}

verify_index() {
  configure_runtime
  curl --fail --silent --show-error \
    "${COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT}/_cat/aliases/freight-order-search-*?v"
  printf '\nPostgres alias count:\n'
  curl --fail --silent --show-error \
    "${COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT}/freight-order-search-postgres/_count?pretty"
  printf '\nCosmos alias count:\n'
  curl --fail --silent --show-error \
    "${COHESIVE_MATERIALIZATION_ELASTIC_ENDPOINT}/freight-order-search-cosmos/_count?pretty"
  printf '\n'
}

test_harness() {
  up
  seed --direct
  verify
  materialize
  seed --cohesive
  verify
  materialize
  configure_runtime
  dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
    --configuration Release \
    --filter "FullyQualifiedName~FreightOrderHarnessModelTests|FullyQualifiedName~FreightOrderRebuildPlanCompilerTests|FullyQualifiedName~FreightOrderMaterializationRelationTests|FullyQualifiedName~PostgresEntityRepositoryTests|FullyQualifiedName~PostgresProcessDurableStoreTests|FullyQualifiedName~PostgresMaterializationStateStoreTests|FullyQualifiedName~InMemoryProcessDurableStoreTests|FullyQualifiedName~ProcessExecutionCommandApiEndpointRouteBuilderExtensionsTests|FullyQualifiedName~TenantScopedMaterializationPagesBindExactPredicateAndRejectCrossTenantContinuation|FullyQualifiedName~PartitionedReaderRequiresMatchingRuntimeAndPhysicalScopes|FullyQualifiedName~LocalPostgres_TenantScopedMaterializationPagesStayWithinTheExactPartition" \
    --logger "console;verbosity=minimal"
}

usage() {
  cat <<'USAGE'
Usage: eng/materialization-harness/harness.sh <command>

Commands:
  up       Start the pinned databases and browser UIs; wait for readiness and preserve volumes.
  seed     Replace both source databases through Cohesive.Storage repositories (default).
  seed-direct Replace both source databases through raw Npgsql/Cosmos SDK calls as an independent oracle.
  validate Validate the canonical scenario journal without starting Docker.
  verify   Verify that both source databases still equal the journal; do not mutate them.
  materialize Build and atomically promote equivalent Postgres and Cosmos Elasticsearch generations.
  host     Run the restartable Process/API host in the foreground.
  process-start [provider|all] Start provider rebuild Processes through the canonical SDK dispatcher.
  process-inspect [provider|all] Inspect durable Process status through the SDK dispatcher.
  process-explain Read its canonical execution explanation through the SDK dispatcher.
  process-traces Read its retained canonical Process traces through the SDK dispatcher.
  process-pause Pause the current attempt at its next page boundary.
  process-continue Continue the same paused attempt and retained continuations.
  process-restart Abandon the current candidate and start a fresh attempt/generation.
  process-cancel Cooperatively cancel the Process and abandon its candidate generation.
  process-limits <provider> <items> Update the canonical rebuild batch-item limit.
  verify-index Show active generation aliases and document counts without mutating Elasticsearch.
  test     Start, seed, materialize, and run the focused verification suite.
  status   Show service and health state.
  logs     Follow service logs.
  down     Stop services while preserving volumes and checkpoints.
  reset    Destroy this Compose project's volumes, restart, and deterministically reseed.
  env      Print non-secret runtime endpoints and the Compose project identity.
USAGE
}

command="${1:-}"
case "$command" in
  up)
    up
    ;;
  seed)
    up
    seed --cohesive
    ;;
  seed-direct)
    up
    seed --direct
    ;;
  validate)
    validate
    ;;
  verify)
    up
    verify
    ;;
  materialize)
    up
    materialize
    ;;
  host)
    up
    process_host
    ;;
  process-start)
    up
    process_command --start "${2:-all}"
    ;;
  process-inspect)
    up
    process_command --inspect "${2:-all}"
    ;;
  process-explain)
    up
    process_command --explain "${2:-all}"
    ;;
  process-traces)
    up
    process_command --traces "${2:-all}"
    ;;
  process-pause)
    up
    process_command --pause "${2:-all}"
    ;;
  process-continue)
    up
    process_command --continue "${2:-all}"
    ;;
  process-restart)
    up
    process_command --restart-attempt "${2:-all}"
    ;;
  process-cancel)
    up
    process_command --cancel "${2:-all}"
    ;;
  process-limits)
    if [[ "$#" -ne 3 ]]; then
      usage
      exit 2
    fi
    up
    process_command --update-limits "$2" "$3"
    ;;
  verify-index)
    up
    verify_index
    ;;
  test)
    test_harness
    ;;
  status)
    compose ps
    ;;
  logs)
    compose logs --follow
    ;;
  down)
    compose down --remove-orphans
    ;;
  reset)
    compose down --volumes --remove-orphans
    up
    seed
    ;;
  env)
    configure_runtime
    printf 'project=%s\n' "$COHESIVE_HARNESS_PROJECT_NAME"
    printf 'postgres=localhost:%s/%s\n' "$COHESIVE_HARNESS_POSTGRES_PORT" "$COHESIVE_HARNESS_POSTGRES_DATABASE"
    printf 'cosmos=https://localhost:%s/\n' "$COHESIVE_HARNESS_COSMOS_PORT"
    printf 'cosmos-health=http://localhost:%s/ready\n' "$COHESIVE_HARNESS_COSMOS_HEALTH_PORT"
    printf 'cosmos-explorer=http://localhost:%s/\n' "$COHESIVE_HARNESS_COSMOS_EXPLORER_PORT"
    printf 'elasticsearch=http://localhost:%s\n' "$COHESIVE_HARNESS_ELASTIC_PORT"
    printf 'elastic-postgres-alias=freight-order-search-postgres\n'
    printf 'elastic-cosmos-alias=freight-order-search-cosmos\n'
    printf 'kibana=http://localhost:%s/\n' "$COHESIVE_HARNESS_KIBANA_PORT"
    printf 'pgadmin=http://localhost:%s/\n' "$COHESIVE_HARNESS_PGADMIN_PORT"
    printf 'pgadmin-email=%s\n' "$COHESIVE_HARNESS_PGADMIN_EMAIL"
    printf 'process-host=http://localhost:%s/\n' "$COHESIVE_HARNESS_HOST_PORT"
    ;;
  *)
    usage
    exit 2
    ;;
esac
