# Tender acceptance distillation sketch

These internal source files are compiled directly by `Cohesive.Tests`; they introduce no package or public
framework API. They author candidate machines using `Machine.Define`, plus a bounded effect-order extraction
and an in-memory failure-injection interpretation. They do not reference or execute the archived application.

Each factory returns a declaration with its frozen `Definition` and named `MachineState` and
`MachineMovement` handles. Stable identifiers are declared once in the fluent source; consumers do not
maintain matching string constants or enums:

```csharp
var tender = TenderAcceptanceMachines.Tender();
var decision = tender.Definition.Evaluate(
    tender.Offered.Id,
    tender.SendPending.Resolve().Edge.Action,
    ObservationValue.FromObject(new AcceptanceFacts(true, false, false)),
    ObservationValue.FromObject(new SendResult(AutoAcknowledged: false)));
// The selected edge targets tender.Pending.Id.
```

An action can select among sibling movements: `SendPending` and `SendConfirmed` share the send-result
action. Resolve the movement's `Edge.Action` when evaluating; its `Id` identifies the individual edge.
Coordination exposes the uncertainty movements through `Uncertain[state]`, keyed by its state handles.
Handles resolve to the declaration's exact definition and cannot mutate it after construction. Runtime
evaluation still accepts identities; the sketch derives them from handles at that boundary.

Read the [extraction dossier](../../../docs/research/tender-acceptance/README.md) and
[coordination model](../../../docs/research/tender-acceptance/coordination.md) before interpreting the model.
In particular, positive receipt validation and reconciliation are proposed improvements, not recovered guarantees.

```sh
dotnet test src/Cohesive.Tests --filter FullyQualifiedName~TenderAcceptanceCharacterizationTests
```

The fluent source is authoritative for the proposal. Tests verify deterministic canonical Machine documents,
fingerprints and serialization. The source-derived trace fixtures pin only the documented clean acceptance
slice; live differential testing against the original application is still required for migration.

Domain clarification D01 assigns customer-ordered stops to the load and actual execution history to trips.
The proposed machines do not require an original trip. The observed trace still includes the legacy trip
notification so that characterization does not silently rewrite the behavior being extracted.

D02 gives the tender a single lifecycle for linkage and acceptance: `AttachLoad` moves from `Unlinked`
to `Offered`, after which acceptance is admitted. The independent linkage machine has been removed.
`LoadAcknowledgement()` returns an explicitly incomplete view of the related load lifecycle;
`Coordination()` describes process progress across participants. Neither is a second binding on the tender.
The tender revision is now `proposal-2` because its graph and initial state changed.

The [entity binding contract](../../../docs/research/tender-acceptance/entity-binding.md) records full
coupling as the required target semantics. These sketches do not implement entity-wide compiler/runtime
coverage enforcement, and the legacy trace host intentionally preserves observed partial commits.
