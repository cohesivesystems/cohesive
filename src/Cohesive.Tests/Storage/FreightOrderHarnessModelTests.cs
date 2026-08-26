using System.Collections.Immutable;
using Cohesive.Control;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

/// <summary>Determinism and capability coverage for the real-container harness semantic authority.</summary>
public sealed class FreightOrderHarnessModelTests
{
    static readonly RelationQueryLogicalPartitionIdentity TenantPartition =
        new("materialization-harness/freight/tenant/tenant-a");

    [Fact]
    public void CanonicalModelIsDeterministicAndRequiresCrossEntityHydration()
    {
        var first = FreightOrderMaterializationModel.Create();
        var second = FreightOrderMaterializationModel.Create();

        Assert.Equal(first.DefinitionFingerprint, second.DefinitionFingerprint);
        Assert.Equal(
            "97e44b577b50923ad9f16a0df1b7527ceb8b9da82e2c2e45d5cb7cb07203c908",
            first.DefinitionFingerprint.Value);
        Assert.Equal(MaterializationSynchronizationMode.All, first.Definition.UpdatePolicy.SupportedModes);
        Assert.Equal(MaterializationConsistencyKind.BaselinePlusCatchUp, first.Definition.UpdatePolicy.Consistency);
        Assert.Single(first.Plan.InputContract.Sources);
        Assert.Equal(3, first.Plan.InputContract.Traversals.Length);
        Assert.Equal(2, first.Plan.InputContract.Expansions.Length);
        Assert.Equal(4, first.Definition.Sources.Length);
        Assert.Equal(2, first.Definition.ControlLoops.Length);
        Assert.Equal(2, first.Definition.ControlWorkloads.Length);
        Assert.All(first.Definition.ControlLoops, static control => Assert.Equal(ControlStageKind.Target, control.Stage));
        Assert.Contains(
            first.Definition.ControlWorkloads,
            static workload => workload.Workload == MaterializationIndexSyncWorkloadKind.Rebuild);
        Assert.Contains(
            first.Definition.ControlWorkloads,
            static workload => workload.Workload == MaterializationIndexSyncWorkloadKind.Realtime);
        Assert.All(
            first.Definition.ControlWorkloads,
            workload => Assert.Contains(
                first.Definition.ControlLoops,
                control => control.Id == workload.LoopId));
        var expansions = first.Plan.Definition.Body.Nodes.OfType<ExpandCollectionQueryNode>().ToImmutableArray();
        Assert.Equal(2, expansions.Length);
        Assert.All(
            expansions,
            static expansion => Assert.Equal(
                FreightOrderMaterializationModel.OrderStopShapeId,
                expansion.ItemShape));
        Assert.All(
            first.Definition.Sources,
            static source => Assert.Contains(
                source.Capabilities,
                capability => capability.Capability == MaterializationCapabilityKind.SourceContinuation));
        Assert.All(
            first.Definition.Sources,
            static source => Assert.Contains(
                source.Capabilities,
                capability => capability.Capability == MaterializationCapabilityKind.SourceChangeDelivery));
        Assert.Equal(FreightOrderMaterializationModel.OrderShapeId, first.Root.Shape);
        Assert.Equal(FreightOrderMaterializationModel.OrderSearchDocumentShapeId, first.Output.Shape);
        Assert.All(
            new[]
            {
                first.Storage.Order,
                first.Storage.CustomerAccount,
                first.Storage.Location
            },
            static entity => Assert.True(entity.Shape.HasRole(Cohesive.Model.ShapeRoles.Entity)));
        Assert.Contains(first.Storage.Order.Fields, static field => field.Name.Value == "createdAt");
        Assert.DoesNotContain(first.Storage.Order.Fields, static field => field.Name.Value is
            "pickupStopId" or "deliveryStopId" or "originLocationId" or "destinationLocationId");
        Assert.Contains(first.Storage.Order.Fields, static field => field.Name.Value == "stops");
        var repositoryStops = Assert.IsType<NamedTypeRef>(
            first.Storage.Order.Fields.Single(static field => field.Name.Value == "stops").Type);
        Assert.True(first.Storage.Order.ValidateShapeGraph().IsValid);
        Assert.Equal(first.Structure.SemanticModel, first.Storage.Order.ShapeGraph?.Document);
        var ownedStops = Assert.Single(first.Structure.OwnedCollections);
        Assert.Equal(FieldPath.FromField("stops"), ownedStops.CollectionPath);
        var canonicalStopsField = first.Structure.SemanticModel.Graph
            .GetShape(first.Structure.RootShape)
            .Fields.Single(static field => field.Name.Value == "stops");
        Assert.Equal(Assert.IsType<NamedTypeRef>(canonicalStopsField.Type).TypeId, ownedStops.ComponentType);
        Assert.Equal(repositoryStops.TypeId, ownedStops.ComponentType);
        Assert.Equal(FieldPath.FromField("sequence"), ownedStops.OrdinalPath);
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(first.Plan)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(second.Plan)));
    }

    [Fact]
    public void MaterializationAuthoringLowersToEquivalentDirectCanonicalIr()
    {
        var authored = FreightOrderMaterializationModel.Create();
        var direct = CreateDirectDefinition(authored);
        var authoredDocument = MaterializationDocument.FromDefinition(authored.Definition);
        var directDocument = MaterializationDocument.FromDefinition(direct);

        Assert.Equal(
            MaterializationDefinitionFingerprinter.Compute(direct),
            authored.DefinitionFingerprint);
        Assert.Equal(
            MaterializationJsonSerializer.GetCanonicalBytes(directDocument),
            MaterializationJsonSerializer.GetCanonicalBytes(authoredDocument));
    }

    [Fact]
    public void MaterializationAuthoringReturnsCanonicalDiagnosticsForUnsupportedProfileCombination()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var authored = Materialization.Define(
                new("freight/order-search/baseline-catch-up"),
                semantics.CompilationRequest,
                semantics.Output.Id)
            .WithUpdatePolicy(new(
                MaterializationSynchronizationMode.Rebuild,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion))
            .WithBoundedRelationRebuildSources(maximumItems: 64, maximumBytes: 1_048_576)
            .WithGenerationalIndexTarget(maximumItems: 16, maximumBytes: 1_048_576)
            .WithFailurePolicy(new(maximumAttempts: 3, MaterializationFailureDisposition.Stop))
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 1_800_000))
            .WithProvenance(Provenance("baseline-catch-up"))
            .Build();

        Assert.False(authored.IsValid);
        Assert.Contains(
            authored.Validation.Diagnostics,
            static diagnostic =>
                diagnostic.Code == MaterializationDefinitionDiagnosticCodes.ProtocolCapabilityMissing
                && diagnostic.Message.Contains(
                    MaterializationCapabilityKind.SourceChangeDelivery.ToString(),
                    StringComparison.Ordinal));
    }

    [Fact]
    public void MaterializationAuthoringRejectsRepeatedSemanticDeclarations()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        var builder = Materialization.Define(
                new("freight/order-search/repeated-policy"),
                semantics.CompilationRequest,
                semantics.Output.Id)
            .WithUpdatePolicy(new(
                MaterializationSynchronizationMode.Rebuild,
                MaterializationConsistencyKind.Reconciliation,
                MaterializationIdempotencyKind.StableOutputIdentity));

        var exception = Assert.Throws<InvalidOperationException>(() => builder.WithUpdatePolicy(new(
            MaterializationSynchronizationMode.Rebuild,
            MaterializationConsistencyKind.CoordinatedSnapshot,
            MaterializationIdempotencyKind.StableOutputIdentity)));

        Assert.Contains("already configured", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanonicalRelationDerivesEndpointsFromOrderedStopsAfterReorderMoveAndDelete()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        FreightOrder order = new()
        {
            Id = "order-1",
            TenantId = "tenant-a",
            OrderNumber = "ORD-001",
            CustomerAccountId = "customer-1",
            EquipmentClass = "Reefer",
            CreatedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z")
        };
        FreightCustomerAccount customer = new()
        {
            Id = "customer-1",
            TenantId = "tenant-a",
            DisplayName = "Acme Foods"
        };
        ImmutableArray<FreightLocation> locations =
        [
            Location("origin-a", "Seattle", "WA"),
            Location("origin-b", "Tacoma", "WA"),
            Location("origin-moved", "Spokane", "WA"),
            Location("destination-a", "Portland", "OR"),
            Location("destination-b", "Eugene", "OR")
        ];
        ImmutableArray<FreightOrderStop> baseline =
        [
            Stop("pickup-a", sequence: 10, stopType: "Pickup", locationId: "origin-a"),
            Stop("pickup-b", sequence: 20, stopType: "Pickup", locationId: "origin-b"),
            Stop("drop-a", sequence: 30, stopType: "Drop", locationId: "destination-a"),
            Stop("drop-b", sequence: 40, stopType: "Drop", locationId: "destination-b")
        ];

        var initial = await ExecuteAsync(semantics, order, customer, baseline, locations, "initial");
        AssertEndpoints(initial, "pickup-a", "Seattle", "drop-b", "Eugene");

        ImmutableArray<FreightOrderStop> reordered =
        [
            baseline[0] with { Sequence = 20 },
            baseline[1] with { Sequence = 10 },
            baseline[2] with { Sequence = 40 },
            baseline[3] with { Sequence = 30 }
        ];
        var afterReorder = await ExecuteAsync(semantics, order, customer, reordered, locations, "reorder");
        AssertEndpoints(afterReorder, "pickup-b", "Tacoma", "drop-a", "Portland");

        var moved = reordered.SetItem(
            index: 1,
            item: reordered[1] with { LocationId = "origin-moved" });
        var afterMove = await ExecuteAsync(semantics, order, customer, moved, locations, "move");
        AssertEndpoints(afterMove, "pickup-b", "Spokane", "drop-a", "Portland");

        var afterDelete = await ExecuteAsync(
            semantics,
            order,
            customer,
            [moved[0], moved[3]],
            locations,
            "delete");
        AssertEndpoints(afterDelete, "pickup-a", "Seattle", "drop-b", "Eugene");
    }

    [Fact]
    public async Task CanonicalRelationRejectsCollectionOccurrenceWithUnknownOwnerEvidence()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        FreightOrder order = new()
        {
            Id = "order-1",
            TenantId = "tenant-a",
            OrderNumber = "ORD-001",
            CustomerAccountId = "customer-1",
            EquipmentClass = "Reefer",
            CreatedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z")
        };
        FreightCustomerAccount customer = new()
        {
            Id = "customer-1",
            TenantId = "tenant-a",
            DisplayName = "Acme Foods"
        };
        var outcome = await EvaluateAsync(
            semantics,
            order,
            customer,
            [
                Stop("pickup-a", sequence: 10, stopType: "Pickup", locationId: "origin-a"),
                Stop("drop-a", sequence: 20, stopType: "Drop", locationId: "destination-a")
            ],
            [
                Location("origin-a", "Seattle", "WA"),
                Location("destination-a", "Portland", "OR")
            ],
            scenario: "unknown-collection-owner");
        var execution = Assert.IsType<RelationQueryPhysicalExecutionResult>(outcome.PhysicalExecution);
        var evidence = Assert.IsType<RelationQueryRuntimeEvidence>(execution.Evidence);
        var occurrence = evidence.CollectionOccurrences[0];
        var malformed = new RelationQueryCollectionOccurrenceEvidence(
            expansion: occurrence.Expansion,
            owner: new("tenant-b/order-1"),
            ordinal: occurrence.Ordinal,
            occurrence: occurrence.Occurrence,
            value: occurrence.Value);
        var malformedEvidence = new RelationQueryRuntimeEvidence(
            evaluation: evidence.Evaluation,
            plan: semantics.Plan,
            completeness: evidence.Completeness,
            sources: evidence.Sources,
            fields: evidence.Fields,
            traversals: evidence.Traversals,
            parameters: evidence.Parameters,
            capabilities: evidence.Capabilities,
            conversionFailures: evidence.ConversionFailures,
            collectionOccurrences:
            [
                malformed,
                .. evidence.CollectionOccurrences.Skip(1)
            ]);

        var analysis = RelationRequirementGapAnalyzer.Analyze(semantics.Plan, malformedEvidence);

        Assert.False(analysis.IsEvidenceValid);
        Assert.Contains(
            analysis.Diagnostics,
            diagnostic => diagnostic.Code == RelationRuntimeDiagnosticCodes.EvidenceConflict
                && diagnostic.Occurrence == occurrence.Occurrence.Id
                && diagnostic.Message.Contains("unknown owner occurrence", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CanonicalRelationRejectsOwnedStopFanOutBeyondThePhysicalBoundary()
    {
        var semantics = FreightOrderMaterializationModel.Create();
        FreightOrder order = new()
        {
            Id = "order-1",
            TenantId = "tenant-a",
            OrderNumber = "ORD-001",
            CustomerAccountId = "customer-1",
            EquipmentClass = "Reefer",
            CreatedAt = DateTimeOffset.Parse("2026-08-01T10:00:00Z")
        };
        FreightCustomerAccount customer = new()
        {
            Id = "customer-1",
            TenantId = "tenant-a",
            DisplayName = "Acme Foods"
        };
        var stops = Enumerable.Range(start: 1, count: 65)
            .Select(sequence => Stop(
                id: $"pickup-{sequence:D2}",
                sequence: sequence,
                stopType: "Pickup",
                locationId: "origin-a"))
            .ToImmutableArray();

        var outcome = await EvaluateAsync(
            semantics,
            order,
            customer,
            stops,
            [Location("origin-a", "Seattle", "WA")],
            scenario: "fan-out-boundary");

        Assert.False(outcome.IsSuccessful);
        var execution = Assert.IsType<RelationQueryPhysicalExecutionResult>(outcome.PhysicalExecution);
        Assert.Contains(
            execution.Diagnostics,
            static diagnostic =>
                diagnostic.Code == RelationQueryPhysicalExecutionDiagnosticCodes.OperatingBoundaryExceeded
                && diagnostic.Message.Contains("fan-out boundary", StringComparison.Ordinal));
    }

    static MaterializationDefinition CreateDirectDefinition(FreightOrderMaterializationSemantics semantics)
    {
        const long maximumReadItems = 64;
        const long maximumReadBytes = 1_048_576;
        const long maximumWriteItems = 16;
        const long maximumWriteBytes = 1_048_576;
        ImmutableArray<MaterializationSourceRequirement> sources =
        [
            .. MaterializationSourceAcquisitionCatalog.GetInputs(semantics.Plan).Select(input =>
            {
                Assert.True(MaterializationSourceAcquisitionCatalog.TryGetReadCapability(
                    semantics.Plan,
                    input,
                    out var readCapability));
                List<MaterializationCapabilityRequirement> capabilities =
                [
                    new(
                        new($"{input.Value}/read"),
                        readCapability,
                        [
                            MaterializationGuaranteeKind.StableOrdering,
                            MaterializationGuaranteeKind.RequestLocalCompleteness
                        ],
                        [
                            new(MaterializationLimitKind.ReadItems, maximumReadItems),
                            new(MaterializationLimitKind.ReadBytes, maximumReadBytes)
                        ],
                        MaterializationSynchronizationMode.All),
                    new(
                        new($"{input.Value}/continuation"),
                        MaterializationCapabilityKind.SourceContinuation,
                        [MaterializationGuaranteeKind.StableOrdering],
                        [],
                        MaterializationSynchronizationMode.Rebuild),
                    new(
                        new($"{input.Value}/changes"),
                        MaterializationCapabilityKind.SourceChangeDelivery,
                        [
                            MaterializationGuaranteeKind.StableOrdering,
                            MaterializationGuaranteeKind.AtLeastOnceDelivery,
                            MaterializationGuaranteeKind.BaselinePlusCatchUp,
                            MaterializationGuaranteeKind.CompleteMutationDelivery,
                            MaterializationGuaranteeKind.BeforeImage
                        ],
                        [
                            new(MaterializationLimitKind.ChangeItems, maximumReadItems),
                            new(MaterializationLimitKind.ReadBytes, maximumReadBytes)
                        ],
                        MaterializationSynchronizationMode.All)
                ];
                if (semantics.Root.Input.Id == input
                    && readCapability != MaterializationCapabilityKind.SourceParameterizedPredicateQuery)
                {
                    capabilities.Add(new(
                        new($"{input.Value}/inverse"),
                        MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
                        [
                            MaterializationGuaranteeKind.StableOrdering,
                            MaterializationGuaranteeKind.RequestLocalCompleteness
                        ],
                        [
                            new(MaterializationLimitKind.ReadItems, maximumReadItems),
                            new(MaterializationLimitKind.ReadBytes, maximumReadBytes)
                        ],
                        MaterializationSynchronizationMode.Incremental));
                }
                return new MaterializationSourceRequirement(input, [.. capabilities]);
            })
        ];
        ImmutableArray<MaterializationCapabilityRequirement> target =
        [
            Target("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation),
            Target("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert),
            Target("target/delete", MaterializationCapabilityKind.TargetBulkDelete),
            Target("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes),
            Target("target/seal", MaterializationCapabilityKind.TargetSeal),
            Target("target/validation", MaterializationCapabilityKind.TargetValidation),
            Target("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion),
            Target("target/abandonment", MaterializationCapabilityKind.TargetGenerationAbandonment),
            Target("target/retirement", MaterializationCapabilityKind.TargetRetirement),
            Target("target/cleanup", MaterializationCapabilityKind.TargetCleanup)
        ];
        var rebuildTargetBatchControl = TargetBatchControl(
            maximumWriteItems,
            MaterializationIndexSyncWorkloadKind.Rebuild);
        var realtimeTargetBatchControl = TargetBatchControl(
            maximumWriteItems,
            MaterializationIndexSyncWorkloadKind.Realtime);
        return new(
            id: new("freight/order-search"),
            relation: MaterializationRelationReference.From(semantics.CompilationRequest, semantics.Output.Id),
            sources: sources,
            targetCapabilities: target,
            updatePolicy: new(
                MaterializationSynchronizationMode.All,
                MaterializationConsistencyKind.BaselinePlusCatchUp,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            failurePolicy: new(maximumAttempts: 3, MaterializationFailureDisposition.Stop),
            freshnessPolicy: new(maximumLagMilliseconds: 1_800_000),
            controlLoops: [rebuildTargetBatchControl, realtimeTargetBatchControl],
            provenance: Provenance("freight-order-search"),
            controlWorkloads:
            [
                new(
                    loopId: rebuildTargetBatchControl.Id,
                    workload: MaterializationIndexSyncWorkloadKind.Rebuild),
                new(
                    loopId: realtimeTargetBatchControl.Id,
                    workload: MaterializationIndexSyncWorkloadKind.Realtime)
            ]);

        static MaterializationCapabilityRequirement Target(
            string id,
            MaterializationCapabilityKind capability) => new(
            new(id),
            capability,
            capability switch
            {
                MaterializationCapabilityKind.TargetGenerationIsolation =>
                    [MaterializationGuaranteeKind.GenerationIsolation, MaterializationGuaranteeKind.FencedMutation],
                MaterializationCapabilityKind.TargetBulkUpsert
                    or MaterializationCapabilityKind.TargetBulkDelete =>
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
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes
                ?
                [
                    new(MaterializationLimitKind.WriteItems, maximumWriteItems),
                    new(MaterializationLimitKind.WriteBytes, maximumWriteBytes)
                ]
                : [],
            capability is MaterializationCapabilityKind.TargetBulkUpsert
                or MaterializationCapabilityKind.TargetBulkDelete
                or MaterializationCapabilityKind.TargetPerItemOutcomes
                ? MaterializationSynchronizationMode.All
                : MaterializationSynchronizationMode.Rebuild);
    }

    static ControlLoopDefinition TargetBatchControl(
        long maximumWriteItems,
        MaterializationIndexSyncWorkloadKind workload) => new(
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
                    maximum: new(maximumWriteItems, ControlUnit.Count)),
                origin: ControlHardLimitOrigin.Semantic,
                authority: "materialization-harness/freight/order-search/v1")
        ]),
        initialOperatingPoint: new([
            new(
                actuator: ControlActuatorKind.BatchItems,
                quantity: new(maximumWriteItems, ControlUnit.Count))
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
        provenance: Provenance("freight-order-search-control"));

    static async Task<RelationQueryOutputRow> ExecuteAsync(
        FreightOrderMaterializationSemantics semantics,
        FreightOrder order,
        FreightCustomerAccount customer,
        ImmutableArray<FreightOrderStop> stops,
        ImmutableArray<FreightLocation> locations,
        string scenario)
    {
        var outcome = await EvaluateAsync(semantics, order, customer, stops, locations, scenario);

        Assert.True(
            outcome.IsSuccessful,
            $"Status: {outcome.Status}{Environment.NewLine}" + string.Join(
                Environment.NewLine,
                outcome.Diagnostics.Select(static diagnostic => $"outcome: {diagnostic.Code}: {diagnostic.Message}")
                    .Concat(outcome.Compilation.Diagnostics.Select(static diagnostic =>
                        $"compilation: {diagnostic.Code}: {diagnostic.Message}"))
                    .Concat(outcome.Realization?.Diagnostics.Select(static diagnostic =>
                        $"realization: {diagnostic.Code}: {diagnostic.Message}") ?? [])
                    .Concat(outcome.PhysicalPlanning?.Diagnostics.Select(static diagnostic =>
                        $"planning: {diagnostic.Code}: {diagnostic.Message}") ?? [])
                    .Concat(outcome.PhysicalExecution?.Diagnostics.Select(static diagnostic =>
                        $"execution: {diagnostic.Code}: {diagnostic.Message}") ?? [])
                    .Concat(outcome.Result?.Diagnostics.Select(static diagnostic =>
                        $"interpretation: {diagnostic.Code}: {diagnostic.Message}") ?? [])
                    .Concat(outcome.Result?.RequirementGapAnalysis.Gaps.Select(static gap =>
                        $"gap: {gap.Cause}: {gap.EvidenceReference}") ?? [])));
        var result = Assert.IsType<RelationQueryExecutionResult>(outcome.Result);
        return Assert.Single(Assert.IsType<RelationQueryRelationResult>(result.Relation).Rows);
    }

    static async Task<RelationQueryEvaluationOutcome> EvaluateAsync(
        FreightOrderMaterializationSemantics semantics,
        FreightOrder order,
        FreightCustomerAccount customer,
        ImmutableArray<FreightOrderStop> stops,
        ImmutableArray<FreightLocation> locations,
        string scenario)
    {
        var evaluation = new RelationQueryEvaluationBuilder(
                document: semantics.CompilationRequest.DefinitionDocument,
                evaluation: new($"tests/freight-order-harness/{scenario}"),
                shapeDocuments: semantics.CompilationRequest.ShapeDocuments,
                relationshipCatalogDocument: semantics.CompilationRequest.RelationshipCatalogDocument,
                planReference: RelationQueryCompiledPlanReference.From(semantics.Plan))
            .Supply(
                values: [order with { Stops = stops }],
                selectIdentity: static candidate => candidate.Id,
                completeness: RelationQueryEvidenceCompleteness.Complete,
                evidenceReference: $"tests/freight-order-harness/{scenario}/root",
                logicalPartition: TenantPartition)
            .Build();
        var catalog = new EntityRelationQuerySourceCatalog(
        [
            Registration(
                semantics.Storage.CustomerAccount,
                FreightOrderMaterializationModel.CustomerAccountShapeId,
                [customer],
                static value => value.Id),
            Registration(
                semantics.Storage.Location,
                FreightOrderMaterializationModel.LocationShapeId,
                locations,
                static value => value.Id)
        ]);
        var evaluator = catalog.CreateEvaluator(new(
            new("tests/freight-order-harness/physical-policy/v1"),
            conventionSetVersion: "tests/freight-order-harness/physical-conventions/v1",
            maximumBatchSize: 64,
            maximumBufferedRows: 256,
            maximumLocalRows: 256,
            maximumFanOut: 64,
            maximumReferenceKeysPerObservation: 64,
            maximumConcurrency: 1));

        return await evaluator.EvaluateAsync(evaluation);
    }

    static EntityRelationQuerySourceRegistration Registration<T>(
        Cohesive.Transitions.Model.EntityDefinition definition,
        QualifiedShapeId shape,
        ImmutableArray<T> values,
        Func<T, string> selectIdentity)
        where T : notnull
    {
        var snapshots = values.Select(value =>
        {
            var identity = selectIdentity(value);
            return new EntitySnapshot(
                definition.CreateState(entityId: identity, stateObject: value).Snapshot,
                PartitionKey: "tenant-a",
                ConcurrencyToken: new($"tests/freight-order-harness/{identity}"));
        }).ToImmutableArray();
        var repository = new InMemoryEntityOutboxRepository(
            definition,
            partitionKeyFieldName: "tenantId",
            seedSnapshots: snapshots);
        return EntityRelationQuerySourceRegistration.InMemory(
            shape,
            repository,
            logicalPartition: TenantPartition,
            identitySemanticPath: FieldPath.FromField("id"));
    }

    static FreightOrderStop Stop(
        string id,
        int sequence,
        string stopType,
        string locationId) => new()
    {
        Id = id,
        Sequence = sequence,
        StopType = stopType,
        LocationId = locationId
    };

    static FreightLocation Location(string id, string city, string region) => new()
    {
        Id = id,
        TenantId = "tenant-a",
        DisplayName = city,
        City = city,
        Region = region
    };

    static void AssertEndpoints(
        RelationQueryOutputRow row,
        string originStop,
        string originCity,
        string destinationStop,
        string destinationCity)
    {
        Assert.Equal(originStop, row.Value.GetProperty("originStopId").String);
        Assert.Equal(originCity, row.Value.GetProperty("originCity").String);
        Assert.Equal(destinationStop, row.Value.GetProperty("destinationStopId").String);
        Assert.Equal(destinationCity, row.Value.GetProperty("destinationCity").String);
    }

    static ExecutionProvenance Provenance(string source) => new(
        new("cohesive-materialization-harness", "1"),
        new($"eng/materialization-harness/model/{source}"),
        DocumentOrigin.Generated);
}
