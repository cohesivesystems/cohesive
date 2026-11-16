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
Length eligibility is checked once, after metadata composition, against the final
field contract. Explicit value-object-to-string overrides are supported for portable
values; an override does not itself supply a CLR-to-value conversion.
The builder discovers `IClrFieldProjectionValidator` hooks once per build and
runs them on each resolved field, including nested fields. JSON providers retain
their own frozen options; this validation hook does not identify concrete provider
classes. When a JSON metadata
provider is configured, its frozen options and resolver govern representation
checks; no independent default serializer or type mapper is consulted. Dictionaries and
property/type/configured
converters that cannot preserve native length are rejected during authoring; object entry counts are not implemented.
Arbitrary Count-shaped CLR objects are outside this subset.

Explicit `RequiredConstraint` retains its presence/non-null meaning; its new
`RejectEmptyStrings` flag defaults to false. The attribute provider sets that flag
from `!RequiredAttribute.AllowEmptyStrings`. Explicit `MaxLengthConstraint(0)`
remains valid, although DataAnnotations rejects `[MaxLength(0)]`.

Attribute and explicit provider constraints are conjunctive. Later metadata does
not override an earlier bound. Contradictory min/max bounds for the same target
are rejected during metadata composition. Unsupported/custom validation
attributes, resource-based messages, and `IValidatableObject` are rejected during
model authoring with coded exceptions. Literal error messages remain metadata;
public error formatting belongs to the host. Messages participate in persisted
constraint identity: changing a constraint's `Message` changes contract equality
and the enclosing definition fingerprint even when acceptance is unchanged. Keep
localized/UI wording in host error mapping when it should not version the model.

## Admission and value contracts

Dictionary, object, ordinal, complete/prepared projection, and named structural
validation use the same required/length evaluator. Constraints on a field apply
to its whole value, including a many-valued collection. Shape constraints may
target one declared field. Nested objects enforce constraints on their own named
shape. Nested target paths and wildcard quantification are unsupported in this
initial subset. Targets use canonical declared names; input value lookup uses
exact spelling first, then ordinal-ignore-case fallback, shared by all constraint
checks. This does not relax surrounding shape admission's canonical-key rules.
`ID` is not a declared target for a field named `id`, consistently for shapes,
named structural types, and object type references. Missing optional targets
use allocation-free traversal for built-in storage; case-insensitive fallback
still scans fields and remains O(n), without caching absent-value evidence.
Other constraint kinds fail admission with
`constraint.unsupported`; unsupported paths use `constraint.unsupportedTarget`.
They are not silently treated as implemented rules.

`ValueContract.FromField` retains field constraints. `FromShape` retains shape
constraints and projects direct field constraints to named targets. A field
constraint with its own relative target is rejected rather than flattened
incorrectly. Constant checks and portable execution admission enforce retained
constraints. Resolving a shape-only contract is idempotent and deduplicates the
single-value rules. For a many-valued contract, collection rules remain local;
item rules remain on the retained qualified shape and require the owning graph. Named types still require their owning graph for complete admission;
`IsSatisfiedByConstant` alone only resolves local semantics. Extracting
`GetEffectiveType()` is structural inspection, not constraint admission.

Empty contract constraints are omitted from JSON, preserving unconstrained
contract fingerprints. Contracts that previously discarded constraints now
include them, intentionally changing those semantic fingerprints. Existing
serialized contracts that omitted rules cannot retroactively acquire them;
re-author constrained definitions when upgrading. No automatic host middleware
or validation-before-side-effects guarantee is implied: call admission at the
application boundary.

## Integration status

This PR provides the SDK mechanism; it does not yet demonstrate order-submission
integration. cohesive-evals#42 owns calling typed admission in the shared queue,
before request-ID retention, capacity checks, and workflow execution. Its
qualification must cover both single and batch submissions. The typed API projects
the record on every call and therefore allocates; the portable-value validation
benchmark measures only admission of an already projected value.

## Trimming and NativeAOT boundary

