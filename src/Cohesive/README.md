# Cohesive

`Cohesive` contains the portable values, shape model, expression IR, execution contracts, and provenance primitives
shared by every Cohesive block and adapter.

## Install

```bash
dotnet add package Cohesive
```

## Start with a shape

Ordinary CLR types can produce a deterministic shape graph without hand-authoring fields or node identities:

<!-- docs-sync:core-shape:start -->
```csharp
using Cohesive.Model;

[ShapeDefinition("shape.shipment", ShapeRoles.Transport)]
public sealed record Shipment(string Id, IReadOnlyList<Stop> Stops);

[ShapeType("type.stop")]
public sealed record Stop(string City, string State);

var graph = new ClrShapeGraphBuilder()
    .AddShape<Shipment>()
    .Build(new("shipping"));
```
<!-- docs-sync:core-shape:end -->

The graph records the semantic types, fields, cardinalities, roles, and CLR provenance used by higher-level blocks.
It can be persisted, validated, generated into another host language, or interpreted by a target adapter.

## What this package provides

- Graph-qualified shapes, fields, paths, scalar types, cardinality, and nullability.
- Immutable `ObservationValue` and `Observation` values with exact shape evidence.
- Explicit `EntityObservationSnapshot` values when identity and version apply.
- Portable `Expr` definitions and expression-site analysis shared by compilers and interpreters.
- Canonical execution-definition, interaction, control, trace, explain, provenance, and compatibility contracts.
- Native operation-telemetry emission with failure isolation for caller-owned activities and instruments.
- Common typed quantities, identifiers, codes, paths, diagnostics, and deterministic serialization helpers.

The package does not define Relations, Transitions, Processes, APIs, presentation, storage, or provider behavior.
Those blocks depend on these shared semantic contracts.

Strict portable-document consumers can use `StrictDocumentJson.ComputeCanonicalSha256(value, options)`
to hash the same bytes as `GetCanonicalBytes` without retaining canonical output. It returns a raw
SHA-256 digest; the consuming block owns its fingerprint profile and prefix. See the
[qualification and operating boundary](../../docs/performance/strict-document-hashes.md).

Declared converter/polymorphic values project directly into owned observations without a mutable
JSON tree. See [typed projection qualification](../../docs/performance/hosted-value-projection.md).
JSON-to-observation conversion reuses repeated property names and string values within large input traversals;
its temporary bounded table never retains parser data after return. Small inputs bypass preparation.
See [property-name qualification](../../docs/performance/observation-name-reuse.md).
CLR property projection prepares immutable property names, ignore decisions and declared-JSON dispatch
once per exact runtime type through Core's weak preparation cache. Caller values and traversal/cycle
state remain invocation-owned; failed preparation is retryable. Property getters still run on every call,
and ordinary CLR projection builds fresh immutable storage. See [projection metadata qualification](../../docs/performance/observation-projection-metadata/README.md).

`ObservationValue.Deserialize<T>` uses pooled UTF-8 storage; results own retained JSON data.
Explicit `IImmutableObservationProjection` types (including `ShapeGraphDocument`) can reuse decoded
interpretations of the same immutable payload under the same frozen serializer profile and binary encoding.
Mutable results remain independent. Reuse conveys no validation or admission evidence.
Shape graph documents prepare their observation representation once by immutable object identity;
new envelopes over the same graph share its payload and typed interpretation. First decoding and
external admission still run normally. Preparation remains owned by the live document/graph. Deterministic converter-backed immutable values
also retain one prepared observation per exact owner and declared type under the fixed profile; mutable
converter values remain independent, failures retry, and nested reuse still enforces the depth limit.
An opaque object read may propose its original storage; the first reprojection verifies converter output
before retaining that storage, so normalization never silently reuses different input.
The hosted materializer reads immutable collections and supported polymorphic members directly from observations;
customized contracts retain their serializer. See [prepared projection qualification](../../docs/performance/prepared-observation-projections.md).
`CanonicalJsonWriter.GetCanonicalSequenceText` returns owned text using the same explicit profile, encoder and exclusions as streaming canonical output.
`AnnotationValue.FromString` retains well-formed strings directly; ordinary serialization and scalar
flattening do not need a JSON document. Requesting `Value` lazily produces an owned compatibility view.
Parsed annotations retain their original owned JSON snapshots. See
[native annotation qualification](../../docs/performance/native-string-annotations.md).
`AnnotationMapJsonConverter` optionally shares owned backing for annotation maps up to 64 KiB of encoded JSON; use it for buffered messages and budget for parsing the complete map (including parser metadata).
See [decoding contracts and allocation budgets](../../docs/performance/pooled-observation-decoding.md).

## Observations and snapshots

An `Observation` describes an immutable shaped value. Entity identity and version remain explicit rather than being
silently attached to every value:

```csharp
Shipment shipment = observation.Materialize<Shipment>();

var snapshot = new EntityObservationSnapshot(
    new EntityId("shipment-42"),
    version: 3,
    observation);
```

Physical layouts, source placement, relation occurrences, and storage concurrency tokens belong to the interpreting
block or adapter.

