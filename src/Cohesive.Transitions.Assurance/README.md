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
