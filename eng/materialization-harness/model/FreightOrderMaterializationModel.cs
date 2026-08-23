using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Model;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;
using Cohesive.Storage.Realization;
using Cohesive.Transitions.Model;

namespace Cohesive.MaterializationHarness.Model;

/// <summary>The canonical simplified freight semantics exercised by the real-container harness.</summary>
public static class FreightOrderMaterializationModel
{
    const int MaximumReadItems = 64;
    const long MaximumReadBytes = 1 * 1024 * 1024;
    const int MaximumWriteItems = 16;
    const long MaximumWriteBytes = 1 * 1024 * 1024;

    /// <summary>Stable graph identity for every harness freight shape.</summary>
    public static GraphId GraphId { get; } = new("cohesive.materialization-harness.freight");

    /// <summary>Canonical Order shape identity.</summary>
    public static QualifiedShapeId OrderShapeId { get; } = Shape("order");

    /// <summary>Canonical CustomerAccount shape identity.</summary>
    public static QualifiedShapeId CustomerAccountShapeId { get; } = Shape("customer-account");

    /// <summary>Canonical OrderStop shape identity.</summary>
    public static QualifiedShapeId OrderStopShapeId { get; } = Shape("order-stop");

    /// <summary>Canonical Location shape identity.</summary>
    public static QualifiedShapeId LocationShapeId { get; } = Shape("location");

    /// <summary>Canonical Elasticsearch value shape identity.</summary>
    public static QualifiedShapeId OrderSearchDocumentShapeId { get; } = Shape("order-search-document");

