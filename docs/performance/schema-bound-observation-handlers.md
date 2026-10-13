# Schema-bound observation handlers

The canonical hosted Query remains the authority for input/result contracts, exact identity,
implementation affinity and configuration. Its CLR declarations need not be instantiated by
handlers that operate on canonical values. `AsObservation()` returns a runtime handle over the
same document and validation; existing observation and deterministic handler registrations,
admission, cancellation and result validation remain in use.

```csharp
var name = FieldPath.Capture<Row>(row => row.Name); // Prepare outside the handler.
var binding = DeterministicHostedQueryBinding.Create(query.AsObservation(), query.Implementation,
    (value, configuration, cancellation) =>
    {
        cancellation.ThrowIfCancellationRequested();
        if (!value.TryGetField(name, out var existing))
            throw new InvalidOperationException("Name is not available.");
        return value.WithFields([new(name, ObservationValue.FromString(existing.GetString() + "/updated"))]);
    });
```

The observation view retains supplied field presence and value kinds. It does not apply CLR
constructor defaults, setters or custom conversion as an implicit side effect. Implementations
must choose and qualify their intended semantics; materialize an individual value when its owning
contract or external library requires that boundary. Portable JSON documents whose contracts only
specify a JSON kind still require their owner's semantic admission. A runtime view is not evidence
that an opaque graph was validated.

No result or consumer CLR object is cached. Observation storage owns its inputs, and the handler
can return an unchanged subtree. Persisted/restored values get ordinary fresh admission. Sparse
reads avoid constructing unused CLR objects; they do not avoid initial JSON parsing. Datasource
pushdown is a separate provider capability. Its loaded-field evidence must distinguish unrequested
fields from absent/null fields before projected data can participate in updates.

`WithFields` extends the existing field-assignment implementation to an existing object. It applies
assignments in order, including overwrites and nested removals, then freezes touched dictionaries
once. Untouched subtrees retain storage. Failure leaves the receiver unchanged. It is an in-memory
update, not a durable patch format. Transition patches that read intermediate before-values must
keep those evidence checks in execution order.

## Qualification

Release .NET 10/macOS, median of seven synchronous calls after four warm-ups, with query authoring,
input construction, logging and assertions outside allocation intervals:

| Workload | Allocated bytes |
| --- | ---: |
| Typed forwarding, 1,024 strings of 128 characters | 495,504 |
| Observation forwarding, same payload | 1,328 |
| Observation forwarding, 8,192 strings of 128 characters | 1,328 |

These totals include fresh input and result validation. The test's fixed 4 KiB ceilings reject
payload-sized conversion; its size comparison is not a latency claim. Tests prove that a CLR schema
whose constructor throws can pass through the existing Process dispatcher without construction,
retains exact Query evidence, shares untouched storage after sparse writes, preserves independent
updated roots, and still rejects invalid input/result values and observes cancellation. A wire
round trip is readmitted and retains its newly owned storage.

Batch update tests compare sequential semantics, exception behavior and exact JSON, including
nested removals and null parents. The 128-field existing-object update has a 60 KiB warm ceiling;
empty assignments retain the receiver. Full JSON decoding and portable document integrity checks
are not bypassed by this API.
