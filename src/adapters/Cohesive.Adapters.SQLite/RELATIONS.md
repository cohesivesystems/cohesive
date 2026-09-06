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

| Semantic operation | SQLite realization and constraints |
| --- | --- |
| Source | Complete table enumeration in one connection's main database. All source instances share one execution domain. Supplied roots, partition selectors and relationship acquisition are rejected. |
| Fields and projection | Top-level scalar paths. Values use `SqliteScalarCodec`, including value-only decimal, temporal and binary payloads. Nested objects and collections are rejected. |
| Equality and partitioning | Int32/Int64, Boolean and String domains. Text uses explicit `BINARY` collation for exact equality, including when the column declares another collation. Missing and explicit null stay distinct. |
| Predicates | Required non-null booleans; `Not`, `And`, `Or`, exact equality/inequality, and ordered comparisons of required non-null integers. Guarded comparisons use shared canonical `ExprGuardRefinement`; unproven nullish operands are rejected. |
| Parameters | Required non-null codec-encoded scalar values without defaults; only demanded parameters are bound. |
| Join | Inner and left joins over distinct source bindings. Optional bindings retain a presence marker separate from nullable field payloads. |
| Representative selection | `ROW_NUMBER` in a derived query, followed by rank = 1. Partition expressions retain missing/null distinction; empty/global partitions follow the canonical operation. |
| Ordering | Int32/Int64, or String fields with explicit ASCII-domain evidence, with explicit direction and null placement. The order must cover a proven unique key; representative partition keys also contribute to its uniqueness proof. |
| Results | Named query rows, selected output fields and winning source occurrences; decoding uses fixed ordinals. |

Each placed source requires a stable, non-null unique integer/text identity. A scalar placement identity names its
semantic field and physical column. `IdentityFields` can instead declare an ordered composite key through exact
canonical field-input IDs; a composite placement uses a source-native identity selector with no single semantic
path. Every identity component must be selected by the plan. Scalar text identities must be nonblank; composite
text components may be empty. No hidden rowid surrogate or schema alteration is inferred.

The compiler derives a bounded uniqueness proof from these keys. A join always retains the combined key; it retains
the left key alone when direct conjunctive equality covers a right unique key. Inner joins also propagate field
equalities and can retain the right key. Cross-side left-join equalities and disjunctive predicates do not establish
those facts. Representative selection establishes uniqueness of its direct field partition keys. Missing and null
are distinct partitions but share an ordering bucket, so an optional nullable partition cannot alone prove a final
order is unique. A plan without proof is rejected even when today's data has no ties. Final `OrderQueryNode` still
requires a unique order: the reference interpreter preserves encounter order for ties, which this compiler cannot
reconstruct. This is a bounded key proof, not a general functional-dependency or data-dependent tie detector.

Required presence is a compile-time fact. Only optional fields carry physical presence bits through intermediate
queries. Repeated value, identity and binding-presence expressions share one internal projection column. Left joins
still test the contributing binding's presence before reconstructing optional fields.

`AsciiOrderingFields` references the exact text inputs whose schema/ingestion authority guarantees U+0000–U+007F.
In that domain SQLite `BINARY` and canonical UTF-16 ordinal order agree. Unrestricted Unicode ordering, ordered text
parameters/literals, decimal numeric ordering and temporal TEXT instant comparisons remain unavailable. Equality
continues to support the full exact text domain independently of ordering evidence.

Shared expression guard analysis recognizes equality/inequality, negation, true conjunctions and false disjunctions.
For example, equality of a joined identity with a required field proves that binding is present. Its required fields
can then be compared; originally optional fields still need their own guards. `field != null` does not prove that a
missing field exists. Native lowering reuses these rules for short-circuit operands and surviving filter rows. It
never treats a raw SQL NULL comparison as a canonical false result without proof. Rooted relations, relationship
traversal, aggregates, distinct, paging and additional expression operations remain outside this profile. Explicit
realization overrides have no execution handler and are rejected.

