namespace Cohesive.Adapters.Postgres;

/// <summary>Receipt-table admission policy for async storage-model preparation.</summary>
public enum PostgresReceiptSchemaInitialization
{
    /// <summary>Require an existing compatible table without changing schema.</summary>
    ValidateOnly,
    /// <summary>Create a missing table in an existing schema, then validate; never alter incompatible schema.</summary>
    CreateIfMissing
}
