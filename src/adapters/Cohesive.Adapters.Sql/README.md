# Cohesive.Adapters.Sql

Shared SQL target construction for concrete adapters. This package depends only on `Cohesive`; it has no provider
SDK, Storage, or Relations dependency. Relations remains authoritative for query semantics. These expression trees
and statement builders construct target SQL, and do not establish portable arithmetic, collation, null, or transaction
guarantees. Semantic compilers must prove those guarantees against their target capabilities.

`SqlSelectBuilder`, `SqlInsertBuilder`, `SqlUpdateBuilder`, and `SqlDeleteBuilder` share expression construction,
identifier escaping, deterministic parameter ordering, and reusable templates. A mutable builder produces an immutable
query or template. Explicit `SqlDialect` policy controls identifier limits, parameter representability, function names,
and supported grammar; an unsupported facility raises `SqlConstructionException` with a stable code and resolution.
Null-safe distinct comparisons and right/full outer joins also require explicit `SqlFeature` support.
Dialect implementations are trusted, immutable compiler policy. They remain in concrete adapter packages.

```csharp
using Cohesive.Adapters.Sql;
using Cohesive.Adapters.SQLite;

var query = new SqlSelectBuilder(new SqlQualifiedTable("quotes"), "q")
    .Select(SqlExpression.Column("q", "price"), "price")
    .Where(SqlExpression.Binary(SqlBinaryOperator.Equal,
        SqlExpression.Column("q", "symbol"), SqlExpression.RuntimeParameter("symbol")))
    .BuildTemplate(SqliteSqlDialect.Instance);
var statement = query.Bind(SqliteSqlDialect.Instance,
    new Dictionary<string, object?> { ["symbol"] = "ABC" });
```

Postgres and SQLite use numbered `$1` markers (positional in Npgsql, named in Microsoft.Data.Sqlite). A template
records its dialect identity and requires matching explicit policy when bound, including after JSON rehydration.
Captured values are immutable tagged CLR values; adapter policy enforces target precision and representation.
SQLite callers encode semantic scalars through `SqliteScalarCodec` before binding INTEGER, TEXT, and BLOB values.

Cosmos retains its document expression tree, query text, JSON value normalization, and SDK boundary. It shares the
first-use parameter-slot allocator and constant/runtime binding kind. Its `@p0` markers and JSON paths remain Cosmos
policy. `SqlParameterSlots<TSlot>` is public: it snapshots adapter-owned payloads and allocates zero-based positions
without imposing a particular marker format. Slot factories run synchronously, cannot reenter the allocator, and
leave its state unchanged if they throw. Payloads must be immutable for snapshots to be deeply immutable.

This extraction covers queries and mutations. DDL, schema migrations, native scalar codecs, connection and transaction
ownership, storage concurrency, and execution remain adapter-owned. `SqlExpression.EqualAny`,
`SqlSelectBuilder.FromArray`, and `CrossJoinLateral` are public, capability-checked construction operations;
SQLite rejects them explicitly. Native array encoding and binding remain provider-owned: generic template `Bind`
accepts the shared scalar/byte value domain, not native arrays. Identifier validation is generic at construction and
target-specific at rendering. `SqlIdentifier.ToSql` and `SqlQualifiedTable.ToSql` render standalone names safely;
`PostgresSqlDialect.Identifier` also supports early name validation. `SqlUtf8` provides the same strict Unicode and
text-domain validation to adapter encoding paths.

## Implementing an adapter

Official adapters use only this package's public API. `InternalsVisibleTo` is reserved for tests; adding an adapter
does not require registration or an assembly-name change in this package. Implement immutable `SqlDialect` policy
and use the public builders, or reuse the parameter allocator with an adapter-owned renderer as Cosmos does.
The shared renderer currently uses double-quoted identifiers and numbered `$1` markers. A new target must support
that construction profile or provide its own renderer; implementing `SqlDialect` does not imply arbitrary statement
grammar, placeholder, or scalar encoding support.

