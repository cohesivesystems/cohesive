using System.Runtime.ExceptionServices;
using Npgsql;
using Cohesive.Prelude;
using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Offline PostgreSQL entity mappings used by native query preparation and runtime registration.</summary>
/// <remarks>Configure once at host composition. Define is the sole authoring entry point and freezes mappings before returning. Runtime registrations
/// share immutable mappings. Completion freezes authoring and rejects late contributions. Preparation performs no IO and retains no credentials or data source.</remarks>
public sealed class PostgresPersistenceBindings
{
    readonly AuthoringState<PostgresPersistenceBindings> state;
    ExceptionDispatchInfo? preparationFailure;
    internal readonly Dictionary<QualifiedShapeId, (EntityDefinition Entity, PostgresEntityRepositoryMapping Mapping)> Tables = [];
    /// <summary>Creates offline bindings for one explicitly identified database.</summary>
    /// <param name="database">Stable database identity shared with the eventual runtime binding.</param>
    /// <exception cref="ArgumentException">Database identity is empty.</exception>
    PostgresPersistenceBindings(PostgresRelationQueryDatabaseId database)
    {
        if (string.IsNullOrWhiteSpace(database.Value)) throw new ArgumentException("Database identity is required.", nameof(database));
        Database = database;
        state = new(() => { preparationFailure?.Throw(); return this; });
    }
    /// <summary>Database identity covered by these mappings.</summary>
    public PostgresRelationQueryDatabaseId Database { get; }

