#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
cd "$repo_root"
container="cohesive-replay-assurance-$(date +%s)-$$"
created=false
cleanup() {
    if [ "$created" = true ]; then
        docker rm -f "$container" >/dev/null
    fi
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
# No volumes; data disappears with the task-owned container. Bind only to loopback.
docker run --detach --rm --name "$container" --label cohesive.task=replay-assurance \
    --publish 127.0.0.1::5432 --tmpfs /var/lib/postgresql/data \
    --env POSTGRES_USER=assurance --env POSTGRES_PASSWORD=assurance-local-only \
    --env POSTGRES_DB=assurance postgres:17.10-alpine3.24 >/dev/null
created=true
ready=false
for attempt in {1..30}; do
    if docker exec "$container" pg_isready -U assurance -d assurance >/dev/null 2>&1; then
        ready=true
        break
    fi
    sleep 1
done
if [ "$ready" != true ]; then
    echo 'Disposable PostgreSQL did not become ready.' >&2
    exit 1
fi
endpoint="$(docker port "$container" 5432)"
port="${endpoint##*:}"
export COHESIVE_POSTGRES_TEST_CONNECTION_STRING="Host=127.0.0.1;Port=$port;Database=assurance;Username=assurance;Password=assurance-local-only;Timeout=10;Command Timeout=15"
dotnet test src/Cohesive.Assurance.Tests -c Release --nologo --disable-build-servers -m:1
if [ "$#" -eq 0 ]; then
    set -- "$repo_root/eng/examples/replay-assurance/artifacts/$(date +%Y%m%d-%H%M%S)-$$"
fi
dotnet run --project eng/examples/replay-assurance -c Release --no-build -- "$@"
