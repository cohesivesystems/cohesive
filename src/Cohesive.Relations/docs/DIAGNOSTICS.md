# Diagnostics and Relation Requirement Gaps

Diagnostics are part of the Cohesive.Relations semantic surface. They identify the definition, plan, node, binding,
input, relationship, expression site, output, placement, and adapter evidence involved wherever that information is
available. Applications can test, present, monitor, or gate on stable codes and typed payloads without parsing
messages.

## Five distinct failure boundaries

Do not collapse these cases into “mapping failed”:

| Boundary | Example | Where to inspect it |
| --- | --- | --- |
| Definition error | A projection references an unknown field or cannot preserve CLR semantics | Authoring validation and static-compilation diagnostics |
| Inference ambiguity | Ari proposes two plausible source expressions for one draft slot | Portable relation draft plus producer-owned inference evidence |
| Runtime requirement gap | Load references `customer-404`, but an authoritative lookup finds no Customer | `RelationQueryExecutionResult.RequirementGapAnalysis` |
| Capability/realization failure | A cross-document traversal is offered to the single-container Cosmos SQL compiler | Profile or bound-realization decisions and diagnostics |
| Adapter/runtime failure | A source read times out, returns partial evidence, or violates its binding | Physical-execution diagnostics, source-read traces, and gaps |

A definition hole prevents acceptance of a relation draft. Inference uncertainty belongs to the producer that
proposed the draft. A runtime requirement gap occurs only after a valid definition has compiled and available
evidence cannot establish one of its demanded inputs.

## Missing Customer: a real requirement gap

Consider a demand-scoped execution of the enriched relation that selects `Id`, `CustomerName`, and
`EquipmentNumber`:

```text
Load.CustomerId -> Customer identity
Load + Customer + Equipment -> LoadSearchDto
```

The Load supplies `CustomerId = "customer-404"`. The Customer traversal completes authoritatively with zero
results. Canonical execution becomes `Incomplete` and emits one `RelationRequirementGap` with the following actual
semantic content (irrelevant fingerprints and generated identities are abbreviated):

```text
Cause: RelatedObservationNotFound
Input: relationship Load.Customer
Occurrence.Binding: load

RelationshipContext:
  Direction: Forward
  JoinKind: Left
  ExpectedCardinality: AtMostOne
  ObservedState: Completed
  Completeness: Complete
  ObservedCount: 0
  ReferenceValue: "customer-404"

RequiredFields:
  Customer.Name

Impacts:
  LoadSearchDto.CustomerName (value/acquisition)

SuggestedResolutions:
  ProvideRelatedObservation
```

Only `Customer.Name` appears because the selected output does not demand `Customer.Type`; the compiled requirement
graph has already pruned that unrelated field and its `CustomerType` assignment. This is not an exception and not
the same as a Customer whose `Name` is null. The payload proves that lookup was
attempted, its empty result was authoritative, and the referenced identity was not found. If the read were partial,
canceled, or failed, the cause and completeness would say so instead.

Inspect it without message parsing:

```csharp
var result = outcome.Result
    ?? throw new InvalidOperationException("Evaluation did not reach canonical interpretation.");

foreach (var gap in result.RequirementGapAnalysis.Gaps)
{
    if (gap.Cause == RelationRequirementGapCause.RelatedObservationNotFound
        && gap.RelationshipContext is { } relationship)
    {
        Console.WriteLine(relationship.Definition.Id);
        Console.WriteLine(relationship.ReferenceValue);
        Console.WriteLine(string.Join(", ", gap.RequiredFields));
        Console.WriteLine(string.Join(", ", gap.SuggestedResolutions));
    }
}
```

The executable enriched conformance scenario verifies that the same absent Customer produces the same cause, input,
field impact, provenance, and partial DTO under both explicit reference evidence and bounded physical acquisition.

## Required fields and consuming assignments

Static compilation starts from demanded outputs and traces every causal input. For a projection assignment such as:

```csharp
CustomerName = customer.Name
```

the plan records:

- The exact `Customer.Name` field input and value contract.
- The relationship input needed to establish the Customer occurrence.
- The `Load.CustomerId` correlation field.
- The projection assignment and expression site that consume the value.
- The output-oriented lineage contribution.
- The input-oriented dependency impact on `LoadSearchDto.CustomerName`.

`RelationRequirementGap.Impacts` is copied from that canonical dependency manifest. This lets an application see
that missing Customer data affects `CustomerName` without treating an unrelated Equipment field as unresolved.

