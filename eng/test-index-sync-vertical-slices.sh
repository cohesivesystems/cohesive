#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
index_sync_filter="FullyQualifiedName~IndexSyncVerticalSliceTests"
index_sync_filter+="|FullyQualifiedName~PullChangeSourceAdapterConformanceTests"
index_sync_filter+="|FullyQualifiedName~ElasticRelationQueryCompilerTests"
index_sync_filter+="|FullyQualifiedName~ElasticRelationQueryArtifactExecutorTests"
index_sync_filter+="|FullyQualifiedName~ElasticMaterializationTargetTests"

dotnet test "$repo_root/src/Cohesive.Tests/Cohesive.Tests.csproj" \
  --configuration Release \
  --filter "$index_sync_filter" \
  --logger "console;verbosity=minimal"
