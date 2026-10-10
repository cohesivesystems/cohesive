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

## Type-level value admission

`ObservationValidator.TryValidateAgainstType(value, type, out error, graph)` exposes the same
type checks used by observation admission without manufacturing a one-field shape. Named types
resolve only in the supplied graph. This validates a concrete type; field presence and nullability
remain the caller's contract. Relation draft admission uses it for graph-owned enum literals.

Nested object and structural validation retains exact-property precedence. On a case-insensitive
fallback it builds one operation-local property index, preserving the first matching source
property; no instance values enter shared caches. Unknown-property diagnostics lazily prepare
case-insensitive name sets on the graph-bound plan node. Standalone checks use one shared weak
declaration table. Metadata slots publish successful preparation once and retry failures.
Allowed union discriminators,
depth limits, values, and diagnostics remain operation-scoped. The existing structural
field lookup uses ordinal identity and cannot replace this case-insensitive diagnostic index.

Graph-bound array, object and named validation lazily binds reachable named definitions and child
links once per exact graph and type object. Graph and root keys are weak; independently requested
roots share prepared children. Distinct named references resolving the same declaration also share
its metadata container within that graph. A per-graph preparation gate coordinates complete recursive
closures,
while warm readers bypass that gate. Failed preparation publishes no incomplete nodes. Preparation
uses an iterative work queue; value validation still applies depth limits and builds fresh diagnostics.
Unbound checks and scalar leaves retain direct dispatch. Nodes retain their original TypeRef, and identity
checks reject root/child identity mismatches in Release as well as Debug. Each metadata container binds its exact owner. Accessor interfaces own the index type, factory and
typed slot. Factory and slot signatures are statically typed; selecting the correct semantic slot remains
an accessor convention. The compact container has three optional slots, so adding another index kind
requires extending it. Exact declaration ownership is checked at runtime.
Reading an indexless node's metadata does not execute a factory or throw.
Field-name accessors constrain their owner
types at compile time; enum and union paths share one hybrid lookup policy.
`ObservationValidationPlan` is an internal interpretation of existing declarations, with no additional type IDs, wire format or public contract.
Large string enums and string-discriminator unions lazily prepare ordinal membership/dispatch indexes
on the graph-bound plan node, or in the same standalone declaration table. The first eight entries
retain direct checks; only later matches or misses prepare an index. Named enum names and literal aliases are accepted, and duplicate union
literals retain the first declared case. Other primitive discriminators retain their existing exact
representation rules. Weak-key, successful-only preparation shares no instance values or results.
The JSON reader checks early union cases directly. Only a late match or miss requests graph-owned
dispatch metadata, independently of validation closure preparation. A concurrent declaration registry
shares the same metadata and index with subsequent validation. Named enums skip members without a literal value
when matching nonstring primitives.
See `UnionReaderBenchmarks` for plain typed union collection decoding, and `LiteralValidationBenchmarks` for late-match, invalid-value and early-match measurements.

See `NestedValidationBenchmarks` and `NamedValidationBenchmarks` for representative repeated validation,
and the [performance overview](../../docs/performance/execution-preparation-allocations.md) for qualification.

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
