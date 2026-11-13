# Simulation / Assurance bridge

Optional `Cohesive.Simulation.Assurance` joins existing scenario traces and replay artifacts
with the Assurance evaluator. Neither core package depends on the other. No new expression
language, generation engine, trace representation, shrinker, or replay-token format is added.

The implemented profile is **one actor in a declared sequential atomic reference realization**.
It supports state invariants and state-changing transition contracts. It explicitly rejects
historical and bounded-response claims until Simulation carries the necessary event evidence.
A scheduled operation with no state replacement has unknown commit status: current traces
cannot distinguish rejected attempts from successful no-op commits. It remains inconclusive
for a transition contract. State invariants can still inspect the retained unchanged state.

```csharp
var binding = new ScenarioAssuranceBinding(
    ObservationContract: observationContract,
    AtomicScope: acceptedSingleActorAtomicScope,
    Target: exactCandidateReference,
    Interpreter: candidate.Identity,
    ActorId: "driver");

var run = await ScenarioAssurance.ExecuteAsync(claim, initialWorld, candidate, binding);
// This invokes candidate operations through ScenarioRunner and retains a fresh trace.

var recheck = ScenarioAssurance.Reevaluate(claim, run.Trace, binding);
// TraceReevaluation: does not execute candidate code.

var repairedRun = await ScenarioAssurance.ReplayAsync(
    claim, run.Trace, repairedCandidate,
    binding with { Target = repairedReference, Interpreter = repairedCandidate.Identity });
// CandidateExecution: reuses initial observations and scheduled inputs, never old after-states.
```

The mapping requires an exact observation contract, target and interpreter identity, and an
explicit atomic/closed-schedule scope assumption. Caller-supplied identity is attribution,
not verification of the candidate binary. No storage guarantee or race safety follows from
the sequential reference runner. External interpreters need independent realization evidence.
The profile closes the finite declared actor schedule; it does not close an external stream.
Uncertain outputs do not invent an external commit receipt.

For invariants, the named actor is the entire declared population and is observed initially
and after every action. For transition contracts, `OperationId` selects operations involving
that actor or changing its state. Step is schedule index + 1; time is ticks since scenario
origin. Witness occurrences retain action and actor identities, linking back to the exact
scenario document. Evaluation inputs pin the complete trace fingerprint. Predicate/source
maps remain in the claim artifact. A trace fingerprint is evidence identity, not a signature.

## Generated search, shrink and replay

[ScenarioAssuranceTests](../Cohesive.Simulation.Assurance.Tests/ScenarioAssuranceTests.cs)
uses Simulation's existing generated property cases to search blocker count and irrelevant
activity. Each candidate input authors a world and scenario through builders, executes the
faulty candidate, and projects `run.ToPropertyEvaluation()` to the existing property protocol.
A supported violation fails; non-vacuous satisfaction passes; uncertainty, exhausted evaluation,
or unexercised triggers discard and count against Simulation's discard limit.

The search finds the seeded Motion DQ-style defect, shrinks to **two blockers and zero noise**,
and retains Simulation's property-case replay token. Replaying that token reconstructs the
source parameters. Scenario replay actually invokes the faulty candidate and reproduces the
violation; running the same inputs against the repaired candidate produces a new satisfied
trace while leaving the original violated evidence intact. This is parameter shrinking that
regenerates scenario source, not a claim of globally minimal schedules or interleaving search.
The policy predicate is independently stated over raw state; it does not call the candidate's
guard. The fixture illustrates policy and does not integrate Motion's production DQ code.

The current generated property runner is synchronous. The search fixture deliberately uses
an entirely synchronous reference candidate and verifies immediate task completion before
reading its result. It never blocks a pending task. Async candidate execution and replay are
supported and tested separately. General async property campaigns require an async search
interpreter; this bridge does not introduce a second search/shrink engine or block async I/O.

Retain the generation replay token/result, binding and method label, serialized claim,
`ScenarioExecutionTraceJsonSerializer` output, and `TraceEvaluation.Serialize()` output as
separate existing artifacts. Restore and re-evaluate the retained trace independently; merely
restoring/rechecking artifacts must never be reported as candidate execution. This package
has no production monitoring adapter, generalized event binding, no-op commit receipt mapping,
transaction scheduler, cross-aggregate ordering, or ARI/ITO integration.
