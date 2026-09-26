# Bounded Azure runtime qualification

This optional adapter performs explicitly requested Cosmos/Blob create-read-delete checks and a
Durable Task orchestration/activity challenge. It uses native SDK clients; it neither provisions
resources nor wraps provider configuration. It is independent of application startup, health polling
and infrastructure deployment. Ordinary Azure readiness collection remains read-only.

## Authority and scope

The caller owns canonical target selection, environment policy, identity/deployment verification and
approval. Native clients own endpoint/authentication/retry configuration. This adapter owns one
attempt's lifecycle, ownership receipts, bounded work, verification, conditional cleanup and redacted
result. `RuntimeQualificationResult` is an operation result, **not** a replacement for Cohesive.Infra
runtime observations or a full-readiness artifact. Do not turn a storage account success into evidence
for every container, a synthetic container success into a product repository transaction claim, or a
scheduler echo into proof of application workflow correctness.

No background probes, automatic retries, provider exception payloads or implicit credentials exist.
Use clients with bounded network timeouts and retries disabled. Each call has a fresh run ID, a random
challenge, at most five minutes for work and one minute for cleanup (callers should select less).
Cancellation is cooperative with native SDKs. Storage cleanup uses an independent budget after caller
cancellation. A process kill can prevent cleanup; save the generated object name before running.

## Storage

```csharp
var policy = new RuntimeQualificationOptions(Guid.NewGuid(), TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(30));
var result = await StorageRuntimeQualification.BlobAsync(reviewedContainerClient, policy, cancellationToken);
// CosmosAsync takes a reviewed native Container with /partitionKey.
```

Blob creates a 32-byte challenge with If-None-Match `*`, reads the original ETag/content, and deletes
with that same ETag. It never includes snapshots in deletion. Cosmos first admits `/partitionKey`,
creates a tiny synthetic document in its own partition using Create (never Upsert), verifies ID,
partition, challenge and original ETag, then deletes with If-Match. The Cosmos admission and operation
share the work deadline. The original ETag is never refreshed during cleanup.

Use an isolated Cosmos container with no product readers, change-feed consumers or inbox dispatch.
Even a short-lived synthetic document in a product collection can break queries or publish events.
Blob callers must review prefix consumers, versioning, soft delete, retention and immutability;
`Deleted` proves accepted logical deletion, not physical erasure of retained versions.

Concrete example: a preexisting synthetic name causes a collision. It is neither overwritten nor
cleaned up. If creation succeeds but its response is lost, ownership is unknown: no blind delete or
retry occurs and the result is Unresolved. If another actor changes an owned object, original-ETag
deletion fails closed. A successful round trip with failed cleanup is not success.

## Scheduler

An independently reviewed worker deployment must opt into `worker.AddRuntimeQualification()`.
This registers `cohesive-runtime-qualification-v1` and one pure echo activity. It resolves no product
handlers and schedules nothing at registration. `SchedulerRuntimeQualification.ExecuteAsync` checks
for an existing instance, schedules once with all statuses deduplicated, waits within its budget,
and requires exact instance/name, Completed status and matching fresh input/output challenge.
A race with an existing instance cannot be admitted by a stale challenge.

History is intentionally retained: native purge lacks the conditional ownership contract used by
storage. There is no termination or purge, including after cancellation. A timeout can leave one
pending/running instance and must be reviewed before any subsequent attempt. Use a fresh ID only
after reviewing the previous outcome; do not automate retries. The echo activity has no data writes,
external effects or training behavior. Worker liveness and activity completion are the only claims.

## Failure, evidence and tests

Results distinguish NotStarted, Collision, Verified and Unverified from NotRequired, Deleted,
HistoryRetained and Unresolved cleanup. Diagnostics are stable stage codes. Never log native SDK
exceptions or payloads as a substitute for this result. Keep target/identity/deployment attribution
in the independently reviewed operator record and use existing runtime observation contracts when
full admission is required.

`Cohesive.Adapters.Azure.Qualification.Tests` covers collision and ambiguous creation, original-ETag
cleanup, cancellation, partition admission, challenge mismatch and native request contracts without
cloud I/O. The packed-package consumer reruns native storage contracts in CI/release. Synthetic tests
do not qualify a cloud deployment. Adding this package or registering a worker is not permission to
run a live probe.
