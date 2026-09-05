# Cohesive.Adapters.Sql

Shared SQL target construction for concrete adapters. This package depends only on `Cohesive`; it has no provider
SDK, Storage, or Relations dependency. Relations remains authoritative for query semantics. These expression trees
and statement builders construct target SQL, and do not establish portable arithmetic, collation, null, or transaction
guarantees. Semantic compilers must prove those guarantees against their target capabilities.

`SqlSelectBuilder`, `SqlInsertBuilder`, `SqlUpdateBuilder`, and `SqlDeleteBuilder` share expression construction,
identifier escaping, deterministic parameter ordering, and reusable templates. A mutable builder produces an immutable
query or template. Explicit `SqlDialect` policy controls identifier limits, parameter representability, function names,
and supported grammar; an unsupported facility raises `SqlConstructionException` with a stable code and resolution.
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
policy. The shared allocator is internal to the official adapters while the common boundary is being established.

This extraction covers queries and mutations. DDL, schema migrations, native scalar codecs, connection and transaction
ownership, storage concurrency, and execution remain adapter-owned. Native array and lateral constructs are presently
internal facilities used by the Postgres compiler; SQLite rejects them explicitly. Identifier validation is generic at
construction and target-specific at rendering. `PostgresSqlDialect.Identifier` also supports early name validation.

## Migration

The former `PostgresSql*` construction types move to `Cohesive.Adapters.Sql` as `Sql*`. Lowering and binding now
require an explicit dialect. `CosmosSqlParameterBindingKind` becomes the shared `SqlParameterBindingKind`.
Postgres compiled Relation artifacts advance to v4 with compiler profile v2: recompile older artifacts from canonical
Relations IR. Compiled SQL is executable code; deserialize templates only from trusted artifact sources.
