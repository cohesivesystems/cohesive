# Cohesive.Transitions.Assurance

Optional binding from authoritative Transition admission IR to finite Assurance checks. Neither
Transitions nor Assurance core depends on this package.

`TransitionAssurance.CheckAdmission(transition, claim, before, members, source, configuration)`
returns ordinary `FiniteGuardCheck.Source<TEntity>`. Call `Build()` and pass the document to
`FiniteGuardChecker.Check`. There is no second authored guard.

The initial profile checks **explicit admission soundness over an existing entity pre-state**.
It preserves the order of every admission rule, binds observation fields to the exact claim
input, and treats an empty rule list as true. The claim's subject and selector must pin the
transition revision. Import the authoritative entity shape into the expression authoring context;
a default CLR-named input is not assumed equivalent to an inline transition observation.

Supported expressions are state bindings/fields, constants/literals, unary/binary expressions and
conditionals admitted by the Assurance predicate profile. Command/local/parameter dependencies,
collection functions, typed field references without explicit rebinding, entity creation and
machine-bound lifecycle transitions are rejected. Unsupported dependencies are rejected even in
a short-circuited branch; they are never erased by simplification.

The derived plan retains the exact transition reference, individual admission-node references,
the binding profile, and catalog/assumption source. Source maps point to the corresponding guard
subexpressions. Execution-node references retain the enclosing definition fingerprint; resolve
the original transition document's source map for host-source locations. Evidence includes those
references as dependencies. Retain the transition document alongside the finite-check artifact
for later inspection; rechecking the finite artifact evaluates its retained expressions and
does not independently retrieve or rebind that external document.

A guard counterexample is a model-admission counterexample, not automatically a committed
implementation violation. Body branches, domain rejection, post-state invariants, reachability,
and storage coordination may impose further conditions. The integration test separately invokes
the real reference interpreter: the faulty transition activates a driver with a remaining blocker,
while the repaired transition rejects that same pre-state. This produces reference decisions,
not production commit receipts.

See [finite checking](../../docs/assurance/finite-checking.md) and the executable
[transition binding tests](../Cohesive.Assurance.Tests/TransitionAdmissionTests.cs).

## Typed offline probes and boundary inputs

```csharp
var probe = TransitionAssurance.Prepare(domain.InventoryTransitions.Reserve);
var cases = probe.GenerateInputs(stock, new StockQuantity(0));
foreach (var input in cases.Inputs)
{
    var result = probe.Execute(stock, input, new ActivationId($"quantity-{input.Quantity}"));
    // Assert independently supplied business requirements against result.State/result.Outcome.
}
```

`Prepare` compiles once; `Execute` delegates to the existing reference interpreter and state
projector. It preserves Applied, NoChange, AdmissionRejected and DomainRejected outcomes.
Other decisions throw with interpreter diagnostics. Complete decision evidence remains available.
No state is committed, no I/O occurs, and emission intents are not dispatched. This adapter is
for bounded test workloads and uses standard observation/CLR conversion; it makes no allocation,
trimming or NativeAOT guarantee.

`GenerateInputs` is a narrow input-proposal profile, not a solver or proof:

- It inspects every admission guard, including unreachable guards. Supported guards compare a
  direct nonnullable Int32 command member with an Int32 constant or supplied pre-state field,
  plus direct/negated boolean pre-state or command fields. Operators are equality/inequality and ordered comparisons.
- Each command member varies independently around each threshold (minus one, exact, plus one),
  with configured inclusive bounds and -1/0/1, clipped to that range. Boolean commands propose both values. Other input members retain the seed. The seed is always included;
  a guardless transition produces only the seed. Candidates are deduplicated deterministically.
- State-only boolean guards are inspected but do not generate state variations. Callers supply
  the state envelope, including both boolean values where needed. Multiple-input combinations,
  body paths, string constraints, nullable values and arithmetic predicates are not generated.
- Unsupported guards fail explicitly with the node identity. Exceeding `maxInputs` fails without
  returning a partial set. A missing production guard can remove proposed cases: retain independent
  requirements and baseline inputs when testing faulty models.
- Results retain the exact transition reference, profile, configured bounds/budget, inspected node IDs and motivating nodes for each candidate. Inspection is not branch
  coverage. Expected outcomes never come from generation.

The existing Simulation generation/property/shrinking APIs remain the general tooling for caller-
authored stochastic scenarios. This adapter supplies model-derived boundary proposals without
adding a second property runner, expression evaluator, random generator or third-party dependency.
See [typed probe tests](../Cohesive.Assurance.Tests/TransitionProbeTests.cs).

## Reusable execution and portable interpretation contract

`Cohesive.Transitions.Execution.OfflineTransition.Prepare(transition)` supplies the same typed
`Execute(before, input, activationId)` API without depending on Assurance. The Assurance probe
is a convenience over it, not a second executor. Storage-backed entity APIs additionally commit
state and receipts; this offline API never makes those guarantees.

Generation returns `Candidates` with `Input` and `MotivatingNodes`; `Inputs` is a convenience
projection. Deduplication merges motivation lists rather than dropping their provenance. A
`TransitionInputGenerationException` carries code `assurance.inputs.unsupported` and a node ID;
interpret that node under the probe's exact reference. No partial result is returned on failure.
Budgets and unsupported support are distinct from a business assertion failing.