Attribute discovery is reflection-based authoring, not a trimming-safe runtime
feature. `UseDataAnnotations` and provider acquisition carry
`RequiresUnreferencedCode`. Build and persist the graph before trimming, or
explicitly preserve the authoring members and attributes. `ValidateInput<T>`
also advertises trimming/runtime-code-generation requirements from CLR projection.
The portable `ObservationValue` overload avoids that projection step. This PR
includes a generated-JSON-metadata test, not whole-SDK NativeAOT certification.

## Message identity design

The current persisted identity policy above remains unchanged in this PR. Prefer
host-owned/localized display text. A follow-up semantic-identity design should
exclude constraint messages while retaining them as document metadata, covering
equality, deduplication, and the versioned fingerprint projection together.
Changing only equality here would leave fingerprint and equality inconsistent.

## Choosing the consumer boundary

For cohesive-evals#42, `OperationQueue.Submit` should validate the portable value
already produced at the admission boundary whenever it is available:

```csharp
var result = ObservationValidator.ValidateInput(inputValue, shapes.GetShape<SubmitInput>());
```

Here `inputValue` is an `ObservationValue`, not the CLR request record. This
avoids the typed overload's reflection/dynamic-code requirements and a second
projection. Calling `ObservationValue.FromObject(request)` just to select this
overload does not remove projection's trimming requirements. In a trimmed or
NativeAOT consumer, load the pre-authored graph and use a separately qualified
portable input decoder/projector. Ordinary JIT consumers may use the typed
convenience API; do not suppress analyzer warnings without qualifying that path.

## Shared and typed validator services

For ordinary JIT-hosted inputs, configure one service and let calls infer the
input type. With `Cohesive.Host.Services` in scope:

```csharp
services.AddCohesiveInputValidation(options => options
    .UseDataAnnotations()
    .For<FulfillmentRequest>());

// Inject IInputValidator into the application service.
var result = inputs.Validate(request);
```

`For<T>()` in registration prepares that type immediately, so unsupported
attributes or configuration fail during registration. Unregistered types are
prepared on first use. Each type has one lazy validator per service instance;
concurrent first use shares the winner, including deterministic authoring errors.
Dispatch uses the declared generic type, not `request.GetType()`. Configure and
call with the intended contract type; runtime subtype properties do not silently
change that contract.

For special contracts, name the type once at registration:

```csharp
services.AddCohesiveInputValidation(options =>
{
    options.UseDataAnnotations();
    options.For<SpecialRequest>(shape => shape.AddMetadataProvider(metadata));
});
```

`Configure(...)` supplies common shape authoring configuration, before per-type
configuration. Per-type configuration applies to that root's graph, not globally
to nested uses of the same CLR type in other roots. Options collections are
snapshotted; callbacks/providers must themselves be deterministic and thread-safe,
with stable captured state. They configure shape authoring, not a replacement CLR
projection/serialization pipeline. All registrations share the existing portable
constraint evaluator; no second interpretation of validation attributes is added.

Consumers may alternatively inject `IInputValidator<SpecialRequest>` and call
`validator.Validate(request)`. Typed injection delegates to the same shared cache.
Outside DI, use `InputValidator.Create(options => options.UseDataAnnotations())`
and optionally retain `inputs.For<SpecialRequest>()`.

Caches retain authored shapes and validators only, never request objects, mutable
projections or validation results. Every call validates the current input. Cache
size grows with the declared application type set; do not feed an unbounded set
of generated types into a long-lived instance. Configuration instances are isolated;
there is no process-global type-only cache. Duplicate DI registration and duplicate
per-type registration fail explicitly rather than changing an existing policy.

The typed APIs retain their trimming/dynamic-code annotations and per-request
projection cost. `inputs.For<T>().ValidatePortable(value)` avoids projection for
an existing portable value, but constructing the validator still requires CLR
shape authoring. For NativeAOT, use the existing portable validator with an
already-authored graph unless the CLR authoring/projection path is separately
qualified. No input-validation service performs queue admission or domain execution.