## Storage evidence

`SqliteRelationQueryTableBinding.Authority` names the versioned schema/ingestion contract establishing:

- Complete, authoritative tables with the declared unique integer/text identities and key component order.
- Declared ASCII ordering domains, when used; constraints or controlled ingestion must cover every stored value.
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
column names for scalar identities. Composite selectors name source-native keys; `IdentityFields` points back to the same field mappings. Source contracts come from the compiled plan.

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

The source occurrence ID format is `sqlite/{escaped-binding}/{escaped-identity}`. Observation identity is invariant
decimal for integers, exact text for scalar strings, or `tuple/v1:` followed by a JSON array of type-tagged components
for composite keys. `SqliteRelationQueryOccurrenceColumn.EncodeIdentity` provides the same encoding for source evidence.
Component order and type are significant; delimiters inside a value cannot collide with tuple boundaries. An absent outer binding contributes no occurrence and an
absent output binding materializes as undefined, distinct from a present object with missing fields.

## Inspecting generated SQL

Compiler profile `cohesive.adapters.sqlite.sql/compiler-v3` retains shared `SqlFormatting.Indented` rendering.
SQL stage aliases derive from canonical node IDs, source aliases from bindings, and column aliases from binding/field
paths. Names are normalized and bounded to 63 UTF-8 bytes for readability; this is a compiler convention, not an
engine limit. Deterministic suffixes disambiguate collisions, including case-only differences. One allocator per
projection namespace keeps field names recognizable through wrappers, while equal physical expressions still share
one column. Ordinal decoding is independent of these display names.

The [generated representative-selection example](examples/representative-selection.sql) is checked against the
compiler by a test. Read it from the inner source outward:

| SQL stage or column | Canonical meaning |
| --- | --- |
| `candidate`, `candidates` | Candidate binding and source node. |
| `representative_ranked`, `representative_rank` | Derived ranking stage for the `representative` node; the unique declared ordering determines the winner. |
| `representative` | Retains rank one before the next filter runs. |
| `eligible_winners` | The `eligible-winners` node filters the chosen winner; it cannot select an older candidate. |
| `result_order` | The `result-order` node establishes final identity order. |
| `candidate_Key_present` | Distinguishes a missing key from an explicit null key. |
| `candidate_binding_present`, `candidate_identity` | Reconstruct binding presence and winning contributor identity. |

The example's `$1`–`$4` slots are captured INTEGER values of one: three required-field presence values and the rank
predicate. `Statement.Parameters` identifies every constant/runtime slot; `Parameters` supplies canonical runtime
parameter contracts. `ResultFields` and `OccurrenceColumns` identify decoded fields and contributors, and
`Provenance` pins their source IR and compilation decisions. Values remain bound separately from SQL text.

The formatted statement is the statement executed. Its fingerprint includes the exact text. Compiler-v3 uses the
canonical execution slice's field requirements to stop carrying values after their last use. Identity, binding
presence and ordering slots remain independently retained. This reduces intermediate row width and SQL size without
moving eligibility filters across representative selection. CTE conversion and query-layer elimination remain
separate lowering changes.

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

## Compiler-v3 adoption and compatibility

Recompile retained IR and reconstruct storage evidence when moving to compiler-v3. Storage binding schema v2 adds
key components and ASCII domain evidence; artifact schema v2 carries typed identity component ordinals. Old versions
are rejected rather than reinterpreted. SQL text, ordinal metadata and fingerprints may change. The SHA-256 digest
size remains fixed; wrappers increase the serialized SQL/artifact size and the bytes hashed. Schema DDL is unchanged.

The shared canonical analyzer now accepts proven guards; this does not change evaluation or authorize unguarded
nullish comparisons. Generic conformance fixtures cover composite/string keys, key-preserving and multiplying joins,
missing/null winners, guarded eligibility, final partition ordering, evidence round trips and unsupported domains.