## Completeness is not nullability

Source and traversal evidence distinguish several states:

- **Complete empty:** acquisition succeeded and proves no matching observation exists.
- **Partial:** some evidence is available, but omission is not authoritative.
- **Failed:** acquisition attempted and failed with an attributable reference.
- **Not attempted:** no resolver or physical stage established the input.
- **Missing:** the field is semantically absent.
- **Null:** the field is present with an explicit null value.
- **Inconclusive:** the runtime cannot prove a value, absence, or definitive failure.

These distinctions determine whether an outer join may emit an unmatched row, whether an aggregation is complete,
and whether retrying acquisition can change the answer. They also prevent a backend's null convention from silently
weakening canonical missing/null semantics.

## Requirement-gap policy

`RelationRequirementGapPolicy.Conventional` leaves all affected values unresolved, reports required impacts, and
retains optional impacts without reporting them. Applications can provide an explicit
`IRelationRequirementGapPolicy` to choose a disposition and reporting action per gap and dependency impact:

- Leave the value unresolved.
- Suppress an affected output.
- Substitute null only when the output contract permits null.
- Substitute an explicit default only when it satisfies the output value contract.
- Report or suppress the diagnostic independently of output disposition.

An unsafe substitution is rejected with its own structured policy diagnostic; policy cannot erase a semantic type,
presence, nullability, or cardinality contract. Keep policy scoped and attributable rather than using it as a global
“ignore missing data” switch.

## Partial DTO materialization

`RelationDtoMapperCompiler` maps canonical output rows and retains their completeness metadata. With
`RelationDtoMappingFailurePolicy.CollectDiagnostics`, the missing-Customer example can still produce:

```text
LoadSearchDto
  Id:              load-42
  CustomerName:    null
  EquipmentNumber: TRUCK-003

Source.IsComplete: false
Source.UnresolvedGaps: [the Customer gap identity]
```

That partial value is explicitly marked incomplete. `RelationDtoMappingFailurePolicy.Strict` governs CLR
construction and conversion failures; it does not discard a nullable DTO that materialized successfully from an
incomplete canonical row. Callers that require complete data reject `mapping.Status == Incomplete`, or configure an
explicit requirement-gap/output policy before materialization. The mapper does not reinterpret absence or perform a
hidden Customer lookup.

## Capability and adapter failures

Capability failures occur before unsupported execution. A realization report contains one decision per demand:

- `Native`: the target directly advertises proof.
- `Composed`: declared facilities together preserve the requirement and guarantees.
- `Constrained`: proof is valid only after named operating boundaries are validated.
- `Override`: an explicit attributable override supplies proof.
- `Unavailable`: no permitted strategy preserves the requirement.

For example, Cosmos SQL reports the single-source boundary for a cross-document traversal. The application may
choose the composed source-reader plan described in [Execution and adapters](EXECUTION_AND_ADAPTERS.md), change
placement, or fail the request. The compiler never silently drops the relationship.

Adapter failures retain their physical source, stage, placement binding, state, completeness, selected fields, and
opaque evidence reference. Human-readable messages avoid embedding provider payloads or source values by default.

## Explain and observability

Project an evaluation outcome into a portable explain artifact:

```csharp
var explain = RelationQueryExplainProjector.Project(outcome);
```

The artifact includes the latest reached stages and a sanitized evaluation summary. Requirement gaps are grouped by
stable cause, input, relationship, and output impact; raw source values are not copied into the explain payload.
Native adapter explain projectors add payload-free artifact fingerprints and provenance.

Runtime operations emit activities and metrics with stable names from `RelationQueryTelemetry`. Tags include status,
compiler/profile identities, plan and artifact fingerprints when enabled, diagnostic counts, and terminal phase.
Sensitive evidence remains behind application-controlled opaque references.

## Testing guidance

Prefer assertions over typed structure:

```csharp
var gap = Assert.Single(result.RequirementGapAnalysis.Gaps);
Assert.Equal(RelationRequirementGapCause.RelatedObservationNotFound, gap.Cause);
Assert.Equal(RelationQueryEvidenceCompleteness.Complete, gap.RelationshipContext!.Completeness);
Assert.Contains(
    RelationRequirementGapResolutionKind.ProvideRelatedObservation,
    gap.SuggestedResolutions);
```

Also assert the affected output fields and that unrelated outputs remain complete. For adapter tests, assert the
source-read constraint, completeness, and evidence reference in addition to the final diagnostic code.
