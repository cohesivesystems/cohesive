# Cohesive.Simulation.Transitions

`Cohesive.Simulation.Transitions` is the optional semantic composition between deterministic simulation scenarios and
canonical Cohesive Transitions. It lets a scenario operation execute through the pure Transition reference
interpreter and turns an accepted sparse patch into an explicit, validated actor observation replacement. The core
`Cohesive.Simulation` package remains independent of the Transitions language.

## Install

The current alpha targets .NET 10:

```bash
dotnet add package Cohesive.Simulation.Transitions --prerelease
```

## Bind operations to Transitions

Compile and retain the Transition document as its semantic authority, then bind its exact plan to a scenario
operation:

```csharp
CompiledTransitionPlan assignLoad = authoredTransition.Compile(shapes.Graph).Plan
    ?? throw new InvalidOperationException("The Transition did not compile.");

var interpreter = new TransitionScenarioActionInterpreter(
[
    new(
        operationId: "freight.assign-load",
        transition: assignLoad,
        subject: ScenarioTransitionSubject.TargetActor)
]);

ScenarioExecutionTraceDocument trace = await ScenarioRunner.ExecuteAsync(initialWorld, interpreter);
ScenarioWorldSnapshot finalWorld = trace.ToFinalWorldSnapshot();
```

The scenario operation input and output contracts must exactly match the bound Transition. Subject selection is
explicit: `Actor` uses the action actor, while `TargetActor` requires the action to name a target. The interpreter
supplies the subject's complete current observation as both evaluation and fresh commit evidence. Accepted patches are
projected through `TransitionStateProjector`, validated against the Transition's exact Shape graph, and returned to the
runner with the prior observation as concurrency evidence. The next scheduled action therefore sees the evolved
state.

The interpreter identity deterministically pins the adapter profile, operation bindings, subject policies, and exact
Transition definition identities, revisions, and fingerprints. Retain the canonical Transition documents beside the
scenario and its execution trace so that identity remains resolvable and inspectable.

Admission and domain rejections remain authored outputs and do not change state. Invalid or infrastructure decisions
become failed portable outputs. The current profile deliberately fails closed for absent-subject creation, emission
intents, and Machine movements: the scenario result model does not yet retain or atomically commit those effects, so
silently dropping them would weaken Transition semantics.
