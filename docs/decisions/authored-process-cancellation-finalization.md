---
kind: decision
status: accepted
authority: cohesive.processes.cancellation-finalization
owners: [cohesive-core]
applies_to: [cohesive-processes, cohesive-storage, cohesive-adapters-durable-task]
last_verified: 2026-08-22
supersedes: []
---

# Model authored cancellation finalization as an exact lifecycle Process

## Context

Canonical lifecycle cancellation currently closes a Process attempt at its next safe point. The reference
interpreter cancels normal tokens, requests cancellation of active children whose policy is `Propagate`, detaches
the remaining children, and immediately reports `Cancelled`. That behavior is correct for a Process without
authored cancellation work, but it cannot faithfully cancel an already accepted provider job, reconcile the
provider's terminal state, or finalize authoritative domain state before cancellation becomes terminal.

A host callback, target-specific cancellation registry, or suspended C# frame would make cancellation meaning
depend on one runtime and would let mutable local state cross a durable boundary. Reusing ordinary outcome
continuations after normal tokens have been closed would also allow late child results to re-enter a branch that
cancellation already superseded.

## Decision

A canonical Process may contain one `CancellationFinalizerProcessNode`. The node is a lifecycle declaration, not
an ordinary graph node: it cannot be the Process entry or the target of a control-flow edge. Its stable node
identity anchors source attribution, child identity, Request capability acquisition, effects, trace, and target
realization requirements.

The declaration pins one exact child Process and its complete Request/Reply invocation protocol. The child input
is framework-shaped and contains only:

- the immutable original Process input;
- the logical Process instance and exact attempt;
- the accepted cancellation command identity;
- the stable cancellation reason code; and
- the optional reason detail as its complete portable JSON representation.

The child result is `ProcessCancellationAcknowledgement`, which echoes the cancelled attempt. A successful child
result for another attempt is invalid evidence. The child may use ordinary canonical Query, Relation, Transition,
Request, timer, recurrence, and child-Process constructs to reacquire mutable state and perform application work.
No arbitrary local binding, runtime service, callback, or target state is available to it.

At a cancellation safe point, reference semantics are:

1. stop normal graph advancement and close its waits and outstanding Requests;
2. request cancellation of every active `Propagate` child and detach children with `Detach` policy;
3. retain the exact cancellation intent and wait until every propagated child has physically and semantically
   closed;
4. start the exact finalizer child once, through its ordinary durable Request/Reply protocol;
5. report lifecycle `Cancelled` only after a valid acknowledgement; and
6. report finalizer failure, cancellation, termination, an invalid acknowledgement, or an unmapped outcome as an
   explicit failed cancellation rather than successful cancellation.

The lifecycle control state therefore distinguishes cancellation requested during an activation from
`Cancelling`, where normal work is closed but child settlement or finalization remains active. A failed finalizer
produces a failed Process terminal outcome while retaining attributable cancellation evidence; it never changes
the lifecycle mode to `Cancelled`. Forced termination remains immediate and distinct and does not promise that
the finalizer ran.

Repeated presentation of the same exact cancellation intent resumes the retained phase and cannot register or
emit the finalizer twice. A different cancellation intent for the same attempt is an identity conflict. Attempt
restart is unavailable after cancellation finalization begins because the original attempt owns both propagated
child closure and finalizer identity. A new cancellation command while `Cancelling` is already requested; after
successful cancellation it is already satisfied.

Processes without a `CancellationFinalizerProcessNode` retain existing immediate cancellation semantics and
canonical JSON bytes. The new node remains absent rather than serializing an optional null definition member.

## Qualification and ownership

The root Process document remains semantic authority. The linked child Process and interaction documents are the
exact dependency authority. Runtime adapter catalogs remain physical capability authority.

The closed node-union discriminator catalog acquires the new construct automatically. Definition validation,
child dependency closure, effect analysis, and exact Request requirement acquisition each give it an explicit
disposition. Target profiles must add an explicit realization disposition; an unchanged profile fails
completeness validation before execution. Durable runtimes may reuse existing child orchestration and durable
Request mechanisms, but may not add a cancellation-only registry or infer support from a handler type.

## Consequences

- Application/provider cancellation becomes ordinary canonical Process work with exact dependencies and adapter
  capabilities.
- The original root input is the only application value carried across cancellation. Mutable domain state must be
  reacquired from its authority.
- Parent status remains nonterminal while propagated children or the finalizer are active.
- Finalizer failure is operationally visible and cannot masquerade as `Cancelled`.
- Native durable-store and Durable Task interpretations require follow-up realization and differential tests
  before advertising support for the new construct.
- A Process needing best-effort or fire-and-forget child behavior continues to express that per child through its
  existing cancellation policy; the finalizer protocol itself is required once declared.

## Alternatives considered

### Add an optional property to `ProcessDefinition`

Rejected because the lifecycle declaration would sit outside the closed construct inventory and require a second
manually synchronized construct catalog. A closed-union node provides one existing exhaustive authority while
remaining explicitly outside ordinary graph reachability.

### Run compensation-purpose children automatically

Rejected because `ProcessChildPurpose.Compensation` classifies explicitly invoked work; it does not declare when,
why, or in which order compensation must run. Inferring lifecycle behavior from purpose would turn descriptive
metadata into hidden control flow.

### Treat accepted cancellation as terminal and run cleanup afterward

Rejected because status would claim success before provider and domain cancellation obligations settle. A failed
or lost cleanup could no longer be represented without contradicting the terminal lifecycle evidence.

### Capture current token bindings for the finalizer

Rejected because branch-local values may be incomplete, duplicated, stale, or unavailable after recovery. The
root input is immutable and authoritative; the finalizer reacquires everything else explicitly.
