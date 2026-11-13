# Cohesive.Assurance

`Claim` is immutable, authoritative structural source. `Build()` lowers it to read-only
`ClaimDefinition` inside a versioned `ClaimDocument`. The canonical claim and document
have no public constructors or setters. Persisted IR can be restored only through the
validated document boundary; edit source and rebuild to change semantics.

The initial forms are state invariants, successful-commit transition contracts,
strictly-earlier existential historical requirements and bounded responses. Historical
and response matching are non-consuming. Responses permit same-step outcomes and include
their deadline. Initial-state inclusion is explicit for invariants. Deadlines use
TimeSpan ticks (100 ns); observation adapters must map their clocks accordingly.

## Structural source

```csharp
var state = new ClaimInput(new("inventory"), ClaimInputRole.State, inventoryContract);
var declaration = Claim.Define(
        id: "inventory.reservations-within-stock", revision: "1",
        subject: inventoryReference, observation: observationContractReference,
        source: reviewedSourceReference, configuration: loweringConfigurationReference)
    .AtEveryCommittedState(populationReference, InitialStateSelection.Include)
    .Require(Expr.Le(Expr.Field(state.Id, "Reserved"), Expr.Field(state.Id, "OnHand")), [state]);

var document = declaration.Build();
var restored = ClaimDocument.Deserialize(document.Serialize());
var references = restored.ValidateReferences(workspaceCatalog);
```

Each `ClaimReference` reuses a domain-owned `SourceReference` with an exact revision and
fingerprint; it does not duplicate entities, selectors or capability definitions. The
observation reference owns completeness, clock and input-role mapping. Population,
transition, trigger and outcome selectors are exact references, not arbitrary callbacks.
`Attach(role, reference)` supports relationships between subjects and `Assuming(reference)`
retains attributable assumptions. A workspace resolves references before interpretation.
`RequireEvidence(reference)` records an exact evidence requirement without implementing
a readiness policy engine. These requirements participate in semantic identity.
`ValidateReferences` checks exact catalog membership, not the truth of provider claims.
Input contracts are part of the fingerprinted semantics. Mapping actual observations
to them is an interpreter/adapter obligation, not inferred from field names.

## Typed source

Typed claims use the shared expression compiler in core’s `Cohesive.Authoring` namespace.
There is no separate authoring package. Assurance has no
Relations dependency. `Input<T>()` declares a typed input without a query node; its role
and observation mapping remain explicit in the claim. Give inputs explicit identities
when they should survive declaration reordering. Typed and structural source lower to
identical semantic IR for equivalent bindings, types and operators. No CLR types,
closures, reflection objects or session handles survive in claim IR.

```csharp
using Cohesive.Assurance;
using Cohesive.Authoring;

var author = new ExpressionAuthoring();
var stock = author.Input<Inventory>("stock");

var invariant = Claim.Define("inventory.safe", "1", inventoryReference,
        observationContractReference, reviewedSourceReference, loweringConfigurationReference)
    .AtEveryCommittedState(populationReference, InitialStateSelection.Include)
    .Require(stock, ClaimInputRole.State, s => s.Reserved <= s.OnHand)
    .Build();
```

For a transition, bind before, command and after to three typed inputs:

```csharp
var before = author.Input<Inventory>();
var command = author.Input<Reserve>();
var after = author.Input<Inventory>();
var contract = declarationBase.ForEachCommitted(reserveTransitionReference)
    .Require(before, command, after,
        (b, c, a) => c.Quantity > 0m && a.Reserved == b.Reserved + c.Quantity)
    .Build();
// Inventory.Reserved and Reserve.Quantity are decimal in this example.
```

For event requirements, bind trigger and witness explicitly:

```csharp
var trigger = author.Input<OrderEvent>();
var witness = author.Input<OrderEvent>();
var history = declarationBase.RequireEarlier(shippedReference, capturedReference)
    .Require(trigger, ClaimInputRole.Trigger, witness, ClaimInputRole.Witness,
        (t, w) => t.OrderId == w.OrderId).Build();
var response = declarationBase.RequireWithin(cancelledReference, releasedReference, TimeSpan.FromSeconds(30))
    .Require(trigger, ClaimInputRole.Trigger, witness, ClaimInputRole.Witness,
        (t, w) => t.OrderId == w.OrderId).Build();
```

