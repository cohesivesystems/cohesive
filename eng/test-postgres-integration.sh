#!/usr/bin/env bash
set -euo pipefail

: "${COHESIVE_POSTGRES_TEST_CONNECTION_STRING:?Set COHESIVE_POSTGRES_TEST_CONNECTION_STRING to a disposable PostgreSQL database.}"
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
  --configuration Release \
  --filter "FullyQualifiedName~PostgresRelationQuerySourceReaderTests.LocalPostgres_ExecutesRelationsReadAndMaterialization_WhenConfigured" \
  --logger "console;verbosity=minimal"
