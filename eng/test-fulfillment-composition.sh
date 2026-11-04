#!/usr/bin/env bash
set -euo pipefail

# Disposable, loopback-only native proof; never uses the application's configured databases.
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
run="cohesive-fulfillment-$$-${RANDOM}"
postgres="${run}-postgres"
scheduler="${run}-scheduler"
elastic="${run}-elastic"
cleanup() { docker rm -f -v "$postgres" "$scheduler" "$elastic" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --name "$postgres" -p 127.0.0.1::5432 -e POSTGRES_PASSWORD=fulfillment-test-only -e POSTGRES_DB=orders \
  postgres:17 -c wal_level=logical -c max_replication_slots=16 >/dev/null
docker run -d --name "$scheduler" -p 127.0.0.1::8080 -e ASPNETCORE_URLS=http://+:8080 \
  mcr.microsoft.com/dts/dts-emulator@sha256:1b49dcf1581168f5c620a4f32083e1291a7dddfa60434acb3eacd8b23355936a >/dev/null
docker run -d --name "$elastic" -p 127.0.0.1::9200 -e discovery.type=single-node -e xpack.security.enabled=false \
  -e ES_JAVA_OPTS='-Xms512m -Xmx512m' docker.elastic.co/elasticsearch/elasticsearch:9.0.3 >/dev/null
postgres_port="$(docker port "$postgres" 5432/tcp | sed 's/.*://')"
scheduler_port="$(docker port "$scheduler" 8080/tcp | sed 's/.*://')"
elastic_port="$(docker port "$elastic" 9200/tcp | sed 's/.*://')"
ready=false
for _ in $(seq 1 90); do
  status="$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${scheduler_port}/" || true)"
  if docker exec "$postgres" pg_isready -U postgres >/dev/null && [[ "$status" != "000" ]] \
      && curl -fsS "http://127.0.0.1:${elastic_port}/_cluster/health" >/dev/null; then
    ready=true
    break
  fi
  sleep 2
done
if [[ "$ready" != true ]]; then
  printf '%s\n' 'Local fulfillment dependencies did not become ready.' >&2
  exit 1
fi
export COHESIVE_ORDER_EXAMPLE_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=${postgres_port};Database=orders;Username=postgres;Password=fulfillment-test-only"
export DURABLE_TASK_SCHEDULER_CONNECTION_STRING="Endpoint=http://127.0.0.1:${scheduler_port};TaskHub=default;Authentication=None"
export COHESIVE_ORDER_EXAMPLE_SCHEDULER_CONNECTION_STRING="$DURABLE_TASK_SCHEDULER_CONNECTION_STRING"
export COHESIVE_SHADOW_ELASTIC="http://127.0.0.1:${elastic_port}"
dotnet test "$root/src/Cohesive.Adapters.Aspire.Tests" -c Release \
  --filter 'FullyQualifiedName~DurableFulfillmentIntegrationTests|FullyQualifiedName~Actual_program_bootstraps_schema_context_and_order_HTTP_contract' "$@"