A future TypeScript implementation should match input/state/outcome observations, decision kinds,
exact source identity, per-input node attribution, profile/bounds and explicit unsupported/limited
coverage semantics. It need not mimic C# generics or CLR records. Compare portable reports and
independent requirements using shared fixtures; do not infer correctness from model agreement.
The TypeScript runtime port is follow-up work; #581 supplies and tests the .NET capability.

### Joint state and command proposals

Configure integer domains with property selectors. JSON property names are resolved through
`FieldPath.Capture`. Use `options.Range(path, minimum, maximum)` for explicit model paths.

```csharp
var domain = new StateCaseOptions<InventoryItem> { MaxCases = 1024 }
    .Range(s => s.Available, 0, int.MaxValue)
    .Range(s => s.MaxPerOrder, 1, int.MaxValue);
var groups = probe.GenerateCases(template, inputSeed, domain);
```

Both generators resolve guards through the same Int32/Boolean profile. A comparison
may read the command, state or a constant on either side. State-to-state comparisons
vary both fields. An integer state field takes its template value and domain endpoints
and their inward neighbors. A literal threshold also contributes its value and both
neighbors, clipped to the domain. A peer state field contributes neighbors of its
template value. This samples equality and its neighbors near that template, without enumerating
every equal pair across the two domains. Unreferenced fields retain their template values. `ValidState` may
filter cross-field combinations before commands count toward the joint budget.
Every admitted state contributes at least the seed command.

The exploration and output are bounded. Unsupported guards, an empty valid domain
and exhausted budgets fail explicitly. These synthetic pre-states carry no history
of how an application could reach them. Execute pairs against independently named
coverage targets and business assertions to establish which behaviors they witness.

Read `probe.GenerationDiagnostic` for the first unsupported guard’s code, node and detail; null means supported.
Call `probe.EnsureGenerationSupported()` at setup to validate the guard profile. Both generators
reuse the same inspection result and perform the check on first use if setup omitted it. Execution alone can use the full
interpreter expression set. Unsupported guard syntax or operand types raise
`TransitionInputGenerationException` with the guard node. Invalid caller configuration or
missing template fields raise argument errors.

Typed offline execution supports existing machine-bound entities. Prepare the production
transition once and call `Execute(before, input, invocation)` for each example. The
result includes the projected state, typed outcome and exact edge evidence in
`Decision.MachineMovements`.

For single-step model-based test generation, prepare a discriminator-bound transition and pass a
template. The binding supplies each mapped state, including states where the action is disallowed.
Compiled edge guards contribute Boolean alternatives and Int32 boundaries using the same bounded
profile as other admission guards. Unrelated entity and input fields retain the template values.

```csharp
var probe = TransitionAssurance.Prepare(orderTransitions.Ship);
var cases = probe.GenerateCases(order, new() { MaxCases = 10 });
```

Cases are proposals. Supply the expected behavior independently. Predicate-only state bindings and
unsupported guard expressions report a generation diagnostic at setup. Sequence exploration is outside this profile.

### Selecting a small suite

```csharp
var options = new CaseSelectionOptions<Order, ShipQuantity> { MaxCases = 8, MaxCandidates = 1024 }
    .StateRange(order => order.Remaining, 1, 4)
    .InputRange(command => command.Quantity, -1, 5);
var suite = probe.SelectCases(order, new ShipQuantity(1), options);
foreach (var test in suite.Cases)
    VerifyAgainstContract(test.State, test.Input);
```

The candidate budget bounds exploration and fails on overflow. The suite budget limits the number returned.
The selector executes candidates offline against the prepared reference. It records observed states, decisions,
admissions and taken edges. Boolean guard values and integer distances of −1, 0 and +1 from guard thresholds
are boundary facts. Two reached boundaries sharing a command or state field form an intersection goal.
Machine boundary credit requires the selected movement. The first case maximizes observed goal gain. Later cases first maximize novel one-field contrasts
with already selected cases. The complete state and input must agree except on the reported field,
and at least one endpoint must be accepted. A contrast distinguishes decision kind, failing admission,
or accepted movement path. Equal contrast counts favor new single-case goals, shorter numeric changes,
rarer goals and finally generation order. `DecisionPairs` gives zero-based endpoints, the changed field
and both decision paths. These are observed witnesses, not a proof of independent effects across all paths. If all observed goals are covered, it fills remaining suite slots with distinct candidates in generation order.
Each pair exposes its covered goals; the suite reports observed goals left uncovered. This is a heuristic
over a finite generated pool. The consumer supplies an independent behavioral oracle.

### Selecting command sequences

Register a finite command vocabulary against typed transitions, then select paths from an actual seed state.
Registration order does not affect search order. Required paths are validated against the registered names
when declared and retain their slot even if their coverage overlaps. For example:

```csharp
var lifecycle = TransitionAssurance.ForSequences<Order>()
    .Command("Pack", transitions.Pack)
    .Commands(transitions.Ship, quantities.Select(q => new ShipQuantity(q)), q => $"Ship:{q.Quantity}")
    .RequireScenario("Pack", "Ship:1");
var suite = lifecycle.SelectSequences(order, new() { MaxSequences = 4, MaxStepsPerSequence = 8 });
foreach (var path in suite.Sequences)
    foreach (var step in lifecycle.ExecuteSequence(order, path.Commands))
        Observe(step.IsAccepted, step.State);
```

The selector scores clean reference decisions, state changes and adjacent decision pairs.
Each optional path must cover a new observed goal. Inspect `CandidateCount`, `SearchTruncated`
and `UncoveredGoals` when interpreting coverage. The default exploration ceiling is 4,096
offline executions and the beam retains up to 32 histories at each depth.
