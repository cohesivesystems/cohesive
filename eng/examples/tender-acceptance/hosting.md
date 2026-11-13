# Binding tender reactions to infrastructure

`TenderReactionModel` declares what a committed tender movement requests on a load. A
`MachineReactionHost` binds one exact policy to the existing durable Process store and an authorized
target. Cosmos, HTTP and native calls leave that declaration unchanged.

The following composition uses application-owned repositories, durable store, authorization functions,
and interaction catalog. `authorizeLoad` authorizes the current worker for the bound operation and
load resource; `authorizeTenderAndStart` returns a trusted `ProcessControlAuthorizationContext`, or
null to deny. Neither callback may treat the supplied receipt or authority identifier as a grant.

```csharp
var model = new TenderReactionModel();
var target = MachineReactionTarget.Local(
    new ProcessTransitionOperationBinding(
        model.MarkPending.Compile().Plan!, loadRepository, interactionContracts),
    authority: organizationScope,
    authorizeEntity: authorizeLoad);

var host = new MachineReactionHost(
    policy: model.PendingAcceptance,
    store: processStore,
    target: target,
    authorizeSource: authorizeTenderAndStart,
    options: new ProcessDurableRuntimeOptions(
        workerId: workerInstance, workerLease: TimeSpan.FromMinutes(5)),
    recovery: new MachineReactionRecoveryPolicy(
        authorize: authorizeRetainedStart,
        maximumAttempts: 5,
        retryDelay: TimeSpan.FromSeconds(30)));

// Source acknowledgement depends only on durable admission.
var instances = await host.AdmitAsync(context, committedTenderReceipt);

// A separately supervised worker discovers starts in the same Process authority.
var recovery = new MachineReactionRecovery(source: processStore, hosts: [host]);
var page = await recovery.RunPageAsync(workerContext, after: null, maximumInstances: 32);
foreach (var (instance, progress) in page.Progress)
    await recordProgress(instance, progress);
var cursor = page.NextCursor;
```

The native target validates the repository's atomic operation-receipt capability at registration.
The host checks the exact target revision against the policy. Each delivery freshly authorizes the
source/start and target evidence; the original admitted start identity and attribution remain retained.
The host routes only receipts in its exact target authority and tenant. Other scopes return an empty
result before authorization or Process admission; denial within the selected scope still fails delivery.
Duplicate deliveries and worker restarts reuse the same instance, initial attempt, activation and
target operation identity, even when the physical clock has advanced. A concurrent worker may hold
the existing Process lease; recovery waits for that worker without blocking source admission.

## HTTP target

On the load service, expose the same authorized native target. The context factory must derive
trusted identity from the authenticated server request. The group supports additional ASP.NET
authorization conventions:

```csharp
app.MapMachineReactionTarget("/internal/load/mark-pending", target, createRequestContext)
    .RequireAuthorization("reaction-worker");
```

On the tender worker, substitute an HTTP target in the host constructor:

```csharp
var target = new HttpMachineReactionTarget(
    transition: model.MarkPending.Reference,
    authority: organizationScope,
    client: authenticatedLoadClient,
    endpoint: new Uri("https://loads.example/internal/load/mark-pending"));
```

The caller owns the HTTP client's authentication, timeouts and lifetime. This protocol supports
concrete string entity subjects. POST to the base route executes/replays the full canonical Process
invocation; POST to `/receipt` resolves an exact receipt locator. Both paths authorize resource
access. The wire uses existing strict Process/storage JSON contracts, with no parallel receipt model.
Execution must return HTTP 200 and a matching full entity receipt: a 202, malformed response,
another operation's receipt or a missing receipt cannot complete a reaction. A target guard rejection
has its own conclusive receipt and appears as Rejected. Known revision and receipt conflicts return
HTTP 409 Problem Details with stable `machine.reaction.protocol.revisionMismatch` or
`machine.reaction.protocol.receiptMismatch` codes. Unexpected infrastructure exceptions remain server
failures; a protocol conflict is not proof that the target did not commit. A lost response retains an Indeterminate attempt;
recovery consults the atomic target receipt and does not repeat the entity mutation.

