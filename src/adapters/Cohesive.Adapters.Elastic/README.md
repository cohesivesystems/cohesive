# Cohesive.Adapters.Elastic

`Cohesive.Adapters.Elastic` interprets Cohesive Relations as Elasticsearch queries and aggregations and realizes the
Storage materialization lifecycle with generation-isolated indexes.

## Install

```bash
dotnet add package Cohesive.Adapters.Elastic
```

## Bind a placed source

When semantic paths match Elasticsearch `_source` paths, conventions provide the ordinary binding surface:

```csharp
var binding = ElasticRelationQueryBinding.For(placedLoads)
    .Index("loads-read")
    .Identity(load => load.Id)
    .FieldsBySemanticPath()
    .Build()
    .RequireValue();
```

Use explicit field mappings when retrieval paths, keyword fields, nested scope, encodings, ordering evidence, or
special query facilities differ from the semantic paths. The successful binding remains affine to the exact compiled
plan and placement.

## Implemented interpretations

- Native Elasticsearch request compilation for the declared Relation/query capability profile.
- Filters, projections, supported aggregation, ordering, paging, and mapped nested correlation.
- Exact physical field bindings with retrieval and query-channel evidence.
- Durable materialization targets using one isolated index per generation.
- Atomic publication through a stable read alias and hidden ownership fencing.
- Materialization lifecycle telemetry and sanitized transport diagnostics.

## Important boundaries

The adapter supports only semantics justified by the index mapping and target profile. Scalar arrays may participate
in supported membership queries but are not generally projectable result fields. Structured collection correlation
requires an attested Elasticsearch `nested` mapping. Temporal filtering, unrestricted Unicode ordinal ordering, and
mutable multi-request pagination require stronger evidence than Elasticsearch supplies by default.

Offset requests describe one bounded view. `search_after` and composite continuation sequences require a declared
stable search view; point-in-time lifecycle support remains outside the current profile.

## Continue

- [Internals](INTERNALS.md) contains generation materialization, the complete query example, exact mappings,
  aggregation, pagination, and compiler boundaries.
- [Relations getting started](../../Cohesive.Relations/docs/GETTING_STARTED.md) introduces the semantic query first.
- [Relations capability reference](../../Cohesive.Relations/docs/CAPABILITIES.md) records the generated profile.
- [`Cohesive.Storage`](../../Cohesive.Storage/README.md) owns the materialization lifecycle semantics.

## Prepared materialized query reads

`ElasticMaterializationReadSelection.PrepareAsync` admits the exact native rebuild
plan and rooted keyed-query projection and produces bounded typed readers.
`ElasticMaterializationReadBinding` exposes that interpretation to declarative
`SelectMaterializedQuery` composition, which owns comparison-file loading and source/
target admission ordering. Declare the binding with the existing target ID and a
native metadata-admission function. It borrows the host's HttpClient keyed by that
ID and checks the returned target and materialization identities. Client allocation,
template policy and configuration section names remain application concerns; the
shared preparation owns evidence validation, decoding and no-fallback semantics.

For direct item access, `PrepareValueReaderAsync` inspects target publication state at
startup without reconciling or writing it and returns a pinned-generation reader. Each warm read makes exactly three
transport calls: generation control read, exact ownership-alias inspection, and
bounded item multi-get. Retention, provisioning, permanent failure, definition and
ownership checks remain fresh. No target-wide publication reconciliation, index
creation, refresh or alias promotion runs on this path. Promotion of another
generation does not affect the pinned reader; loss of its generation or ownership
fails closed. The existing `ReadValueAsync` remains a standalone admission-plus-read
convenience operation. Operator promotion APIs retain full target reconciliation.

Tests pin the three-call boundary and reject eligibility/ownership changes after
preparation. The optimization deliberately keeps native checks rather than caching
retention or adding a freshness window. It does not promise read-your-writes or
continuous comparison freshness.
