# Canonical Relations queries on SQLite

`SqliteRelationQueryCompiler` lowers a bounded canonical row-query slice to one parameterized SQLite statement
per requested result. The canonical Relations IR owns meaning; source placement owns field and identity selectors;
`SqliteRelationQueryStorageBinding` supplies physical table and schema evidence. Shared `Cohesive.Adapters.Sql`
builders own identifiers, parameters, derived queries, joins and window syntax. No application SQL or schema is
embedded in the compiler.

## Supported boundary

The versioned `SqliteRelationQueryTargetProfile.Default` advertises the supported operation families. Profile
feasibility is followed by inspection of the exact branch and storage binding; it is not sufficient to authorize
execution by itself.

| Semantic operation | SQLite v1 realization and constraints |
| --- | --- |
| Source | Complete table enumeration in one connection's main database. All source instances share one execution domain. Supplied roots, partition selectors and relationship acquisition are rejected. |
| Fields and projection | Top-level scalar paths. Values use `SqliteScalarCodec`, including value-only decimal, temporal and binary payloads. Nested objects and collections are rejected. |
| Equality and partitioning | Int32/Int64, Boolean and String domains. Text uses explicit `BINARY` collation for exact equality, including when the column declares another collation. Missing and explicit null stay distinct. |
| Predicates | Required non-null booleans; `Not`, `And`, `Or`, exact equality/inequality, and ordered comparisons of required non-null integers. Nullable ordered comparisons need further refinement proof and are rejected. |
| Parameters | Required non-null codec-encoded scalar values without defaults; only demanded parameters are bound. |
| Join | Inner and left joins over distinct source bindings. Optional bindings retain a presence marker separate from nullable field payloads. |
| Representative selection | `ROW_NUMBER` in a derived query, followed by rank = 1. Partition expressions retain missing/null distinction; empty/global partitions follow the canonical operation. |
| Ordering | Int32/Int64 keys with explicit direction and null placement. The tuple must contain the identity of **every contributing source**, including nullable identities after left joins. |
| Results | Named query rows, selected output fields and winning source occurrences; decoding uses fixed ordinals. |

Each placed source requires a stable, non-null, unique INTEGER identity with a semantic field path. Its demanded
field mapping must use the same physical column. Requiring all source identities is deliberately conservative:
one identity can repeat after a join. Including all identities proves a unique order tuple for every possible
bound input and avoids SQLite arbitrarily resolving a canonical top tie. A plan without that proof is rejected
even if today's data happens to have no ties, or a later filter would remove the tied group. This first slice does
not implement data-dependent tie detection or infer functional dependencies from join predicates.

Required presence is a compile-time fact. Only optional fields carry physical presence bits through intermediate
queries. Repeated value, identity and binding-presence expressions share one internal projection column. Left joins
still test the contributing binding's presence before reconstructing optional fields.

Text ordering is intentionally unavailable: SQLite `BINARY` and .NET ordinal ordering need not agree for all Unicode
strings. Decimal TEXT does not establish numeric comparisons; offset-preserving timestamp TEXT does not establish
instant comparisons. String/composite identities, proven text ordering, nullable-operand refinement, rooted
relations, relationship traversal, aggregates, distinct, paging and additional expression operations remain outside
this profile. Explicit realization overrides have no execution handler in v1 and are rejected. These are explicit
capability or binding diagnostics, never implicit fallback SQL.

## Storage evidence

`SqliteRelationQueryTableBinding.Authority` names the versioned schema/ingestion contract establishing:

- Complete, authoritative tables with the declared unique INTEGER identities.
- Every field's exact codec encoding and canonical value domain; SQLite storage affinity alone is insufficient.
- Optional-field presence stored as INTEGER NOT NULL 0/1. A missing field has presence 0 **and a SQL NULL payload**;
  an explicit null has presence 1 and a SQL NULL payload.
- Stable data access in the declared database domain. A statement reads one SQLite snapshot; use one read transaction
  when multiple artifacts must share a snapshot.

The compiler consumes this declared evidence; it does not introspect or scan a live database to prove it. Schema
owners should establish it through constraints and controlled ingestion. Incorrect evidence invalidates the native
guarantees; result decoding catches invalid returned encodings but cannot inspect rows filtered out inside SQLite.
Source placement's acquisition limits are retained in the fingerprint. Native statements do not truncate source
sets to those limits; streaming result consumption and database execution resource policy remain caller-owned.

