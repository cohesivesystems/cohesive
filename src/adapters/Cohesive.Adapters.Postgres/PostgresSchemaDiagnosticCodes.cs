namespace Cohesive.Adapters.Postgres;

/// <summary>Stable codes reported by product and attachment schema admission.</summary>
public static class PostgresSchemaDiagnosticCodes
{
    /// <summary>The required native table is absent.</summary>
    public const string TableMissing = "postgres.schema.tableMissing";
    /// <summary>A column has an incompatible type, nullability or collation.</summary>
    public const string ColumnMismatch = "postgres.schema.columnMismatch";
    /// <summary>The table has columns outside the declared contract.</summary>
    public const string UndeclaredColumns = "postgres.schema.undeclaredColumns";
    /// <summary>A required immediate primary or unique key is absent or incompatible.</summary>
    public const string KeyMismatch = "postgres.schema.keyMismatch";
    /// <summary>A generated identity column does not match the declaration.</summary>
    public const string IdentityMismatch = "postgres.schema.identityMismatch";
    /// <summary>A foreign key does not match the declared target or enforcement semantics.</summary>
    public const string ReferenceMismatch = "postgres.schema.referenceMismatch";
    /// <summary>A declared CHECK predicate is absent or incompatible.</summary>
    public const string CheckMismatch = "postgres.schema.checkMismatch";
}
