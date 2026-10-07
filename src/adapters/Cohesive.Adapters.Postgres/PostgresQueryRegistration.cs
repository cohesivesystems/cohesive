using Cohesive.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Realization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Adapters.Postgres;

/// <summary>Native PostgreSQL registration for canonical entity queries on one explicitly bound database.</summary>
/// <remarks>Configure at host composition. Register prepares once per call; retain the returned reader.
/// No connections are opened during preparation. No provider-neutral facade or global cache is introduced.</remarks>
public sealed class PostgresQueryRegistration
{
    readonly PostgresNpgsqlRuntimeBinding runtime;
    readonly Dictionary<QualifiedShapeId, PostgresEntityRepositoryMapping> tables = [];

    /// <summary>Creates an invocation-local registration builder.</summary>
    /// <param name="runtime">Explicit database identity and caller-owned native data source.</param>
    /// <exception cref="ArgumentNullException">Runtime is null.</exception>
    public PostgresQueryRegistration(PostgresNpgsqlRuntimeBinding runtime) =>
        this.runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>Attaches an existing physical mapping to its exact canonical entity graph.</summary>
    /// <typeparam name="T">Entity state type.</typeparam>
    /// <param name="entity">Canonical entity authority.</param>
    /// <param name="mapping">Native table/column configuration, reused from repository registration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">A shape is already mapped or the repository mapping is invalid.</exception>
    public PostgresQueryRegistration Entity<T>(DomainEntity<T> entity, PostgresEntityRepositoryMapping mapping) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(mapping);
        PostgresEntityRepository.ValidateMapping(entity.Definition, mapping);
        tables.Add(entity.Definition.StateShape.QualifiedId, mapping);
        return this;
    }

    /// <summary>Validates and prepares a typed query using the existing static, placement and native compilers.</summary>
    /// <typeparam name="TInput">Invocation type.</typeparam>
    /// <typeparam name="TResult">Application result type.</typeparam>
    /// <param name="query">Backend-independent query and local presentation projection.</param>
    /// <param name="maximumRows">Complete-result row bound.</param>
    /// <param name="maximumBytes">Decoded scalar byte bound.</param>
    /// <returns>A reusable typed reader with an inspectable native artifact.</returns>
    /// <exception cref="ArgumentNullException">Query is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A result bound is invalid.</exception>
    /// <exception cref="NotSupportedException">The artifact requires unsupported temporal execution.</exception>
    /// <exception cref="PostgresQueryPreparationException">Static or native compilation fails; original results are retained.</exception>
    /// <exception cref="InvalidOperationException">An input shape has no registered native mapping.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Placement or binding is invalid.</exception>
    public PostgresQueryReader<TInput, TResult> Register<TInput, TResult>(RelationQuery<TInput, TResult> query,
        int maximumRows, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(query);
        var compilation = RelationQueryStaticCompiler.Compile(query.CompilationRequest);
        var plan = compilation.Plan ?? throw new PostgresQueryPreparationException(compilation, null);
        var builder = RelationQueryPlacement.For(plan);
        var source = builder.Source("postgres/query", PostgresRelationQuerySourceTargetProfile.Default, new(runtime.Database.Value));
        foreach (var input in plan.InputContract.Sources)
            Place(builder.Place(input, source), input.Shape);
        foreach (var input in plan.InputContract.Traversals)
            Place(builder.Place(input, source), input.ResultShape);
        var placement = builder.Build().RequireValue();
        var binding = PostgresRelationQueryBinding.For(placement).Database(runtime.Database);
        foreach (var input in placement.Inputs) binding.Table(input, Mapping(input.Shape));
        var storage = binding.Build().RequireValue();
        var feasibility = RelationQueryRealizationCompiler.Compile(plan, PostgresRelationQueryTargetProfile.Default,
            PostgresRelationQueryTargetProfile.Policy, RelationQueryResultObservability.NotRequested);
        var compiler = new PostgresRelationQueryCompiler();
        var bound = compiler.Realize(new(plan, feasibility, placement.Placement), storage);
        var native = compiler.Compile(new RelationQueryNativeCompilationRequest(plan, bound, placement.Placement), storage);
        if (!native.IsSuccessful) throw new PostgresQueryPreparationException(compilation, native);
        var artifact = native.Artifacts.Single();
        return new(query, artifact, new PostgresQueryRowsReader(artifact, runtime, maximumRows, maximumBytes));

        PostgresEntityRepositoryMapping Mapping(QualifiedShapeId shape) => tables.TryGetValue(shape, out var mapping)
            ? mapping : throw new InvalidOperationException($"No PostgreSQL entity mapping is registered for query shape '{shape}'.");
        void Place(RelationQueryPlacementInputBuilder input, QualifiedShapeId shape)
        {
            var mapping = Mapping(shape);
            input.Identity(FieldPath.FromField(mapping.IdentityField), mapping.IdentityField).FieldsBySemanticPath();
        }
    }
}

/// <summary>A typed execution binding to one prepared native query; safe for concurrent reads.</summary>
/// <typeparam name="TInput">Invocation parameter type.</typeparam>
/// <typeparam name="TResult">Application result type.</typeparam>
public sealed class PostgresQueryReader<TInput, TResult> : IRelationQueryReader<TInput, TResult>
{
    readonly IRelationQueryRowsReader rows;
    /// <inheritdoc />
    public RelationQuery<TInput, TResult> Definition { get; }
    /// <summary>Retains the exact definition and execution artifacts produced together by registration.</summary>
    /// <param name="query">Canonical query authority and local result projection.</param>
    /// <param name="artifact">Native artifact compiled from that definition.</param>
    /// <param name="rows">Complete-row execution binding prepared for the artifact.</param>
    internal PostgresQueryReader(RelationQuery<TInput, TResult> query, PostgresRelationQueryCompiledArtifact artifact,
        IRelationQueryRowsReader rows)
    {
        Definition = query;
        Artifact = artifact;
        this.rows = rows;
    }
    /// <summary>Prepared artifact, retained for inspection and provenance.</summary>
    public PostgresRelationQueryCompiledArtifact Artifact { get; }
    /// <summary>Executes the bounded native query and materializes its declared result.</summary>
    /// <param name="input">Value for the declared canonical parameter.</param>
    /// <param name="cancellationToken">Cancellation for native IO.</param>
    /// <returns>The query's typed result, including its declared empty-result policy.</returns>
    /// <remarks>No compilation or retry occurs here. Provider, validation and result-projection failures propagate.</remarks>
    public async Task<TResult> ReadAsync(TInput input, CancellationToken cancellationToken = default) =>
        Definition.Project(await rows.ReadAsync(new Dictionary<Cohesive.Relations.IR.QueryParameterId, ObservationValue>
        { [Definition.Parameter] = ObservationValue.FromObject(input) }, cancellationToken).ConfigureAwait(false));
}

/// <summary>Registration failed to prepare a query, retaining exact compiler diagnostics.</summary>
public sealed class PostgresQueryPreparationException : InvalidOperationException
{
    internal PostgresQueryPreparationException(RelationQueryCompilationResult compilation, PostgresRelationQueryCompilationResult? native)
        : base("PostgreSQL query preparation failed: " + string.Join("; ", native is null
            ? compilation.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")
            : native.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")))
    {
        Compilation = compilation;
        NativeCompilation = native;
    }
    /// <summary>Static compilation evidence and structured diagnostics.</summary>
    public RelationQueryCompilationResult Compilation { get; }
    /// <summary>Native compilation evidence when that phase was reached.</summary>
    public PostgresRelationQueryCompilationResult? NativeCompilation { get; }
}
