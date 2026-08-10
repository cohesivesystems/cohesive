#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
emulator_image="mcr.microsoft.com/dts/dts-emulator@sha256:1b49dcf1581168f5c620a4f32083e1291a7dddfa60434acb3eacd8b23355936a"
emulator_name="cohesive-durable-task-emulator-$$"
emulator_port="${COHESIVE_DURABLE_TASK_EMULATOR_PORT:-8080}"

cleanup() {
  if [[ "${started_emulator:-false}" == "true" ]]; then
    docker rm --force "$emulator_name" >/dev/null
  fi
}
trap cleanup EXIT

if [[ -z "${DURABLE_TASK_SCHEDULER_CONNECTION_STRING:-}" ]]; then
  docker run \
    --detach \
    --name "$emulator_name" \
    --publish "127.0.0.1:${emulator_port}:8080" \
    --env ASPNETCORE_URLS=http://+:8080 \
    "$emulator_image" >/dev/null
  started_emulator=true
  export DURABLE_TASK_SCHEDULER_CONNECTION_STRING="Endpoint=http://localhost:${emulator_port};TaskHub=default;Authentication=None"

  for _ in {1..60}; do
    status="$(curl --silent --output /dev/null --write-out '%{http_code}' "http://localhost:${emulator_port}/" || true)"
    if [[ "$status" != "000" ]]; then
      break
    fi
    sleep 1
  done
fi

dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
  --configuration Release \
  --filter "FullyQualifiedName~DurableTaskSequentialProcessInterpreterTests.SchedulerEmulator_" \
  --logger "console;verbosity=minimal"