For expression syntax beyond the built-in function/operator catalogs, an adapter publishes a stable intrinsic identity
and implements `SqlDialect.WriteIntrinsic`. Author through `SqlExpression.Intrinsic(identity, operands)`. The expression
retains only the identity and immutable operands; it never retains a callback or dialect instance. The identity is a
lookup key, not SQL text. Dialects must reject unknown identities and validate arity and target constraints before
emitting syntax. The default implementation raises a structured `SqlConstructionException`.

```csharp
public const string JsonValueIntrinsic = "example.json-value/v1";

public override void WriteIntrinsic(
    string intrinsic, ImmutableArray<SqlExpression> arguments, SqlExpressionWriter writer)
{
    if (intrinsic != JsonValueIntrinsic)
    {
        base.WriteIntrinsic(intrinsic, arguments, writer);
        return;
    }
    if (arguments.Length != 2)
        throw new ArgumentException("JSON_VALUE requires a document and a path.", nameof(arguments));
    writer.WriteSyntax("JSON_VALUE(");
    writer.WriteExpression(arguments[0]);
    writer.WriteSyntax(", ");
    writer.WriteExpression(arguments[1]);
    writer.WriteSyntax(")");
}
```

This example is a method on an adapter's `SqlDialect` subclass, using `System.Collections.Immutable` and
`Cohesive.Adapters.Sql`. Callers supply both operands as expressions, including constant or runtime parameter values.
The stack-only `SqlExpressionWriter` shares the containing statement's parameter allocation, including across nested
intrinsics and subqueries. Its syntax method accepts **trusted compiler-owned grammar only**: never concatenate values,
identifiers, intrinsic identities, or parameter markers into it. Use `WriteExpression` for operands and `WriteIdentifier`
for names. Each intrinsic must render a complete expression with parentheses where required by precedence.

Postgres uses this public extension for `PostgresSqlDialect.ClockTimestampIntrinsic`, without a special case in the
shared function catalog. Intrinsics extend expression syntax; they are not arbitrary statement or table-source
extensions. Those require a deliberate shared grammar change or an adapter-owned renderer. Compiled templates retain
SQL, slots, and dialect identity, not executable rendering policy. Version intrinsic identities and dialect profiles
when their contracts change. The SQL trees are target-construction artifacts, not a replacement for canonical Relations
IR or a portable guarantee of the intrinsic's semantics.

The public-contract tests compile and run a dialect under an unrelated assembly name, exercising nested intrinsics,
identifier quoting, parameter reuse, native array/lateral construction, and template serialization and binding.

## Migration

`SqlExpression.ScalarSubquery(query)` embeds one value (SQL NULL for no row) and shares the outer parameter allocator.
The query must project exactly one column and use `Limit(1)`; unbounded queries are rejected so dialect-specific
multirow behavior cannot silently select different semantics. Callers needing a deterministic representative must
also declare an ordering or establish uniqueness. Rendering requires `SqlFeature.ScalarSubquery` support. This is
a SQL construction facility, not a canonical Relations representative-selection operator.

The former `PostgresSql*` construction types move to `Cohesive.Adapters.Sql` as `Sql*`. Lowering and binding now
require an explicit dialect. `CosmosSqlParameterBindingKind` becomes the shared `SqlParameterBindingKind`.
Postgres compiled Relation artifacts advance to v4 with compiler profile v2: recompile older artifacts from canonical
Relations IR. Compiled SQL is executable code; deserialize templates only from trusted artifact sources.

`SqlFunction.ClockTimestamp` is replaced by
`SqlExpression.Intrinsic(PostgresSqlDialect.ClockTimestampIntrinsic)`. Its emitted PostgreSQL SQL is unchanged.

See the [SQLite adoption release notes](../../../docs/release-notes/sqlite-adoption.md) for the intentional public API breaks.
