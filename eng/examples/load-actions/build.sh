#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../../.."
dotnet run --project eng/examples/load-actions -- --generate
node node_modules/typescript/bin/tsc -p eng/examples/load-actions