    /// <summary>Creates the immutable compiled relation and materialization definition used by every provider.</summary>
    /// <returns>A complete canonical semantic fixture.</returns>
    /// <exception cref="InvalidOperationException">Authoring, compilation, realization, or validation fails.</exception>
    public static FreightOrderMaterializationSemantics Create()
    {
        var author = RelationQuery.Expression();
        var stopShape = author.Clr.Shape<FreightOrderStop>(OrderStopShapeId, ShapeRoles.ValueObject);
        var orderShape = author.Clr.Shape<FreightOrder>(OrderShapeId);
        var customerShape = author.Clr.Shape<FreightCustomerAccount>(CustomerAccountShapeId);
        var locationShape = author.Clr.Shape<FreightLocation>(LocationShapeId);
        var searchShape = author.Clr.Shape<FreightOrderSearchDocument>(OrderSearchDocumentShapeId, ShapeRoles.Projection);

        var orderCustomer = author.Relationship<FreightOrder, FreightCustomerAccount>(
            order => order.CustomerAccountId,
            new("freight-order.customer-account"));
        var stopLocation = author.Relationship<FreightOrderStop, FreightLocation>(
            stop => stop.LocationId,
            new("freight-order-stop.location"));
        var orders = author.Source(orderShape, "materialization-harness/freight/orders");
        var customers = author.Traverse(
            orders,
            orderCustomer,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/customer");
        var pickupStops = author.Expand(
            customers.Node,
            (FreightOrder order) => order.Stops,
            orders.Binding,
            sourceReference: "materialization-harness/freight/pickup-stops");
        var pickupCandidates = author.Filter(
            pickupStops.Node,
            (FreightOrderStop stop) => stop.StopType == "Pickup",
            pickupStops.Binding,
            sourceReference: "materialization-harness/freight/pickup-filter");
        var orderedPickupCandidates = author.Order(
            pickupCandidates,
            [
                author.Ordering(
                    (FreightOrder order) => order.Id,
                    orders.Binding,
                    sourceReference: "materialization-harness/freight/pickup-order/order"),
                author.Ordering(
                    (FreightOrderStop stop) => stop.Sequence,
                    pickupStops.Binding,
                    sourceReference: "materialization-harness/freight/pickup-order/sequence"),
                author.Ordering(
                    (FreightOrderStop stop) => stop.Id,
                    pickupStops.Binding,
                    sourceReference: "materialization-harness/freight/pickup-order/id")
            ],
            sourceReference: "materialization-harness/freight/pickup-order");
        var selectedPickupStops = author.Distinct(
            orderedPickupCandidates,
            (FreightOrder order) => order.Id,
            orders.Binding,
            sourceReference: "materialization-harness/freight/pickup-per-order");
        var originLocations = author.Traverse(
            selectedPickupStops,
            pickupStops.Binding,
            stopLocation,
            joinKind: JoinKind.Inner,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/pickup-location");

        var deliveryStops = author.Expand(
            originLocations.Node,
            (FreightOrder order) => order.Stops,
            orders.Binding,
            sourceReference: "materialization-harness/freight/delivery-stops");
        var deliveryCandidates = author.Filter(
            deliveryStops.Node,
            (FreightOrderStop stop) => stop.StopType == "Drop",
            deliveryStops.Binding,
            sourceReference: "materialization-harness/freight/delivery-filter");
        var orderedDeliveryCandidates = author.Order(
            deliveryCandidates,
            [
                author.Ordering(
                    (FreightOrder order) => order.Id,
                    orders.Binding,
                    sourceReference: "materialization-harness/freight/delivery-order/order"),
                author.Ordering(
                    (FreightOrderStop stop) => stop.Sequence,
                    deliveryStops.Binding,
                    direction: QuerySortDirection.Descending,
                    sourceReference: "materialization-harness/freight/delivery-order/sequence"),
                author.Ordering(
                    (FreightOrderStop stop) => stop.Id,
                    deliveryStops.Binding,
                    direction: QuerySortDirection.Descending,
                    sourceReference: "materialization-harness/freight/delivery-order/id")
            ],
            sourceReference: "materialization-harness/freight/delivery-order");
        var selectedDeliveryStops = author.Distinct(
            orderedDeliveryCandidates,
            (FreightOrder order) => order.Id,
            orders.Binding,
            sourceReference: "materialization-harness/freight/delivery-per-order");
        var destinationLocations = author.Traverse(
            selectedDeliveryStops,
            deliveryStops.Binding,
            stopLocation,
            joinKind: JoinKind.Inner,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/delivery-location");
        Expression<Func<
            FreightOrder,
            FreightCustomerAccount,
            FreightOrderStop,
            FreightLocation,
            FreightOrderStop,
            FreightLocation,
            FreightOrderSearchDocument>> projection =
            (
                FreightOrder order,
                FreightCustomerAccount customer,
                FreightOrderStop pickup,
                FreightLocation origin,
                FreightOrderStop delivery,
                FreightLocation destination) =>
                new FreightOrderSearchDocument
                {
                    Id = order.Id,
                    TenantId = order.TenantId,
                    OrderId = order.Id,
                    OrderNumber = order.OrderNumber,
                    CustomerName = customer.DisplayName,
                    EquipmentClass = order.EquipmentClass,
                    OriginStopId = pickup.Id,
                    OriginCity = origin.City,
                    OriginRegion = origin.Region,
                    DestinationStopId = delivery.Id,
                    DestinationCity = destination.City,
                    DestinationRegion = destination.Region
                };
        var projected = author.Project(
            destinationLocations.Node,
            searchShape,
            projection,
            [
                orders.Binding,
                customers.Binding,
                pickupStops.Binding,
                originLocations.Binding,
                deliveryStops.Binding,
                destinationLocations.Binding
            ],
            sourceReference: "materialization-harness/freight/order-search-document");
        var authored = author.BuildRelation(
            orders,
            projected,
            document => document.Id,
            id: new("freight-order-search"),
            name: new("FreightOrderSearch"),
            sourceReference: "materialization-harness/freight/relation");
        Require(authored.Validation.IsValid, authored.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message));

        var compilationRequest = new RelationQueryCompilationRequest(
            authored.CreateDocument(),
            author.ShapeDocuments,
            author.CreateRelationshipCatalogDocument());
        var compilation = RelationQueryStaticCompiler.Compile(compilationRequest);
        Require(compilation.IsSuccessful && compilation.Plan is not null,
            compilation.Diagnostics.Select(static diagnostic => diagnostic.Message));
        var plan = compilation.Plan!;
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        Require(realization.IsRealizable, realization.Diagnostics.Select(static diagnostic => diagnostic.Message));
        var root = plan.InputContract.Sources.Single(static source =>
            source.Role == RelationQuerySourceInputRole.RelationRoot);
        var output = plan.RequirementGraph.Outputs.Single(static candidate => candidate.Field is null);
        var rebuildTargetBatchControl = TargetBatchControl(MaterializationIndexSyncWorkloadKind.Rebuild);
        var realtimeTargetBatchControl = TargetBatchControl(MaterializationIndexSyncWorkloadKind.Realtime);
        var materialization = Materialization.Define(
                new("freight/order-search"),
                compilationRequest,
                output.Id)
            .WithUpdatePolicy(new(
                MaterializationSynchronizationMode.All,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion))
            .WithBoundedRelationBaselineCatchUpSources(
                maximumReadItems: MaximumReadItems,
                maximumReadBytes: MaximumReadBytes,
                maximumChangeItems: MaximumReadItems,
                maximumChangeBytes: MaximumReadBytes)
            .WithGenerationalIndexTarget(
                maximumItems: MaximumWriteItems,
                maximumBytes: MaximumWriteBytes)
            .WithFailurePolicy(new(
                maximumAttempts: 3,
                exhaustedDisposition: MaterializationFailureDisposition.Stop))
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 1_800_000))
            .WithControls(
                loops: [rebuildTargetBatchControl, realtimeTargetBatchControl],
                workloads:
                [
                    new(
                        loopId: rebuildTargetBatchControl.Id,
                        workload: MaterializationIndexSyncWorkloadKind.Rebuild),
                    new(
                        loopId: realtimeTargetBatchControl.Id,
                        workload: MaterializationIndexSyncWorkloadKind.Realtime)
                ])
            .WithProvenance(new(
                new("cohesive-materialization-harness", "1"),
                new("eng/materialization-harness/model/freight-order-search"),
                DocumentOrigin.Generated))
            .Build();
        Require(
            materialization.Validation.IsValid,
            materialization.Validation.Diagnostics.Select(static diagnostic => diagnostic.Message));
        var document = materialization.CreateDocument();
        var definition = document.Definition;
        var canonicalOrderShape = orderShape.Document.Graph.TryGetShape(orderShape.Id)
            ?? throw new InvalidOperationException("Canonical Order shape is missing.");
        var stopsField = canonicalOrderShape.Fields.Single(static field => field.Name.Value == "stops");
        var stopType = stopsField.Type as NamedTypeRef
            ?? throw new InvalidOperationException("Canonical Order.Stops must reference one named component type.");
        var structure = new StorageStructureDefinition(
            id: new("freight/order"),
            semanticModel: orderShape.Document,
            rootShape: orderShape.Id,
            rootIdentityPath: FieldPath.FromField("id"),
            partitionPath: FieldPath.FromField("tenantId"),
            ownedCollections:
            [
                new(
                    id: new("order/stops"),
                    collectionPath: FieldPath.FromField("stops"),
                    componentType: stopType.TypeId,
                    localIdentityPath: FieldPath.FromField("id"),
                    ordinalPath: FieldPath.FromField("sequence"))
            ],
            provenance: new(
                producer: new("cohesive-materialization-harness", "1"),
                source: new("eng/materialization-harness/model/freight-order-storage"),
                origin: DocumentOrigin.Generated));
        var structureValidation = StorageRealizationValidator.ValidateStructure(structure);
        Require(
            structureValidation.IsValid,
            structureValidation.Diagnostics.Select(static diagnostic => diagnostic.Message));
        var orderEntity = Entity(
            orderShape.Id.ShapeId.Value,
            canonicalOrderShape,
            orderShape.Document);
        var storage = new FreightOrderStorageDefinitions(
            Order: orderEntity,
            CustomerAccount: Entity(
                customerShape.Id.ShapeId.Value,
                customerShape.Document.Graph.TryGetShape(customerShape.Id),
                customerShape.Document),
            Location: Entity(
                locationShape.Id.ShapeId.Value,
                locationShape.Document.Graph.TryGetShape(locationShape.Id),
                locationShape.Document));

        return new(
            compilationRequest,
            plan,
            realization,
            root,
            output,
            structure,
            storage,
            document,
            definition,
            MaterializationDefinitionFingerprinter.Compute(definition));

        static EntityDefinition Entity(string name, Shape? shape, ShapeGraphDocument document)
        {
            var source = shape ?? throw new InvalidOperationException($"Canonical entity shape '{name}' is missing.");
            return new(
                name: new(name),
                shapeGraph: new(
                    shape: new(document.Graph.Id, source.Id),
                    document: document));
        }
    }

