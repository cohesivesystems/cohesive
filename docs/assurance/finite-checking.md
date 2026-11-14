# Finite-domain guard checking

The finite reference checker answers two queries over an explicitly listed catalog `D`:

- Counterexample: `D ∧ A ∧ G ∧ ¬P`.
- Non-vacuity: `D ∧ A ∧ G`.

`A` is the conjunction of authored assumptions, `G` is an authored guard, and `P` is the existing
claim predicate. The first profile supports one state input or one transition pre-state input.
It does not reinterpret temporal claims, after-state predicates, or multi-input contracts.
Unsupported profiles are rejected at authoring/restore time.

## Authoring example

```csharp
record Qualification(bool OriginalResolved, int RemainingBlockers);

var input = new ExpressionAuthoring().Input<Qualification>("qualification");
var claim = Claim.Define("reactivate.all-gates", "1", transitionReference,
        observationContract, claimSource, claimConfiguration)
    .ForEachCommitted(transitionReference)
    .Require(input, ClaimInputRole.Before, q => q.RemainingBlockers == 0)
    .Build();

// This source explicitly declares every member of D. No generation or sampling occurs.
Qualification[] domain =
[
    new(false, 0), new(false, 1), new(false, 2),
    new(true, 0),  new(true, 1),  new(true, 2)
];

var declaration = FiniteGuardCheck.Define(
    claim, input, domain, guardSource, checkerConfiguration);
var faulty = declaration.When(q => q.OriginalResolved).Build();
var repaired = declaration.When(q => q.RemainingBlockers == 0).Build();

var failure = FiniteGuardChecker.Check(faulty);
var success = FiniteGuardChecker.Check(repaired);

// A retained source member, not a synthesized or injected execution after-state.
var witness = failure.Plan.Members[failure.CounterexampleIndex!.Value]
    .Deserialize<Qualification>(); // OriginalResolved=true, RemainingBlockers=1
```

Authoritative source consists of the domain declaration, guard and optional `.Assuming(...)`
expressions. Lowering uses the shared typed expression authoring infrastructure and produces a
portable `FiniteGuardCheckDocument`. No compiled predicate, closure, CLR type or callback survives
in that document. Domain members are materialized during authoring and checked against the claim's
input contract once at the document boundary. Fixed source, ordering and configuration produce
identical derived artifacts. Source maps identify `/members`, `/guard` and `/assumptions`; the
claim retains its own reviewed source and source map.

## Binding the actual Transition admission rules

The optional `Cohesive.Transitions.Assurance` package replaces the separately authored
`.When(...)` with the admission predicates retained by an actual Cohesive transition.
The following uses the same entity shape for transition execution and claim authoring:

```csharp
var entity = ObjectEntityDefinition.For<Driver>(new("driver"));
var reactivate = TransitionAuthoring.Create<Driver, bool, bool>(
    entity.Shape, new("driver/reactivate"), new("1"), t => t
        .Requires(new("activation-eligibility"),
            (driver, command) => driver.OriginalResolved,
            (driver, command) => false)
        .Set(new("activate"), driver => driver.Active, true)
        .Return(new("activated"), TransitionOutcomeDisposition.Applied, true));

// Import the authoritative entity shape; do not assume a CLR-named type is equivalent.
var graph = ShapeGraphDocument.FromGraph(new ShapeGraph(new("driver-model"), [entity.Shape]));
var author = new ExpressionAuthoring();
var before = author.Input(
    author.Clr.Shape<Driver>(graph, new(graph.Graph.Id, entity.Shape.Id)), "before");

var reference = ClaimReference.FromExecution(reactivate.Reference);
var claim = Claim.Define("activation.requires-no-blockers", "1",
        reference, observationContract, claimSource, claimConfiguration)
    .ForEachCommitted(reference)
    .Require(before, ClaimInputRole.Before, driver => driver.RemainingBlockers == 0)
    .Build();

Driver[] catalog =
[
    new(false, false, 1), new(false, true, 0),
    new(false, true, 1), new(false, true, 2)
];
var plan = TransitionAssurance.CheckAdmission(
    reactivate, claim, before, catalog, catalogSource, checkerConfiguration).Build();
var failure = FiniteGuardChecker.Check(plan);
// Witness: Driver(Active: false, OriginalResolved: true, RemainingBlockers: 1).
```

Here `Driver` is `record Driver(bool Active, bool OriginalResolved, int RemainingBlockers)`.
Repair the transition's `.Requires` predicate to `driver.RemainingBlockers == 0`, give it a
new revision, and rebuild the claim against that new exact reference with the same policy expression.
The finite checker derives the changed guard automatically; no test-owned guard needs editing.

Every explicit admission rule participates in original evaluation order. An empty list means true.
The first profile rejects command-dependent guards, entity creation, lifecycle machine bindings,
and unsupported expressions. It checks the full pre-state value contract and exact transition
subject/selector, rather than guessing compatibility from the CLR type. Source-map entries retain
the transition and individual admission-node identities plus the versioned binding profile, and
all become evidence dependencies.

