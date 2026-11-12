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
        workerId: workerInstance, workerLease: TimeSpan.FromMinutes(5)));

// A trusted native receipt source can deliver directly.
var progress = await host.DeliverAsync(context, committedTenderReceipt);
```

The native target validates the repository's atomic operation-receipt capability at registration.
The host checks the exact target revision against the policy. Each delivery freshly authorizes the
source/start and target evidence; the original admitted start identity and attribution remain retained.
The host routes only receipts in its exact target authority and tenant. Other scopes return an empty
result before authorization or Process admission; denial within the selected scope still fails delivery.
Duplicate deliveries and worker restarts reuse the same instance, initial attempt, activation and
target operation identity, even when the physical clock has advanced. A concurrent worker may hold
the existing Process lease; the source then retries rather than acknowledging the delivery.

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
failures; a protocol conflict is not proof that the target did not commit. A lost response leaves the source unacknowledged;
replay consults the atomic target receipt and does not repeat the entity mutation.

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

Checkpointing occurs only after every matching receipt in the batch yields Completed or Rejected.
Failure, denied authorization, a held Process lease or uncertain commitment prevents checkpointing.
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

This realization deliberately keeps source delivery outstanding until target evidence is conclusive.
It does not provide detached queue admission, dead-lettering, cross-authority dispatch, fan-out,
new-attempt restarts or an automatic durable transport-failure journal. A permanently missing target
or unsupported operation can block the source and needs operator intervention; retry is not claimed
to make all failures recoverable. `InspectAsync` derives progress from the existing Process checkpoint
and authorized target receipt. Its caller must first authorize access to the Process instance.
Without separately retained failure evidence, an interrupted dispatch remains Pending even when a
target commit may have happened; Pending is not proof that the target was untouched.

The regression suite exercises native/HTTP guard rejection, concurrent delivery, fresh authorization,
exact receipt mismatch, lost acknowledgement recovery and Cosmos batch checkpoint gating. HTTP tests
run a real local ASP.NET server. Cosmos callback tests use real repository encoding/decoding and the
shared processor seam; they do not claim a live Cosmos change-feed deployment.