    static QualifiedShapeId Shape(string value) => new(GraphId, new(value));

    static ControlLoopDefinition TargetBatchControl(MaterializationIndexSyncWorkloadKind workload) => new(
        schemaVersion: ControlLoopDefinition.CurrentSchemaVersion,
        id: new($"freight-order-search/elastic-target-batch/{workload.ToString().ToLowerInvariant()}"),
        target: "freight/order-search",
        applicationAuthority: MaterializationIndexSyncControlCompiler.ApplicationAuthority,
        stage: ControlStageKind.Target,
        hardLimits: new([
            new(
                range: new(
                    actuator: ControlActuatorKind.BatchItems,
                    minimum: new(1, ControlUnit.Count),
                    maximum: new(MaximumWriteItems, ControlUnit.Count)),
                origin: ControlHardLimitOrigin.Semantic,
                authority: "materialization-harness/freight/order-search/v1")
        ]),
        initialOperatingPoint: new([
            new(
                actuator: ControlActuatorKind.BatchItems,
                quantity: new(MaximumWriteItems, ControlUnit.Count))
        ]),
        objectives:
        [
            new(
                metric: ControlMetricKind.RejectionRatio,
                statistic: ControlStatisticKind.Last,
                direction: ControlObjectiveDirection.HigherIsCongested,
                recoveryBoundary: new(0, ControlUnit.BasisPoints),
                congestionBoundary: new(2_500, ControlUnit.BasisPoints))
        ],
        policy: AimdControlPolicyResolver.Resolve(
            actuator: ControlActuatorKind.BatchItems,
            layers: [new AimdControlPolicyLayer(
                origin: EffectiveConfigurationOrigin.Explicit,
                authority: "materialization-harness/freight/control-policy/v1",
                settings: new AimdControlPolicySettings(
                    additiveIncrease: 1,
                    multiplicativeDecreaseBasisPoints: 5_000,
                    healthyObservationCount: 2,
                    recoveryCooldownMilliseconds: 1_000,
                    minimumDwellMilliseconds: 1_000,
                    maximumObservationAgeMilliseconds: 60_000,
                    minimumSampleCount: 1))]),
        budgets: [],
        provenance: new(
            producer: new("cohesive-materialization-harness", "1"),
            source: new("eng/materialization-harness/model/freight-order-search-control"),
            origin: DocumentOrigin.Generated));