## Cosmos source

```csharp
var source = new CosmosMachineReactionSource(
    repository: tenderCosmosRepository,
    leaseContainer: reactionLeases,
    host: host,
    processorName: "tender-pending-to-load",
    instanceName: workerInstance,
    fromUtc: rolloutBoundaryUtc);

await source.RunAsync(createWorkerContext, stoppingToken);
```

The source monitors the repository's own container and checks its entity-machine binding. It reuses
the existing Cosmos managed change-feed processor lifecycle, manual checkpointing and repository
receipt decoder. Only immutable transition receipt documents produce movement occurrences. Mutable
entity documents, outbox envelopes, unrelated entity types and creation receipt indexes do not.
Corrupt receipt content fails delivery rather than manufacturing a movement.

Checkpointing occurs after every matching receipt in the batch has a durably discoverable Process start.
Admission failure or denied source authorization prevents checkpointing. Target outages, held execution
leases and uncertain target commitment do not block this checkpoint; the recovery worker owns them.
The worker lifetime fails on callback error; an application supervisor must restart it with the same
configuration. Already completed receipts in a partially processed batch replay safely.

`ProcessorName` exposes the effective lease namespace, derived deterministically from the deployment
seed, exact policy revision, authority/tenant, source and lease placement, and initial observation boundary. Worker
identity does not affect it. The initial time is explicit and used by Cosmos only before leases exist.
Tenant-specific hosts can therefore observe one shared container using independent checkpoints;
each skips receipts outside its scope. Adding authority to the lease identity changes namespaces
from the first hosting sketch: deploy with an explicit historical boundary and retain Process/target
receipts so replay remains idempotent.
Changing the policy or boundary creates another subscription identity; operators must explicitly
decide whether historical movements should run under a new policy. Receipt retention must cover
the observation/recovery window. No full entity-state scan is substituted for missing history.

## Guarantees and current limits

The semantic authority remains the reaction's canonical Process. Storage owns atomic starts,
leases and checkpoints; the entity repository owns atomic mutation and receipt replay. The host
introduces no scheduler, mailbox, mutable reaction status or independent journal. Native and HTTP
targets share the same operation/result and receipt protocol. An actor integration can implement
`MachineReactionTarget` when it can preserve those contracts; actor delivery acceptance alone is
insufficient.

Admission requires `IProcessInstanceSource` on the same store that owns atomic initialization.
The in-memory reference store and PostgreSQL implement it; SQLite currently does not. There is no
separate enqueue operation that could be lost after admission. PostgreSQL discovery is scoped to its
configured storage authority. `MachineReactionRecovery` reads bounded pages and returns evidence-derived progress. Register both
the pending-acceptance and receipt-confirmation hosts to recover the full episode. It loads each
instance once for routing by exact Process revision and authority, then passes that snapshot to the
selected host. Execution still performs the reads needed for fencing and reconciliation. Duplicate
registrations and hosts using a different store instance fail registration. Unregistered instances
advance the scan cursor without dispatch; an authorization or storage failure propagates without
returning a new cursor, so the supervisor can retry the same page. The supervisor continues
from the returned cursor and starts a new pass after reaching an empty page. Ordering is provider-local;
keep cursors with their provider. New identities inserted behind a cursor appear on the next pass.
Recovery uses `ListPendingInstancesAsync`: terminal instances, including guard rejections, are excluded
before routing or target calls. PostgreSQL projects terminal status atomically with the canonical
checkpoint into the existing root row, with an index on authority, terminal status and ordered identity.
The projection is derived evidence, not a second reaction status. Older roots begin conservatively as
nonterminal candidates and are corrected on their first read; the worker also checks the loaded
checkpoint so migration or concurrent completion never causes a terminal target call. The in-memory
reference implementation filters metadata without materializing terminal snapshots. Roll out the new
store readers and writers together: older writers do not maintain this projection and must not resume
writing after the upgrade. See the [discovery query comparison](../../../docs/performance/reaction-discovery/README.md).

