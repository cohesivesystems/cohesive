#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
compose_file="$script_dir/.runtime/compose.yaml"
infra_generator_built=false
aspire_apphost="$script_dir/apphost/Cohesive.MaterializationHarness.AppHost.csproj"

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
  generate_infra --runtime >/dev/null
  docker compose \
    --project-name "$COHESIVE_HARNESS_PROJECT_NAME" \
    --file "$compose_file" \
    "$@"
}

generate_infra() {
  if [[ "$infra_generator_built" != "true" ]]; then
    dotnet build \
      "$script_dir/infra/Cohesive.MaterializationHarness.Infra.csproj" \
      --configuration Release \
      --maxcpucount:1 \
      --property:UseSharedCompilation=false \
      --nodeReuse:false
    infra_generator_built=true
  fi
  dotnet \
    "$script_dir/infra/bin/Release/net10.0/Cohesive.MaterializationHarness.Infra.dll" \
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

aspire_cli() {
  ASPIRE_CLI_TELEMETRY_OPTOUT=1 dnx --yes aspire.cli@13.5.2 -- \
    --non-interactive \
    --nologo \
    "$@"
}

aspire_up() {
  local profile="${1:-interactive}"
  if [[ "$profile" != "interactive" && "$profile" != "isolated" ]]; then
    printf 'aspire-up profile must be interactive or isolated.\n' >&2
    exit 2
  fi
  export COHESIVE_HARNESS_ASPIRE_PROFILE="$profile"
  aspire_cli start --apphost "$aspire_apphost" --format Json
  for resource in postgres cosmos elasticsearch pgadmin kibana; do
    aspire_cli wait "$resource" --apphost "$aspire_apphost" --status healthy --timeout 240
  done
}

aspire_command() {
  aspire_cli resource materialization-workflow "$1" --apphost "$aspire_apphost"
}

up() {
  if [[ "${COHESIVE_HARNESS_SKIP_INFRA_UP:-false}" == "true" ]]; then
    return
  fi
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

mutate() {
  configure_runtime
  dotnet run \
    --project "$script_dir/seed/Cohesive.MaterializationHarness.Seed.csproj" \
    --configuration Release \
    -- \
    --apply-changes
}

verify_final() {
  configure_runtime
  dotnet run \
    --project "$script_dir/seed/Cohesive.MaterializationHarness.Seed.csproj" \
    --configuration Release \
    -- \
    --verify-final
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

matrix_tools_built=false

build_matrix_tools() {
  if [[ "$matrix_tools_built" == "true" ]]; then
    return
  fi
  dotnet build \
    "$script_dir/host/Cohesive.MaterializationHarness.Host.csproj" \
    --configuration Release
  dotnet build \
    "$script_dir/supervise/Cohesive.MaterializationHarness.Supervise.csproj" \
    --configuration Release
  matrix_tools_built=true
}

matrix_cells() {
  dotnet \
    "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
    catalog \
    "$1"
}

failure_test() {
  local provider="${1:-postgres}"
  local boundary="${2:-AfterTargetBatch}"
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="$script_dir/artifacts/failure-$run_id"

  compose down --volumes --remove-orphans
  up
  seed --cohesive
  configure_runtime
  export COHESIVE_MATERIALIZATION_REPOSITORY_ROOT="$repo_root"
  dotnet build \
    "$script_dir/host/Cohesive.MaterializationHarness.Host.csproj" \
    --configuration Release
  dotnet build \
    "$script_dir/supervise/Cohesive.MaterializationHarness.Supervise.csproj" \
    --configuration Release
  dotnet \
    "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
    resume \
    "$provider" \
    "$boundary" \
    "$artifact_root/resume"
  dotnet \
    "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
    restart-attempt \
    "$provider" \
    "$boundary" \
    "$artifact_root/restart-attempt"
  printf 'failure-artifacts=%s\n' "$artifact_root"
}

control_equivalence_test() {
  local provider="${1:-postgres}"
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="$script_dir/artifacts/control-equivalence-$run_id"

  compose down --volumes --remove-orphans
  up
  seed --cohesive
  configure_runtime
  export COHESIVE_MATERIALIZATION_REPOSITORY_ROOT="$repo_root"
  dotnet build \
    "$script_dir/host/Cohesive.MaterializationHarness.Host.csproj" \
    --configuration Release
  dotnet build \
    "$script_dir/supervise/Cohesive.MaterializationHarness.Supervise.csproj" \
    --configuration Release
  dotnet \
    "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
    control-equivalence \
    "$provider" \
    "$artifact_root"
  printf 'control-equivalence-artifacts=%s\n' "$artifact_root"
}

source_matrix_test() {
  local requested_provider="${1:-all}"
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="${2:-$script_dir/artifacts/source-matrix-$run_id}"
  local providers=()

  build_matrix_tools
  while IFS= read -r provider; do
    if [[ "$requested_provider" == "all" || "$requested_provider" == "$provider" ]]; then
      providers+=("$provider")
    fi
  done < <(matrix_cells source-providers)
  if [[ "${#providers[@]}" -eq 0 ]]; then
    printf 'source-matrix-test provider must be a catalog provider or all.\n' >&2
    exit 2
  fi
  for provider in "${providers[@]}"; do
    compose down --volumes --remove-orphans
    up
    seed --cohesive
    configure_runtime
    export COHESIVE_MATERIALIZATION_REPOSITORY_ROOT="$repo_root"
    dotnet \
      "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
      source-matrix \
      "$provider" \
      "$artifact_root/$provider"
  done
  if [[ "${#providers[@]}" -eq 2 ]]; then
    cmp \
      "$artifact_root/postgres/final-documents.json" \
      "$artifact_root/cosmos/final-documents.json"
  fi
  printf 'source-matrix-artifacts=%s\n' "$artifact_root"
}

elastic_failure_test() {
  local provider="${1:-postgres}"
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="${2:-$script_dir/artifacts/elastic-failure-$run_id}"
  local faults=()
  local provider_found=false

  build_matrix_tools
  while IFS= read -r candidate; do
    if [[ "$provider" == "$candidate" ]]; then
      provider_found=true
    fi
  done < <(matrix_cells source-providers)
  if [[ "$provider_found" != "true" ]]; then
    printf 'elastic-failure-test provider must be a catalog provider.\n' >&2
    exit 2
  fi
  while IFS= read -r fault; do
    faults+=("$fault")
  done < <(matrix_cells elastic-failures)
  for fault in "${faults[@]}"; do
    compose down --volumes --remove-orphans
    up
    seed --cohesive
    configure_runtime
    export COHESIVE_MATERIALIZATION_REPOSITORY_ROOT="$repo_root"
    dotnet \
      "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
      elastic-failure \
      "$provider" \
      "$fault" \
      "$artifact_root/$fault"
  done
  printf 'elastic-failure-artifacts=%s\n' "$artifact_root"
}

compatibility_drift_test() {
  local requested_provider="${1:-all}"
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="${2:-$script_dir/artifacts/compatibility-drift-$run_id}"
  local providers=()

  build_matrix_tools
  while IFS= read -r provider; do
    if [[ "$requested_provider" == "all" || "$requested_provider" == "$provider" ]]; then
      providers+=("$provider")
    fi
  done < <(matrix_cells source-providers)
  if [[ "${#providers[@]}" -eq 0 ]]; then
    printf 'compatibility-drift-test provider must be a catalog provider or all.\n' >&2
    exit 2
  fi
  for provider in "${providers[@]}"; do
    compose down --volumes --remove-orphans
    up
    seed --cohesive
    configure_runtime
    export COHESIVE_MATERIALIZATION_REPOSITORY_ROOT="$repo_root"
    dotnet \
      "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
      compatibility-drift \
      "$provider" \
      "$artifact_root/$provider"
  done
  printf 'compatibility-drift-artifacts=%s\n' "$artifact_root"
}

matrix_test() {
  local run_id
  run_id="$(date -u +%Y%m%dT%H%M%SZ)-$$"
  local artifact_root="$script_dir/artifacts/matrix-$run_id"

  build_matrix_tools
  source_matrix_test all "$artifact_root/source"
  elastic_failure_test postgres "$artifact_root/elastic"
  compatibility_drift_test all "$artifact_root/drift"
  dotnet \
    "$script_dir/supervise/bin/Release/net10.0/Cohesive.MaterializationHarness.Supervise.dll" \
    aggregate-manifest \
    "$artifact_root"
  printf 'matrix-artifacts=%s\n' "$artifact_root"
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
  mutate
  verify_final
  mutate
  verify_final
  configure_runtime
  dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
    --configuration Release \
    --filter "FullyQualifiedName~MaterializationConformanceRunnerTests|FullyQualifiedName~FreightScenarioJournalTests|FullyQualifiedName~FreightOrderHarnessModelTests|FullyQualifiedName~FreightOrderRebuildPlanCompilerTests|FullyQualifiedName~FreightOrderMaterializationInverseExecutorTests|FullyQualifiedName~LogicalReplication_FixedPartitionFiltersRowsAndProjectsPartitionMoves|FullyQualifiedName~PostgresEntityRepositoryTests|FullyQualifiedName~PostgresProcessDurableStoreTests|FullyQualifiedName~PostgresMaterializationStateStoreTests|FullyQualifiedName~InMemoryProcessDurableStoreTests|FullyQualifiedName~ProcessExecutionCommandApiEndpointRouteBuilderExtensionsTests|FullyQualifiedName~MaterializationHarnessControlScenarioResultTests|FullyQualifiedName~TenantScopedMaterializationPagesBindExactPredicateAndRejectCrossTenantContinuation|FullyQualifiedName~PartitionedReaderRequiresMatchingRuntimeAndPhysicalScopes|FullyQualifiedName~LocalPostgres_TenantScopedMaterializationPagesStayWithinTheExactPartition" \
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
  infra-generate Regenerate Compose YAML and its exact provenance manifest from Cohesive.Infra.
  infra-check Fail when either checked-in generated artifact differs from the canonical realization.
  infra-parity Compare the generated default artifact with the handwritten Compose parity oracle.
  aspire-up [interactive|isolated] Start the canonical topology through Aspire and wait for every service.
  aspire-status Inspect live Aspire resource identity, endpoints, readiness, and dashboard links.
  aspire-logs [resource] Read or follow Aspire/DCP resource logs.
  aspire-seed Execute the canonical seed operation through the Aspire UI/API command surface.
  aspire-materialize Execute the canonical materialization operation through Aspire.
  aspire-verify Execute the canonical read-only verification operation through Aspire.
  aspire-test Start, seed, verify, materialize, and verify through the Aspire interpretation.
  aspire-stop Stop the AppHost without deleting interactive named volumes.
  verify   Verify that both source databases still equal the journal; do not mutate them.
  mutate   Apply the deterministic incremental journal suffix to both source replicas.
  verify-final Verify both source replicas equal the journal's final semantic state.
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
  process-evidence <provider> [generation] Capture bounded Process, checkpoint, and target evidence.
  failure-test [provider] [boundary] Clean-reset, kill/restart the real host, and emit bounded artifacts.
  control-equivalence-test [provider] Clean-reset and compare SDK/HTTP control semantics.
  source-matrix-test [provider|all] Clean-reset and prove replay, ordering, and fencing for real sources.
  elastic-failure-test [provider] Clean-reset and prove Elastic rejection and promotion recovery.
  compatibility-drift-test [provider|all] Fail closed on retained plan, binding, schema, generation, and cursor drift.
  matrix-test Run every source, Elastic, and compatibility cell and write one validated aggregate manifest.
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
  infra-generate)
    generate_infra
    ;;
  infra-check)
    generate_infra --check
    "$script_dir/compare-compose.sh"
    ;;
  infra-parity)
    "$script_dir/compare-compose.sh"
    ;;
  aspire-up)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    aspire_up "${2:-interactive}"
    ;;
  aspire-status)
    aspire_cli describe --apphost "$aspire_apphost" --format Table
    ;;
  aspire-logs)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    if [[ "$#" -eq 2 ]]; then
      aspire_cli logs "$2" --apphost "$aspire_apphost" --tail 200
    else
      aspire_cli logs --apphost "$aspire_apphost" --tail 200
    fi
    ;;
  aspire-seed)
    aspire_command seed
    ;;
  aspire-materialize)
    aspire_command materialize
    ;;
  aspire-verify)
    aspire_command verify
    ;;
  aspire-test)
    aspire_up interactive
    aspire_command seed
    aspire_command verify
    aspire_command materialize
    aspire_command verify
    ;;
  aspire-stop)
    aspire_cli stop --apphost "$aspire_apphost"
    ;;
  verify)
    up
    verify
    ;;
  mutate)
    up
    mutate
    ;;
  verify-final)
    up
    verify_final
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
  process-evidence)
    if [[ "$#" -lt 2 || "$#" -gt 3 ]]; then
      usage
      exit 2
    fi
    up
    if [[ "$#" -eq 3 ]]; then
      process_command --failure-evidence "$2" "$3"
    else
      process_command --failure-evidence "$2"
    fi
    ;;
  failure-test)
    if [[ "$#" -gt 3 ]]; then
      usage
      exit 2
    fi
    failure_test "${2:-postgres}" "${3:-AfterTargetBatch}"
    ;;
  control-equivalence-test)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    control_equivalence_test "${2:-postgres}"
    ;;
  source-matrix-test)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    source_matrix_test "${2:-all}"
    ;;
  elastic-failure-test)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    elastic_failure_test "${2:-postgres}"
    ;;
  compatibility-drift-test)
    if [[ "$#" -gt 2 ]]; then
      usage
      exit 2
    fi
    compatibility_drift_test "${2:-all}"
    ;;
  matrix-test)
    if [[ "$#" -ne 1 ]]; then
      usage
      exit 2
    fi
    matrix_test
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
