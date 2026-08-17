using System.Collections.Immutable;
using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Cohesive.Execution;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;

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
        var orderShape = author.Clr.Shape<FreightOrder>(OrderShapeId);
        var customerShape = author.Clr.Shape<FreightCustomerAccount>(CustomerAccountShapeId);
        var locationShape = author.Clr.Shape<FreightLocation>(LocationShapeId);
        var searchShape = author.Clr.Shape<FreightOrderSearchDocument>(OrderSearchDocumentShapeId);

        var orderCustomer = author.Relationship<FreightOrder, FreightCustomerAccount>(
            order => order.CustomerAccountId,
            new("freight-order.customer-account"));
        var orderOrigin = author.Relationship<FreightOrder, FreightLocation>(
            order => order.OriginLocationId,
            new("freight-order.origin-location"));
        var orderDestination = author.Relationship<FreightOrder, FreightLocation>(
            order => order.DestinationLocationId,
            new("freight-order.destination-location"));

        var orders = author.Source(orderShape, "materialization-harness/freight/orders");
        var customers = author.Traverse(
            orders,
            orderCustomer,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/customer");
        var originLocations = author.Traverse(
            customers,
            orders.Binding,
            orderOrigin,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/pickup-location");
        var destinationLocations = author.Traverse(
            originLocations,
            orders.Binding,
            orderDestination,
            requirement: QueryInputRequirement.Required,
            sourceReference: "materialization-harness/freight/delivery-location");
        Expression<Func<FreightOrder, FreightCustomerAccount, FreightLocation, FreightLocation, FreightOrderSearchDocument>> projection =
            (FreightOrder order, FreightCustomerAccount customer, FreightLocation origin, FreightLocation destination) =>
                new FreightOrderSearchDocument
                {
                    Id = order.TenantId + "/" + order.Id,
                    TenantId = order.TenantId,
                    OrderId = order.Id,
                    OrderNumber = order.OrderNumber,
                    CustomerName = customer.DisplayName,
                    EquipmentClass = order.EquipmentClass,
                    OriginStopId = order.PickupStopId,
                    OriginCity = origin.City,
                    OriginRegion = origin.Region,
                    DestinationStopId = order.DeliveryStopId,
                    DestinationCity = destination.City,
                    DestinationRegion = destination.Region
                };
        var projected = author.Project(
            destinationLocations.Node,
            searchShape,
            projection,
            [orders.Binding, customers.Binding, originLocations.Binding, destinationLocations.Binding],
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
        var definition = CreateDefinition(
            plan,
            MaterializationRelationReference.From(compilationRequest, output.Id));
        var validation = MaterializationDefinitionValidator.Validate(definition);
        Require(validation.IsValid, validation.Diagnostics.Select(static diagnostic => diagnostic.Message));

        return new(
            compilationRequest,
            plan,
            realization,
            root,
            output,
            definition,
            MaterializationDefinitionFingerprinter.Compute(definition));
    }

    static MaterializationDefinition CreateDefinition(
        CompiledRelationQueryPlan plan,
        MaterializationRelationReference relation)
    {
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. plan.InputContract.Sources.Select(source => SourceRequirement(
                source.Input.Id,
                MaterializationCapabilityKind.SourceBoundedEnumeration)),
            .. plan.InputContract.Traversals.Select(traversal => SourceRequirement(
                traversal.Input.Id,
                traversal.Input.Direction == RelationshipTraversalDirection.Forward
                    ? MaterializationCapabilityKind.SourceBatchedPointRead
                    : MaterializationCapabilityKind.SourceParameterizedPredicateQuery))
        ];
        ImmutableArray<MaterializationCapabilityRequirement> target =
        [
            TargetRequirement("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation),
            TargetRequirement("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert),
            TargetRequirement("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes),
            TargetRequirement("target/seal", MaterializationCapabilityKind.TargetSeal),
            TargetRequirement("target/validation", MaterializationCapabilityKind.TargetValidation),
            TargetRequirement("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion),
            TargetRequirement("target/abandonment", MaterializationCapabilityKind.TargetGenerationAbandonment),
            TargetRequirement("target/retirement", MaterializationCapabilityKind.TargetRetirement),
            TargetRequirement("target/cleanup", MaterializationCapabilityKind.TargetCleanup)
        ];
        return new(
            id: new("freight/order-search"),
            relation,
            sources,
            target,
            updatePolicy: new(
                MaterializationSynchronizationMode.Rebuild,
                MaterializationConsistencyKind.Reconciliation,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(3, MaterializationFailureDisposition.Stop),
            freshnessPolicy: new(maximumLagMilliseconds: 30_000),
            controlLoops: [],
            provenance: new(
                new("cohesive-materialization-harness", "1"),
                new("eng/materialization-harness/model/freight-order-search"),
                DocumentOrigin.Generated));
    }

    static MaterializationSourceRequirement SourceRequirement(
        RelationQueryInputId input,
        MaterializationCapabilityKind read) => new(
        input,
        [
            new(
                new($"{input.Value}/read"),
                read,
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness,
                    MaterializationGuaranteeKind.Reconciliation
                ],
                [
                    new(MaterializationLimitKind.ReadItems, MaximumReadItems),
                    new(MaterializationLimitKind.ReadBytes, MaximumReadBytes)
                ],
                MaterializationSynchronizationMode.Rebuild),
            new(
                new($"{input.Value}/continuation"),
                MaterializationCapabilityKind.SourceContinuation,
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.Reconciliation
                ],
                [],
                MaterializationSynchronizationMode.Rebuild)
        ]);

    static MaterializationCapabilityRequirement TargetRequirement(
        string id,
        MaterializationCapabilityKind capability) => new(
        new(id),
        capability,
        capability switch
        {
            MaterializationCapabilityKind.TargetGenerationIsolation =>
                [MaterializationGuaranteeKind.GenerationIsolation, MaterializationGuaranteeKind.FencedMutation],
            MaterializationCapabilityKind.TargetBulkUpsert =>
                [
                    MaterializationGuaranteeKind.IdempotentWrite,
                    MaterializationGuaranteeKind.FencedMutation,
                    MaterializationGuaranteeKind.VersionConditionalWrite
                ],
            MaterializationCapabilityKind.TargetPerItemOutcomes =>
                [MaterializationGuaranteeKind.ExactPerItemOutcome],
            MaterializationCapabilityKind.TargetFencedPromotion =>
                [MaterializationGuaranteeKind.AtomicPromotion, MaterializationGuaranteeKind.FencedPromotion],
            MaterializationCapabilityKind.TargetGenerationAbandonment =>
                [MaterializationGuaranteeKind.AtomicDurableGenerationExclusion],
            _ => [MaterializationGuaranteeKind.FencedMutation]
        },
        capability is MaterializationCapabilityKind.TargetBulkUpsert
            or MaterializationCapabilityKind.TargetPerItemOutcomes
            ?
            [
                new(MaterializationLimitKind.WriteItems, MaximumWriteItems),
                new(MaterializationLimitKind.WriteBytes, MaximumWriteBytes)
            ]
            : [],
        MaterializationSynchronizationMode.Rebuild);

    static QualifiedShapeId Shape(string value) => new(GraphId, new(value));

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
/// <param name="Definition">Backend-independent materialization definition.</param>
/// <param name="DefinitionFingerprint">Stable fingerprint shared by provider realizations.</param>
public sealed record FreightOrderMaterializationSemantics(
    RelationQueryCompilationRequest CompilationRequest,
    CompiledRelationQueryPlan Plan,
    RelationQueryRealizationReport Realization,
    RelationQuerySourceInputContract Root,
    RelationQueryOutputReference Output,
    MaterializationDefinition Definition,
    ExecutionDefinitionFingerprint DefinitionFingerprint);

/// <summary>Simplified immutable freight order root.</summary>
public sealed record FreightOrder
{
    /// <summary>Tenant-local order identity.</summary>
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

    /// <summary>First pickup stop selected from canonical stop order.</summary>
    [JsonPropertyName("pickupStopId")]
    public required string PickupStopId { get; init; }

    /// <summary>Last delivery stop selected from canonical stop order.</summary>
    [JsonPropertyName("deliveryStopId")]
    public required string DeliveryStopId { get; init; }

    /// <summary>Location selected by the first unambiguous pickup stop.</summary>
    [JsonPropertyName("originLocationId")]
    public required string OriginLocationId { get; init; }

    /// <summary>Location selected by the last drop in canonical stop order.</summary>
    [JsonPropertyName("destinationLocationId")]
    public required string DestinationLocationId { get; init; }
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

/// <summary>Simplified immutable order stop.</summary>
public sealed record FreightOrderStop
{
    /// <summary>Tenant-local stop identity.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>Owning tenant identity.</summary>
    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    /// <summary>Tenant-local order identity.</summary>
    [JsonPropertyName("orderId")]
    public required string OrderId { get; init; }

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
    /// <summary>Globally unique tenant/order index identity.</summary>
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
