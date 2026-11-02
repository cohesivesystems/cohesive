using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Cohesive.Adapters.Postgres;
using AspireFirst.Materialization;
using Cohesive.Adapters.Elastic;
using Cohesive.Api.Execution.Services;
using Cohesive.Model;
using Cohesive.Prelude;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Execution;
using Cohesive.Storage.Materialization;

namespace AspireFirst.Orders;

/// <summary>Explicitly selects one generation for order details; all writes and other queries stay native.</summary>
public static class OrderDetailsReadSelection
{
    /// <summary>Checks operator comparison evidence before opening Elasticsearch. Evidence is local, not a signed attestation.</summary>
    /// <param name="comparison">Successful bounded comparison produced by the operator command.</param>
    /// <param name="definition">The materialization prepared from this worker's canonical relation.</param>
    /// <param name="now">Admission clock.</param>
    public static void Validate(MaterializationShadowComparison comparison, PreparedMaterialization definition, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        if (!comparison.Matches || comparison.LiveCount < 0 || comparison.LiveCount != comparison.ShadowCount
            || comparison.ObservedAtUtc > now || comparison.Convergence.Materialization != definition.Document.Definition.Id
            || comparison.Convergence.DefinitionFingerprint != definition.Document.DefinitionFingerprint
            || !comparison.Convergence.ValidateFreshness(now).IsValid)
            throw new InvalidOperationException("Read selection requires fresh matching comparison evidence for this exact order-details definition.");
    }

    /// <summary>Prepares the storage-owned serving selection from host-owned clients and operator evidence.</summary>
    /// <param name="services">Native clients and runtime configuration.</param>
    /// <param name="queries">Canonical queries selected by storage.</param>
    /// <param name="storage">Shared source and rebuild declaration.</param>
    /// <param name="databaseIdentity">Existing keyed PostgreSQL resource identity.</param>
    /// <param name="cancellationToken">Cancels artifact and native inspection.</param>
    /// <returns>Bounded-reader factory, prepared once before native source schema preparation.</returns>
    public static async ValueTask<Func<int, long, IRelationQueryReader<string, OrderDetails?>>> PrepareAsync(
        IServiceProvider services, FulfillmentQueries queries, PostgresMaterializationStorage storage,
        string databaseIdentity, CancellationToken cancellationToken)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var comparisonPath = configuration["OrderReads:ComparisonFile"]
            ?? throw new InvalidOperationException("OrderReads:ComparisonFile is required for materialized reads.");
        var comparison = JsonSerializer.Deserialize<MaterializationShadowComparison>(
            await File.ReadAllTextAsync(comparisonPath, cancellationToken))
            ?? throw new InvalidOperationException("Read comparison evidence is missing.");
        Validate(comparison, storage.Materialization, DateTimeOffset.UtcNow);
        var key = Convert.FromBase64String(configuration["Shadow:PositionKey"]
            ?? throw new InvalidOperationException("Shadow:PositionKey is required for exact read-plan admission."));
        var incarnation = configuration["Shadow:SlotGeneration"]
            ?? throw new InvalidOperationException("Shadow:SlotGeneration is required for exact read-plan admission.");
        var database = services.GetRequiredKeyedService<NpgsqlDataSource>(databaseIdentity);
        var target = await ShadowElastic.BindAsync(services.GetRequiredKeyedService<HttpClient>(ShadowElastic.Resource),
            storage.Materialization, cancellationToken);
        _ = await target.ReadValueAsync(OperationContext.Create(cancellationToken: cancellationToken), comparison.Convergence.Generation,
            storage.Materialization.Document.DefinitionFingerprint, MaterializationItemId.FromRootIdentity("read-admission"));
        var runtime = await storage.BindAsync(database, services.GetRequiredKeyedService<PostgresLogicalReplicationConnectionFactory>(databaseIdentity),
            target, key, incarnation, cancellationToken);
        return PrepareReaders(queries, runtime, comparison);
    }

    /// <summary>Admits exact comparison evidence and prepares bounded readers without changing repository bindings.</summary>
    /// <param name="queries">Canonical query instance.</param>
    /// <param name="runtime">Exact admitted source, target and rebuild plan.</param>
    /// <param name="comparison">Operator comparison evidence.</param>
    /// <returns>Factory sharing native target and admitted generation; failures never select another backend.</returns>
    public static Func<int, long, IRelationQueryReader<string, OrderDetails?>> PrepareReaders(FulfillmentQueries queries,
        BoundMaterialization runtime, MaterializationShadowComparison comparison)
    {
        if (queries.OrderDetailsRelation.Definition.DefinitionDocument.DefinitionFingerprint
                != runtime.Resolved.Plan.Materialization.Definition.Relation.CompilationRequest.DefinitionDocument.DefinitionFingerprint)
            throw new ArgumentException("Selected generation does not realize this query relation.", nameof(queries));
        if (!comparison.Matches || !comparison.Convergence.ValidateAgainst(runtime.Resolved.Plan, DateTimeOffset.UtcNow).IsValid)
            throw new InvalidOperationException("Read selection requires fresh comparison evidence for the exact native rebuild plan.");
        var target = runtime.Resolved.Target as ElasticMaterializationTarget
            ?? throw new ArgumentException("This realization requires the admitted Elasticsearch target.", nameof(runtime));
        return (_, bytes) => new Reader(queries.OrderDetails, target, runtime.Resolved.Plan.Materialization.DefinitionFingerprint, comparison.Convergence.Generation, bytes);
    }

    sealed class Reader(RelationQuery<string, OrderDetails?> query, ElasticMaterializationTarget target,
        Cohesive.Execution.ExecutionDefinitionFingerprint definition, MaterializationGenerationId generation, long maximumBytes)
        : IRelationQueryReader<string, OrderDetails?>
    {
        readonly Func<ObservationValue, OrderDetails?> materialize = ObservationMaterializer.PrepareJsonValue<OrderDetails>();
        public RelationQueryConsistency Consistency => RelationQueryConsistency.Eventual;
        public RelationQuery<string, OrderDetails?> Definition => query;
        public async Task<OrderDetails?> ReadAsync(string input, CancellationToken cancellationToken = default)
        {
            var value = await target.ReadValueAsync(OperationContext.Create(cancellationToken: cancellationToken), generation,
                definition, MaterializationItemId.FromRootIdentity(input), (int)Math.Min(maximumBytes, int.MaxValue));
            if (value is null) return null;
            return materialize(value.Value) ?? throw new InvalidOperationException("A retained order-details item cannot contain a null result.");
        }
    }
}
