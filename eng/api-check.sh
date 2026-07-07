#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

dotnet pack "$repo_root/Cohesive.sln" \
  --configuration Release \
  --no-restore \
  /p:EnablePackageValidation=true
