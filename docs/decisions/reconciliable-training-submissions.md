---
kind: decision
status: implemented
authority: cohesive.ai.training-submission-recovery
owners: [cohesive-core]
applies_to: [cohesive-ai, cohesive-adapters-azureml]
last_verified: 2026-08-18
supersedes: []
---

# Bind training submission identity to exact request content

## Context

A durable Process may lose its worker after a training provider accepts a job but before the Process persists the
provider response. The previous `IModelTrainer.StartAsync(TrainingRequest)` boundary asked the adapter to generate
a random provider job identity and returned that identity only after acceptance. After an ambiguous failure, the
workflow therefore had neither a stable lookup key nor evidence that a discovered job represented the same request.
Retry could create a second training job, while “reconciliation” could only be declared rather than performed.

Durable-operation attempt identity cannot solve this problem. Attempts are physical, may change across retries, and
must remain evidence about dispatch rather than become provider job authority.

## Decision

`TrainingJobSubmission` binds a caller-supplied stable logical submission identity to an immutable snapshot of one
exact `TrainingRequest`. It derives a versioned `sha256-v1` request fingerprint from a fixed canonical JSON
projection. Dataset bindings are ordered by their ordinal binding names because collection order is not provider
meaning; duplicate binding names are invalid. All other request values, including exact configuration text,
participate in the fingerprint.

`IModelTrainer` exposes two submission operations:

- `SubmitAsync(TrainingJobSubmission)` submits or returns the provider job already bound to the exact submission;
- `ReconcileSubmissionAsync(TrainingJobSubmission)` returns the closed result `Accepted`, `ConfirmedAbsent`, or
  `Unresolved` without creating another logical job.

If provider evidence for the stable identity contains a different request fingerprint, or lacks the identity
binding required to prove equivalence, the adapter throws `TrainingJobSubmissionConflictException`. A conflict is
an invalid reuse of semantic identity, not an ambiguous provider outcome.

Azure ML derives one valid provider job name from the complete logical identity using a readable normalized prefix
plus the full SHA-256 identity digest. It stores the unmodified logical identity and request fingerprint in job
properties, checks existing evidence before submission, and uses direct deterministic lookup for reconciliation.
Azure infrastructure remains the provider-job authority; Cohesive owns the portable submission identity, content
binding, and reconciliation result semantics.

## Alternatives considered

### Search provider jobs by request metadata after an ambiguous failure

Rejected because list/search availability, authorization, consistency, and cardinality differ by provider. A search
can miss an accepted job or return several plausible jobs and does not establish exact request equivalence without
the same identity and content binding.

### Persist the provider-generated identity in an Ari-local ledger

Rejected because a worker can fail before the generated identity reaches that ledger. The ledger would document
known acceptance but could not distinguish “not accepted” from “accepted but not recorded,” leaving the original
ambiguity intact and duplicating reusable training-provider semantics in one product.

### Use each durable physical attempt identity as the provider job identity

Rejected because a retry would intentionally select a new provider identity. It would couple a logical external
operation to one runtime's attempt model and defeat stable-identity idempotency.

### Treat not-found as unresolved forever

Rejected for adapters that can authoritatively look up the deterministic provider identity. Those adapters may
return `ConfirmedAbsent`; adapters whose targets cannot make that claim must return `Unresolved` and preserve the
target limitation explicitly.

## Consequences

- The pre-1.0 `IModelTrainer` API changes from `StartAsync(TrainingRequest)` to stable `SubmitAsync` plus explicit
  reconciliation; implementations must adopt both operations.
- Replaying the same identity and exact request is idempotent at the provider-job boundary. Reusing the identity for
  different content fails early and diagnostically.
- Provider adapters must declare whether direct lookup can establish absence. They must not silently convert query,
  authorization, timeout, or consistency failures into `ConfirmedAbsent`.
- Workflow runtimes retain physical attempts, dispatch fences, deadlines, cancellation, retries, and escalation.
  The provider-neutral submission identity is related evidence, not a replacement for that operational ledger.
- The request-fingerprint projection is a portable compatibility contract. Any future normalization change requires
  a new algorithm identifier rather than silently changing `sha256-v1`.
