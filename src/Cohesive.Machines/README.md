# Cohesive.Machines

Cohesive.Machines declares lifecycle legality independently of entity updates, storage, and
orchestration. It implements the flat-machine slice of the
[Machines specification](https://app.notion.com/p/3758cf7881f98014afadcdd4dff8655f).

## Start small

```csharp
var source = Machine.Define("order.lifecycle", revision: "1");
var draft = source.State("draft").Initial();
var submitted = source.State("submitted");
var completed = source.State("completed").Final();
var submit = source.Edge("submit").From(draft).To(submitted);
var complete = source.Edge("complete").From(submitted).To(completed);
var lifecycle = source.Build();

var offered = lifecycle.Enabled(lifecycle.Initial);
var edge = lifecycle.Decide(lifecycle.Initial, "submit");
var exactMovement = submit.Resolve();
```

Handles reject foreign endpoints. Build freezes typed authoring, caches the immutable result,
and rejects malformed graphs. The structural `MachineBuilder` is another source projection:
its Build returns detached immutable definitions and its Validate supports incomplete drafts.
Both lower to the same payload. A named movement is sufficient input for the simplest machine.
Distinct edges can share an action through `.On("action")` or the structural `action` argument.

A host commits decisions using its own concurrency boundary. Neither evaluation nor a relationship
performs I/O. Standalone machines can drive UI navigation, devices, simulations and abstract protocols.

## Typed inputs and portable guards

```csharp
submit.When((OrderFacts facts, SubmitOrder input) =>
    facts.ItemCount > 0 && input.AcceptTerms);
```

Configure guards before Build. Typed expressions use the existing Relations expression lowerer,
shared Expr IR, shared analyzer, and portable evaluator. Facts contain the immutable snapshot and
explicit environmental observations. Captured objects, arbitrary callbacks and hidden I/O are
rejected by lowering. Input contracts are retained in IR and checked at evaluation.

`Evaluate(state, action, facts, input)` returns Accepted, UnknownAction, Illegal, Disabled,
Ambiguous or Indeterminate. Facts and input are portable ObservationValues. Accepted carries an
edge; it does not mutate a configuration. A guard that cannot be evaluated is Indeterminate.
`AdmissionMatrix(facts, input)` projects the same decisions for inspection and UI tooling.
The simple Enabled/Decide overloads refuse guarded inputs rather than guessing.

Structural ambiguity is rejected during Build. `AnalyzeDeterminism()` proves the single-edge case;
competing guarded edges retain Unknown until disjointness can be established. Runtime admission
always rejects overlapping enabled edges. No guard priority is inferred from declaration order.

## Entity integration

Cohesive.Machines.Transitions owns the integration; Machines has no dependency on Transitions.

```csharp
var links = new MachineDiscriminatorBinding<Order, OrderStatus>(
        lifecycle, order => order.Status)
    .Map(draft, OrderStatus.Draft)
    .Map(submitted, OrderStatus.Submitted)
    .Map(completed, OrderStatus.Completed)
    .Build();

// Inside ordinary Transition authoring:
transition.Move(submit, rejection: (_, _) => SubmitOutcome.InvalidState);
```

Supply `links` to `TransitionStaticCompiler.Compile(..., machineLinks: links)`. The existing
MoveMachine node pins the exact machine document and edge. The movement owns the discriminator
assignment; compilation rejects direct updates that overlap edge-owned fields in the supplied
catalog. Supply the binding catalog for every transition over the bound entity, including
transitions that do not declare movements. The compiler cannot enforce an omitted binding.

For computed configurations, `MachinePredicateBinding<TEntity,TInput>` maps each state to a
predicate instead. Typed state-handle overloads accept `Expression<Func<TEntity,bool>>`.
Transitions establish fields through ordinary patches. Source admission and the **final** candidate
must recognize exactly one declared state; overlapping states fail closed. Action-payload-dependent
state definitions are rejected. Edge guards also participate in source admission. Predicate
bindings support fields, constants, literals, unary and binary expressions; unsupported expression
forms produce an explicit error rather than a callback fallback.

Target checks run after all patches and derived-field recomputation. Empty movement assignment
arrays are therefore meaningful. A failed target produces InvalidDefinition with no commit artifacts.
Existing transition invariants still run. Predicate exhaustiveness and invariant preservation are
runtime checks unless independently proven; this implementation does not claim a general symbolic
proof. Binding catalogs are explicit compiler inputs and must be retained with the compilation
configuration. Predicate field changes outside movement-bearing transitions need application-level
binding discipline; automatic whole-domain binding registration is not implemented.

## Relationships

`MachineProjectionBuilder(source, target)` declares a total state mapping and an explicit image
for every generating edge. `.Edge(sourceEdge, targetEdge1, targetEdge2)` maps to a path; an empty
path explicitly declares stuttering. Validate checks coverage, composability, endpoints, initiality
and terminal preservation. Build(id, revision) persists exact participant revisions and fingerprints.
This proves structural graph correspondence, not payload/effect preservation, simulation coverage,
or liveness. Guarded path feasibility remains an additional proof obligation.

`MachineCoherenceBuilder(left, right).Allow(leftState, rightState)` declares allowed simultaneous
configurations. Analyze explores independent interleavings on demand, with a configurable pair bound.
It returns Proven, a Disproven counterexample trace, or Unknown for guards or exhausted bounds.
It does not eagerly materialize the Cartesian product. Build persists the constraint independently
of whether a selected realization preserves it. Immediate coherence does not imply atomicity or
message delivery; transient/eventual coherence requires an explicitly modeled Process protocol.

`MachineRelationships.ReadProjection` and `ReadCoherence` check participant identity, canonical wire
and semantics on read. Machine relationships are immutable document artifacts, never live observers
or peer-state writers.

## Persistence, inspection and diagnostics

Definitions use the shared `ExecutionDefinitionDocument`, type table, canonicalization, fingerprint,
provenance and source-map contracts. The typed MachinePayload is derived from source, never the
normal editing surface. `CanonicalJson` persists the whole document; `Deserialize` checks document
integrity, machine kind, graph validity and canonical order. Unknown extensions are refused.
`ToMermaid()` produces a derived graph with generated node identifiers.

Validation flags duplicate identities, unresolved states, initial-state errors, unreachable states,
nonterminal dead ends, terminal outgoing edges, input-contract conflicts and invalid expressions.
Unreachable/dead-end diagnostics currently block Build. Cycles are legal without a termination claim.
Proof state is separate from execution admission and acceptance policy. Runtime unknown inputs do
not become successful decisions. Analysis reports identify their scope and bounds explicitly.

## Design boundaries

The delivered component covers flat machines, fluent and structural source, typed guarded admission,
entity discriminator/predicate bindings, finite projection/coherence relationships, persistence,
diagnostics, diagrams and admission matrices. Rich statecharts, timers, history, Process invocation,
temporal logic, automated guarded refinement proofs, generated TypeScript and migration planning are
later extensions from the broader Notion research program, not implemented capabilities.

Reuse: shared expression lowering/analysis/evaluation, canonical execution documents, source maps,
Transition compilation, patches, decisions and commit boundaries. The Relations dependency is the
existing typed expression frontend authority; no third expression translator was introduced.
Predicate rebinding is a deliberately bounded integration operation rather than a second evaluator.

Verification lives in Cohesive.Machines.Tests and the existing Transition compiler/interpreter
conformance suites. Authoring and reference analysis prioritize clarity; no performance claim is made.