The binding constructor normalizes declarations, fingerprints them together with the exact placement, and checks
any persisted version/fingerprint. It can be serialized and reopened with `System.Text.Json`. It introduces no
second field-mapping catalog: `RelationQuerySourceFieldBinding.SourceSelector` and the identity selector are literal
column names, and source contracts come from the compiled plan.

## Compile once, execute repeatedly

Given a statically compiled `plan`, its exact `placement`, the candidate `placementBindingId`, and the optional
key's `keyInputId` from that placement:

```csharp
var feasibility = RelationQueryRealizationCompiler.Compile(
    plan,
    SqliteRelationQueryTargetProfile.Default,
    SqliteRelationQueryTargetProfile.Policy,
    RelationQueryResultObservability.ExactContributors);

var storage = new SqliteRelationQueryStorageBinding(placement,
[
    new(placementBindingId, "candidates", "application/candidate-schema-v1",
        [new(keyInputId, "key_present")])
]);
var compiler = new SqliteRelationQueryCompiler();
var compilation = compiler.Compile(new(plan, feasibility, placement), storage);
if (!compilation.IsSuccessful)
    throw new InvalidOperationException(string.Join("\n", compilation.Diagnostics));
var artifact = compilation.Artifacts.Single();

using var connection = database.OpenConnection(cancellationToken);
using var transaction = connection.BeginTransaction(deferred: true);
using var scope = new SqliteCommandScope(database, connection, transaction);
using var reader = scope.ExecuteReader(
    artifact.Command, cancellationToken, artifact.BindParameters(parameterValues));
while (reader.Read())
{
    var row = artifact.ReadCurrentRow(reader);
    // row.Value contains selected fields; row.Occurrences contains only winning contributors.
}
```

`parameterValues` is an `IReadOnlyDictionary<QueryParameterId, ObservationValue>` containing exactly the artifact's
declared parameters. Use an empty dictionary for an unparameterized query. The immutable artifact is shareable;
each operation owns its connection, transaction, scope and reader. An operation can reuse the same prepared command
with new parameter values after disposing its previous reader. `ReadCurrentRow` allocates owned canonical results
and never looks up a provider column by name.

Native artifact dispatch is explicit. Registering this capability profile does not install a source reader or cause
`RelationQueryEvaluator` to execute a native statement automatically.

`Realize(request, storage)` exposes contextual proof without publishing an executable artifact. `Compile` prepares
each branch once, reuses that work when attaching proof, and returns no artifacts if any selected branch fails.
Both paths use the canonical contextual-assessment projector and bound-realization compiler. Native provenance is
created by `RelationQueryNativeCompilationProvenanceFactory`, retaining exact plan, placement, binding, requirement
decisions and compiler/convention versions. `artifact.ToJson()` exports an inspectable derived artifact. Recompile
retained canonical IR and verified storage evidence after restart; importing executable SQL artifacts is not a v1 API.

The source occurrence ID format is `sqlite/{escaped-binding}/{integer-identity}`. Observation identity remains the
invariant decimal representation of the source identity. An absent outer binding contributes no occurrence and an
absent output binding materializes as undefined, distinct from a present object with missing fields.

## Verification and performance

`SqliteRelationQueryCompilerTests` runs real SQLite differential checks against the reference interpreter for
representatives, post-selection filtering, null placement, missing partitions, inner/left joins, projection,
selected-field demand, winning provenance and parameter rebinding. It also checks rejected ordering/presence
evidence, exact binding affinity, persistence fingerprints and use of a compound index in `EXPLAIN QUERY PLAN`.

`SqliteRepresentativeSelectionBenchmarks` compares compiled SQL with an independently prepared SQL statement using
the **same ordinal reader and canonical result layout**. It also measures full reference execution over prebuilt
evidence. Compilation, database creation and connection acquisition are outside these warmed execution measurements.
See [measured results](../../Cohesive.Relations.Benchmarks/RESULTS.md#sqlite-native-representative-selection).

This is the first native compiler slice. Application query migration still needs compatible identity/comparison
evidence and application-owned schema/ingestion guarantees. It does not replace a store's existing queries by itself.
