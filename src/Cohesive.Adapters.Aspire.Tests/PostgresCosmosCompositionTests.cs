using Cohesive.Adapters.Postgres.Services;
using Cohesive.Relations.Execution;
using Aspire.Hosting;
using AspireFirst.Orders;
using AspireFirst.PostgresCosmos;
using Cohesive.Host.Composition;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Api.Execution.Services;
using Cohesive.Relations.Compilation;
using Microsoft.Azure.Cosmos;
using Npgsql;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class PostgresCosmosCompositionTests
{
    internal const string EmulatorKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static CosmosClient Client() => new("http://127.0.0.1:1", EmulatorKey, new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });

    [Fact]
    public void Both_compositions_share_semantic_contracts_but_only_one_places_inventory_in_Postgres()
    {
        var single = ModelActivator.Create<FulfillmentApplication>();
        var mixed = ModelActivator.Create<PostgresCosmosApplication>();
        Assert.Equal(single.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint,
            mixed.Queries.OrderDetails.CompilationRequest.DefinitionDocument.DefinitionFingerprint);
        Assert.Equal(single.Api.Definition.Endpoints.Select(e => e.Id), mixed.Api.Definition.Endpoints.Select(e => e.Id));
        Assert.Throws<ArgumentException>(() => mixed.Storage.PostgresBindings.Mapping(mixed.Domain.Inventory));
        Assert.NotNull(single.Storage.Bindings.Mapping(single.Domain.Inventory));
        Assert.Single(single.Storage.Definition);
        Assert.Equal(3, mixed.Storage.Definition.Length);
        Assert.Same(mixed.Storage.PostgresBindings, mixed.Storage.Definition[0]);
        Assert.Same(mixed.Storage.Inventory, mixed.Storage.Definition[1]);
        Assert.Same(mixed.Domain, mixed.Composition.Get<FulfillmentDomain>());
        Assert.Same(mixed.Process, mixed.Api.Fulfill.Process);
        Assert.Same(mixed.Process, mixed.Composition.Get<FulfillmentProcess>());
        Assert.Equal(single.Process.Definition.Reference, mixed.Process.Definition.Reference);
        Assert.Throws<KeyNotFoundException>(() => mixed.Composition.Get<OrderTransitions>());
        Assert.Throws<KeyNotFoundException>(() => mixed.Composition.Get<InventoryTransitions>());
    }

    [Fact]
    public void Both_shared_queries_prepare_against_Cosmos_without_IO_and_wrong_partition_is_rejected()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        using var client = Client();
        var inventory = client.GetContainer(PostgresCosmosStorage.InventoryDatabase, PostgresCosmosStorage.InventoryContainer);
        var source = CosmosEntityRelationQuerySourceRegistration.Create(model.Domain.Inventory.Definition.StateShape.QualifiedId,
            inventory, inventory.Database.Id, inventory.Id, model.Storage.Inventory.Query.Policy,
            identitySemanticPath: FieldPath.FromField(FulfillmentDomain.InventoryIdField));
        var catalog = new EntityRelationQuerySourceCatalog([source]);
        var orders = model.Storage.PostgresBindings.Bind(database, authority: "test");
        Assert.Same(model.Queries.OrderDetails, orders.QueryComposed(model.Queries.OrderDetails, catalog, PostgresCosmosStorage.Policy(1000, 1_000_000)).Definition);
        var inferred = Assert.IsType<Cohesive.Relations.Execution.RelationQuerySubplanReader<string, ReservationAvailability[]>>(
            orders.QueryComposed(model.Queries.ReservationAvailability, catalog, PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Same(model.Queries.ReservationAvailability, inferred.Definition);
        var explicitReader = Assert.IsType<Cohesive.Relations.Execution.RelationQuerySubplanReader<string, ReservationAvailability[]>>(
            orders.QueryComposed(model.Queries.ReservationAvailability, catalog, PostgresCosmosStorage.Policy(1000, 1_000_000),
                inferred.Plan.Cut.Id));
        Assert.Equal(explicitReader.Plan.Prefix.Request.DefinitionDocument.DefinitionFingerprint, inferred.Plan.Prefix.Request.DefinitionDocument.DefinitionFingerprint);
        Assert.Single(inferred.Plan.Prefix.Plan.Definition.Body.Nodes.OfType<Cohesive.Relations.IR.TraverseRelationshipQueryNode>());
        Assert.Empty(inferred.Plan.Remainder.Plan!.Definition.Body.Nodes.OfType<Cohesive.Relations.IR.TraverseRelationshipQueryNode>());
        var native = orders.Prepare(inferred.Plan.Prefix.Request, 1000, 1_000_000);
        Assert.Contains("LEFT JOIN", native.Artifact.Statement.Text);
        Assert.DoesNotContain("cohesive_inventory", native.Artifact.Statement.Text);
        var terminal = ((Cohesive.Relations.IR.QueryDefinition)model.Queries.ReservationAvailability.CompilationRequest.DefinitionDocument.Definition).Results[0].Input;
        Assert.Equal("relationQuery.subplan.cutInvalid", Assert.Throws<RelationQueryPreparationException>(() =>
            orders.QueryComposed(model.Queries.ReservationAvailability, catalog, PostgresCosmosStorage.Policy(1000, 1_000_000), terminal)).Code);
        var missing = Assert.Throws<RelationQueryPreparationException>(() => orders.QueryComposed(model.Queries.OrderDetails,
            new EntityRelationQuerySourceCatalog([]), PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.remoteMappingMissing", missing.Code);
        var full = new FulfillmentStorage(model.Domain, new FulfillmentPostgresMappings(model.Domain)).Bindings.Bind(database, authority: "test/fulfillment");
        var ambiguous = Assert.Throws<RelationQueryPreparationException>(() => full.QueryComposed(model.Queries.OrderDetails,
            catalog, PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.placementAmbiguous", ambiguous.Code);
        var wrong = CosmosEntityRelationQuerySourceRegistration.Create(model.Domain.Inventory.Definition.StateShape.QualifiedId,
            inventory, inventory.Database.Id, inventory.Id, new("partitionKey", new("other"), fixedPartitionKey: new PartitionKey("other")));
        var error = Assert.Throws<RelationQueryPreparationException>(() => orders.QueryComposed(model.Queries.OrderDetails,
            new EntityRelationQuerySourceCatalog([wrong]), PostgresCosmosStorage.Policy(1000, 1_000_000)));
        Assert.Equal("postgres.composed.partitionMismatch", error.Code);
    }

    [Fact]
    public void Separate_AppHost_authors_native_Cosmos_and_Postgres_without_starting_resources()
    {
        var model = ModelActivator.Create<PostgresCosmosFulfillmentInfrastructure>();
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions { Args = [], DisableDashboard = true });
        var association = model.Configure(builder);
        Assert.True(association.Deployment.IsComplete);
        Assert.Equal(3, association.Resources.Count);
        Assert.Same(builder.Resources.Single(resource => resource.Name == "worker"),
            association.Resources[PostgresCosmosFulfillmentInfrastructure.Worker]);
        Assert.Equal(2, model.Definition.ResourcePreparations.Count);
        Assert.Equal(2, model.Definition.Definition.Definition.Resources.Length);
        Assert.Contains(builder.Resources, resource => resource.Name == PostgresCosmosStorage.CosmosAccount);
        Assert.Contains(builder.Resources, resource => resource.Name == FulfillmentStoragePolicy.OrdersDatabaseName);
        using var app = builder.Build(); // Build the native model only; do not start either database.
    }

    [Fact]
    public void Mixed_evidence_comes_from_offline_sources_and_never_advertises_a_shared_snapshot()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        var source = model.Storage.Inventory.Query.Binding;
        var offline = new EntityRelationQuerySourceCatalog([source]);
        Assert.Throws<InvalidOperationException>(() => offline.SourceReaders);
        Assert.All(model.Endpoints.Plan.QueryDependencies.Queries,
            query => Assert.Equal(RelationQueryConsistency.IndependentSources, query.Consistency));
        var prepared = model.Storage.Preparation;
        Assert.Equal(2, prepared.ExecutionEvidence.Length);
        Assert.Equal(2, prepared.Postgres.Requirements.Length);
        var remote = Assert.Single(prepared.Sources).Value;
        Assert.Equal(2, remote.Requirements.Length);
        Assert.All(prepared.Requirements.Concat(prepared.Postgres.Requirements).Concat(remote.Requirements),
            capability => Assert.DoesNotContain("snapshot", capability.Value, StringComparison.OrdinalIgnoreCase));
        Assert.All(prepared.ExecutionEvidence, evidence => Assert.Contains(evidence.SourceReferences,
            reference => reference.ToString().Contains("relations-physical-plan", StringComparison.Ordinal)));
        Assert.Throws<ServiceBindingValidationException>(() => model.Storage.PostgresBindings.PrepareComposedService(
            model.Endpoints.Plan, offline, (_, _) => PostgresCosmosStorage.Policy(999, 1_000_000)));
        Assert.Throws<ArgumentException>(() => prepared.Postgres.ForSource("another-database"));
        Assert.Throws<ArgumentException>(() => remote.ForSource("another-source"));
    }

    [Fact]
    public void Single_snapshot_is_rejected_during_mixed_infrastructure_preparation_without_clients()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        var plan = new ServiceImplementationPlan();
        plan.Declare(model.Api.Definition);
        foreach (var endpoint in model.Api.Definition.Endpoints) plan.Implement(model.Api.Definition, endpoint);
        plan.Query(model.Queries.OrderDetails, new("1"), 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot);
        var error = Assert.Throws<ServiceBindingValidationException>(() => model.Storage.PostgresBindings.PrepareComposedService(
            plan, new([model.Storage.Inventory.Query.Binding]), PostgresCosmosStorage.Policy));
        Assert.Contains(error.Validation.Diagnostics, diagnostic => diagnostic.Code == ServiceBindingDiagnosticCodes.QueryPlacementUnsupported);
    }

    [Fact]
    public void Offline_preparation_binds_exact_native_sources_and_rejects_stronger_runtime_requirement()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        using var db = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        using var client = Client();
        var container = client.GetContainer(PostgresCosmosStorage.InventoryDatabase, PostgresCosmosStorage.InventoryContainer);
        var registration = model.Storage.Inventory.Query.Bind(container);
        var catalog = new EntityRelationQuerySourceCatalog([registration]);
        var postgres = model.Storage.PostgresBindings.Bind(db, "test/composed");
        var reader = model.Storage.Preparation.Bind(postgres, model.Queries.ReservationAvailability, catalog, 1000, 1_000_000);
        Assert.Equal(RelationQueryConsistency.IndependentSources, reader.Consistency);
        Assert.Throws<ServiceBindingValidationException>(() => ServiceQueryConsistencyValidation.Require(reader, RelationQueryConsistency.SingleSnapshot));
        Assert.Throws<ServiceBindingValidationException>(() => model.Storage.Preparation.Bind(postgres, model.Queries.ReservationAvailability, catalog, 999, 1_000_000));
        Assert.Throws<ArgumentException>(() => model.Storage.Inventory.Query.Bind(client.GetContainer("wrong", "stock")));
        var changedSelectors = new EntityRelationQuerySourceRegistration(registration.Shape, registration.Source, registration.Reader,
            registration.IdentitySourceSelector, registration.IdentitySemanticPath, _ => "observation.wrong");
        Assert.Throws<ArgumentException>(() => model.Storage.Preparation.Bind(postgres, model.Queries.ReservationAvailability,
            new([changedSelectors]), 1000, 1_000_000));
        var wrong = CosmosEntityRelationQuerySourceRegistration.Create(model.Domain.Inventory.Definition.StateShape.QualifiedId,
            container, container.Database.Id, container.Id, model.Storage.Inventory.Query.Policy);
        Assert.Throws<ArgumentException>(() => model.Storage.Preparation.Bind(postgres, model.Queries.ReservationAvailability,
            new([wrong]), 1000, 1_000_000));
    }

    [Fact]
    public void Declared_storage_resolves_selected_queries_once_without_repeating_registrations()
    {
        var model = ModelActivator.Create<PostgresCosmosApplication>();
        using var db = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        using var client = Client();
        var remote = model.Storage.Inventory.Bind(client.GetContainer(PostgresCosmosStorage.InventoryDatabase, PostgresCosmosStorage.InventoryContainer));
        var postgres = model.Storage.PostgresBindings.Bind(db, "test/composed");
        var persistence = model.Storage.Preparation.Bind(postgres, sources: [remote]);
        Assert.Same(remote.Repository, persistence.Repository(ValueContract.FromShape(model.Domain.Inventory.Definition.Shape)));
        Assert.Same(model.Domain.Orders.Definition, persistence.Repository(ValueContract.FromShape(model.Domain.Orders.Definition.Shape)).EntityDefinition);
        var details = persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.IndependentSources);
        var availability = persistence.Query(model.Queries.ReservationAvailability, 1000, 1_000_000, RelationQueryConsistency.IndependentSources);
        Assert.Same(details, persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.IndependentSources));
        Assert.Same(availability, persistence.Query(model.Queries.ReservationAvailability, 1000, 1_000_000, RelationQueryConsistency.IndependentSources));
        Assert.Equal(RelationQueryConsistency.IndependentSources, details.Consistency);
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(model.Queries.OrderDetails, 999, 1_000_000, RelationQueryConsistency.IndependentSources));
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(model.Queries.OrderDetails, 1000, 1_000_001, RelationQueryConsistency.IndependentSources));
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot));
        var foreign = ModelActivator.Create<PostgresCosmosApplication>();
        Assert.Throws<ArgumentException>(() => model.Storage.Preparation.Bind(foreign.Storage.PostgresBindings.Bind(db, "foreign"), sources: [remote]));
        Assert.Throws<ServiceBindingValidationException>(() => persistence.Query(foreign.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.IndependentSources));
        var missing = model.Storage.Preparation.Bind(postgres);
        Assert.Throws<ArgumentException>(() => missing.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.IndependentSources));
        Assert.Throws<ArgumentException>(() => model.Storage.Preparation.Bind(postgres, sources: [remote, remote]));
    }

    [Fact]
    public void Explicit_service_query_selection_is_cached_and_frozen()
    {
        var model = ModelActivator.Create<FulfillmentApplication>();
        using var database = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=orders;Username=test");
        var postgres = model.Storage.Bindings.Bind(database, authority: "test/fulfillment");
        var calls = 0;
        var builder = new ServicePersistenceBuilder().Query(model.Queries.OrderDetails, (rows, bytes) =>
        { calls++; return postgres.Query(model.Queries.OrderDetails, rows, bytes); });
        var resolver = builder.Build();
        var reader = resolver.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot);
        Assert.Same(reader, resolver.Query(model.Queries.OrderDetails, 1000, 1_000_000, RelationQueryConsistency.SingleSnapshot));
        Assert.Equal(1, calls);
        Assert.Throws<InvalidOperationException>(() => builder.Query(model.Queries.OrderDetails, (r, b) => reader));
    }
}
