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
  plus negated boolean pre-state fields. Operators are equality/inequality and ordered comparisons.
- Each command member varies independently around each threshold (minus one, exact, plus one),
  with Int32 extremes and -1/0/1. Other input members retain the seed. The seed is always included;
  a guardless transition produces only the seed. Candidates are deduplicated deterministically.
- State-only boolean guards are inspected but do not generate state variations. Callers supply
  the state envelope, including both boolean values where needed. Multiple-input combinations,
  body paths, string constraints, nullable values and arithmetic predicates are not generated.
- Unsupported guards fail explicitly with the node identity. Exceeding `maxInputs` fails without
  returning a partial set. A missing production guard can remove proposed cases: retain independent
  requirements and baseline inputs when testing faulty models.
- Results retain the exact transition reference and inspected node IDs. Inspection is not branch
  coverage. Expected outcomes never come from generation.

The existing Simulation generation/property/shrinking APIs remain the general tooling for caller-
authored stochastic scenarios. This adapter supplies model-derived boundary proposals without
adding a second property runner, expression evaluator, random generator or third-party dependency.
See [typed probe tests](../Cohesive.Assurance.Tests/TransitionProbeTests.cs).