    static void Require(bool condition, IEnumerable<string> diagnostics)
    {
        if (!condition)
            throw new InvalidOperationException(string.Join(Environment.NewLine, diagnostics));
    }
}

/// <summary>Canonical compiled semantics shared by every physical provider realization.</summary>
/// <param name="CompilationRequest">Exact authoring input retained by the materialization definition.</param>
/// <param name="Plan">Canonical compiled relation plan.</param>
/// <param name="Realization">Canonical in-memory relation realization used for hydration.</param>
/// <param name="Root">Relation root supplied by each bounded source page.</param>
/// <param name="Output">Complete derived OrderSearchDocument output.</param>
/// <param name="Structure">Canonical aggregate ownership and ordering authority.</param>
/// <param name="Storage">Repository entity-state definitions deterministically projected from canonical shapes.</param>
/// <param name="Document">Portable canonical materialization document retained for planning and execution.</param>
/// <param name="Definition">Backend-independent materialization definition.</param>
/// <param name="DefinitionFingerprint">Stable fingerprint shared by provider realizations.</param>
public sealed record FreightOrderMaterializationSemantics(
    RelationQueryCompilationRequest CompilationRequest,
    CompiledRelationQueryPlan Plan,
    RelationQueryRealizationReport Realization,
    RelationQuerySourceInputContract Root,
    RelationQueryOutputReference Output,
    StorageStructureDefinition Structure,
    FreightOrderStorageDefinitions Storage,
    MaterializationDocument Document,
    MaterializationDefinition Definition,
    ExecutionDefinitionFingerprint DefinitionFingerprint);

