---
kind: decision
status: implemented
authority: cohesive.ai.training-job-cancellation
owners: [cohesive-core]
applies_to: [cohesive-ai, cohesive-adapters-azureml]
last_verified: 2026-08-21
supersedes: []
---

# Model provider cancellation as an exact capability and closed result

## Context

Cancelling a workflow wait is not the same operation as cancelling an accepted provider training job. A provider
may continue consuming resources after its caller stops waiting, and a completed or failed job may win a race with
a cancellation request. A transport failure can also hide whether the provider accepted cancellation. A Boolean
or thrown exception cannot retain these distinctions for durable reconciliation.

Provider job state previously collapsed Azure ML's cancellation-requested state into `Running`. That representation
could not distinguish normal execution from a provider that had accepted cancellation but had not yet reached a
terminal state.

## Decision

`TrainingJobCancellation` is the provider-neutral cancellation input. It binds a stable logical cancellation
identity supplied by the workflow to one provider job identity. The complete pair is exact replay input. If an
implementation observes the cancellation identity already bound to another job, it throws
`TrainingJobCancellationConflictException`; identity reuse is a semantic conflict, not a second operation.

`ICancellableModelTrainer : IModelTrainer` is the explicit capability surface. A trainer that only supports
submission and observation does not implicitly claim that it can stop accepted work. A workflow requiring
compensating cancellation can therefore reject an incompatible realization before execution.

`CancelAsync` returns the portable, JSON-discriminated `TrainingJobCancellationResult` union:

- `Accepted` means the provider accepted cancellation, but does not claim terminal job state;
- `AlreadyTerminal` retains exact provider-owned completed, failed, or cancelled state;
- `NotFound` is an authoritative absence observation for the supplied provider job identity;
- `Rejected` is a deterministic provider or adapter refusal;
- `Unresolved` retains an ambiguous or transient attempt that requires reconciliation before blind redispatch.

`TrainingJobStatus.CancellationRequested` represents the provider-owned intermediate state. It is appended to the
existing enum so the numeric representations of prior states remain stable. Only `Completed`, `Failed`, and
`Cancelled` are terminal.

The method's `CancellationToken` controls the caller's wait. It does not carry provider-cancellation meaning and
cannot be used as evidence that a job stopped. After `Accepted` or an ambiguous attempt, the caller reconciles
through provider observation and finalizes domain state only from terminal provider evidence.

## Alternatives considered

### Add `CancelAsync` directly to `IModelTrainer`

Deferred because it would make every trainer claim cancellation support and would couple the core contract PR to
every adapter implementation. The derived capability keeps absence explicit while preserving an idiomatic trainer
surface for implementations that support the operation. The interfaces can be consolidated later only if
cancellation becomes a universal invariant of all supported trainers.

### Return a Boolean or throw for every non-acceptance

Rejected because already-terminal state, authoritative absence, deterministic rejection, and ambiguous transport
failure have different recovery rules. Exceptions remain appropriate for invalid input, identity conflict, and
caller-wait cancellation; provider outcomes are typed values.

### Treat cancellation-requested as running

Rejected because it destroys evidence required to reconcile an ambiguous cancellation response and makes status
views misrepresent provider intent.

### Use `CancellationToken` as the provider operation

Rejected because the token governs one in-process wait and has no durable provider identity, idempotency, receipt,
or terminal observation.

## Consequences

- Provider adapters opt into `ICancellableModelTrainer` and must interpret every closed result without leaking
  target-specific types into `Cohesive.AI`.
- Repeated exact cancellation input is observationally idempotent. Provider-specific realizations may use native
  idempotency, persisted operation evidence, or state-first reconciliation, but may not silently dispatch distinct
  logical cancellations.
- `Accepted` never authorizes terminal domain finalization. Provider status remains the training-job authority.
- Callers must retain the operation identity and result as durable evidence and distinguish caller cancellation
  from provider cancellation.
- Azure ML realizes idempotency by observing state before dispatch and again after any failed dispatch. Native
  `CancelRequested` proves provider acceptance without redundant redispatch; terminal state always wins a race.
- Local Python and in-memory interpretations remain separate dependent changes and must share the Azure ML
  conformance cases for terminal races, absence, rejection, ambiguity, and replay.