The candidate set still includes unrelated nonterminal Processes, delayed work and exhausted retries;
this is not a due-time scheduler. Poll cadence, retention and worker lifetime remain deployment choices.
`ListInstancesAsync` still enumerates all retained history for explicit administration. Explicit
`InspectAsync` and direct per-instance `RecoverAsync` can resolve terminal receipts when requested;
background page scans do not refresh those receipts.

`MachineReactionRecoveryPolicy` validates its authorization callback, dispatch budget and delay when
constructed. Hosts reject nondiscoverable stores at construction; background-worker registration
rejects hosts without a recovery policy. `authorizeRetainedStart` freshly authorizes the worker for each stored start. Discovery is not an
access grant. Target execution and receipt resolution retain their own fresh authorization checks.
`InspectAsync` requires the caller to authorize the Process instance before inspecting it.

Recoverable native calls retain `ProcessTransitionAttempt` records in the existing atomic Process
local-state ledger. The reserved resource prefix is `process/transition-attempt/v1/`; application
local-mutation planners must not write it. The versioned payload uses the existing strict storage JSON
codec and portable string contract, so no checkpoint schema or independent journal is introduced.
A fenced Dispatched record is committed before calling the target, followed by Failed or Acknowledged
evidence, then the ordinary activation checkpoint. An acknowledged result can finalize after restart
without another dispatch. Cancellation or lost acknowledgement leaves explicit uncertain evidence.
This adds durable writes to the native call path in exchange for a recoverable dispatch boundary.
Parsed attempt evidence is weakly cached by immutable snapshot identity; admission, inspection and
execution reuse that parse without retaining snapshots after their owners release them. Replay uses
canonical Process operation keys and idempotent start comparison instead of independent field lists.

`DeliverAsync` remains an explicitly requested synchronous dispatch/replay convenience; automatic
retry policy belongs to `RecoverAsync`. Use the recovery API for background workers.

A deferred physical dispatch returns `ProcessDurableRuntimeDisposition.Deferred` without an exception
or semantic checkpoint. Target dispatch and receipt resolution share `MachineReactionTarget.DescribeFailure`;
custom transports supply one classification policy for both boundaries.

Retry delay and physical-attempt budget are checked again under the acquired Process lease, so stale
worker discovery cannot bypass them. An interrupted dispatch waits until its claim expires plus the
retry delay. Use target timeouts compatible with the Process lease; this change adds no lease heartbeat.
Target adapters conservatively classify failures as ambiguous unless they can prove non-execution.
Terminal failures and exhausted budgets stop automatic dispatch. Known non-commit then reports Failed;
uncertain commitment remains Indeterminate with a recovery-stopped diagnostic. A domain guard rejection
is conclusive Rejected and is never retried automatically. Operator investigation must retain the same
operation identity; this example does not add a retry/abandon command or dead-letter subsystem.

The tender episode first requests pending load acceptance, then confirms acknowledgement. Its confirmation
reaction now declares `.After(PendingAcceptance)` with one-shot source-entity identity. Register its host
with `prerequisite: host` and include both hosts in recovery; see [causal coordination](coordination.md).
Out-of-order confirmation waits durably. A rejected prerequisite blocks dispatch with an explicit
diagnostic; a successful prerequisite still does not bypass the confirmation target's guard.
Cross-authority dispatch, multi-prerequisite joins and new-attempt restarts need additional semantics.

The regression suite covers outages, source checkpointing before target execution, serialized worker
replacement, interrupted calls, bounded retries, authorization, guard rejection and unchanged target
versions after lost acknowledgement. A native PostgreSQL test recreates the store and connection pool
before discovering and recovering an admitted reaction. HTTP tests run a local ASP.NET server. Cosmos
callback tests use real repository decoding and the shared processor seam; they do not claim a live
Cosmos deployment. Retain source history through admission and Process/target receipts through recovery.