This is admission soundness, not a proof of complete transition behavior: body rejection,
post-state invariants, reachability and commit coordination are outside this query. A counterexample
is only an admitted model state until actual execution reproduces it. The integration test runs
the reference interpreter on the witness, verifies the faulty candidate activates, and verifies
the repaired candidate rejects; it also compares admission with the checker for every catalog row.
No production commit is inferred.

Retain the transition document to resolve node-level source locations or rederive the guard.
`Recheck` reevaluates the retained finite plan; it does not fetch external source artifacts.

## Completion and evidence

`Check` visits the catalog in declared order. Rows excluded by assumptions or the guard still count
against `maxMembers`. It retains the first enabling row and stops immediately on a validated
counterexample. Finding a counterexample completes the refutation query without claiming every
row was visited: `EnumerationComplete` remains false when the checker stops early.

Successful implication requires visiting the entire catalog. An exhausted budget, cancellation or
unsupported expression cannot produce successful implication evidence. Non-vacuity is independent:
if an enabling row was found before interruption, its existence remains established even though
implication is inconclusive. An empty catalog or unreachable guard satisfies implication vacuously,
but `ToEvidence()` withholds a definitive successful verdict in that case.

```csharp
var evidence = success.ToEvidence();
var assessment = ClaimAssessment.Evaluate(
    evidence, acceptancePolicy,
    acceptance: [EvidenceAcceptance.Of(evidence)], asOf: reviewTime);

// Conditional: complete over D under A, with the bounded plan retained as a residual.
Assert.Equal(ProofState.Conditional, assessment.State);
```

The projection always emits `ExhaustiveCheck` evidence for a **model obligation**, pinned to the
exact plan. Its declared claim scope is the claim's selector; the finite plan is a named residual.
It never automatically promotes a bounded result to universal proof or implementation refinement.
The faulty report is `Disproven` for that exact modeled guard under the retained domain/assumptions.
Actual implementation reproduction still requires executing the candidate through Simulation or
another qualified adapter. The bridge integration test reconstructs the known two-blocker scenario
from a finite witness, runs both scheduled operations, and checks the actual commit pre-state against
the witness. This is a domain-specific reconstruction fixture, not a general inverse mapping.
The integration fixture retains and reassesses the model report and the implementation trace report
separately. Both refute the same claim in the faulty example, but their subjects and obligations
differ; combining them into one assessment or relabeling the trace as model evidence is rejected.
Both reports preserve the finite guard source as a dependency of the reproduction workflow.

The repaired guard and accepted predicate intentionally agree; implication checks that agreement,
not the independent correctness of the policy itself. A spurious modeled state is not automatically a reachable execution.

A caller may build a catalog from previously generated values, but the checker then proves only
over that explicit catalog. Sampling does not establish that the catalog exhausts some external
universe. Catalog completeness is by definition for the retained list; no reachability, storage
isolation, provider guarantee or external domain coverage is inferred. Duplicate rows are allowed
and remain in source order; they add work, not independent evidence.

## Retention and rechecking

```csharp
var json = success.Serialize();
var independentlyChecked = FiniteGuardCheckResult.Recheck(json);
Assert.Equal(success.Verdict, independentlyChecked.Verdict);
```

The result retains the complete plan, exact method/schema, budget, visited count, witness indices
and diagnostics. Rechecking reruns the catalog and compares the complete canonical result. Unknown
fields, incompatible versions and forged outcomes or witness indices are rejected. External evidence
consumers must retain the result sidecar: `ClaimEvidence` references its exact fingerprint and does
not embed or automatically re-run every external artifact.

Cancelled attempts can be retained and yield unknown proof state, but `Recheck` rejects them because
no deterministic cancellation schedule is recorded. Explicitly rerun their retained plan for a new
attempt. Budget-exhausted and unsupported attempts are deterministically recheckable.

This implementation uses the existing core portable expression evaluator, strict serializer and
fingerprint helper. It adds no SMT dependency or Simulation dependency. Checking is an offline
reference operation: the complete catalog is retained in memory, and `maxMembers` bounds row visits,
not expression complexity, input bytes or wall-clock time. It is not an untrusted-code isolation
boundary. Cartesian-domain construction, symbolic solvers, imported domain-completion certificates,
and automatic witness-to-execution reconstruction remain follow-on extensions.

## Guard provenance format

Finite plan schema `cohesive-assurance-finite-guard/v2` retains `GuardSources` separately from
catalog/assumption `Source`. The typed `.When(expression)` overload attributes its guard to that
source. The structural `.When(Expr, sources)` overload supports semantic producers such as the
transition bridge; it does not expose direct plan construction. Both surfaces lower equivalent
source to identical artifacts when their expressions and source mappings agree.

V1 plans/results must be regenerated from authoritative source; missing guard provenance is not
silently inferred during restoration. Finite-result schema `cohesive-assurance-finite-result/v2`
wraps the v2 plan.

Guard source paths identify actual expression nodes and are validated when supplied and when restored.
A path may have multiple contributors: `/guard` can reference the transition, binding profile, and a
single admission rule. Consumers group entries by path; only identical path/source pairs are deduplicated.