`PortableExecutionValidator` reuses successful checks of immutable inline type declarations and
structural evidence for immutable observation storage. Type compatibility and contract modifiers
are checked on every call; named types and shapes resolve against that call's graph. Newly decoded
or changed payloads require fresh structural validation. See the [structural-validation qualification](../../docs/performance/observation-structure-validation/README.md).

## Native operation telemetry

Library instrumentation can compose `OperationTelemetryEmitter` over its own native .NET diagnostic objects. The
instrumentation owner still defines every scope, instrument, operation, status, tag, and log; the emitter only pairs
activity completion with duration/failure measurements and prevents synchronous observers from changing the measured
operation. Duration histograms use seconds.

```csharp
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Cohesive.Observability;

ActivitySource activities = new("Example.Library");
Meter meter = new("Example.Library");
Histogram<double> duration = meter.CreateHistogram<double>("example.operation.duration", "s");
Counter<long> failures = meter.CreateCounter<long>("example.operation.failures", "{failure}");
OperationTelemetryEmitter operations = new(activities, duration, failures);

var activity = operations.StartActivity("example.operation");
var started = operations.StartTimer();
Exception? failure = null;
try
{
    RunOperation();
}
catch (Exception exception)
{
    failure = exception;
    throw;
}
finally
{
    TagList tags = default;
    tags.Add("example.operation.kind", "compile");
    operations.CompleteOperation(
        activity,
        started,
        failure is null ? ActivityStatusCode.Ok : ActivityStatusCode.Error,
        tags,
        failure);
}
```

Hosts collect the source and meter with their native OpenTelemetry configuration. The emitter neither references the
OpenTelemetry SDK nor owns logging, export, sampling, or service-level policy.

## Go deeper

- [Core internals](INTERNALS.md) covers observations, portable JSON values, execution catalogs, interactions,
  durable requests, Process control, and expression analysis.
- [System-wide documentation](../../docs/index.md) explains how the blocks fit together.
- [Semantic model](../../docs/concepts/semantic-model.md) introduces the shared vocabulary.
- [Observation identity decision](../../docs/decisions/observation-identity-snapshot-and-occurrence-semantics.md)
  records the ownership boundary between values, snapshots, and occurrences.
- [Native OpenTelemetry registration decision](../../docs/decisions/native-opentelemetry-registration.md) records the
  boundary between library emission, host collection, and adapter/provider scopes.

Related application blocks include
[`Cohesive.Relations`](../Cohesive.Relations/README.md),
[`Cohesive.Transitions`](../Cohesive.Transitions/README.md), and
[`Cohesive.Processes`](../Cohesive.Processes/README.md).

### Ordinal observation construction

`Observation.Create(shape, layout, immutableValues)` retains an `ImmutableArray<ObservationValue>` in a shared
`ObservationLayout`; the span overload snapshots caller-owned values. Layouts belong to the exact graph instance and
shape from which they were compiled. Each slot is one canonical field: `Undefined` means absent, while `Null` remains
present. Full shape validation still runs. Name-based `Fields` is an immutable view over the same vector, and canonical
serialization, equality, and fingerprints are independent of physical field order. The field view also implements
`IOrdinalObservationFieldReader`, allowing a materializer compiled against that layout to read directly by ordinal.

Default CLR materialization converts native byte observations directly to an independently owned `byte[]`, including
inside conventional nested records and arrays. This copies mutable output once; it does not route bytes through JSON
text. Explicit serializer customizations retain their chosen conversion contract. Durable detached observation values
can opt into `PortableValueJsonConverter.TaggedObservationValues`, which reuses the PortableValue node encoding to
preserve byte, temporal, numeric, and undefined kinds. Opting in changes the wire format and requires a versioned profile;
ordinary observation JSON remains unchanged. Entity receipts use the explicit `EntityStorageJson` profile in Storage.
Ordinary `ObservationValue.WriteTo` retains each store's existing field order, including layout order.
It directly enumerates common small, owned-dictionary and ordinal storage without boxing; other
collection implementations retain their normal enumerator and disposal behavior. See
[JSON writing qualification](../../docs/performance/observation-json-writing/README.md).

Portable validation preserves deterministic diagnostics and writers preserve ordinal field encoding;
see [field-ordering qualification](../../docs/performance/portable-observation-field-ordering.md).

## Type-level value admission

`ObservationValidator.TryValidateAgainstType(value, type, out error, graph)` exposes the same
type checks used by observation admission without manufacturing a one-field shape. Named types
resolve only in the supplied graph. This validates a concrete type; field presence and nullability
remain the caller's contract. Relation draft admission uses it for graph-owned enum literals.

Nested object and structural validation preserves exact-property precedence and uses case-insensitive
fallback matching. Unknown-property diagnostics preserve declaration order. Named enums accept both
member names and literal aliases; duplicate union discriminators select the first declared case.
Nonstring primitive literals retain their representation-sensitive matching rules.

Repeated validation shares immutable preparation within the exact graph and type declarations.
Cache keys are weak, concurrent preparation is safe, and failed preparation can be retried. Instance
values, allowed union discriminators, depth limits and diagnostics remain operation-scoped.
JSON decoding shares prepared dispatch indexes with validation without preparing unrelated closures.
These optimizations introduce no public type IDs or wire contracts and do not replace validation.

