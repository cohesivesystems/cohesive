using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Cohesive.Adapters.Cosmos;
using Cohesive.Adapters.Postgres;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;

namespace Cohesive.Tests.Storage;

/// <summary>Canonical simplified freight relation used by the materialization integration harness.</summary>
public sealed class FreightOrderMaterializationRelationTests
{
    static readonly FieldPath TenantIdPath = FieldPath.FromField("tenantId");
    static readonly FieldPath CustomerAccountIdPath = FieldPath.FromField("customerAccountId");
    static readonly FieldPath PickupStopIdPath = FieldPath.FromField("pickupStopId");
    static readonly FieldPath DeliveryStopIdPath = FieldPath.FromField("deliveryStopId");
    static readonly FieldPath EquipmentTypePath = FieldPath.FromField("equipmentType");
    static readonly FieldPath CustomerRatePath = FieldPath.FromField("customerRate");
    static readonly FieldPath CustomerNamePath = FieldPath.FromField("name");
    static readonly FieldPath StopLocationIdPath = FieldPath.FromField("locationId");
    static readonly FieldPath LocationCityPath = FieldPath.FromField("city");
    static readonly FieldPath LocationStatePath = FieldPath.FromField("state");

    [Fact]
    public async Task SameCanonicalFreightRelationProducesEquivalentPostgresAndCosmosProjections()
    {
        var canonical = CreateCanonicalRelation();
        var postgres = CreatePhysicalScenario(
            canonical,
            "postgres",
            PostgresRelationQuerySourceTargetProfile.Default);
        var cosmos = CreatePhysicalScenario(
            canonical,
            "cosmos",
            CosmosRelationQuerySourceReader.TargetProfile);

        var postgresResult = await ExecuteAsync(canonical, postgres);
        var cosmosResult = await ExecuteAsync(canonical, cosmos);

        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(canonical.Plan)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(postgres.PhysicalPlan.Plan));
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(canonical.Plan)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(cosmos.PhysicalPlan.Plan));
        Assert.NotEqual(postgres.PhysicalPlan.Fingerprint, cosmos.PhysicalPlan.Fingerprint);
        Assert.Equal(postgresResult.Value, cosmosResult.Value);
        Assert.Equal("tenant-a", postgresResult.Value.GetProperty("tenantId").String);
        Assert.Equal("order-1", postgresResult.Value.GetProperty("orderId").String);
        Assert.Equal("Acme Foods", postgresResult.Value.GetProperty("customerName").String);
        Assert.Equal("Seattle", postgresResult.Value.GetProperty("originCity").String);
        Assert.Equal("Reefer", postgresResult.Value.GetProperty("equipmentType").String);
        Assert.Equal(2_450.50m, postgresResult.Value.GetProperty("customerRate").Decimal);
        Assert.All(postgres.Placement.Bindings, static binding => Assert.NotNull(binding.Partition));
        Assert.All(cosmos.Placement.Bindings, static binding => Assert.NotNull(binding.Partition));
        Assert.Single(postgres.Customers.Requests);
        Assert.Single(postgres.Stops.Requests);
        Assert.Single(postgres.Locations.Requests);
        Assert.Single(cosmos.Customers.Requests);
        Assert.Single(cosmos.Stops.Requests);
        Assert.Single(cosmos.Locations.Requests);
    }

    static CanonicalRelation CreateCanonicalRelation()
    {
        var author = RelationQuery.Expression();
        var orderShape = author.Clr.Shape<FreightOrder>();
        var customerShape = author.Clr.Shape<FreightCustomerAccount>();
        var stopShape = author.Clr.Shape<FreightOrderStop>();
        var locationShape = author.Clr.Shape<FreightLocation>();
        var orderCustomer = author.Relationship<FreightOrder, FreightCustomerAccount>(
            order => order.CustomerAccountId,
            new("FreightOrder.CustomerAccount"));
        var orderPickup = author.Relationship<FreightOrder, FreightOrderStop>(
            order => order.PickupStopId,
            new("FreightOrder.PickupStop"));
        var stopLocation = author.Relationship<FreightOrderStop, FreightLocation>(
            stop => stop.LocationId,
            new("FreightOrderStop.Location"));
        var orders = author.Source(orderShape, "materialization/freight/orders");
        var customers = author.Traverse(
            orders,
            orderCustomer,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization/freight/customers");
        var pickupStops = author.Traverse(
            customers,
            orders.Binding,
            orderPickup,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization/freight/pickup-stops");
        var pickupLocations = author.Traverse(
            pickupStops,
            stopLocation,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization/freight/pickup-locations");
        var projected = author.Project(
            pickupLocations,
            (FreightOrder order, FreightCustomerAccount customer, FreightLocation pickup) =>
                new FreightOrderSearchDocument
                {
                    TenantId = order.TenantId,
                    OrderId = order.Id,
                    CustomerName = customer.Name,
                    OriginCity = pickup.City,
                    OriginState = pickup.State,
                    EquipmentType = order.EquipmentType,
                    CustomerRate = order.CustomerRate
                },
            orders.Binding,
            customers.Binding,
            sourceReference: "materialization/freight/project-search-document");
        var relation = projected.BuildRelation(
            document => document.OrderId,
            id: new("freight-order-search"),
            name: new("FreightOrderSearch"),
            sourceReference: "materialization/freight/relation");
        if (!relation.Validation.IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                relation.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        var relationshipCatalog = author.CreateRelationshipCatalogDocument();
        var compilation = RelationQueryStaticCompiler.Compile(new(
            relation.CreateDocument(),
            author.ShapeDocuments,
            relationshipCatalog));
        if (!compilation.IsSuccessful || compilation.Plan is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(compilation.Plan);
        if (!realization.IsRealizable)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                realization.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }
        return new(
            compilation.Plan,
            realization,
            orderShape.Id,
            customerShape.Id,
            stopShape.Id,
            locationShape.Id);
    }

    static PhysicalScenario CreatePhysicalScenario(
        CanonicalRelation canonical,
        string adapter,
        RelationQueryTargetCapabilityProfile profile)
    {
        var customerSourceId = new RelationQuerySourceInstanceId($"{adapter}/freight/customers");
        var stopSourceId = new RelationQuerySourceInstanceId($"{adapter}/freight/stops");
        var locationSourceId = new RelationQuerySourceInstanceId($"{adapter}/freight/locations");
        var bindings = ImmutableArray.CreateBuilder<RelationQuerySourcePlacementBinding>();
        foreach (var source in canonical.Plan.InputContract.Sources)
        {
            bindings.Add(new(
                new($"{adapter}/placement/{Uri.EscapeDataString(source.Input.Id.Value)}"),
                source.Input.Id,
                source.Node,
                source.Binding,
                source.Shape,
                new($"{adapter}/freight/orders"),
                RelationQuerySourcePlacementBindingKind.SourceSet,
                RelationQuerySourceAcquisitionKind.Supplied,
                RelationQuerySourcePlacementOrigin.Explicit,
                identity: null,
                fields: Fields(source.Fields),
                partition: new("tenantId")));
        }
        foreach (var traversal in canonical.Plan.InputContract.Traversals)
        {
            bindings.Add(new(
                new($"{adapter}/placement/{Uri.EscapeDataString(traversal.Input.Id.Value)}"),
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                traversal.ResultShape == canonical.CustomerShape
                    ? customerSourceId
                    : traversal.ResultShape == canonical.StopShape
                        ? stopSourceId
                        : locationSourceId,
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, "id"),
                Fields(traversal.Fields),
                relationshipKeys: [],
                partition: new("tenantId")));
        }
        var limits = new RelationQuerySourcePlacementLimits(
            maximumBatchSize: 20,
            maximumBufferedRows: 100,
            maximumFanOut: 20,
            maximumConcurrency: 4);
        var executionDomain = new RelationQueryExecutionDomainId($"{adapter}/freight/domain");
        var sources = ImmutableArray.Create(
            new RelationQuerySourceInstance(customerSourceId, executionDomain, profile, limits),
            new RelationQuerySourceInstance(stopSourceId, executionDomain, profile, limits),
            new RelationQuerySourceInstance(locationSourceId, executionDomain, profile, limits),
            new RelationQuerySourceInstance(
                new($"{adapter}/freight/orders"),
                executionDomain,
                profile,
                limits));
        var placement = new RelationQuerySourcePlacement(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(canonical.Plan),
            $"tests/materialization/freight/{adapter}/placement/v1",
            sources,
            bindings.ToImmutable());
        var policy = new RelationQueryPhysicalPlanningPolicy(
            new($"tests/materialization/freight/{adapter}/policy/v1"),
            $"tests/materialization/freight/{adapter}/conventions/v1",
            maximumBatchSize: 20,
            maximumBufferedRows: 100,
            maximumLocalRows: 100,
            maximumFanOut: 20,
            maximumReferenceKeysPerObservation: 20,
            maximumConcurrency: 4);
        var physical = RelationQueryPhysicalPlanner.Compile(
            canonical.Plan,
            canonical.Realization,
            placement,
            policy);
        if (!physical.IsSuccessful || physical.Plan is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                physical.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        }

        var scope = new RelationQuerySourceReaderPartitionScope("tenantId", "tests/freight/tenant-a");
        var customers = Reader(
            sources.Single(source => source.Id == customerSourceId),
            scope,
            [
                Row(
                    "customer-1",
                    (TenantIdPath, ObservationValue.FromString("tenant-a")),
                    (CustomerNamePath, ObservationValue.FromString("Acme Foods")))
            ]);
        var stops = Reader(
            sources.Single(source => source.Id == stopSourceId),
            scope,
            [
                Row(
                    "stop-delivery-1",
                    (TenantIdPath, ObservationValue.FromString("tenant-a")),
                    (StopLocationIdPath, ObservationValue.FromString("location-destination"))),
                Row(
                    "stop-pickup-1",
                    (TenantIdPath, ObservationValue.FromString("tenant-a")),
                    (StopLocationIdPath, ObservationValue.FromString("location-origin")))
            ]);
        var locations = Reader(
            sources.Single(source => source.Id == locationSourceId),
            scope,
            [
                Row(
                    "location-destination",
                    (TenantIdPath, ObservationValue.FromString("tenant-a")),
                    (LocationCityPath, ObservationValue.FromString("Portland")),
                    (LocationStatePath, ObservationValue.FromString("OR"))),
                Row(
                    "location-origin",
                    (TenantIdPath, ObservationValue.FromString("tenant-a")),
                    (LocationCityPath, ObservationValue.FromString("Seattle")),
                    (LocationStatePath, ObservationValue.FromString("WA")))
            ]);
        return new(
            placement,
            physical.Plan,
            customers,
            stops,
            locations,
            SuppliedOrder(canonical.Plan, placement));

        static ImmutableArray<RelationQuerySourceFieldBinding> Fields(
            ImmutableArray<RelationQueryFieldInputContract> fields) =>
        [
            .. fields.Select(static field => new RelationQuerySourceFieldBinding(
                field.Input.Id,
                field.Input.Field.Path,
                field.Input.Field.Path.ToString()))
        ];
    }

    static async ValueTask<RelationQueryOutputRow> ExecuteAsync(
        CanonicalRelation canonical,
        PhysicalScenario scenario)
    {
        var result = await new RelationQueryPhysicalExecutor(
                [scenario.Customers, scenario.Stops, scenario.Locations])
            .ExecuteAsync(new(
                canonical.Plan,
                scenario.PhysicalPlan,
                canonical.Realization,
                new($"tests/freight/{scenario.PhysicalPlan.Fingerprint.Value}"),
                suppliedSources: [scenario.SuppliedOrder],
                capabilities:
                [
                    .. canonical.Plan.RequirementGraph.Inputs
                        .OfType<RelationQueryCapabilityInput>()
                        .Select(static input => new RelationQueryCapabilityEvidence(
                            input.Id,
                            RelationQueryCapabilityEvidenceState.Available,
                            "tests/freight/capability"))
                ]));
        Assert.Equal(RelationQueryExecutionStatus.Succeeded, result.Status);
        Assert.Empty(result.Diagnostics);
        return Assert.Single(Assert.IsType<RelationQueryExecutionResult>(result.Interpretation).Relation!.Rows);
    }

    static DeterministicReader Reader(
        RelationQuerySourceInstance source,
        RelationQuerySourceReaderPartitionScope partitionScope,
        ImmutableArray<SourceRow> rows) => new(
        new(source.Id, source.ExecutionDomain, source.TargetProfile, partitionScope),
        rows);

    static RelationQuerySuppliedSourceInput SuppliedOrder(
        CompiledRelationQueryPlan plan,
        RelationQuerySourcePlacement placement)
    {
        var source = plan.InputContract.Sources.Single();
        var binding = placement.Bindings.Single(candidate => candidate.Input == source.Input.Id);
        var row = Row(
            "order-1",
            (TenantIdPath, ObservationValue.FromString("tenant-a")),
            (FieldPath.FromField("id"), ObservationValue.FromString("order-1")),
            (CustomerAccountIdPath, ObservationValue.FromString("customer-1")),
            (PickupStopIdPath, ObservationValue.FromString("stop-pickup-1")),
            (DeliveryStopIdPath, ObservationValue.FromString("stop-delivery-1")),
            (EquipmentTypePath, ObservationValue.FromString("Reefer")),
            (CustomerRatePath, ObservationValue.FromDecimal(2_450.50m)));
        return new(
            source.Input.Id,
            RelationQueryEvidenceCompleteness.Complete,
            [
                new RelationQuerySourceReadObservation(
                    row.Identity,
                    source.Shape,
                    [
                        .. source.Fields.Select(field => new RelationQuerySourceReadFieldResult(
                            new(
                                field.Input.Id,
                                field.Input.Field.Path,
                                binding.Fields.Single(candidate => candidate.Input == field.Input.Id).SourceSelector,
                                RelationQuerySourceReadFieldPurpose.SemanticInput),
                            RelationQuerySourceReadFieldState.Value,
                            row.Fields[field.Input.Field.Path]))
                    ])
            ],
            "tests/freight/supplied-order");
    }

    static SourceRow Row(
        string identity,
        params (FieldPath Path, ObservationValue Value)[] fields) => new(
        identity,
        fields.ToImmutableDictionary(
            static field => field.Path,
            static field => field.Value));

    sealed class DeterministicReader : IRelationQuerySourceReader
    {
        readonly ImmutableDictionary<string, SourceRow> rows;
        readonly List<RelationQuerySourceReadRequest> requests = [];

        public DeterministicReader(
            RelationQuerySourceReaderDescriptor descriptor,
            ImmutableArray<SourceRow> rows)
        {
            Descriptor = descriptor;
            this.rows = rows.ToImmutableDictionary(static row => row.Identity, StringComparer.Ordinal);
        }

        public RelationQuerySourceReaderDescriptor Descriptor { get; }

        public ImmutableArray<RelationQuerySourceReadRequest> Requests => [.. requests];

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requests.Add(request);
            var identities = Assert.IsType<RelationQueryIdentityBatchLookup>(request.Constraint).Identities;
            var observations = identities
                .Where(rows.ContainsKey)
                .Select(identity => rows[identity].Project(request))
                .ToImmutableArray();
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                observations.IsEmpty
                    ? RelationQuerySourceReadState.NotFound
                    : RelationQuerySourceReadState.Complete,
                observations,
                $"tests/freight/{Descriptor.Source.Value}"));
        }
    }

    sealed record SourceRow(string Identity, ImmutableDictionary<FieldPath, ObservationValue> Fields)
    {
        public RelationQuerySourceReadObservation Project(RelationQuerySourceReadRequest request) => new(
            Identity,
            request.Shape,
            [
                .. request.Fields.Select(field => Fields.TryGetValue(field.SemanticPath, out var value)
                    ? new RelationQuerySourceReadFieldResult(
                        field,
                        RelationQuerySourceReadFieldState.Value,
                        value)
                    : new RelationQuerySourceReadFieldResult(
                        field,
                        RelationQuerySourceReadFieldState.Missing))
            ]);
    }

    sealed record CanonicalRelation(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        QualifiedShapeId OrderShape,
        QualifiedShapeId CustomerShape,
        QualifiedShapeId StopShape,
        QualifiedShapeId LocationShape);

    sealed record PhysicalScenario(
        RelationQuerySourcePlacement Placement,
        CompiledRelationQueryPhysicalPlan PhysicalPlan,
        DeterministicReader Customers,
        DeterministicReader Stops,
        DeterministicReader Locations,
        RelationQuerySuppliedSourceInput SuppliedOrder);

    sealed record FreightOrder
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("customerAccountId")]
        public string CustomerAccountId { get; init; } = string.Empty;

        [JsonPropertyName("pickupStopId")]
        public string PickupStopId { get; init; } = string.Empty;

        [JsonPropertyName("deliveryStopId")]
        public string DeliveryStopId { get; init; } = string.Empty;

        [JsonPropertyName("equipmentType")]
        public string EquipmentType { get; init; } = string.Empty;

        [JsonPropertyName("customerRate")]
        public decimal CustomerRate { get; init; }
    }

    sealed record FreightCustomerAccount
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;
    }

    sealed record FreightOrderStop
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("locationId")]
        public string LocationId { get; init; } = string.Empty;
    }

    sealed record FreightLocation
    {
        [JsonPropertyName("id")]
        public string Id { get; init; } = string.Empty;

        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("city")]
        public string City { get; init; } = string.Empty;

        [JsonPropertyName("state")]
        public string State { get; init; } = string.Empty;
    }

    sealed record FreightOrderSearchDocument
    {
        [JsonPropertyName("tenantId")]
        public string TenantId { get; init; } = string.Empty;

        [JsonPropertyName("orderId")]
        public string OrderId { get; init; } = string.Empty;

        [JsonPropertyName("customerName")]
        public string CustomerName { get; init; } = string.Empty;

        [JsonPropertyName("originCity")]
        public string OriginCity { get; init; } = string.Empty;

        [JsonPropertyName("originState")]
        public string OriginState { get; init; } = string.Empty;

        [JsonPropertyName("equipmentType")]
        public string EquipmentType { get; init; } = string.Empty;

        [JsonPropertyName("customerRate")]
        public decimal CustomerRate { get; init; }
    }
}
