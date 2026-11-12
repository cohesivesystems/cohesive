# Entity ownership and machine binding contract

Status: accepted design direction from the tender distillation discussion (D02); the existing-entity binding contract is implemented by canonical Transition compilation and reference execution. This contract governs the proposed replacement,
not the observed legacy traces.

## Authority

An entity has one authoritative machine. Its valid concrete snapshots and operations form a
transition system; the machine gives that system named abstract states and movements. A bound
state classifies entity snapshots, rather than constituting independently mutable state beside them.

The machine owns permitted abstract behavior. Entity Transitions own concrete changes. Their
binding owns classification and the correspondence between operations and movements. Authoring
source lowers to immutable, exact-revision IR. Classification predicates and movement references
must be persisted semantics, not callbacks held by a runtime registry.

An owned component may have a machine, but its composition into the entity authority must be
explicit. Another entity's machine is an external participant. Projections are derived views;
they do not independently govern the entity. Parallel state dimensions are a possible composition
mechanism, not permission to register multiple unrelated lifecycle authorities.

## Required checks

1. Every valid snapshot in the binding's domain classifies into exactly one machine state.
   Predicates cannot depend on the current command. Overlap and uncovered configurations are
   definition/state errors. Static proof may establish coverage; otherwise runtime checks remain.
2. Every registered entity operation has an interpretation as a machine movement or identity
   (state preservation). Undeclared operations are invalid. Proven preservation may be inferred,
   but its evidence must remain inspectable. An explicit preservation declaration requires a check,
   not trust in an annotation.
3. Movement execution checks source classification and admission, applies concrete changes and
   derived-field recomputation, and verifies the final target classification before commit.
4. Identity interpretation verifies unchanged classification. It is not a substitute for a named
   self-loop with guards and observable identity. Operations whose semantics require that movement
   must declare it even though their source and target labels coincide.
5. Creation establishes an allowed initial configuration. Import, migration, and repair require
   explicit semantics and attributable evidence; there is no generic bypass flag. Deletion and
   component composition need explicit contracts before those operations can claim full coupling.
6. Direct storage writes violate the contract unless constrained by the selected realization.
   Change-feed detection is useful evidence, but cannot retrospectively enforce pre-commit checks.

The binding closes the registered transition surface. Checking a movement only when an operation
opts into it does not meet this contract. Framework adoption must retain the binding through entity
authoring, serialization, Transition lowering, compilation, registration, and every supported interpreter.

## Tender slice

The following predicates describe the intended configurations of the ordinary acceptance episode.
They explain the predicates authored once in `TenderEntityModel`; that source lowers them into the binding. Cancellation, bids, changes, and migration snapshots need additional extraction before this
can be called the complete tender lifecycle.

| State | Entity configuration |
|---|---|
| Unlinked | Not accepted, no selected load reference, no pending acceptance receipt. |
| Offered | Not accepted, selected load reference exists, no pending acceptance receipt. |
| Pending | Accepted, selected load reference exists, acceptance receipt pending. |
| Confirmed | Accepted, selected load reference exists, acceptance receipt no longer pending. |

Load existence is external evidence, distinct from the tender storing a load reference. `AttachLoad`
requires the host to establish that relationship; a standalone graph step does not prove referential
integrity. `send-result` retains the extracted admission facts. `receipt-confirmed` retains P01's
proposed positive/correlated receipt requirement.

The acknowledgement view belongs to the related load, whose full lifecycle remains unextracted.
The coordination process tracks cross-entity work; its Complete state requires participant commit
evidence. It does not make the participant writes atomic.

## Occurrences, reactions, and runtime realization

A committed movement intrinsically produces attributable occurrence evidence. Machine authoring
does not require an Emit action or a duplicate event-name catalog. Guard rejection is an admission
outcome, not evidence of a completed movement. Concrete operations interpreted as identities still
have entity execution evidence, but do not invent a named machine movement.

External reaction policy selects movement occurrences, correlates target entities, and projects typed
action requests. A request does not guarantee target admission or commit. Reaction instances require
identity, exact policy revision, source occurrence, target correlation, attempt history, and diagnostic
evidence distinguishing pending, completed, rejected, failed, and indeterminate outcomes. Indeterminate
means a commit may have occurred; it must not be treated as a known non-completion for blind retry.

Runtime bindings select observation, transport, execution, and persistence independently. Cosmos
change-feed evidence, HTTP requests, actors, and Cohesive execution are possible realizations, subject
to explicit capability checks. Delivery acceptance does not satisfy a completion requirement for a
committed target transition. No concrete reaction-policy IR, adapter, or durable runtime is introduced here.

## Verification needed for framework enforcement

- Reject a second authoritative entity-machine binding, including a different revision of the same machine.
- Reject unaccounted operations and movement references to a different authority.
- Exercise overlapping/uncovered classification, wrong source, rejected guard, and wrong final target.
- Exercise state preservation, derived-field changes, and guarded self-loops separately.
- Verify equivalent checks after serialization and across interpreters; omit required binding evidence
  and assert failure rather than silently compiling an unconstrained transition.
- Verify creation and explicit migration boundaries before claiming those capabilities.
- Verify that failed or rejected execution never records a committed movement occurrence.

The graph characterization suite covers: linkage and acceptance share a tender
definition, acceptance cannot start in Unlinked, linkage cannot repeat in Offered, and independent
process/load interleavings do not establish completion coherence. Existing golden traces continue to
characterize the legacy ordering and partial commits without rewriting history.


## Implemented realization

`TenderEntityModel` is the first consumer of `.Bind(entity)` and `PreserveMachineState()`. The bound
shape and each derived Transition retain the same portable authority. Compilation requires one movement
on every successful path or explicit preservation; runtime checks enforce unique classification and
final classification/target agreement. Registration and state preparation reject omitted or changed
binding evidence. Predicate/discriminator bindings and document round trips are covered by regressions.

Creation, multi-movement paths, component composition, and automatic preservation inference remain
unsupported. Coverage is scoped to registered canonical Transitions and their reference interpreter;
direct storage writes are not made safe by this contract. Committed occurrences can now be projected
from atomic entity receipts without an explicit Emit declaration. [Reaction realization](reactions.md)
documents that boundary and the next policy/runtime slice. Reaction execution and occurrence transport
remain design requirements rather than implemented delivery guarantees.
