namespace Cohesive.Adapters.Postgres;

/// <summary>Storage schema admission policy for async storage-model preparation.</summary>
public enum PostgresSchemaInitialization
{
    /// <summary>Require compatible existing schema without changing it.</summary>
    ValidateOnly,
    /// <summary>Create missing adapter-owned schema objects, then validate; never alter incompatible objects.</summary>
    CreateIfMissing
}
