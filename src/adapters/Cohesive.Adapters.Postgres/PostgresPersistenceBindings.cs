using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Transitions.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Realization;

namespace Cohesive.Adapters.Postgres;

/// <summary>Offline PostgreSQL entity mappings used by native query preparation and runtime registration.</summary>
/// <remarks>Configure once at host composition. Bind snapshots the mappings; later edits cannot change an
/// existing runtime registration. Preparation performs no IO and retains no credentials or data source.</remarks>
public sealed class PostgresPersistenceBindings
{
    internal readonly Dictionary<QualifiedShapeId, (EntityDefinition Entity, PostgresEntityRepositoryMapping Mapping)> Tables = [];
    /// <summary>Creates offline bindings for one explicitly identified database.</summary>
    /// <param name="database">Stable database identity shared with the eventual runtime binding.</param>
    /// <exception cref="ArgumentException">Database identity is empty.</exception>
    public PostgresPersistenceBindings(PostgresRelationQueryDatabaseId database)
    {
        if (string.IsNullOrWhiteSpace(database.Value)) throw new ArgumentException("Database identity is required.", nameof(database));
        Database = database;
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
    public PostgresPersistenceBindings Entity<T>(DomainEntity<T> entity, PostgresEntityRepositoryMapping mapping) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mapping);
        PostgresEntityRepository.ValidateMapping(entity.Definition, mapping);
        Tables.Add(entity.Definition.StateShape.QualifiedId, (entity.Definition, mapping));
        return this;
    }

    /// <summary>Attaches a caller-owned data source to a snapshot of these mappings.</summary>
    /// <param name="runtime">Exact runtime binding for the declared database.</param>
    /// <returns>Runtime registration with independent mapping ownership.</returns>
    /// <exception cref="ArgumentNullException">Runtime is null.</exception>
    /// <exception cref="ArgumentException">Database identities differ.</exception>
    public PostgresPersistenceRegistration Bind(PostgresNpgsqlRuntimeBinding runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        if (runtime.Database != Database) throw new ArgumentException("Offline and runtime database identities must match.", nameof(runtime));
        var snapshot = new PostgresPersistenceBindings(Database);
        foreach (var pair in Tables) snapshot.Tables.Add(pair.Key, pair.Value);
        return new(runtime, snapshot);
    }

    /// <summary>Compiles one native query through the existing semantic, placement and PostgreSQL validators.</summary>
    /// <param name="request">Canonical query and exact semantic snapshots.</param>
    /// <returns>Fingerprint-validated native artifact, without opening a database connection.</returns>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    /// <exception cref="PostgresQueryPreparationException">Semantic or native compilation fails.</exception>
    /// <exception cref="InvalidOperationException">A required entity mapping is absent.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Placement or binding is invalid.</exception>
    public PostgresRelationQueryCompiledArtifact Prepare(RelationQueryCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var compilation = RelationQueryStaticCompiler.Compile(request);
        if (!compilation.IsSuccessful) throw new PostgresQueryPreparationException(compilation, null);
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

        PostgresEntityRepositoryMapping Mapping(QualifiedShapeId shape) => Tables.TryGetValue(shape, out var mapping)
            ? mapping.Mapping : throw new InvalidOperationException($"No PostgreSQL entity mapping is registered for query shape '{shape}'.");
        void Place(RelationQueryPlacementInputBuilder input, QualifiedShapeId shape)
        {
            var mapping = Mapping(shape);
            input.Identity(FieldPath.FromField(mapping.IdentityField), mapping.IdentityField).FieldsBySemanticPath();
        }
    }
}
