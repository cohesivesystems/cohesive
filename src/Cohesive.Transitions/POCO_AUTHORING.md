# POCO transition authoring

`TransitionAuthoring.Create<TState, TInput, TOutcome>` accepts ordinary immutable records and readable POCO properties.
It materializes canonical IR during authoring. The builder, expressions, type mapper, and member resolver are not
retained in the execution document. Existing explicit `Entity<T>` declarations use the same builder and compiler.

## Compile once, then execute and materialize

Using `entity` and `approve` from the [README example](README.md#start-with-an-immutable-record):

```csharp
var compilation = approve.Compile();
if (!compilation.IsSuccessful)
    throw new InvalidOperationException(string.Join("; ", compilation.Validation.Diagnostics));
var plan = compilation.Plan!;
var materializer = ObservationMaterializer.For<RunControl>(entity.StateShape).Compile();

var original = new RunControl(Eligible: true, Status: "pending");
var before = ObservationValue.FromObject(original);
var decision = TransitionReferenceInterpreter.DecideFullState(
    plan,
    new("activation/run-1/approve"),
    PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new ApproveRun(true))),
    PortableValue.Concrete(plan.Definition.Observation, before));
var candidate = TransitionStateProjector.Apply(before, decision);
var result = materializer.Materialize(Observation.Create(entity.StateShape, candidate));
// result.Status == "approved"; original.Status == "pending"
```

The relevant namespaces are `Cohesive.Model`, `Cohesive.Execution`, `Cohesive.Transitions.Authoring`,
`Cohesive.Transitions.Execution`, and `Cohesive.Transitions.IR`. This example evaluates a decision; persisting it and its
concurrency evidence belongs to the repository/commit layer.

Admission predicates and ordinary body expressions read the invocation snapshot, even after an earlier `Set`.
For example, returning `state.Status` returns `"pending"` in this invocation. Patches accumulate in execution order;
candidate-state invariants run after the body. Failed guards and failed invariants retain no committable patch.
`TransitionStateProjector.Apply` verifies patch before-values and produces an observation; the compiled materializer
constructs a new record. Missing optional fields and explicit nulls remain distinct in that observation even when a
CLR materialization policy maps both to a nullable property's default.

## Stable names and configured contracts

`ObjectEntityDefinition.For<T>()` caches only the default definition for a CLR type. The overload taking an
`EntityTypeName` gives the model a stable name independent of its CLR class name. Configured definitions are not
cached by CLR type: own and reuse the resolved definition and its compiled materializer for that configuration.

Default shape discovery, transition property paths, and default observation materialization use
`JsonPropertyNameAttribute` when present, otherwise the CLR property name. Fields are ordered by their canonical
names. A CLR rename can retain its canonical identity through a fixed JSON property name. Duplicate serialized names
are rejected. An arbitrary serializer naming policy is not inferred by these defaults.

For explicitly authored shapes, `memberPathResolver` maps state property chains to the shape's canonical paths.
Configure the matching observation producer and `ObservationMaterializer.Map(...)` bindings against that same shape.
The resolver is an authoring projection and cannot alter the supplied shape. Input and output structural names still
follow the selected type mapper and the supported serialization conventions. Full graph-metadata propagation through
every projection remains follow-up work in COH-84; a state resolver alone does not configure a serializer.

Presence and nullability are independent canonical field constraints. The default CLR convention maps nullable
properties to optional, nullable fields. Supply an explicit `Shape` when the contract instead requires a nullable
field to be present, or permits absence but forbids explicit null. `Set(..., null)` and `Remove(...)` lower to distinct
operations. The compiler rejects removal of required fields. The authoring frontend rejects patches to declared
identity and computed fields; use explicit subject creation to initialize identities. Write-once initialization
continues to use explicit admission rules, as in the existing entity facade. Carrying all field mutation policies in
the portable execution contract remains follow-up work; this frontend check does not establish a new runtime
authority. CLR property names such as `Id` do not independently declare identity semantics.

## Explicit scalar identifiers

A domain type's custom JSON converter is executable CLR behavior and cannot be inferred as portable semantics.
Declare its canonical value contract explicitly:

```csharp
using Cohesive.Model.Authoring;

var types = new DefaultClrTypeRefMapper(new Dictionary<Type, TypeRef>
{
    [typeof(RunId)] = new ScalarTypeRef(ScalarTypeKind.String)
});
var entity = ObjectEntityDefinition.For<RunControlWithId>(new("run-control"), types);
// Supply typeRefMapper: types to TransitionAuthoring.Create as well.
```

The mapper copies the dictionary and normalizes nullable keys. It uses the declared contracts recursively in inputs,
outcomes, and nested object fields. Runtime observation conversion and materialization must implement the same wire
contract, for example a `JsonConverter<RunId>` that reads and writes a string. A mismatching value fails canonical
validation. Unknown converter-backed types produce `unsupportedValueConverter` inference diagnostics unless they
have an explicit portable contract. Existing supported JSON values, enum wire catalogs, structured quantities, and
single-value wrapper conventions retain their declared interpretations.

An explicit scalar mapping does not give a custom CLR equality, arithmetic, or conversion operator portable meaning.
Those operators are rejected for POCO expressions. Pass the intended primitive comparison value as input or express
the operation in canonical IR with an explicit semantic contract.

## Supported subset and remaining work

This first COH-83 increment supports direct and nested property reads and updates over inline structural contracts,
the existing guards/choices/locals/outcomes/invariants, and the existing scalar and collection patch operations.
Enums use their declared wire member names. Immutable constructor and init-only materialization uses the existing
observation materializer. Updates address semantic fields; they do not invoke CLR setters.

Nested selectors cannot traverse collections, a scalar identifier's CLR implementation properties, or named graph
types. Select and replace the complete value for those cases. Captures, arbitrary methods, custom CLR operators,
indexers, and host-language mutation are outside this subset. Named graph traversal, a fully resolved mapping profile
shared by all projections, and SQLite persistence/conformance remain tracked under COH-83's dependent issues.

The implementation changes cold shape/authoring/materializer compilation. Compiled transition execution and warm
materialization continue through the existing runtime mechanisms. No new per-invocation mapper or reflection pass is
introduced. Regression fixtures compare exact semantic bytes across POCO, explicit-entity, and direct-IR authoring,
then check the existing reference interpreter and materializer with valid and failing lifecycle invocations.
