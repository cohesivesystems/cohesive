using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Cohesive.Model.Expressions;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Storage.Materialization;
using Npgsql;

namespace Cohesive.Adapters.Postgres;

/// <summary>Overrides for a single-partition, baseline-plus-catch-up PostgreSQL target.</summary>
public sealed record PostgresAsyncRowsOptions
{
    /// <summary>Preferred number of changes to process per page.</summary>
    public int PageSize { get; init; } = 100;
    /// <summary>Maximum fan-out admitted for one change.</summary>
    public int MaximumRoots { get; init; } = 5000;
    /// <summary>Per-page and inverse-read byte budget.</summary>
    public int MaximumBytes { get; init; } = 2_000_000;
    /// <summary>Interval between checks for pending work.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);
    /// <summary>Override the deterministic PostgreSQL schedule name.</summary>
    public string? ScheduleName { get; init; }
    /// <summary>Override provider placement, publication, slot, checkpoint or replication policy before binding.</summary>
    public Func<PostgresMaterializationStorageOptions, PostgresMaterializationStorageOptions>? ConfigureStorage { get; init; }
}

public sealed partial class PostgresPersistenceRegistration
{
    /// <summary>Maintain a flat projection asynchronously. One rooted graph supplies the SQL rows,
    /// the change-impact plan and the typed publication key. Initialization is explicit.</summary>
    public PostgresAsyncRows<TRow> MaterializeAsyncRows<TRow>(RootedRowProjection<TRow> projection,
        string table, PostgresRelationQueryPartitionScope partition, PostgresAsyncRowsOptions? options = null)
        where TRow : class
    {
        ArgumentNullException.ThrowIfNull(projection);
        ArgumentNullException.ThrowIfNull(partition);
        options ??= new();
        var relation = (RelationDefinition)projection.Relation.DefinitionDocument.Definition;
        var root = relation.Body.Nodes.OfType<SourceQueryNode>().Single(source => source.Binding == relation.RootBinding);
        var output = relation.Body.Nodes.OfType<ProjectQueryNode>().Single(node => node.Id == relation.Output.Node);
        var key = output.Assignments.Single(field => field.Target == projection.KeyField).Value;
        if (key is not FieldExpr field || field.Binding != relation.RootBinding ||
            field.Path != FieldPath.FromField(bindings.Mapping(root.Shape).IdentityField))
            throw new ArgumentException($"Projected key '{projection.KeyField}' must select the mapped identity of the relation root.", nameof(projection));

        var plan = RelationQueryStaticCompiler.Compile(projection.Relation);
        if (!plan.IsSuccessful) throw new RelationQueryPreparationException("async row impact", plan);
        var completeOutput = plan.Plan!.RequirementGraph.Outputs.Single(item => item.Field is null);
        var id = relation.Id.Value + "/" + table;
        var page = options.PageSize;
        var roots = options.MaximumRoots;
        var bytes = options.MaximumBytes;
        var provenance = new Cohesive.Execution.ExecutionProvenance(new("cohesive/postgres/async-rows", "1"), new(id));
        var declaration = Materialization.Define(new(id), projection.Relation, completeOutput.Id)
            .WithUpdatePolicy(new(MaterializationSynchronizationMode.All, MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion))
            .WithBoundedRelationBaselineCatchUpSources(roots, bytes, roots, bytes)
            .WithGenerationalIndexTarget(page, bytes)
            .WithFailurePolicy(new(3, MaterializationFailureDisposition.Stop))
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 300_000))
            .WithProvenance(new(new("cohesive/postgres/async-rows", "1"), new(id), DocumentOrigin.Generated))
            .Build().Prepare(new(new(id + "/impact"), [MaterializationImpactStrategyKind.InverseTraversal], roots, bytes));
        static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
        var prefix = "coh_" + Hash(id);
        var native = new PostgresMaterializationStorageOptions
        {
            PartitionField = partition.SourceSelector,
            Planning = new(new(id + "/physical"), id, page, roots, roots, page, page, 1),
            Reads = new(page, roots, page, bytes, partitionScope: partition),
            Rebuild = new(page, bytes, page, bytes, 128, 1, 1, 16),
            Publication = prefix,
            Slot = input => prefix + "_" + Hash(input.Value),
            Replication = new(readInactivityTimeout: TimeSpan.FromSeconds(1)),
            Checkpoints = new(id), Provider = id,
            Capacity = new(new(id + "/worker"), 1, ["postgres-session-advisory-lock"]),
            Provenance = provenance
        };
        var storage = new PostgresMaterializationStorage(bindings, declaration, options.ConfigureStorage?.Invoke(native) ?? native);
        var sql = bindings.Prepare(projection.Rows);
        var materialize = ObservationMaterializer.PrepareJsonValue<TRow>();
        var target = new PostgresMaterializedRows<TRow>(runtime.DataSource, "public", table, sql,
            sql.Bind(new Dictionary<QueryParameterId, ObservationValue>()), rows => rows.Select(row => materialize(row)!).ToArray());
        return new(runtime.DataSource, storage, target, projection, options.ScheduleName ?? prefix, options.PollInterval);
    }
}

/// <summary>Bound typed lookup, schedule and hosted-worker factory for one asynchronous row projection.</summary>
public sealed class PostgresAsyncRows<TRow> where TRow : class
{
    readonly NpgsqlDataSource database;
    readonly PostgresMaterializationStorage storage;
    readonly PostgresMaterializedRows<TRow> target;
    readonly RootedRowProjection<TRow> projection;
    readonly TimeSpan interval;
    readonly PostgresMaterializedLookup<string, TRow> lookup;

    internal PostgresAsyncRows(NpgsqlDataSource database, PostgresMaterializationStorage storage,
        PostgresMaterializedRows<TRow> target, RootedRowProjection<TRow> projection, string schedule, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        (this.database, this.storage, this.target, this.projection, this.interval) = (database, storage, target, projection, interval);
        lookup = target.Lookup(projection.Key);
        Schedule = storage.Schedule(database, schedule);
    }

    /// <summary>Prepared impact plan derived from the same graph as the stored SQL rows.</summary>
    public MaterializationImpactPlan Impact => storage.Materialization.Impact;

    /// <summary>Durable operator repair and freshness operations.</summary>
    public PostgresRowsSchedule Schedule { get; }
    /// <summary>Read all committed target rows for a root.</summary>
    public Task<TRow[]> ReadAsync(string id, CancellationToken token = default) => lookup.ReadAsync(id, token);

    /// <summary>Initialize relation-derived PostgreSQL sources and scheduling objects.</summary>
    public async Task InitializeAsync(CancellationToken token = default)
    {
        await storage.InitializeSourcesAsync(database, token);
        await Schedule.InitializeAsync(token);
    }

    /// <summary>Bind the hosted worker with deployment-owned replication credentials and slot incarnation.</summary>
    public Task<PostgresRootedRowsWorker<TRow>> BindWorkerAsync(PostgresLogicalReplicationConnectionFactory replication,
        ReadOnlyMemory<byte> positionKey, string slotGeneration = "v1", CancellationToken token = default) =>
        storage.BindRowsWorkerAsync(database, replication, positionKey, slotGeneration, target, projection.Key, interval, Schedule, token);
}
