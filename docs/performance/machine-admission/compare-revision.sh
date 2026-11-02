#!/usr/bin/env bash
# Run the current fixture against actual production code at an explicit Git revision.
set -euo pipefail
revision="${1:?Supply the Git revision to measure}"
artifacts="${2:?Supply an absolute results directory}"
case "$artifacts" in /*) ;; *) echo "Results directory must be absolute" >&2; exit 1;; esac
root="$(git rev-parse --show-toplevel)"
resolved="$(git rev-parse --verify "${revision}^{commit}")"
checkout="$(mktemp -d "${TMPDIR:-/tmp}/machine-benchmark.XXXXXX")"
trap 'rm -rf "$checkout"' EXIT
git archive "$resolved" | tar -x -C "$checkout"
cp "$root/src/Cohesive.Relations.Benchmarks/MachineAdmissionBenchmarks.cs" "$checkout/src/Cohesive.Relations.Benchmarks/"
mkdir -p "$artifacts"
printf '%s\n' "$resolved" > "$artifacts/production-revision.txt"
cp "$root/src/Cohesive.Relations.Benchmarks/MachineAdmissionBenchmarks.cs" "$artifacts/fixture.cs"
cd "$checkout"
dotnet run -c Release --project src/Cohesive.Relations.Benchmarks -- --filter '*MachineAdmissionBenchmarks*' --artifacts "$artifacts"
