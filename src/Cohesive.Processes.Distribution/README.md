# Cohesive.Processes.Distribution

Portable job placement, durable competing-consumer claims, worker-pool execution, leases, fencing, capacity, fairness,
and recovery for canonical `Cohesive.Processes` work.

## Install

```bash
dotnet add package Cohesive.Processes.Distribution
```

Add a provider package for production persistence. The first durable reference provider is
`Cohesive.Adapters.Postgres`.

## Authority boundary

This package distributes work; it does not become Process execution authority. A `ProcessWorkSubmission` contains an
exact definition revision and fingerprint, Process IR schema version, continuation identity, semantic path, work kind,
requirements, and provenance. It never serializes a callback, service, credential, compiled plan, or host object.

`IProcessDistributionStore` is the single portable lifecycle authority for logical pools, worker incarnations,
idempotent admission, eligibility, capacity reservations, claims, renewals, fencing, recovery, cancellation,
reconciliation, and terminal evidence. `IProcessDistributedWorkExecutor` resolves a live `ProcessWorkClaim` against the
application's canonical definition registry and Process runtime. Only that interpreter may advance Process semantics.

Delivery is intentionally at least once. The system promises at most one *live fenced claim* for a logical work unit,
not exactly-once physical execution. Effects must be idempotent or reconciled according to each work unit's declared
`ProcessWorkEffectGuarantee` and `ProcessWorkRecoveryMode`. After ambiguous loss, the ledger fails closed in
`ReconciliationRequired` until explicit evidence authorizes redispatch or terminal settlement.

Claim requests have their own stable identities, separate from logical work and physical dispatch identities. The
worker runtime performs bounded exact retries after outcome-ambiguous provider exceptions. A committed claim,
completion, or release is therefore replayed instead of creating another attempt or guessing about the first commit.
After the configured exact-retry bound, the original exception propagates; lease expiry and the declared recovery mode
remain the durable fallback after process loss.

## Worker pools

Define one effective pool policy and retain the source of every explicit, scoped, adapter, or conventional value:

```csharp
var pool = new ProcessWorkerPoolDefinition(
    ProcessDistributionWireNames.CurrentSchemaVersion,
    new("pool/reporting"),
    new ProcessWorkerPoolPolicy(
        maximumConcurrentClaims: 32,
        maximumAttempts: 5,
        workerLeaseDuration: TimeSpan.FromSeconds(30),
        claimLeaseDuration: TimeSpan.FromSeconds(30),
        capacity: [new("cpu", 32, "slots")],
        capacityDomains: [new("tenant/heavy", 4)],
        oversizedWorkBehavior: ProcessOversizedWorkBehavior.Poison,
        evidence: new(
            ProcessDistributionConfigurationSource.Explicit,
            "reporting/worker-pool",
            "configuration/process-distribution")));

await store.EnsurePoolAsync(context, pool);
```

Workers advertise immutable incarnation identities, accepted pools, Process IR versions, work kinds, preservable effect
guarantees, capabilities, affinities, capacity, and local claim concurrency. Run any number of
`ProcessWorkerPoolExecutor` instances over the same store.
Each lane competes for eligible work; executing work is not moved merely because another worker joins. A draining
worker receives no new claims but may settle its current fenced claims.

Pool policy bounds total live claims and aggregate resource capacity. Optional capacity domains bound named classes of
work, and fairness keys rotate runnable tenants within priority bands. Oversized work is either retained or poisoned
according to explicit policy. Deadlines, delayed retries, maximum attempts, worker expiry, and claim expiry are durable
ledger decisions rather than process-local timers.

## Providers and production validation

Every store publishes `ProcessDistributionStoreCapabilities`. Before production binding, call
`ProcessDistributionCapabilityValidator.ValidateProduction` and treat any diagnostic as a failed binding. Validation
checks durability, atomic competing claims, compare-and-swap, worker and claim leases, monotonic fences, runnable
discovery, capacity reservations, poison evidence, and—when required—atomic Process-state/work admission.

The in-memory store is the deterministic reference interpreter and conformance oracle. It is not durable and therefore
fails production validation. It can capture and restore the complete provider-neutral
`ProcessDistributionLedgerDocument`, whose strict JSON schema is versioned by
`ProcessDistributionWireNames.CurrentSchemaVersion`.

The PostgreSQL adapter persists one complete ledger document per configured authority row. Each mutation uses a
serializable transaction, row lock, provider clock, revision compare-and-swap, and the same reference algorithm as the
in-memory interpreter. This first realization favors semantic auditability over maximum claim rate: claims against one
authority serialize briefly at the ledger row, while work executes concurrently outside that transaction. Partition
independent workloads across authority IDs when greater claim throughput is needed.

```csharp
var options = new PostgresProcessDistributionStoreOptions("reporting/production");
var store = new PostgresProcessDistributionStore(dataSource, options);

// Run explicitly during deployment/bootstrap; ordinary operations never perform DDL.
await store.EnsureCreatedAsync(context);
```

The PostgreSQL distribution transaction does not currently share a commit with the separate
`IProcessDurableStore`. Its `SupportsAtomicProcessCommit` capability is therefore `false`; configurations that require
atomic Process-state and new-work admission fail closed. Applications must use an attributable transactional outbox or
a future co-located adapter before claiming that stronger composition guarantee.

## Target profiles

`ProcessDistributionTargetProfiles` binds portable semantics to declared realization strategies for Azure App Service,
Orleans, Akka.NET, Kubernetes, and Azure Functions. Profiles validate semantic requirements against provider
capabilities and target constraints; they are compiler policy and evidence, not a second scheduler.

- App Service uses competing consumers over a shared durable store.
- Orleans and Akka.NET describe logical worker placement without requiring direct node addresses in work records.
- Kubernetes describes long-lived worker deployment or per-job realization.
- Azure Functions requires an explicit host-attested maximum execution duration and rejects unsupported affinity.

Target mismatch diagnostics are structured and fail closed. A profile never silently drops affinity, duration,
durability, recovery, or effect guarantees.

## Observability

Activities and metrics use the stable `Cohesive.Processes.Distribution` source and meter. The runtime emits bounded
operation dispositions, duration, admission-to-claim delay, attempt ordinals, lease outcomes, terminal outcomes, and
safe pool queue/worker/capacity snapshots. Call `ProcessDistributionTelemetry.RecordPoolSnapshot` after inspecting a
pool to publish its current health projection.

Configured pool and capacity names are metric dimensions. Logical work, worker-incarnation, dispatch, and fence
identities appear only on traces, preventing per-job metric-cardinality growth. Durable ledger records remain the
semantic authority; telemetry is an attributable operational projection and cannot mutate execution.

## Related packages

- `Cohesive.Processes` for canonical Process IR, compilation, and continuation semantics.
- `Cohesive.Storage` for durable Process checkpoints and the Process-state store contract.
- `Cohesive.Adapters.Postgres` for the first durable distribution-ledger realization.
