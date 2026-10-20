using Cohesive.Adapters.Marten;
using Cohesive.Adapters.Postgres;
using Cohesive.Host.Composition;
using Cohesive.Storage;
using Microsoft.Extensions.Configuration;

namespace AspireFirst.Orders;

/// <summary>Validated environment selection shared by AppHost and worker model activation.</summary>
[ExplicitModelRegistration]
public sealed class FulfillmentHistoryPolicy
{
    /// <summary>Selects authority and reconstruction without opening native resources.</summary>
    /// <param name="authority">Authoritative entity representation.</param>
    /// <param name="reconstitution">History acquisition strategy.</param>
    public FulfillmentHistoryPolicy(EntityStateAuthority authority = EntityStateAuthority.State,
        EntityReconstitutionMode reconstitution = EntityReconstitutionMode.CheckpointAndTail)
    {
        History = new(authority, reconstitution, PostgresSchemaInitialization.CreateIfMissing,
            schemaName: "cohesive_events");
    }
    /// <summary>History expectations and initialization policy owned by the storage model.</summary>
    public MartenEntityHistoryConfiguration History { get; }
    /// <summary>Selected state authority.</summary>
    public EntityStateAuthority Authority => History.Authority;
    /// <summary>Selected replay strategy.</summary>
    public EntityReconstitutionMode Reconstitution => History.Reconstitution;
    /// <summary>Separate physical databases prevent an authority switch from reinterpreting existing data.</summary>
    public string DatabaseName => Authority == EntityStateAuthority.Events ? "orders_events" : FulfillmentStoragePolicy.OrdersDatabaseName;
    /// <summary>Reads the same validated configuration in either executable.</summary>
    /// <param name="configuration">Executable configuration; absent values use the documented demo defaults.</param>
    /// <returns>Immutable validated selection, including native history expectations.</returns>
    /// <exception cref="ArgumentException">A configured selection is unknown.</exception>
    /// <exception cref="ArgumentNullException">Configuration is null.</exception>
    public static FulfillmentHistoryPolicy FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new(Read(configuration, "EntityState:Authority", EntityStateAuthority.State),
            Read(configuration, "EntityState:Reconstitution", EntityReconstitutionMode.CheckpointAndTail));
    }
    static T Read<T>(IConfiguration configuration, string key, T fallback) where T : struct, Enum
    {
        var text = configuration[key];
        if (text is null) return fallback;
        if (Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)) return value;
        throw new ArgumentException($"{key} must be one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }
}
