#!/usr/bin/env bash
set -euo pipefail

: "${COHESIVE_POSTGRES_LOGICAL_REPLICATION_TEST_CONNECTION_STRING:?Set COHESIVE_POSTGRES_LOGICAL_REPLICATION_TEST_CONNECTION_STRING to a disposable PostgreSQL database with wal_level=logical and permission to create schemas, publications, and logical replication slots.}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
  --configuration Release \
  --filter "FullyQualifiedName~PostgresLogicalReplicationIntegrationTests.LocalPostgres_" \
  --logger "console;verbosity=minimal"