See the [performance overview](../../docs/performance/execution-preparation-allocations.md) for cache
ownership, implementation invariants, benchmarks and qualification.

## Exact decimal text

`ObservationValue.TryParseExactDecimal(text, out value)` validates signed invariant decimal text
without rounding. It shares bounded coefficient parsing with JSON-number acquisition, while JSON
alone permits exponent notation. The expression evaluator delegates to this helper. The existing
`TryGetDecimal` convenience coercion keeps its broader BCL syntax and rounding behavior.

The [execution preparation allocation overview](../../docs/performance/execution-preparation-allocations.md)
describes current immutable type reuse, guarded compact reference replay, canonical-byte interning,
shared node/element canonical traversal, and retryable weak metadata preparation. It links the final
qualification reports. Execution normalization, type ordering and fingerprints share one canonical
JSON authority; pooled buffers never escape their operation. Imported documents retain strict validation.

### Concurrent JSON profile metadata preparation

`SystemTextJsonClrShapeMetadataProvider` may be shared by independent CLR graph builders, as in the
relation authoring defaults. It freezes serializer options and retains nested field-profile metadata by
property for the provider lifetime. One reentrant gate protects cache lookup/publication and the active
profile set across nested preparation. A concurrent first use must not be mistaken for a recursive
converter profile. Real recursive profiles remain rejected, and failed preparation clears recursion
state without caching failure. The gate covers synchronous metadata preparation, not query execution,
source reads or observation materialization. Retained named types are reused after successful preparation.

Ari's parallel qualification exposed duplicate-key insertion and false recursive-profile failures.
`ClrShapeGraphBuilderMetadataTests` reproduces cold concurrent access for plain, nested and collection
properties, verifies shared retained type identity, and preserves real-cycle rejection and failure cleanup.
Warm allocation guards separate cache preparation from 1,000 repeated reads after 10,000 warm-up calls.
A local .NET 10 allocation probe over 100,000 warm reads measured approximately 152/1,496/1,584 bytes per
plain/nested/collection lookup both before and after synchronization. These existing costs include field
metadata/reflection projection; longer converter type names in the regression fixtures measure
2,032/2,120 bytes for nested/collection fields and use separate bounds. The change makes no throughput
or end-to-end latency claim. Cold preparation
is measured separately and includes serializer/CLR metadata initialization, so its total is not retained
cache size. No additional per-row cache or alternative semantic model was introduced.

Execution documents retain their independently computed semantic fingerprint for their immutable lifetime.
Imported documents compute from their own payload on first use; declared metadata and graph-dependent
extension validation remain fresh on every admission. Only the digest is retained. See
[fingerprint reuse qualification](../../docs/performance/execution-definition-fingerprint-reuse.md)
for concurrency, exact-byte equivalence, cold/warm evidence, and cache scope.

## Canonical JSON sequences

For immutable JSON tokens, `CanonicalJsonWriter.WriteCanonicalSequence(writer,
root, profile)` streams into a caller-owned `Utf8JsonWriter`. Select
`CanonicalJsonSequenceProfile.ExactDecimalRational` for normalized numbers and
rejected duplicate keys, or `PreserveLexical` for unchanged numeric spellings and
stable duplicate-key order. Both preserve arrays and sort keys by ordinal UTF-16.
The caller's encoder controls escaping. The lexical profile flushes complete tokens
periodically; a single large token can still grow the writer buffer. Failures can
leave partial output, so the caller owns disposal and transactional publication.

Use `ComputeCanonicalSequenceSha256(element, profile, writerOptions)` to hash through
Core's pooled incremental sink without retaining canonical output. Match `writerOptions`
to the direct writer, including its encoder, to preserve exact hash bytes.

`CanonicalJsonPropertyExclusions` prepares exact object-property JSON Pointer paths
such as `/identity/contentHash` once. Pass it to sequence writing or hashing to omit
those properties during traversal. Rules are ordinal, apply to repeated occurrences,
and do not propagate into array elements. Omitted values are not validated: use this
for an explicit fingerprint projection, alongside separate document admission.

Ordered canonical hashing can combine structural array ordering, exact property exclusions and a raw
version prefix in one traversal. Hash APIs require an explicit writer-options argument; choose the same
encoder as the existing fingerprint contract. Relations preserves its relaxed escaping and versioned
bytes. See [repeated representation qualification](../../docs/performance/repeated-representation-work.md).

`ObservationMaterializer.PrepareJsonValue<T>()` prepares plain JSON interpretation for
already-admitted portable values. Conventional members, including standard read-only lists,
are projected directly; a customized member retains its JSON contract without forcing the
whole row through JSON. Results remain independent. Shape-qualified admission is separate.

`ObservationValue.WithFields(assignments)` applies an ordered batch of field-only assignments
without rebuilding the whole object for each edit. `Undefined` removes a field; unchanged
subtrees retain their immutable storage. It does not replace Transition before-value evidence
checks or establish a backend patch operation.
