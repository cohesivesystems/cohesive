using System.Text.Json;
using AspireFirst.Materialization;
using Cohesive.Storage.Materialization;
using Cohesive.Adapters.Postgres.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Adapters.Marten;
using Marten;
using Cohesive.Application;
using AspireFirst;
using AspireFirst.Orders;
using Cohesive.Adapters.AspNet;
using Cohesive.Adapters.AspNet.Services;
using Npgsql;
using Cohesive.Storage;

var builder = WebApplication.CreateBuilder(args);
var policy = FulfillmentHistoryPolicy.FromConfiguration(builder.Configuration);
var reads = builder.Configuration.GetSection("OrderReads").Get<OrderReadPolicy>() ?? new();
reads.Validate();
var model = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(policy).AddSingleton(reads));
builder.Services.AddRequestOperationContext();
builder.Services.AddFixedRequestIdentity(FulfillmentDemoIdentity.Context);
builder.Services.AddCohesiveExceptionHandling();
var connectionString = builder.Configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
    ?? throw new InvalidOperationException("The native Aspire orders database reference is required.");
builder.Services.AddKeyedSingleton<NpgsqlDataSource>(policy.DatabaseName,
    (_, _) => NpgsqlDataSource.Create(connectionString));
builder.Services.AddKeyedSingleton<IDocumentStore>(policy.DatabaseName, (services, _) => DocumentStore.For(options =>
{
    options.Connection(services.GetRequiredKeyedService<NpgsqlDataSource>(policy.DatabaseName));
    policy.History.Configure(options);
}));
if (reads.Source == OrderDetailsReadSource.Live)
    builder.Services.AddCohesiveServices(model);
else
{
    var materializations = new FulfillmentMaterializations(model.Queries);
    var definition = materializations.OrderDetails;
    var materializationStorage = new FulfillmentMaterializationStorage(model.Storage, materializations);
    var positionKey = Convert.FromBase64String(builder.Configuration["Shadow:PositionKey"]
        ?? throw new InvalidOperationException("Shadow:PositionKey is required for exact read-plan admission."));
    var slotGeneration = builder.Configuration["Shadow:SlotGeneration"]
        ?? throw new InvalidOperationException("Shadow:SlotGeneration is required for exact read-plan admission.");
    var comparisonPath = builder.Configuration["OrderReads:ComparisonFile"]
        ?? throw new InvalidOperationException("OrderReads:ComparisonFile is required for materialized reads.");
    var comparison = JsonSerializer.Deserialize<MaterializationShadowComparison>(await File.ReadAllTextAsync(comparisonPath))
        ?? throw new InvalidOperationException("Read comparison evidence is missing.");
    OrderDetailsReadSelection.Validate(comparison, definition, DateTimeOffset.UtcNow);
    var endpoint = new Uri(builder.Configuration["Shadow:Elasticsearch"]
        ?? throw new InvalidOperationException("Shadow:Elasticsearch is required for materialized reads."));
    builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = endpoint });
    var native = model.Storage.RequireComposition<PostgresStorageComposition>();
    // Admit all logical repository/query dependencies before native preparation. The explicit override below changes only Details.
    var admission = new ServiceStorageComposition([native]);
    admission.Validate([model.Endpoints.Plan]);
    builder.Services.AddCohesiveServices(model.Endpoints, async (services, ct) =>
    {
        var target = await ShadowElastic.BindAsync(services.GetRequiredService<HttpClient>(), definition, ct);
        // Native affinity and retention must pass before admitting HTTP traffic, even for an empty generation.
        _ = await target.ReadValueAsync(Cohesive.Prelude.OperationContext.Create(cancellationToken: ct),
            comparison.Convergence.Generation, definition.Document.DefinitionFingerprint,
            MaterializationItemId.FromRootIdentity("read-admission"));
        var runtime = await materializationStorage.OrderDetails.BindAsync(
            services.GetRequiredKeyedService<NpgsqlDataSource>(policy.DatabaseName),
            () => new Npgsql.Replication.LogicalReplicationConnection(connectionString), target, positionKey, slotGeneration, ct);
        if (!comparison.Convergence.ValidateAgainst(runtime.Resolved.Plan, DateTimeOffset.UtcNow).IsValid)
            throw new InvalidOperationException("Read comparison does not match this worker's exact native source placement.");
        var live = await admission.BindAsync(services, ct);
        return OrderDetailsReadSelection.Bind(live, model.Queries, runtime, comparison);
    });
}

await using var app = builder.Build();
app.Logger.LogInformation("OrderDetails read source: {Source}; consistency: {Consistency}. Selection is fixed for this host lifetime.", reads.Source, reads.Consistency);
app.UseExceptionHandler();
app.UseRequestOperationContext();
app.UseAuthorization();
if (reads.Source == OrderDetailsReadSource.Live) await app.MapServicesAsync();
else await app.MapServicesAsync<OrderEndpoints>();
await app.RunAsync();