    /// <summary>Validates and attaches the exact canonical entity and native mapping.</summary>
    /// <typeparam name="T">Canonical entity state.</typeparam>
    /// <param name="entity">Canonical entity authority.</param>
    /// <param name="mapping">Native table and field bindings.</param>
    /// <returns>This setup builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">Mapping is invalid or the entity is already attached.</exception>
    /// <exception cref="InvalidOperationException">The bindings have been frozen by Define.</exception>
    public PostgresPersistenceBindings Entity<T>(DomainEntity<T> entity, PostgresEntityRepositoryMapping mapping) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mapping);
        PostgresEntityRepository.ValidateMapping(entity.Definition, mapping);
        return state.Edit(() =>
        {
            Tables.Add(entity.Definition.StateShape.QualifiedId, (entity.Definition, mapping));
            return this;
        });
    }

    /// <summary>Authors and freezes offline mappings through the existing native mapping builder.</summary>
    /// <param name="database">Stable connection/database identity.</param>
    /// <param name="configure">Setup-only contribution; never retained or run per request.</param>
    /// <returns>Frozen mappings usable for offline evidence and runtime binding.</returns>
    /// <exception cref="ArgumentNullException">Configure is null.</exception>
    /// <exception cref="ArgumentException">A database or mapping is invalid or duplicated.</exception>
    public static PostgresPersistenceBindings Define(PostgresRelationQueryDatabaseId database,
        Action<PostgresPersistenceBindings> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var bindings = new PostgresPersistenceBindings(database);
        try { configure(bindings); }
        catch (Exception error) { bindings.preparationFailure = ExceptionDispatchInfo.Capture(error); }
        _ = bindings.state.Value;
        return bindings;
    }

    /// <summary>Contributes a native mapping directly, without a parallel mapping property catalog.</summary>
    /// <typeparam name="T">Canonical entity state.</typeparam>
    /// <param name="entity">Canonical entity authority.</param>
    /// <param name="configure">Native PostgreSQL mapping configuration.</param>
    /// <returns>This setup builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A mapping is invalid or duplicated.</exception>
    /// <exception cref="InvalidOperationException">The bindings are frozen or required mapping fields are missing.</exception>
    public PostgresPersistenceBindings Entity<T>(DomainEntity<T> entity,
        Action<PostgresEntityRepositoryMappingBuilder<T>> configure) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(configure);
        return state.Edit(() =>
        {
            var builder = PostgresEntityRepositoryMapping.For(entity);
            configure(builder);
            return Entity(entity, builder.Build());
        });
    }

    /// <summary>Returns a declared mapping for inspection or explicit native composition.</summary>
    /// <typeparam name="T">Canonical state type.</typeparam>
    /// <param name="entity">Exact entity authority used during registration.</param>
    /// <returns>The immutable native mapping.</returns>
    /// <exception cref="ArgumentNullException">Entity is null.</exception>
    /// <exception cref="ArgumentException">The entity is missing or belongs to another declaration instance.</exception>
    public PostgresEntityRepositoryMapping Mapping<T>(DomainEntity<T> entity) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!Tables.TryGetValue(entity.Definition.StateShape.QualifiedId, out var entry)
            || !ReferenceEquals(entry.Entity, entity.Definition))
            throw new ArgumentException("The exact entity is not registered in these database bindings.", nameof(entity));
        return entry.Mapping;
    }

    /// <summary>Attaches a caller-owned data source using this declaration's database identity.</summary>
    /// <param name="dataSource">Single-host native data source; ownership remains with the caller.</param>
    /// <param name="authority">Stable non-secret identity attesting the data source implements the declared database.</param>
    /// <returns>A runtime registration sharing these frozen mappings.</returns>
    /// <remarks>Performs no IO and does not infer or verify the server's database name. The caller attests
    /// the association; the existing explicit runtime-binding overload supports additional replication configuration.</remarks>
    /// <exception cref="ArgumentNullException">Data source or authority is null.</exception>
    /// <exception cref="ArgumentException">The data source is multi-host or the authority is invalid.</exception>
    public PostgresPersistenceRegistration Bind(NpgsqlDataSource dataSource, string authority) =>
        Bind(new PostgresNpgsqlRuntimeBinding(Database, dataSource, authority));

    /// <summary>Attaches a caller-owned data source to these frozen mappings.</summary>
    /// <param name="runtime">Exact runtime binding for the declared database.</param>
    /// <returns>Runtime registration sharing immutable mappings; data-source ownership remains with the caller.</returns>
    /// <exception cref="ArgumentNullException">Runtime is null.</exception>
    /// <exception cref="ArgumentException">Database identities differ.</exception>
    public PostgresPersistenceRegistration Bind(PostgresNpgsqlRuntimeBinding runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Database != Database) throw new ArgumentException("Offline and runtime database identities must match.", nameof(runtime));
        _ = state.Value;
        return new(runtime, this);
    }

    /// <summary>Compiles one native query through the existing semantic, placement and PostgreSQL validators.</summary>
    /// <param name="request">Canonical query and exact semantic snapshots.</param>
    /// <returns>Fingerprint-validated native artifact, without opening a database connection.</returns>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native compilation fails.</exception>
    /// <exception cref="InvalidOperationException">A required entity mapping is absent.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Placement or binding is invalid.</exception>
    public PostgresRelationQueryCompiledArtifact Prepare(RelationQueryCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = state.Value;
        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful) throw new RelationQueryPreparationException("query preparation", compilation);
        var plan = compilation.Plan!;
        var builder = RelationQueryPlacement.For(plan);
        var source = builder.Source("postgres/query", PostgresRelationQuerySourceTargetProfile.Default, new(Database.Value));
        foreach (var input in plan.InputContract.Sources)
            Place(builder.Place(input, source), input.Shape);
        foreach (var input in plan.InputContract.Traversals)
            Place(builder.Place(input, source), input.ResultShape);
        var placement = builder.Build().RequireValue();
        var binding = PostgresRelationQueryBinding.For(placement).Database(Database);
        foreach (var input in placement.Inputs) binding.Table(input, Mapping(input.Shape));
        var storage = binding.Build().RequireValue();
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy, RelationQueryResultObservability.NotRequested);
        var compiler = new PostgresRelationQueryCompiler();
        var bound = compiler.Realize(new(plan, feasibility, placement.Placement), storage);
        var native = compiler.Compile(new RelationQueryNativeCompilationRequest(plan, bound, placement.Placement), storage);
        if (!native.IsSuccessful) throw new PostgresQueryPreparationException(compilation, native);
        var artifact = native.Artifacts.Single();
        return artifact;
    }

    internal PostgresEntityRepositoryMapping Mapping(QualifiedShapeId shape, Func<Exception>? missing = null) =>
        Tables.TryGetValue(shape, out var mapping) ? mapping.Mapping
            : throw missing?.Invoke() ?? new InvalidOperationException($"No PostgreSQL entity mapping is registered for query shape '{shape}'.");

    internal void Place(RelationQueryPlacementInputBuilder input, QualifiedShapeId shape,
        string? partitionSelector = null, Func<Exception>? missing = null, Func<Exception>? partitionMismatch = null)
    {
        var mapping = Mapping(shape, missing);
        if (partitionSelector is not null && mapping.PartitionField != partitionSelector)
            throw partitionMismatch?.Invoke() ?? new ArgumentException("Partition selector differs from the registered mapping.", nameof(partitionSelector));
        input.Identity(FieldPath.FromField(mapping.IdentityField), mapping.IdentityField).FieldsBySemanticPath();
        if (partitionSelector is not null) input.Partition(mapping.PartitionField);
    }
}
