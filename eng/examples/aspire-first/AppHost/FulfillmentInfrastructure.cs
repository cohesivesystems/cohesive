using Aspire.Hosting;
using Microsoft.Extensions.Configuration;
using Cohesive.Adapters.Aspire;
using Cohesive.Adapters.Aspire.Postgres;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Storage;
using AspireFirst.Orders;
using Cohesive.Adapters.Postgres.Services;

namespace AspireFirst;

/// <summary>A native AppHost with optional Cohesive requirement validation attached to selected resources.</summary>
public sealed class FulfillmentInfrastructure : InfrastructureModel
{
    readonly FulfillmentHistoryPolicy policy;
    readonly FulfillmentApplication application;
    /// <summary>Canonical order storage identity.</summary>
    public static InfrastructureNodeId Store { get; } = new("resources/orders");
    /// <summary>Canonical order application identity.</summary>
    public static InfrastructureNodeId Worker { get; } = new("workloads/orders");
    /// <summary>Required application execution capability.</summary>
    public static InfrastructureCapabilityId ApplicationExecution { get; } = AspireInfrastructure.DotNetProjectExecution;

    static readonly SourceReference Source = SourceReference.Create("example", "aspire-first/orders/v1");

    /// <summary>Prepares requirements from the same offline composition as the worker.</summary>
    /// <param name="model">Same activated application graph consumed by the worker.</param>
    /// <param name="policy">Explicit environment selection used to activate that graph.</param>
    public FulfillmentInfrastructure(FulfillmentApplication model, FulfillmentHistoryPolicy policy)
    {
        this.policy = policy;
        application = model;
        Define(
            id: new("example/orders"),
            revision: new("4"),
            bindingProfileId: new("example/orders/bindings"),
            infra =>
            {
                infra.Resource(Store).Persistent()
                    .Prepared(model.Storage.RequireComposition<PostgresStorageComposition>().PrepareEvidence(model.Endpoints.Plan.QueryDependencies.Queries));
                infra.Workload(Worker).Requires(ApplicationExecution).RequiresReady(Store);
            });
    }

    /// <summary>Projects the same activated model and association into an offline inspection report.</summary>
    /// <param name="association">Association produced by Configure for this model.</param>
    /// <returns>Canonical artifacts plus the selected state/history policy.</returns>
    public Cohesive.Application.ApplicationExplanationDocument Explain(AspireInfrastructureAssociation association) =>
        Cohesive.Application.ApplicationExplanation.Create(application, Definition, association.Deployment,
            "development", SourceReference.Create("example", Definition.Definition.Definition.Id.Value));

    /// <summary>Starts with native resources, then associates only the resources governed by the requirements.</summary>
    /// <param name="builder">Existing AppHost builder.</param>
    /// <returns>Canonical validation and original native objects, without building or starting the AppHost.</returns>
    /// <exception cref="ArgumentNullException">Builder is null.</exception>
    /// <exception cref="ArgumentException">The model already contains conflicting resource names.</exception>
    public AspireInfrastructureAssociation Configure(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var postgres = builder.AddPostgres("postgres").WithDataVolume();
        var database = postgres.AddDatabase(FulfillmentStoragePolicy.OrdersDatabaseName, databaseName: policy.DatabaseName);
        var worker = builder.AddProject<Projects.OrderWorker>("worker")
            .WithHttpEndpoint(env: "ASPNETCORE_HTTP_PORTS")
            .WithReference(database).WaitFor(database)
            .WithEnvironment("EntityState__Authority", policy.Authority.ToString())
            .WithEnvironment("EntityState__Reconstitution", policy.Reconstitution.ToString());
        _ = builder.AddContainer("native-only", "redis", "7.4");
        if (builder.Configuration.GetValue<bool>("Shadow:Enabled"))
        {
            postgres.WithArgs("-c", "wal_level=logical", "-c", "max_replication_slots=16");
            var elastic = builder.AddContainer("order-shadow-elastic", "docker.elastic.co/elasticsearch/elasticsearch", "9.0.3")
                .WithEnvironment("discovery.type", "single-node")
                .WithEnvironment("xpack.security.enabled", "false")
                .WithEnvironment("ES_JAVA_OPTS", "-Xms512m -Xmx512m")
                .WithHttpEndpoint(targetPort: 9200, name: "http")
                .WithVolume("order-shadow-elastic", "/usr/share/elasticsearch/data");
            // A separate operator process owns shadow writes; the HTTP worker keeps its existing native readers.
            builder.AddProject<Projects.OrderMaterialization>("order-shadow")
                .WithReference(database).WaitFor(worker).WaitFor(elastic)
                .WithEnvironment("Shadow__Elasticsearch", elastic.GetEndpoint("http"))
                .WithEnvironment("Shadow__PositionKey", builder.Configuration["Shadow:PositionKey"])
                .WithEnvironment("Shadow__SlotGeneration", builder.Configuration["Shadow:SlotGeneration"])
                .WithEnvironment("EntityState__Authority", policy.Authority.ToString())
                .WithEnvironment("EntityState__Reconstitution", policy.Reconstitution.ToString())
                .WithArgs(builder.Configuration.GetSection("Shadow:Arguments").Get<string[]>() ?? ["--help"]);
        }


        // Associate existing Aspire resources with the canonical requirements and implementations.
        return builder.AttachCohesive(
            model: this,
            id: new("example/orders/local"),
            variant: new("development"),
            associations => associations
                .Resource(Store, database, authority: new("aspire/example/orders"), sources: [Source])
                .Workload(Worker, worker, sources: [Source])
            );
    }
}
