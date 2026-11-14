# Typed input constraints

Build the input model once and validate each input before retaining an identity,
checking capacity, or starting a workflow:

```csharp
using System.ComponentModel.DataAnnotations;
using Cohesive.Model;

public sealed record SubmitRequest(
    [property: Required, MaxLength(64)] string RequestId);

var inputs = new ClrShapeGraphBuilder()
    .UseDataAnnotations()
    .AddShape<SubmitRequest>()
    .BuildResult();

var validation = ObservationValidator.ValidateInput(request, inputs);
if (!validation.IsValid)
    return MapInvalidInput(validation.Diagnostics);
// Application admission starts here.
```

The generic overload projects the record with the standard observation projector.
Callers that already have a portable value use `ValidateInput(value,
inputs.GetShape<SubmitRequest>())`. Both return the first deterministic diagnostic,
with a stable code such as `constraint.maxLength` and a location such as
`$.RequestId`. This API does not retain an identity or invoke application work.
Hosts own HTTP error mapping and the choice of whole-request versus per-item
batch outcomes. Cache the graph, not the validation result for a mutable input.

## Mapping

`UseDataAnnotations()` is opt-in. It lowers attributes into the same portable
`ShapeConstraint` records used by explicit model authoring; no CLR validator
callback survives in the graph.

| Attribute | Portable meaning |
| --- | --- |
| `Required` | Required presence, non-nullability, and `RequiredConstraint(rejectEmptyStrings: true)` semantics. Empty and all-whitespace strings fail. |
| `Required(AllowEmptyStrings = true)` | Required presence and non-nullability; empty/whitespace strings are allowed. |
| `MinLength(n)` | At least n UTF-16 code units for strings, or n items for arrays/collections. Null and absent optional values pass. |
| `MaxLength(n)` | At most n UTF-16 code units or items. Null and absent optional values pass. The attribute's -1 means unbounded; 0 and values below -1 are invalid DataAnnotations declarations. |

Length is neither UTF-8 byte count nor grapheme count: 32 supplementary Unicode
characters occupy 64 UTF-16 code units. Portable byte arrays count bytes as items.
The attribute provider accepts strings, arrays, and `ICollection`/`ICollection<T>`
contracts. Arbitrary enumerable/Count-shaped CLR objects are outside this subset.

Explicit `RequiredConstraint` retains its presence/non-null meaning; its new
`RejectEmptyStrings` flag defaults to false. The attribute provider sets that flag
from `!RequiredAttribute.AllowEmptyStrings`. Explicit `MaxLengthConstraint(0)`
remains valid, although DataAnnotations rejects `[MaxLength(0)]`.

Attribute and explicit provider constraints are conjunctive. Later metadata does
not override an earlier bound. Contradictory min/max bounds for the same target
are rejected during metadata composition. Unsupported/custom validation
attributes, resource-based messages, and `IValidatableObject` are rejected during
model authoring with coded exceptions. Literal error messages remain metadata;
public error formatting belongs to the host.

## Admission and value contracts

Dictionary, object, ordinal, complete/prepared projection, and named structural
validation use the same required/length evaluator. Constraints on a field apply
to its whole value, including a many-valued collection. Shape constraints may
target one declared field. Nested objects enforce constraints on their own named
shape. Nested target paths and wildcard quantification are unsupported in this
initial subset. Other constraint kinds fail admission with
`constraint.unsupported`; unsupported paths use `constraint.unsupportedTarget`.
They are not silently treated as implemented rules.

`ValueContract.FromField` retains field constraints. `FromShape` retains shape
constraints and projects direct field constraints to named targets. A field
constraint with its own relative target is rejected rather than flattened
incorrectly. Constant checks and portable execution admission enforce retained
constraints. Named types still require their owning graph for complete admission;
`IsSatisfiedByConstant` alone only resolves local semantics. Extracting
`GetEffectiveType()` is structural inspection, not constraint admission.

Empty contract constraints are omitted from JSON, preserving unconstrained
contract fingerprints. Contracts that previously discarded constraints now
include them, intentionally changing those semantic fingerprints. Existing
serialized contracts that omitted rules cannot retroactively acquire them;
re-author constrained definitions when upgrading. No automatic host middleware
or validation-before-side-effects guarantee is implied: call admission at the
application boundary.
