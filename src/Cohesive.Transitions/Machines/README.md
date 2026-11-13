# Machine bindings in Cohesive.Transitions

Entity bindings in the existing `Cohesive.Transitions` assembly for [Cohesive.Machines](../../Cohesive/Machines/README.md).
Typed discriminator and predicate authoring project exact machine references into the existing
TransitionMachineLinkCatalog. Both bindings enforce edge guards. Overlapping enabled edges produce InvalidDefinition with
`transitions.execution.machine.admissionAmbiguous`; false guards produce ordinary AdmissionRejected decisions.
Discriminator movements own their status assignments; predicate movements rely on ordinary entity patches.
Guard facts bind to entity observation and guard input binds to the transition command. The integration introduces no storage, runtime or commit protocol.
See the Machines README for examples, ownership rules, supported predicate expressions and proof limits.

## Authoritative entity binding

Use `.Bind(entity)` on a complete discriminator or predicate binding to attach one lifecycle authority:

```csharp
var entity = new MachinePredicateBinding<TenderState, SendResult>(machine.Definition)
    .State(machine.Unlinked, t => t.LoadId == "" && !t.Accepted && !t.PendingReceipt)
    .State(machine.Offered, t => t.LoadId != "" && !t.Accepted && !t.PendingReceipt)
    .State(machine.Pending, t => t.LoadId != "" && t.Accepted && t.PendingReceipt)
    .State(machine.Confirmed, t => t.LoadId != "" && t.Accepted && !t.PendingReceipt)
    .Bind(ObjectEntityDefinition.For<TenderState>());
```

The returned entity's shape carries the `cohesive.entity.machine/v1` annotation. It contains the exact
machine document and lowered observation-only predicates (plus assignments for discriminator bindings).
`EntityDefinition.Machine` is a decoded projection of that annotation. A second binding is rejected,
including a second revision of the same lifecycle. Bind before publishing a graph-backed shape.

`TransitionAuthoring.Create` carries that authority from the shape into the canonical Transition IR.
Compilation derives links from it; a bound transition needs no external link catalog, and optional
catalog arguments cannot override the entity's authority. Serialization retains the predicates and
exact machine reference. Service, process, and ASP.NET registration and entity state preparation check
that the transition/evidence and entity retain the same binding.

Every successful control-flow path must execute exactly one movement, or the operation must explicitly
call `PreserveMachineState()`:

```csharp
var edit = TransitionAuthoring.Create<TenderState, UpdateNote, string>(entity.Shape,
    new("tender.edit-note"), new("1"), t => t
        .PreserveMachineState()
        .Set(new("note"), tender => tender.Note, (tender, input) => input.Note)
        .Return(new("result"), TransitionOutcomeDisposition.Applied, "Updated"));
var plan = edit.Compile();
```

Preservation is checked, not trusted. Classification runs before admission and after derived-field
recomputation and successful changes. Exactly one predicate must hold. A movement retains its source,
unique guard admission, and final target obligations. A named guarded self-loop is a movement with
observable identity, not preservation. A rejected operation retains no patches or movements. Entity
`ValidateState` also checks classification. Decision evidence retains the checked authority so an
unbound decision cannot be applied to a bound entity through the standard state projector.

Diagnostics include `transitions.compilation.machine.coverage`,
`transitions.execution.machine.classificationInvalid`, and
`transitions.execution.machine.preservationViolated`, in addition to existing admission/target diagnostics.
The compiler uses its existing bounded condition solver for coverage; conservative rejection is preferable
to claiming unproven coverage. Arbitrary predicate partition proofs and automatic preservation inference
are not implemented. Classification evaluates predicates in canonical state order and costs O(number of
states) per initial/final check; no serialization or guard lowering occurs per activation.

This first realization supports existing entities and one movement per successful path. Bound creation,
multi-movement paths, owned-component composition, migrations, and repair semantics are explicit unsupported
boundaries, not bypass flags. Unbound entities and the older `.Build()` link-catalog API remain available;
using a catalog alone does not establish entity-wide coupling. Direct repository writes are outside the
registered Transition enforcement boundary. Hosts still own concurrency and commit; a reference decision
is not proof that a movement committed. Durable reaction publication is separate future work.

See `eng/examples/tender-acceptance/TenderEntityModel.cs` for a compiled consumer covering linkage,
automatic/deferred acceptance, receipt confirmation, and note edits.
