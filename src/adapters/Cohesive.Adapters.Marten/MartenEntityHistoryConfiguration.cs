using Cohesive.Adapters.Postgres;
using Cohesive.Storage;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Cohesive.Adapters.Marten;

/// <summary>Immutable authority, replay and schema policy; native Marten clients remain host-owned.</summary>
public sealed class MartenEntityHistoryConfiguration : IPostgresEntityHistoryConfiguration
{
    /// <summary>Declares history without accessing native resources.</summary>
    /// <param name="authority">State or events as the authoritative representation.</param>
    /// <param name="reconstitution">Full replay or checkpoint and tail.</param>
    /// <param name="schemaName">Expected document and event schema for the dedicated history store.</param>
    /// <param name="initialization">Validation-only by default; creation never updates existing objects.</param>
    /// <exception cref="ArgumentOutOfRangeException">A policy is unknown.</exception>
    public MartenEntityHistoryConfiguration(EntityStateAuthority authority = EntityStateAuthority.State,
        EntityReconstitutionMode reconstitution = EntityReconstitutionMode.CheckpointAndTail,
        PostgresSchemaInitialization initialization = PostgresSchemaInitialization.ValidateOnly, string schemaName = "public")
    {
        if (!Enum.IsDefined(authority)) throw new ArgumentOutOfRangeException(nameof(authority));
        if (!Enum.IsDefined(reconstitution)) throw new ArgumentOutOfRangeException(nameof(reconstitution));
        if (!Enum.IsDefined(initialization)) throw new ArgumentOutOfRangeException(nameof(initialization));
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);
        SchemaName = schemaName;
        Authority = authority; Reconstitution = reconstitution; Initialization = initialization;
    }
    /// <summary>Expected native event schema, also used when configuring the DI-owned store.</summary>
    public string SchemaName { get; }
    /// <summary>Applies the declared history requirements to caller-owned native options before store construction.</summary>
    /// <param name="options">Native options; connection and client lifetime remain host-owned.</param>
    /// <returns>The same native options.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    public StoreOptions Configure(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.AutoCreateSchemaObjects = JasperFx.AutoCreate.None;
        options.DatabaseSchemaName = SchemaName;
        options.Events.DatabaseSchemaName = SchemaName;
        return options.UseCohesiveEntityHistory();
    }
    /// <summary>Authority retained in history events.</summary>
    public EntityStateAuthority Authority { get; }
    /// <summary>History reconstitution policy.</summary>
    public EntityReconstitutionMode Reconstitution { get; }
    /// <summary>Schema admission policy.</summary>
    public PostgresSchemaInitialization Initialization { get; }
    /// <inheritdoc />
    /// <remarks>Uses the store keyed by the existing database resource identity. Native schema creation covers
    /// all objects configured on that store; use a dedicated history store when isolating schema ownership.
    /// CreateOnly never updates existing objects. Failed preparation is not retried; already-created objects
    /// may remain. Configure native AutoCreate.None to prevent implicit migrations outside preparation.</remarks>
    /// <exception cref="ArgumentException">Native source, schema, stream identity, event registration or automatic schema policy differs.</exception>
    /// <exception cref="InvalidOperationException">The keyed store is missing.</exception>
    /// <exception cref="Weasel.Core.Migrations.DatabaseValidationException">Schema differs from configuration.</exception>
    /// <exception cref="Weasel.Core.SchemaMigrationException">Create-only initialization cannot satisfy the schema.</exception>
    /// <exception cref="OperationCanceledException">Preparation is canceled.</exception>
    /// <exception cref="NpgsqlException">Native schema access fails.</exception>
    public async ValueTask<PostgresEntityHistoryStorage> PrepareAsync(IServiceProvider services, string database,
        NpgsqlDataSource dataSource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        cancellationToken.ThrowIfCancellationRequested();
        var store = services.GetRequiredKeyedService<IDocumentStore>(database);
        if (store.Options.Events.DatabaseSchemaName != SchemaName || store.Options.DatabaseSchemaName != SchemaName)
            throw new ArgumentException("Marten schemas must match the declared history schema.", nameof(store));
        if (store.Options is not StoreOptions nativeOptions || nativeOptions.AutoCreateSchemaObjects != JasperFx.AutoCreate.None)
            throw new ArgumentException("Marten automatic schema changes must be disabled; the storage model owns initialization.", nameof(store));
        return await MartenEntityHistoryStorage.PrepareAsync(dataSource, store, Authority, Reconstitution,
            Initialization, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
