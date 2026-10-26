using AspireFirst.Orders;
using AspireFirst.Shadow;
using Cohesive.Adapters.Postgres;
using Cohesive.Application;
using Cohesive.Cli;
using Cohesive.Prelude;
using Cohesive.Storage.Materialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Globalization;
using System.Text.Json;

var cli = new CliApplication(description: "Bounded OrderDetails shadow generation; live HTTP reads remain in PostgreSQL.");
foreach (var operation in new[] { "initialize", "rebuild", "catch-up" })
{
    var selected = operation;
    var options = new Dictionary<string, CliOption>();
    if (operation != "initialize")
    {
        options["attempt"] = CliOption.For<string>(description: "Stable attempt ID retained when resuming this generation") with { Required = true };
        options["started"] = CliOption.For<string>(description: "Stable UTC attempt start (ISO 8601), retained on resume") with { Required = true };
        options["invocation"] = CliOption.For<string>(description: "Catch-up activation ID; retain on retry, change for a new cut") with { Required = true };
    }
    cli.Command(operation, options: options).OnExecute(async context =>
    {
        var ct = context.CancellationToken;
        var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var connection = configuration.GetConnectionString(FulfillmentStoragePolicy.OrdersDatabaseName)
            ?? throw new InvalidOperationException("ConnectionStrings__orders is required.");
        var endpoint = configuration["Shadow:Elasticsearch"]
            ?? throw new InvalidOperationException("Shadow__Elasticsearch is required.");
        var policy = FulfillmentHistoryPolicy.FromConfiguration(configuration);
        var application = ModelActivator.Create<FulfillmentApplication>(services => services.AddSingleton(policy));
        await using var database = NpgsqlDataSource.Create(connection);
        using var elastic = new HttpClient { BaseAddress = new Uri(endpoint) };
        await using var lease = await PostgresSessionLease.AcquireAsync(database, ShadowBootstrap.WriterLock, ct);
        try
        {
            if (selected == "initialize")
            {
                await ShadowBootstrap.InitializeAsync(application, database, elastic, ct);
                context.Io.WriteLine("Shadow publication, slots, template and checkpoint store initialized. No generation or live route changed.");
                return 0;
            }
            var key = Convert.FromBase64String(configuration["Shadow:PositionKey"]
                ?? throw new InvalidOperationException("Shadow__PositionKey must be a persistent base64 key of at least 32 bytes."));
            var incarnation = configuration["Shadow:SlotGeneration"]
                ?? throw new InvalidOperationException("Shadow__SlotGeneration is required and must change if slots are recreated.");
            var definition = new FulfillmentShadowDefinition(application.Queries);
            var target = await ShadowElastic.BindAsync(elastic, definition, ct);
            var runtime = await FulfillmentShadowRuntime.BindAsync(application, database, connection, target, key, incarnation, ct);
            var id = context.Configuration.Get<string>("attempt")!;
            var started = DateTimeOffset.Parse(context.Configuration.Get<string>("started")!, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
            var attempt = new MaterializationRebuildAttempt(new(new("order-shadow/" + id), new(id)), started);
            var operationContext = OperationContext.Create(cancellationToken: ct);
            if (selected == "rebuild")
            {
                var executor = new MaterializationRebuildExecutor(runtime.Resolved);
                var begun = await executor.BeginAttemptAsync(operationContext, attempt);
                if (begun.Disposition != MaterializationRebuildInitializationDisposition.Ready)
                    throw new InvalidOperationException(JsonSerializer.Serialize(begun));
                foreach (var shard in runtime.Compilation.Plan.Shards)
                {
                    var result = await executor.RunShardAsync(operationContext, attempt, shard.Id);
                    if (result.Disposition != MaterializationRebuildShardDisposition.BaselineCompleteCatchUpRequired)
                        throw new InvalidOperationException(JsonSerializer.Serialize(result));
                }
            }
            var synchronized = await new MaterializationSynchronizationExecutor(runtime.Resolved, runtime.State, MaterializationIndexSyncWorkloadKind.Rebuild)
                .ConvergeAsync(operationContext, attempt, new(context.Configuration.Get<string>("invocation")), new("order-shadow/operator"));
            context.Io.WriteLine(JsonSerializer.Serialize(new
            {
                generation = MaterializationRebuildIdentities.Generation(runtime.Compilation.Plan, attempt).Value,
                Disposition = synchronized.Disposition.ToString(),
                synchronized.Diagnostics,
                liveReads = "postgres",
                promotion = "disabled"
            }));
            return synchronized.Receipt is not null ? 0 : 2;
        }
        catch (PostgresLogicalReplicationException error)
        {
            context.Io.WriteLine($"CDC {error.FailureKind}: {error.Observation.EvidenceReference}");
            return 1;
        }

    });
}
return await cli.RunAsync(args);