/// <summary>Source entity-state definitions deterministically projected for PostgreSQL and Cosmos repositories.</summary>
/// <param name="Order">Immutable freight order aggregate definition.</param>
/// <param name="CustomerAccount">Customer account definition.</param>
/// <param name="Location">Freight location definition.</param>
public sealed record FreightOrderStorageDefinitions(
    EntityDefinition Order,
    EntityDefinition CustomerAccount,
    EntityDefinition Location);

/// <summary>Simplified immutable freight order root.</summary>
public sealed record FreightOrder
{
    /// <summary>Globally unique order identity used as the stable materialization item identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning tenant identity.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Human-readable order number.</summary>
    [JsonPropertyName("orderNumber")]
    public required string OrderNumber { get; init; }

    /// <summary>Tenant-local customer account identity.</summary>
    [JsonPropertyName("customerAccountId")]
    public required string CustomerAccountId { get; init; }

    /// <summary>Required equipment class.</summary>
    [JsonPropertyName("equipmentClass")]
    public required string EquipmentClass { get; init; }

    /// <summary>Source creation instant retained for incremental ordering and spot checks.</summary>
    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Ordered stops owned by this aggregate; component lifetime and tenant scope are inherited.</summary>
    [JsonPropertyName("stops")]
    public ImmutableArray<FreightOrderStop> Stops { get; init; } = [];
}

/// <summary>Simplified freight customer account.</summary>
public sealed record FreightCustomerAccount
{
    /// <summary>Tenant-local customer identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning tenant identity.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Customer display name.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }
}

/// <summary>Simplified immutable order-stop component owned by one <see cref="FreightOrder"/>.</summary>
public sealed record FreightOrderStop
{
    /// <summary>Order-local stable stop identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Positive order-relative stop sequence.</summary>
    [JsonPropertyName("sequence")]
    public int Sequence { get; init; }

    /// <summary>Pickup or Drop semantic stop kind.</summary>
    [JsonPropertyName("stopType")]
    public required string StopType { get; init; }

    /// <summary>Tenant-local location identity.</summary>
    [JsonPropertyName("locationId")]
    public required string LocationId { get; init; }
}

/// <summary>Simplified freight location.</summary>
public sealed record FreightLocation
{
    /// <summary>Tenant-local location identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning tenant identity.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Location display name.</summary>
    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    /// <summary>City or locality.</summary>
    [JsonPropertyName("city")]
    public required string City { get; init; }

    /// <summary>State, province, or region.</summary>
    [JsonPropertyName("region")]
    public required string Region { get; init; }
}

/// <summary>Canonical value written beneath the Elasticsearch materialization envelope.</summary>
public sealed record FreightOrderSearchDocument
{
    /// <summary>Stable index identity equal to the canonical root order identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning tenant identity.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Tenant-local order identity.</summary>
    [JsonPropertyName("orderId")]
    public required string OrderId { get; init; }

    /// <summary>Human-readable order number.</summary>
    [JsonPropertyName("orderNumber")]
    public required string OrderNumber { get; init; }

    /// <summary>Customer display name.</summary>
    [JsonPropertyName("customerName")]
    public required string CustomerName { get; init; }

    /// <summary>Required equipment class.</summary>
    [JsonPropertyName("equipmentClass")]
    public required string EquipmentClass { get; init; }

    /// <summary>Selected origin stop identity.</summary>
    [JsonPropertyName("originStopId")]
    public required string OriginStopId { get; init; }

    /// <summary>Origin city.</summary>
    [JsonPropertyName("originCity")]
    public required string OriginCity { get; init; }

    /// <summary>Origin state, province, or region.</summary>
    [JsonPropertyName("originRegion")]
    public required string OriginRegion { get; init; }

    /// <summary>Selected destination stop identity.</summary>
    [JsonPropertyName("destinationStopId")]
    public required string DestinationStopId { get; init; }

    /// <summary>Destination city.</summary>
    [JsonPropertyName("destinationCity")]
    public required string DestinationCity { get; init; }

    /// <summary>Destination state, province, or region.</summary>
    [JsonPropertyName("destinationRegion")]
    public required string DestinationRegion { get; init; }
}
