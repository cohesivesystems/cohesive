# Constructive finite comparison

`FiniteComparison.Compare(oldPlan, newPlan, target)` searches a shared, explicitly declared
catalog for both directions: `¬old ∧ new` (newly permitted) and `old ∧ ¬new` (newly forbidden).
Both meanings are independently evaluated on each row. The first witness for each direction
is retained as an index into the common catalog.

The comparison target is mandatory:

- `Admission` compares `A ∧ G`. Use it for actual transition admission plans derived by
  `TransitionAssurance.CheckAdmission`; the claim predicate does not silently become a guard.
- `Requirement` compares `(A ∧ G) ⇒ P`. Use it for proposed claim requirements. Outside its
  applicability a requirement is vacuously satisfied. A newly permitted row here means the
  requirement now accepts that row, not that a transition admits or commits it.

```csharp
var comparison = FiniteComparison.Compare(
    faultyPlan, repairedPlan, FiniteComparisonTarget.Admission);
var retained = FiniteComparisonResult.Recheck(comparison.Serialize());
var witness = retained.OldCandidate.Members[retained.NewlyForbiddenIndex!.Value]
    .Deserialize<Driver>();
// OriginalResolved=true, RemainingBlockers=1:
// faulty admission permits it; repaired admission rejects it.
```

Authoritative builders still supply both plans. Comparison introduces no alternate source format
or hand-edited IR. Each retained candidate contains its exact claim, assumptions, guard, source
maps, configuration and catalog. The comparison artifact additionally pins the interpretation,
selected target, budget, outcomes and witness indices. Rechecking evaluates both candidates again
and rejects forged or incompatible artifacts. Cancelled results are retainable but require a new
run: no deterministic cancellation schedule is retained.

## Boundaries

Candidates must share the ordered catalog, input value contract, claim form/input role, exact
observation mapping and external assumption references. Binding identifiers can differ: each
candidate resolves the same row through its own binding. Changed mappings or external assumptions
are rejected because this interpreter cannot establish their semantic correspondence. Selector,
source and configuration revisions are retained separately; the caller supplies the common
population as the explicit catalog. This does not establish equivalence of external populations
or completeness outside that catalog.

Changed guard, predicate, and executable assumption expressions participate according to the
selected target. Claim-predicate changes intentionally do not affect an admission comparison.
For example, restricting an assumption can newly satisfy a requirement by vacuity; it can also
remove admission. These are distinct outcomes, not interchangeable claims of safety.

A found witness remains valid if a later row exhausts the budget or is unsupported. The missing
direction remains inconclusive. Absence is definitive only after complete enumeration, including
an empty catalog. Finding both directions can terminate early without claiming enumeration is
complete. No result claims universal equivalence, reachability, implementation refinement, or a
change to proof state. A catalog collected by sampling supports conclusions only over those
retained rows; no witness in that catalog says nothing about unsampled states.

## Reproducing a witness

The transition integration test obtains a newly forbidden row from the actual faulty/repaired
admission IR and executes that row through both reference interpreters. The faulty candidate
applies and the repaired candidate rejects. The Simulation integration test reconstructs the
two-blocker scenario from a distinguishing row using initial state and scheduled commands,
then reproduces the actual faulty commit. It does not inject an after-state.

The reconstruction remains domain-specific. Automatic mapping into scenarios, comparison of
changed observation mappings, and comparison of external population definitions require explicit
adapters or additional semantic models. Comparison artifacts are retained separately from model
proof evidence and implementation trace evidence; they do not relabel either obligation.