The 30-second duration is an example, not a default policy. The typed translator preserves
its existing exactness rules: ordinary C# integer addition and some numeric conversions
are rejected rather than reinterpreted as canonical decimal arithmetic. Structural
`Expr.Add` explicitly requests canonical arithmetic. Arbitrary calls and captured values
produce structured diagnostics.

The same compiler serves Relations query bindings and neutral inputs. Relations retains
query visibility, traversal and parameter declaration rules; Assurance retains claim roles,
selectors and temporal semantics. Neither input declaration nor lambda lowering joins
entities or establishes observation consistency.

## Integrity, source mapping and validation

The semantic fingerprint covers schema, predicate profile, form, exact subjects/selectors,
observation contract, assumptions, role/type bindings, predicate and temporal parameters.
The artifact fingerprint additionally covers exact reviewed source, effective configuration,
producer and lowering version. Equivalent typed/structural source therefore shares a
semantic fingerprint while retaining distinct producer provenance in the artifact hash.
Canonical serialization uses the existing strict core serializer and SHA-256. Canonical
set ordering is validated; unknown fields, duplicated JSON properties, unknown versions,
invalid predicates and fingerprint tampering are rejected on restoration.

Source maps associate stable claim/expression paths with the exact source reference.
Use a source reference pointing at the declaration/fragment; this implementation does not
invent C# line/column spans. Analyzer diagnostics retain their semantic expression paths.
Lowered contract arrays are immutable and caller-owned input enumerables are copied.

`AssurancePredicates` selects core capabilities; it does not duplicate scalar semantics.
Analysis requires Boolean predicates and validates the whole expression, including
short-circuited branches. Scope/role errors fail before producing IR. Build errors are
ordinary construction exceptions or `ClaimValidationException` with structured core
diagnostics; typed lowering preserves shared structured expression diagnostics.

The reference batch evaluator now interprets these claims over scoped semantic observations;
see [batch evaluation](../../docs/assurance/batch-evaluation.md) for the contract and examples.
It consumes materialized joint alternatives, preserving uncertainty correlations. It does not
construct uncertainty domains, infer serialization orders, monitor production incrementally,
or prove an infrastructure guarantee. Document creation and batch evaluation are offline
reference operations; no runtime throughput claim is made. No new external dependency was introduced.

Run `dotnet test src/Cohesive.Assurance.Tests/Cohesive.Assurance.Tests.csproj` for the
four-form authoring, trace semantics, evidence re-evaluation and persistence suite.


## Staged source and typed execution references

`Define → choose one form → Require → Build` is enforced by the authoring surface.
`Claim.Declaration` exposes form selection, `Claim.Predicate` requires a predicate, and
completed `Claim` source supports immutable metadata edits and lowering. Add attachments,
assumptions and evidence requirements after `Require`.

Use `ClaimReference.FromExecution(transition.Reference)` for an authored Cohesive transition
(or another exact execution-definition reference). This preserves the owning model's revision
and complete fingerprint profile without a parallel identity or hashing implementation.
See [the executed inventory example](../Cohesive.Assurance.Tests/TransitionAssuranceTests.cs)
and [the observation boundary](../../docs/assurance/batch-evaluation.md) for the distinction
between an interpreted candidate and production commit evidence.


Source instances use reference identity; compare derived document fingerprints for semantic
comparison. Execution-reference text uses the shared `ToCanonicalString()` formatter owned
by the fingerprint type, with colon/percent escaping rather than URI component encoding.

Current consumers include the optional [Simulation bridge](../Cohesive.Simulation.Assurance/README.md),
reference-model integration tests and the benchmark.
No production Cohesive observation adapter, ARI integration, or ITO integration ships here.

## Evidence and proof state

[Evidence and proof state](../../docs/assurance/evidence.md) describes the common immutable report,
explicit method acceptance, exact model/encoding/realization boundaries, dependency reassessment,
and authority-recorded dispositions. Sampled success remains unknown; a waiver never changes proof
state. Trace and external methods share `AssuranceAttemptStatus` (renamed from `TraceEvaluationStatus`).

For articles and tests, [fixture reference scopes](../../docs/assurance/fixture-references.md)
provide `ClaimReference.Fixtures(root, revision).Define(path, definition)` and an automatically
collected immutable reference catalog. They identify fixture prose, not compiled domain definitions.
