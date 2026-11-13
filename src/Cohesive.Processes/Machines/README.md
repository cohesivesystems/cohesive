# Declarative machine reactions

A reaction selects a committed source movement and projects a command for another entity. The
source machine does not emit an extra event. The target transition retains its own guards,
authorization boundary, classification and commit requirements.

```csharp
PendingAcceptance = MachineReaction.On<TenderState>(
        Tender.Entity,
        Tender.Machine.SendPending,
        id: "tender-to-load.pending",
        revision: "2")
    .OncePerSourceEntity()
    .Request(
        MarkPending,
        subject: tender => tender.LoadId,
        input: tender => new AcknowledgeLoad(false));
```

See the compiled [tender consumer](../../../eng/examples/tender-acceptance/TenderReactionModel.cs).
Its load acknowledgement episode is deliberately bounded; it does not claim to describe the complete
load lifecycle. Customer-ordered stops still belong to the load, with trips recording execution.

## Semantic authority

The typed declaration lowers immediately to a `machine.reaction/v1` execution document containing
the source entity shape and its authoritative machine, selected movement, exact target Transition,
and a canonical Process. That Process owns the subject/input expressions and target invocation;
there is no second expression evaluator, command catalog, scheduler or mutable reaction state.

The source projections use the existing fail-closed Relations expression lowerer. Arbitrary methods,
captured executable state and runtime services are rejected. Contracts are checked against the actual
source shape and target input. This initial expression projection uses CLR member conventions;
custom renamed or graph-dependent mappings are not inferred. Unsupported projections fail lowering
or Process compilation. The target declaration must compile with its entity-machine binding.

The Process has one target invocation and returns its domain outcome. A declared `.After(prerequisite)`
adds an ordinary durable AwaitMatch before invocation; the host releases it only after verifying the
prerequisite's successful target receipt. See [causal coordination](../../../eng/examples/tender-acceptance/coordination.md). A returned rejection is still
an ordinary Process result; target admission and committed movement are determined from the exact
target receipt, never from the Process's Completed label alone. Serialization verifies the policy
envelope, source movement, target compilation, and bounded Process structure. Policy identity,
revision, fingerprint and source attribution use the existing execution-document format.

## Existing Process realization

`MachineReactionInvocation.Prepare(policy, occurrence, authorization, issuedAtUtc)` in
`Cohesive.Storage.Processes` accepts an occurrence from the existing atomic entity-receipt protocol.
It returns null for a nonmatching source entity/machine/movement. A matching occurrence must retain
the exact source binding and remain in the host-authorized authority scope.

The resulting `Start` is an ordinary `ProcessStartRequest`. Submit it through existing start
admission and use `policy.Compile()` with the existing `ProcessDurableRuntime` and
`EntityTransitionProcessOperationAdapter`. Preparation alone does not durably accept or dispatch
anything. `MachineReactionHost` now supplies registration, durable admission and dispatch over these
same facilities; see [infrastructure hosting](../../../eng/examples/tender-acceptance/hosting.md).
Applications still supply trusted authorization and worker lifetime.
The source receipt is attribution, not a grant to mutate a load.

The instance/command/idempotency identities derive from the exact policy reference and source
receipt/node identity by default. `.OncePerSourceEntity()` instead binds one occurrence to the exact
policy, authority and source entity identity; conflicting second occurrences fail admission. `.After`
requires that scope so a missing prerequisite can be addressed before its receipt arrives. Duplicate
deliveries prepare the same intent. `ProcessStartRequest.Causation`
retains portable origin evidence independently of domain input, so
`MachineReactionInvocation.Restore(policy, checkpoint.Start.Request)` can reconstruct inspection
after restart without reading the source's later state. Causation participates in idempotent intent
comparison and is omitted from old start encodings when null. A different policy revision creates
a different identity; hosts must explicitly choose whether to replay historical sources under it.

The reference Process interpreter and the existing durable Process/entity interpretation consume the
same Process IR. Tests compare their invocations and terminal outcomes. The native entity adapter's
exact-operation replay is the reconciliation mechanism if the entity commits but its acknowledgement
is lost. The failed activation must resume with the same activation/operation identity and input.

## Evidence-derived progress

`invocation.Inspect(checkpoint, resolvedTargetReceipt)` derives progress from durable evidence:

| Status | Evidence |
|---|---|
| Pending | Start exists, but no target operation result has been retained. |
| Completed | The exact retained target receipt proves Applied or NoChange. |
| Rejected | The exact retained target receipt proves admission or domain rejection. |
| Indeterminate | A target result exists but its conclusive receipt has not been resolved. |

`InspectFailure(operation, failure)` projects the existing `DurableOperationFailure` evidence:
ambiguous effects are Indeterminate, terminal known non-commit is Failed, and retryable known
non-commit remains Pending. These values are inspection results, not another execution state machine.
Exact policy, process start, operation, authority and receipt affinities are checked. A receipt for
another command cannot mark this reaction complete.

`MachineReactionHost` persists native dispatch, failure and acknowledgement evidence through the
existing Process local-state ledger. `AdmitAsync` confirms discoverable starts without target calls;
`RecoverAsync` resumes their original operation identities with fresh authorization, a delay and a
bounded physical-attempt budget supplied by `MachineReactionRecoveryPolicy`. `ProcessTransitionAttempt.Read(snapshot)` exposes retained history.
An interrupted call is Indeterminate; absence of a receipt never proves non-commit. Exhausted known
non-commit is Failed, while exhausted ambiguity remains Indeterminate. See the
[hosting contract](../../../eng/examples/tender-acceptance/hosting.md) for adapter guarantees and limits.

## Current boundary

This profile covers one source occurrence, one target transition, one initial Process attempt,
and same-authority execution. `CosmosMachineReactionSource` can observe committed receipt documents,
and `HttpMachineReactionTarget` realizes exact target dispatch and receipt resolution. Neither is
part of the semantic policy. Restarting the Process as a new attempt is explicitly rejected by this inspection profile;
recover the existing attempt instead. Component composition, multiple-prerequisite joins, cross-authority dispatch,
historical policy replay and actor adapters need explicit additional semantics. Cosmos checkpoints
after durable admission; independent workers discover and recover admitted nonterminal work. Terminal history is excluded
from background scans before target access; explicit inspection still resolves target receipts. Discovery currently
supports the in-memory reference and PostgreSQL Process stores. No cross-policy ordering is inferred; `.After` explicitly declares the supported one-shot prerequisite.

Authoring and validation are cold operations. Preparation hashes one bounded origin record and
materializes the committed source snapshot once per delivered occurrence. Execution uses existing
Process and Transition plans. Inspection scans retained operation evidence and performs no storage
I/O in the pure invocation inspector; the storage host resolves and authorizes receipts through the repository boundary.

The storage host also exposes `InspectLifecycleAsync` and `AbandonAsync`. Abandonment uses canonical
Process cancellation and retains its revision, reason and command receipt; it never clears target evidence.
`MachineReactionRecovery.InspectDependentsAsync` provides bounded, authorized history inspection separately
from pending recovery. See [operator lifecycle](../../../eng/examples/tender-acceptance/lifecycle.md).
