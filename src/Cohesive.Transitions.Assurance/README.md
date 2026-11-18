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

Call `probe.GenerateCases(template, inputSeed, ranges, maxCases: 1024)` to vary state
fields referenced by the supported admission guards. Supply inclusive integer domains
keyed by the model's serialized field paths. Identity and other unrelated fields come
from the template. Each Boolean takes both values. Integer thresholds take the template
value, endpoints and their inward neighbors. For each resulting state the existing
command generator proposes values around its thresholds. The result groups commands
and their guard provenance by pre-state. An optional predicate further filters states
for cross-field invariants.

The exploration and output are bounded. Unsupported guards, an empty valid domain
and exhausted budgets fail explicitly. These synthetic pre-states carry no history
of how an application could reach them. Execute pairs against independently named
coverage targets and business assertions to establish which behaviors they witness.
