using Cohesive.Storage;
using Cohesive.Model;
using Cohesive.Transitions.Model;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.Realization;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Adapters.Postgres;

/// <summary>Native PostgreSQL entity attachments shared by repositories and queries on one explicitly bound database.</summary>
/// <remarks>Bind an offline mapping snapshot at host composition, then retain its repositories and prepared query readers.
/// Query prepares once per call; no invocation result cache is introduced.
/// No connections are opened during preparation. No provider-neutral facade or global cache is introduced.</remarks>
public sealed partial class PostgresPersistenceRegistration
{
    readonly PostgresNpgsqlRuntimeBinding runtime;
    readonly PostgresPersistenceBindings bindings;
    Dictionary<QualifiedShapeId, (EntityDefinition Entity, PostgresEntityRepositoryMapping Mapping)> tables => bindings.Tables;

    internal PostgresPersistenceRegistration(PostgresNpgsqlRuntimeBinding runtime, PostgresPersistenceBindings bindings)
    { this.runtime = runtime; this.bindings = bindings; }

    /// <summary>Exact immutable mappings from which this native runtime was bound.</summary>
    public PostgresPersistenceBindings Bindings => bindings;

    /// <summary>Creates a repository from the same entity attachment used by query preparation.</summary>
    /// <typeparam name="T">Canonical entity state type.</typeparam>
    /// <param name="entity">Exact entity handle registered on this persistence binding.</param>
    /// <param name="selectEntityId">Optional explicit typed-write identity selector; otherwise compiled from the registered canonical identity field at setup.</param>
    /// <param name="selectVersion">Optional typed-write semantic version selector; otherwise existing Version/zero conventions apply.</param>
    /// <param name="transitionReceipts">Optional schema-validated receipt storage bound to this same native data source.</param>
    /// <param name="stateHistory">Optional schema-validated authority and history attachment on this data source.</param>
    /// <returns>A repository using the registered mapping and caller-owned native data source.</returns>
    /// <exception cref="ArgumentNullException">Entity is null.</exception>
    /// <exception cref="InvalidOperationException">The exact entity definition has not been registered, or its identity cannot be mapped to one readable property.</exception>
    public IEntityRepository<T> Repository<T>(DomainEntity<T> entity,
        Func<T, string>? selectEntityId = null, Func<T, long>? selectVersion = null,
        PostgresTransitionReceiptStorage? transitionReceipts = null, PostgresEntityHistoryStorage? stateHistory = null) where T : notnull
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (!tables.TryGetValue(entity.Definition.StateShape.QualifiedId, out var attachment)
            || !ReferenceEquals(attachment.Entity, entity.Definition))
            throw new InvalidOperationException("Register this exact entity before creating its repository.");
        return new TypedEntityRepository<T>(new PostgresEntityRepository(attachment.Entity, runtime, attachment.Mapping, transitionReceipts, stateHistory),
            selectEntityId, selectVersion);
    }

    /// <summary>Resolves an untyped repository from an exact entity observation contract in these mappings.</summary>
    /// <param name="observation">Required canonical entity observation.</param>
    /// <param name="transitionReceipts">Optional schema-validated receipt storage on this data source.</param>
    /// <param name="stateHistory">Optional schema-validated authority and history attachment on this data source.</param>
    /// <returns>A repository retaining the registered entity authority and native mapping.</returns>
    /// <exception cref="InvalidOperationException">No unique exact mapping exists.</exception>
    public IEntityRepository Repository(ValueContract observation, PostgresTransitionReceiptStorage? transitionReceipts = null, PostgresEntityHistoryStorage? stateHistory = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var matches = tables.Values.Where(item => ValueContract.FromShape(item.Entity.Shape) == observation).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Exactly one registered entity mapping must match the observation contract.");
        var attachment = matches[0];
        return new PostgresEntityRepository(attachment.Entity, runtime, attachment.Mapping, transitionReceipts, stateHistory);
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
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native compilation fails; original results are retained.</exception>
    /// <exception cref="InvalidOperationException">An input shape has no registered native mapping.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Placement or binding is invalid.</exception>
    public PostgresQueryReader<TInput, TResult> Query<TInput, TResult>(RelationQuery<TInput, TResult> query,
        int maximumRows, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(query);
        var rows = Prepare(query.CompilationRequest, maximumRows, maximumBytes);
        return new(query, rows.Artifact, rows);
    }

    /// <summary>Prepares a canonical row query, including a derived subplan, using native PostgreSQL compilation.</summary>
    /// <param name="request">Exact query and semantic snapshots.</param>
    /// <param name="maximumRows">Complete-result row limit; overflow fails.</param>
    /// <param name="maximumBytes">Complete decoded scalar-value byte limit.</param>
    /// <returns>A plan-affine reader retaining its native artifact.</returns>
    /// <exception cref="RelationQueryPreparationException">Semantic compilation fails.</exception>
    /// <exception cref="PostgresQueryPreparationException">Native compilation fails.</exception>
    /// <exception cref="ArgumentNullException">Request is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A result bound is invalid.</exception>
    /// <exception cref="NotSupportedException">The artifact requires unsupported temporal execution.</exception>
    /// <exception cref="InvalidOperationException">An input shape has no registered mapping.</exception>
    /// <exception cref="RelationQueryArtifactAuthoringException">Placement or storage binding is invalid.</exception>
    /// <exception cref="ArgumentException">Runtime affinity is invalid.</exception>
    public PostgresQueryRowsReader Prepare(RelationQueryCompilationRequest request, int maximumRows, long maximumBytes)
    {
        return new PostgresQueryRowsReader(bindings.Prepare(request), runtime, maximumRows, maximumBytes);
    }
}

/// <summary>A typed execution binding to one prepared native query; safe for concurrent reads.</summary>
/// <typeparam name="TInput">Invocation parameter type.</typeparam>
/// <typeparam name="TResult">Application result type.</typeparam>
public sealed class PostgresQueryReader<TInput, TResult> : IRelationQueryReader<TInput, TResult>
{
    /// <inheritdoc />
    public RelationQueryConsistency Consistency => RelationQueryConsistency.SingleSnapshot;
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
        Definition.AssembleResult(await rows.ReadAsync(new Dictionary<Cohesive.Relations.IR.QueryParameterId, ObservationValue>
        { [Definition.Parameter] = ObservationValue.FromObject(input) }, cancellationToken).ConfigureAwait(false));
}

/// <summary>Registration failed to prepare a query, retaining exact compiler diagnostics.</summary>
public sealed class PostgresQueryPreparationException : PreparationException
{
    internal PostgresQueryPreparationException(RelationQueryCompilationResult compilation, PostgresRelationQueryCompilationResult? native)
        : base(native is null ? "semantic" : "native", native is null ? "postgres.preparation.semantic" : "postgres.preparation.native", "PostgreSQL query preparation failed: " + string.Join("; ", native is null
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
