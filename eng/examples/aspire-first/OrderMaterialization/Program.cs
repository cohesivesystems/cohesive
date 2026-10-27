using AspireFirst.Orders;
using AspireFirst.Materialization;
using Cohesive.Adapters.Postgres;
using Cohesive.Application;
using Cohesive.Application.Cli;
using Cohesive.Cli;
using Cohesive.Prelude;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Npgsql.Replication;

var cli = new CliApplication(description: "OrderDetails materialization in shadow mode; live HTTP reads remain in PostgreSQL.");
cli.AddMaterializationShadowCommands("order-shadow",
    initialize: ct => WithResources(async (model, database, elastic, configuration) =>
    {
        await ShadowBootstrap.InitializeAsync(model, database, elastic, ct);
        return 0;
    }, ct),
    execute: (invocation, ct) => WithResources(async (model, database, elastic, configuration) =>
    {
        var key = Convert.FromBase64String(configuration["Shadow:PositionKey"]
            ?? throw new InvalidOperationException("Shadow__PositionKey must be a persistent base64 key of at least 32 bytes."));
        var incarnation = configuration["Shadow:SlotGeneration"]
            ?? throw new InvalidOperationException("Shadow__SlotGeneration is required and must change if slots are recreated.");
        var target = await ShadowElastic.BindAsync(elastic, model.Materializations.OrderDetails, ct);
        var runtime = await model.Storage.OrderDetails.BindAsync(database,
            () => new LogicalReplicationConnection(configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)!),
            target, key, incarnation, ct);
        return await MaterializationShadowExecution.RunAsync(runtime,
            invocation.Operation, OperationContext.Create(cancellationToken: ct), invocation.Attempt,
            invocation.Invocation, new("order-shadow/operator"));
    }, ct));
return await cli.RunAsync(args);

static async Task<T> WithResources<T>(Func<OrderMaterializationApplication, NpgsqlDataSource, HttpClient, IConfiguration, Task<T>> action,
    CancellationToken ct)
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    var connection = configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
        ?? throw new InvalidOperationException("ConnectionStrings__orders is required.");
    var endpoint = configuration["Shadow:Elasticsearch"]
        ?? throw new InvalidOperationException("Shadow__Elasticsearch is required.");
    var policy = FulfillmentHistoryPolicy.FromConfiguration(configuration);
    var model = ModelActivator.Create<OrderMaterializationApplication>(services => services.AddSingleton(policy));
    await using var database = NpgsqlDataSource.Create(connection);
    using var elastic = new HttpClient { BaseAddress = new Uri(endpoint) };
    await using var lease = await PostgresSessionLease.AcquireAsync(database, ShadowBootstrap.WriterLock, ct);
    return await action(model, database, elastic, configuration);
}
