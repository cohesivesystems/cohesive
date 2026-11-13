# Inspecting and abandoning tender reactions

Reaction progress describes target evidence. Process control describes whether execution may continue.
These are distinct: a cancelled Process can still have an uncertain target commitment. Inspection
projects both from existing durable records; it does not introduce another lifecycle journal.

```csharp
var inspection = await confirmationHost.InspectLifecycleAsync(context, confirmationId);
if (inspection is null) return; // Absent or outside this registered policy and authority.

var targetEvidence = inspection.Progress;
var requiredAcceptance = targetEvidence.Prerequisite;
var acceptanceEvidence = inspection.PrerequisiteProgress;
var lifecycle = inspection.Control;
```

Inspection requires fresh recovery/evidence authorization for the reaction and its existing prerequisite.
Target receipt access remains separately authorized. It never invokes either target. Progress includes
physical attempts, exact operation identity, resolved receipt, failure and diagnostic. Canonical control
includes the current revision, attempt, lifecycle mode and attributable command receipts. Prerequisite
evidence is a current observation, not an atomic snapshot across both instances.

Use `recovery.InspectDependentsAsync(context, acceptanceId, after, maximumInstances: 32)` to page
authorized dependents. Advance its cursor even when the result is empty; a null cursor ends the scan.
This explicit diagnostic query scans retained history, including cancelled dependents. Background recovery
continues to use pending-instance discovery. This implementation does not add a reverse-dependency index.

## Explicit abandonment

Suppose acceptance has been rejected or withdrawn and an operator decides confirmation should never run.
Build a canonical Process cancellation command from the inspected revision:

```csharp
var command = new CancelProcessCommand(
    ProcessControlCommand.CurrentSchemaVersion,
    new ProcessControlCommandContext(
        commandId: operatorCommandId,
        idempotencyKey: operatorIdempotencyKey,
        processInstanceId: inspection.Instance,
        authorization: operatorAuthorization,
        issuedAtUtc: context.UtcNow,
        provenance: operatorProvenance),
    new ProcessControlExpectation(
        new ProcessContinuationIdentity(
            inspection.Instance, inspection.Control.CurrentAttempt.AttemptId),
        inspection.Control.Revision),
    new ProcessControlReason("tender.withdrawn"));

var result = await confirmationHost.AbandonAsync(
    context, command, authorizeAbandonment);
```

`authorizeAbandonment` is a trusted host callback receiving the caller, retained start and exact command.
It must freshly authorize this operator action and validate command attribution. Recovery permission
alone is insufficient. Applications must not let clients supply this callback or treat portable authorization
evidence as a credential.

The existing Process cancellation protocol owns concurrency, terminal continuation, optimistic revision
checks and command receipts. Inspect `result.Disposition` and diagnostics: a conflict, lease fence or
unknown commit outcome is not success. Persist and retry the **same command** after an uncertain response;
do not mint a new identity or refresh its expected revision automatically. A deliberately revised decision
requires a new inspection and operator command.

Before any dispatch, successful cancellation projects `Abandoned`. Cancelled instances leave pending
discovery. Duplicate source delivery and recovery cannot revive them. A dependent whose prerequisite was
abandoned reports `machine.reaction.prerequisite.abandoned` and remains durably blocked. There is no cascade:
inspect and explicitly abandon each dependent if that is the intended business decision. An already
retained successful prerequisite release remains valid evidence; cancellation does not retract history.

## Recovery is not a new business request

| Evidence | Action and meaning |
|---|---|
| Missing or unfinished prerequisite | Wait; recovery may finish the prerequisite. |
| Lost target acknowledgement | Recover the same instance and exact operation identity within its recovery policy. |
| Conclusive target rejection | Recovery returns the same rejection; it does not execute a fresh request. |
| Cancelled before dispatch | `Abandoned`; recovery cannot restart it. |
| Cancelled with uncertain target commitment | Control is `Cancelled`, progress remains `Indeterminate`; reconcile the retained operation externally. |

Abandonment is a stop decision, not compensation, rollback, or proof that an in-flight target did not
commit. A call already in progress can finish; ordinary Process lease/control fences determine which
decision commits. Retained effects and ambiguous attempts remain visible. Prefer reconciling uncertain
operations through normal recovery before cancelling. This API provides no force-retry,
restart-attempt, or post-cancellation redispatch escape hatch.

A fresh request after conclusive rejection is new business intent. The current one-occurrence-per-entity
profile intentionally rejects a second receipt under the same reaction identity. Explicit episode identity
and a source-machine transition authorizing another attempt remain separate work; changing a policy revision
solely to bypass deduplication is not a recovery procedure.

Tests cover blocked tender confirmation, explicit dependent abandonment, persisted reasons, authorization,
stale and conflicting commands, concurrent replay, lost target acknowledgement, conclusive rejection and
PostgreSQL reopening with terminal discovery filtering. No presentation surface is introduced.
