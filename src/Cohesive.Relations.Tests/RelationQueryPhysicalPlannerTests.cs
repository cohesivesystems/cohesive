using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.Tests;

public sealed class RelationQueryPhysicalPlannerTests
{
    static readonly RelationQueryPhysicalPlanningPolicy PhysicalPolicy = new(
        new("tests/federated-physical-policy/v1"),
        conventionSetVersion: "tests/federated-conventions/v1",
        maximumBatchSize: 64,
        maximumBufferedRows: 1_000,
        maximumLocalRows: 1_000,
        maximumFanOut: 100,
        maximumReferenceKeysPerObservation: 1_000,
        maximumConcurrency: 4);

    [Fact]
    public void Compile_FederatedQueryBuildsDeterministicThreeSourceStageGraphWithExactFields()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var placement = CreatePlacement(semantic);
        var realization = Realize(semantic);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            realization,
            placement,
            PhysicalPolicy);

        var physical = SuccessfulPlan(result);
        RelationQueryPhysicalStageKind[] expectedStageKinds =
        [
            RelationQueryPhysicalStageKind.SourceRead,
            RelationQueryPhysicalStageKind.ExactFieldProjection,
            RelationQueryPhysicalStageKind.RelationshipKeyExtraction,
            RelationQueryPhysicalStageKind.KeyDeduplication,
            RelationQueryPhysicalStageKind.BatchedIdentityLookup,
            RelationQueryPhysicalStageKind.ExactFieldProjection,
            RelationQueryPhysicalStageKind.LocalCorrelation,
            RelationQueryPhysicalStageKind.RelationshipKeyExtraction,
            RelationQueryPhysicalStageKind.KeyDeduplication,
            RelationQueryPhysicalStageKind.BatchedIdentityLookup,
            RelationQueryPhysicalStageKind.ExactFieldProjection,
            RelationQueryPhysicalStageKind.LocalCorrelation,
            RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly,
            RelationQueryPhysicalStageKind.ReferenceInterpreterTerminal
        ];
        Assert.Equal(
            expectedStageKinds.OrderBy(static kind => (int)kind),
            physical.Stages.Select(static stage => stage.Kind).OrderBy(static kind => (int)kind));

        var source = Assert.Single(semantic.InputContract.Sources);
        Assert.Equal(
            [
                FederatedLoadRelationFixture.LoadCustomerIdPath,
                FederatedLoadRelationFixture.LoadEquipmentIdPath,
                FederatedLoadRelationFixture.LoadIdPath
            ],
            Paths(source.Fields));
        Assert.DoesNotContain(source.Fields, field =>
            field.Input.Field.Path is var path
            && (path == FederatedLoadRelationFixture.LoadStatusPath
                || path == FederatedLoadRelationFixture.LoadAmountPath));

        var customer = Traversal(
            semantic,
            FederatedLoadRelationFixture.CustomerTraversalNodeId);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        Assert.Equal([FederatedLoadRelationFixture.CustomerNamePath], Paths(customer.Fields));
        Assert.Equal([FederatedLoadRelationFixture.EquipmentNumberPath], Paths(equipment.Fields));
        Assert.DoesNotContain(customer.Fields, field =>
            field.Input.Field.Path == FederatedLoadRelationFixture.CustomerTypePath);
        Assert.DoesNotContain(equipment.Fields, field =>
            field.Input.Field.Path == FederatedLoadRelationFixture.EquipmentTypePath);

        foreach (var binding in placement.Bindings)
        {
            var expected = binding.Fields.Select(static field => field.Input)
                .OrderBy(static input => input.Value, StringComparer.Ordinal);
            var sourceBacked = Assert.Single(
                physical.Stages,
                stage => stage.PlacementBinding == binding.Id);
            Assert.Equal(expected, sourceBacked.RequestedFields);
            Assert.Equal(expected, sourceBacked.SemanticInputs.Where(binding.Fields
                .Select(static field => field.Input)
                .ToHashSet().Contains));
        }

        var rootProjection = Assert.Single(
            physical.Stages,
            stage => stage.Kind == RelationQueryPhysicalStageKind.ExactFieldProjection
                && stage.Provenance.Inputs.Contains(source.Input.Id));
        var customerExtraction = Assert.Single(
            physical.Stages,
            stage => stage.Kind == RelationQueryPhysicalStageKind.RelationshipKeyExtraction
                && stage.Provenance.Nodes.Contains(FederatedLoadRelationFixture.CustomerTraversalNodeId));
        var equipmentExtraction = Assert.Single(
            physical.Stages,
            stage => stage.Kind == RelationQueryPhysicalStageKind.RelationshipKeyExtraction
                && stage.Provenance.Nodes.Contains(FederatedLoadRelationFixture.EquipmentTraversalNodeId));
        var customerCorrelation = Assert.Single(
            physical.Stages,
            stage => stage.Kind == RelationQueryPhysicalStageKind.LocalCorrelation
                && stage.Provenance.Nodes.Contains(FederatedLoadRelationFixture.CustomerTraversalNodeId));
        var equipmentCorrelation = Assert.Single(
            physical.Stages,
            stage => stage.Kind == RelationQueryPhysicalStageKind.LocalCorrelation
                && stage.Provenance.Nodes.Contains(FederatedLoadRelationFixture.EquipmentTraversalNodeId));
        Assert.Equal([rootProjection.Id], customerExtraction.Dependencies.ToArray());
        Assert.Equal([customerCorrelation.Id], equipmentExtraction.Dependencies.ToArray());
        Assert.All(
            physical.Stages.Where(static stage => stage.Kind == RelationQueryPhysicalStageKind.BatchedIdentityLookup),
            static stage => Assert.Equal(64, stage.BatchSize));

        var evidence = Assert.Single(
            physical.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.RuntimeEvidenceAssembly);
        Assert.Equal([equipmentCorrelation.Id], evidence.Dependencies.ToArray());
        Assert.All(physical.Stages, static stage =>
            Assert.False(stage.Provenance.Nodes.IsDefaultOrEmpty
                && stage.Provenance.Inputs.IsDefaultOrEmpty
                && stage.Provenance.Requirements.IsDefaultOrEmpty));
        var terminal = Assert.Single(
            physical.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.ReferenceInterpreterTerminal);
        Assert.Equal(
            realization.Requirements.Select(static requirement => requirement.Id),
            terminal.Provenance.Requirements);
        Assert.Empty(physical.Diagnostics);
    }

    [Fact]
    public void Compile_PlacementDeclarationOrderDoesNotAffectPlacementOrPlanIdentity()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var realization = Realize(semantic);
        var forward = CreatePlacement(semantic, reverseDeclarations: false);
        var reversed = CreatePlacement(semantic, reverseDeclarations: true);

        var forwardPlan = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            realization,
            forward,
            PhysicalPolicy));
        var priorCulture = CultureInfo.CurrentCulture;
        var priorUiCulture = CultureInfo.CurrentUICulture;
        CompiledRelationQueryPhysicalPlan reversedPlan;
        try
        {
            var nonDefaultCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = nonDefaultCulture;
            CultureInfo.CurrentUICulture = nonDefaultCulture;
            reversedPlan = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
                semantic,
                realization,
                reversed,
                PhysicalPolicy));
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
            CultureInfo.CurrentUICulture = priorUiCulture;
        }

        Assert.Equal(forward.Fingerprint, reversed.Fingerprint);
        Assert.Equal(forwardPlan.Fingerprint, reversedPlan.Fingerprint);
        Assert.Equal(StageSignatures(forwardPlan), StageSignatures(reversedPlan));
        Assert.Equal(
            forwardPlan.EvaluationOrder.ToArray(),
            reversedPlan.EvaluationOrder.ToArray());
    }

    [Fact]
    public void Compile_IdOnlyRelationDemandPrunesEveryTraversalStage()
    {
        var semantic = Compile(
            FederatedLoadRelationFixture.RelationDocument,
            RelationQueryCompilationDemand.ForRelationFields(
            [
                new(
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    FederatedLoadRelationFixture.SearchIdPath)
            ]));
        Assert.Empty(semantic.InputContract.Traversals);
        var placement = CreatePlacement(semantic);

        var physical = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy));

        Assert.Single(placement.Bindings);
        Assert.Equal(
            [FederatedLoadRelationFixture.LoadIdPath],
            Paths(Assert.Single(semantic.InputContract.Sources).Fields));
        Assert.DoesNotContain(
            physical.Stages,
            static stage => stage.Kind is RelationQueryPhysicalStageKind.RelationshipKeyExtraction
                or RelationQueryPhysicalStageKind.KeyDeduplication
                or RelationQueryPhysicalStageKind.BatchedIdentityLookup
                or RelationQueryPhysicalStageKind.BatchedPredicateLookup
                or RelationQueryPhysicalStageKind.LocalCorrelation);
        Assert.Single(
            physical.Stages,
            static stage => stage.Kind == RelationQueryPhysicalStageKind.SuppliedInput);
    }

    [Fact]
    public void Compile_FieldlessAggregationDoesNotRequireOrCiteFieldProjection()
    {
        var semantic = Compile(FederatedLoadRelationFixture.AggregationDocument);
        var source = Assert.Single(semantic.InputContract.Sources);
        Assert.Empty(source.Fields);
        var sourceId = SourceForShape(source.Shape);
        var withoutProjection = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(
                semantic,
                capabilityDeficientSource: sourceId,
                missingCapability: RelationQueryPrimitiveCapabilityKind.FieldProjection),
            PhysicalPolicy));
        var withProjection = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(semantic),
            PhysicalPolicy));
        var projectionEvidence = new RelationQueryTargetCapabilityEvidenceId(
            $"evidence/{(int)RelationQueryPrimitiveCapabilityKind.FieldProjection}");

        CompiledRelationQueryPhysicalPlan[] plans = [withoutProjection, withProjection];
        foreach (var physical in plans)
        {
            var read = Assert.Single(
                physical.Stages,
                static stage => stage.Kind == RelationQueryPhysicalStageKind.SourceRead);
            Assert.Empty(read.RequestedFields);
            Assert.DoesNotContain(
                read.Provenance.CapabilityEvidence,
                evidence => evidence.Evidence == projectionEvidence);
        }
    }

    [Fact]
    public void Compile_LocalCorrelationProvenanceAttributesCoreLoweringWithoutSourceEvidence()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipmentSource = SourceForShape(FederatedLoadRelationFixture.EquipmentShapeId);
        var physical = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(
                semantic,
                capabilityDeficientSource: equipmentSource,
                missingCapability: RelationQueryPrimitiveCapabilityKind.LocalCorrelation),
            PhysicalPolicy));
        var correlations = physical.Stages
            .Where(static stage => stage.Kind == RelationQueryPhysicalStageKind.LocalCorrelation)
            .ToArray();

        Assert.Equal(2, correlations.Length);
        Assert.All(correlations, static stage =>
        {
            Assert.Empty(stage.Provenance.CapabilityEvidence);
            Assert.Single(stage.Provenance.PlacementBindings);
            Assert.NotNull(stage.Provenance.LoweringRule);
            Assert.Single(stage.Provenance.PolicyDecisions);
        });
        var localCorrelationEvidence = new RelationQueryTargetCapabilityEvidenceId(
            $"evidence/{(int)RelationQueryPrimitiveCapabilityKind.LocalCorrelation}");
        Assert.DoesNotContain(
            physical.Stages.SelectMany(static stage => stage.Provenance.CapabilityEvidence),
            evidence => evidence.Evidence == localCorrelationEvidence);
    }

    [Fact]
    public void Compile_MissingTraversalPlacementFailsClosedWithAttributableDiagnostic()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        var placement = CreatePlacement(semantic, omittedInput: equipment.Input.Id);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMissing
            && diagnostic.Input == equipment.Input.Id);
    }

    [Fact]
    public void Compile_MissingLookupCapabilityFailsClosedWithAttributableDiagnostic()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        var equipmentSource = SourceForShape(FederatedLoadRelationFixture.EquipmentShapeId);
        var placement = CreatePlacement(
            semantic,
            capabilityDeficientSource: equipmentSource,
            missingCapability: RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.CapabilityEvidenceMissing
            && diagnostic.Input == equipment.Input.Id);
    }

    [Fact]
    public void Compile_ForwardReferenceReadRequiresExplicitSourceCapability()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var loadSource = Assert.Single(semantic.InputContract.Sources);
        var placement = CreatePlacement(
            semantic,
            capabilityDeficientSource: SourceForShape(FederatedLoadRelationFixture.LoadShapeId),
            missingCapability: RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.CapabilityEvidenceMissing
            && diagnostic.Input == loadSource.Input.Id);
    }

    [Fact]
    public void Compile_LookupCapabilityOutsideItsBatchBoundaryFailsClosed()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        var equipmentSource = SourceForShape(FederatedLoadRelationFixture.EquipmentShapeId);
        var placement = CreatePlacement(
            semantic,
            boundaryConstrainedSource: equipmentSource,
            boundaryConstrainedCapability: RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            capabilityBoundaryKind: RelationQueryOperatingBoundaryKind.MaximumBatchSize,
            capabilityBoundaryLimit: 8);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.OperatingBoundaryInvalid
            && diagnostic.Input == equipment.Input.Id);
    }

    [Fact]
    public void Compile_SatisfiedCapabilityBoundaryIsRetainedInStageProvenance()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);
        var equipmentSource = SourceForShape(FederatedLoadRelationFixture.EquipmentShapeId);
        var boundary = new RelationQueryOperatingBoundaryId(
            $"boundary/{(int)RelationQueryOperatingBoundaryKind.MaximumBatchSize}");
        var placement = CreatePlacement(
            semantic,
            boundaryConstrainedSource: equipmentSource,
            boundaryConstrainedCapability: RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            capabilityBoundaryKind: RelationQueryOperatingBoundaryKind.MaximumBatchSize,
            capabilityBoundaryLimit: PhysicalPolicy.MaximumBatchSize);

        var physical = SuccessfulPlan(RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            placement,
            PhysicalPolicy));

        var stage = Assert.Single(physical.Stages, candidate =>
            candidate.Kind == RelationQueryPhysicalStageKind.BatchedIdentityLookup
            && candidate.SemanticInputs.Contains(equipment.Input.Id));
        Assert.Contains(boundary, stage.Provenance.OperatingBoundaries);
        Assert.Contains(stage.Provenance.CapabilityEvidence, evidence =>
            evidence.Source == equipmentSource
            && evidence.Evidence == new RelationQueryTargetCapabilityEvidenceId(
                $"evidence/{(int)RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup}"));

        var json = JsonSerializer.Serialize(physical, RelationQueryJsonSerializer.CreateOptions());
        var roundTrip = Assert.IsType<CompiledRelationQueryPhysicalPlan>(
            JsonSerializer.Deserialize<CompiledRelationQueryPhysicalPlan>(
                json,
                RelationQueryJsonSerializer.CreateOptions()));
        var roundTripStage = Assert.Single(roundTrip.Stages, candidate => candidate.Id == stage.Id);
        Assert.Equal(
            stage.Provenance.OperatingBoundaries.ToArray(),
            roundTripStage.Provenance.OperatingBoundaries.ToArray());
        Assert.Equal(
            stage.Provenance.CapabilityEvidence.ToArray(),
            roundTripStage.Provenance.CapabilityEvidence.ToArray());
        Assert.Equal(physical.Fingerprint, roundTrip.Fingerprint);
    }

    [Fact]
    public void Compile_PartitionPlacementFailsDuringPlanningInsteadOfExecution()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var equipment = Traversal(
            semantic,
            FederatedLoadRelationFixture.EquipmentTraversalNodeId);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(semantic, partitionedInput: equipment.Input.Id),
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.OperatingBoundaryInvalid
            && diagnostic.Input == equipment.Input.Id);
    }

    [Fact]
    public void Compile_StaleRealizationAndPlacementAreRejectedBeforeLowering()
    {
        var query = Compile(FederatedLoadRelationFixture.QueryDocument);
        var relation = Compile(FederatedLoadRelationFixture.RelationDocument);
        var queryRealization = Realize(query);
        var relationRealization = Realize(relation);
        var queryPlacement = CreatePlacement(query);
        var relationPlacement = CreatePlacement(relation);

        var staleRealization = RelationQueryPhysicalPlanner.Compile(
            relation,
            queryRealization,
            relationPlacement,
            PhysicalPolicy);
        var stalePlacement = RelationQueryPhysicalPlanner.Compile(
            relation,
            relationRealization,
            queryPlacement,
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Invalid, staleRealization.Status);
        Assert.Contains(staleRealization.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.RealizationInvalid);
        Assert.Equal(RelationQueryPhysicalPlanningStatus.Invalid, stalePlacement.Status);
        Assert.Contains(stalePlacement.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.PlacementMismatch);
    }

    [Fact]
    public void Compile_NoncanonicalTerminalRealizationIsRejectedBeforeLowering()
    {
        var semantic = Compile(FederatedLoadRelationFixture.QueryDocument);
        var alternateInterpreter = new RelationQueryInMemoryInterpreter(
            RelationQueryTemporalExecutionCapabilityProfile.None);
        var alternateRealization = alternateInterpreter.Realize(semantic);
        Assert.True(alternateRealization.IsRealizable);
        Assert.NotEqual(Realize(semantic).Fingerprint, alternateRealization.Fingerprint);

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            alternateRealization,
            CreatePlacement(semantic),
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Invalid, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.RealizationInvalid);
    }

    [Fact]
    public void Compile_ArbitraryCrossSourceJoinIsUnavailableInsteadOfSilentlyLowered()
    {
        var semantic = Compile(CreateArbitraryJoinDocument());

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(semantic),
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.CrossSourceJoinUnsupported);
    }

    [Fact]
    public void Compile_NonUniqueEquijoinIsRejectedAsUnboundedLocalWork()
    {
        var semantic = Compile(CreateNonUniqueEquijoinDocument());

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(semantic),
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.LocalWorkUnbounded);
    }

    [Fact]
    public void Compile_TraversalAfterFilterIsRejectedUntilReachabilityCanBeStaged()
    {
        var semantic = Compile(CreateFilteredTraversalDocument());

        var result = RelationQueryPhysicalPlanner.Compile(
            semantic,
            Realize(semantic),
            CreatePlacement(semantic),
            PhysicalPolicy);

        Assert.Equal(RelationQueryPhysicalPlanningStatus.Unavailable, result.Status);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == RelationQueryPhysicalPlanningDiagnosticCodes.LoweringUnavailable);
    }

    static CompiledRelationQueryPlan Compile(
        RelationQueryDocument document,
        RelationQueryCompilationDemand? demand = null)
    {
        var result = RelationQueryStaticCompiler.Compile(new(
            document,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument,
            demand));
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPlan>(result.Plan);
    }

    static RelationQueryRealizationReport Realize(CompiledRelationQueryPlan plan) =>
        RelationQueryInMemoryInterpreter.Default.Realize(plan);

    static CompiledRelationQueryPhysicalPlan SuccessfulPlan(RelationQueryPhysicalPlanningResult result)
    {
        Assert.True(
            result.IsSuccessful,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        return Assert.IsType<CompiledRelationQueryPhysicalPlan>(result.Plan);
    }

    static RelationQuerySourcePlacement CreatePlacement(
        CompiledRelationQueryPlan plan,
        bool reverseDeclarations = false,
        RelationQueryInputId? omittedInput = null,
        RelationQuerySourceInstanceId? capabilityDeficientSource = null,
        RelationQueryPrimitiveCapabilityKind? missingCapability = null,
        RelationQuerySourceInstanceId? boundaryConstrainedSource = null,
        RelationQueryPrimitiveCapabilityKind? boundaryConstrainedCapability = null,
        RelationQueryOperatingBoundaryKind? capabilityBoundaryKind = null,
        long? capabilityBoundaryLimit = null,
        RelationQueryInputId? partitionedInput = null)
    {
        List<RelationQuerySourcePlacementBinding> bindings = [];
        foreach (var source in plan.InputContract.Sources)
        {
            if (source.Input.Id == omittedInput)
                continue;
            var sourceId = SourceForShape(source.Shape);
            bindings.Add(new(
                new($"placement/{Uri.EscapeDataString(source.Input.Id.Value)}"),
                source.Input.Id,
                source.Node,
                source.Binding,
                source.Shape,
                sourceId,
                RelationQuerySourcePlacementBindingKind.SourceSet,
                source.Role == RelationQuerySourceInputRole.RelationRoot
                    ? RelationQuerySourceAcquisitionKind.Supplied
                    : RelationQuerySourceAcquisitionKind.BoundedEnumeration,
                RelationQuerySourcePlacementOrigin.Explicit,
                identity: source.Role == RelationQuerySourceInputRole.RelationRoot
                    ? null
                    : new(source.Shape, "$identity"),
                fields: FieldBindings(source.Fields),
                partition: source.Input.Id == partitionedInput
                    ? new("$partition")
                    : null));
        }

        foreach (var traversal in plan.InputContract.Traversals)
        {
            if (traversal.Input.Id == omittedInput)
                continue;
            var sourceId = SourceForShape(traversal.ResultShape);
            bindings.Add(new(
                new($"placement/{Uri.EscapeDataString(traversal.Input.Id.Value)}"),
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                sourceId,
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, "$identity"),
                FieldBindings(traversal.Fields),
                traversal.Input.Direction == RelationshipTraversalDirection.Inverse
                    ? [new(traversal.Input.Id, traversal.Definition.SourceReference, "$relationship")]
                    : [],
                partition: traversal.Input.Id == partitionedInput
                    ? new("$partition")
                    : null));
        }

        var sources = bindings
            .Select(static binding => binding.Source)
            .Distinct()
            .Select(source => new RelationQuerySourceInstance(
                source,
                new($"domain/{source.Value}"),
                PrimitiveProfile(
                    source,
                    source == capabilityDeficientSource ? missingCapability : null,
                    source == boundaryConstrainedSource ? boundaryConstrainedCapability : null,
                    source == boundaryConstrainedSource ? capabilityBoundaryKind : null,
                    source == boundaryConstrainedSource ? capabilityBoundaryLimit : null),
                new(
                    maximumBatchSize: 128,
                    maximumBufferedRows: 1_000,
                    maximumFanOut: 100,
                    maximumConcurrency: 4)))
            .ToArray();

        if (reverseDeclarations)
        {
            Array.Reverse(sources);
            bindings.Reverse();
        }

        return new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            conventionSetVersion: "tests/federated-placement-conventions/v1",
            [.. sources],
            [.. bindings]);
    }

    static ImmutableArray<RelationQuerySourceFieldBinding> FieldBindings(
        ImmutableArray<RelationQueryFieldInputContract> fields) =>
    [
        .. fields.Select(static field => new RelationQuerySourceFieldBinding(
            field.Input.Id,
            field.Input.Field.Path,
            $"field/{Uri.EscapeDataString(field.Input.Id.Value)}"))
    ];

    static RelationQueryTargetCapabilityProfile PrimitiveProfile(
        RelationQuerySourceInstanceId source,
        RelationQueryPrimitiveCapabilityKind? excluded,
        RelationQueryPrimitiveCapabilityKind? constrained = null,
        RelationQueryOperatingBoundaryKind? boundaryKind = null,
        long? boundaryLimit = null)
    {
        RelationQueryPrimitiveCapabilityKind[] capabilities =
        [
            RelationQueryPrimitiveCapabilityKind.KeyExtraction,
            RelationQueryPrimitiveCapabilityKind.BatchedKeyLookup,
            RelationQueryPrimitiveCapabilityKind.PredicateRead,
            RelationQueryPrimitiveCapabilityKind.CompleteSetEnumeration,
            RelationQueryPrimitiveCapabilityKind.LocalCorrelation,
            RelationQueryPrimitiveCapabilityKind.HashJoin,
            RelationQueryPrimitiveCapabilityKind.FieldProjection,
            RelationQueryPrimitiveCapabilityKind.ObservationIdentityRead,
            RelationQueryPrimitiveCapabilityKind.RelationshipReferenceRead,
            RelationQueryPrimitiveCapabilityKind.ProvenanceTracking,
            RelationQueryPrimitiveCapabilityKind.BatchedPredicateLookup
        ];
        RelationQueryOperatingBoundaryId? boundaryId = boundaryKind is { } kind
            ? new($"boundary/{(int)kind}")
            : null;
        return new(
            new($"fake/{source.Value}"),
            new($"fake/{source.Value}/profile-v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                .. capabilities
                    .Where(capability => capability != excluded)
                    .Select(capability => new RelationQueryTargetCapabilityEvidence(
                        new($"evidence/{(int)capability}"),
                        new PrimitiveRelationQueryCapability(capability),
                        capability == constrained && boundaryId is { } id ? [id] : []))
            ],
            boundaryId is { } declaredBoundary && boundaryKind is { } declaredKind
                ? [new(declaredBoundary, declaredKind, boundaryLimit)]
                : []);
    }

    static RelationQuerySourceInstanceId SourceForShape(QualifiedShapeId shape)
    {
        if (shape == FederatedLoadRelationFixture.LoadShapeId)
            return new("fake/loads");
        if (shape == FederatedLoadRelationFixture.CustomerShapeId)
            return new("fake/customers");
        if (shape == FederatedLoadRelationFixture.EquipmentShapeId)
            return new("fake/equipment");
        throw new InvalidOperationException($"No fake physical source is configured for '{shape}'.");
    }

    static RelationQueryTraversalInputContract Traversal(
        CompiledRelationQueryPlan plan,
        QueryNodeId node) =>
        Assert.Single(plan.InputContract.Traversals, traversal => traversal.Input.Traversal == node);

    static FieldPath[] Paths(ImmutableArray<RelationQueryFieldInputContract> fields) =>
    [
        .. fields.Select(static field => field.Input.Field.Path)
            .OrderBy(static path => path.ToString(), StringComparer.Ordinal)
    ];

    static string[] StageSignatures(CompiledRelationQueryPhysicalPlan plan) =>
    [
        .. plan.Stages.Select(static stage => string.Join(
            "|",
            stage.Id.Value,
            ((int)stage.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(",", stage.Dependencies.Select(static dependency => dependency.Value)),
            stage.PlacementBinding?.Value ?? string.Empty,
            string.Join(",", stage.SemanticInputs.Select(static input => input.Value)),
            string.Join(",", stage.RequestedFields.Select(static input => input.Value)),
            stage.BatchSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty))
    ];

    static RelationQueryDocument CreateArbitraryJoinDocument()
    {
        var customerSource = new QueryNodeId("arbitrary-customers");
        var join = new QueryNodeId("arbitrary-join");
        var project = new QueryNodeId("arbitrary-project");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("federated-arbitrary-join-query"),
            new("FederatedArbitraryJoinQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    FederatedLoadRelationFixture.LoadBinding,
                    FederatedLoadRelationFixture.LoadShapeId),
                new SourceQueryNode(
                    customerSource,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.CustomerShapeId),
                new JoinQueryNode(
                    join,
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    customerSource,
                    JoinKind.Inner,
                    Expr.Const(true)),
                new ProjectQueryNode(
                    project,
                    join,
                    FederatedLoadRelationFixture.SearchBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchIdAssignmentId,
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath))
                    ])
            ]),
            [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateNonUniqueEquijoinDocument()
    {
        var customerSource = new QueryNodeId("non-unique-customers");
        var join = new QueryNodeId("non-unique-join");
        var project = new QueryNodeId("non-unique-project");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("federated-non-unique-equijoin-query"),
            new("FederatedNonUniqueEquijoinQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    FederatedLoadRelationFixture.LoadBinding,
                    FederatedLoadRelationFixture.LoadShapeId),
                new SourceQueryNode(
                    customerSource,
                    FederatedLoadRelationFixture.CustomerBinding,
                    FederatedLoadRelationFixture.CustomerShapeId),
                new JoinQueryNode(
                    join,
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    customerSource,
                    JoinKind.Inner,
                    Expr.Eq(
                        Expr.Field(
                            FederatedLoadRelationFixture.LoadBinding,
                            FederatedLoadRelationFixture.LoadStatusPath),
                        Expr.Field(
                            FederatedLoadRelationFixture.CustomerBinding,
                            FederatedLoadRelationFixture.CustomerTypePath))),
                new ProjectQueryNode(
                    project,
                    join,
                    FederatedLoadRelationFixture.SearchBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchIdAssignmentId,
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath))
                    ])
            ]),
            [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }

    static RelationQueryDocument CreateFilteredTraversalDocument()
    {
        var filter = new QueryNodeId("filtered-loads");
        var traversal = new QueryNodeId("filtered-customer");
        var project = new QueryNodeId("filtered-project");
        var definition = new Cohesive.Relations.IR.QueryDefinition(
            new("federated-filtered-traversal-query"),
            new("FederatedFilteredTraversalQuery"),
            new LogicalQueryDefinition(
            [
                new SourceQueryNode(
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    FederatedLoadRelationFixture.LoadBinding,
                    FederatedLoadRelationFixture.LoadShapeId),
                new FilterQueryNode(
                    filter,
                    FederatedLoadRelationFixture.LoadSourceNodeId,
                    Expr.Eq(
                        Expr.Field(
                            FederatedLoadRelationFixture.LoadBinding,
                            FederatedLoadRelationFixture.LoadStatusPath),
                        Expr.Const("Open"))),
                new TraverseRelationshipQueryNode(
                    traversal,
                    filter,
                    FederatedLoadRelationFixture.LoadBinding,
                    FederatedLoadRelationFixture.LoadCustomerRelationshipId,
                    RelationshipTraversalDirection.Forward,
                    FederatedLoadRelationFixture.CustomerBinding,
                    JoinKind.Left,
                    QueryInputRequirement.Optional),
                new ProjectQueryNode(
                    project,
                    traversal,
                    FederatedLoadRelationFixture.SearchBinding,
                    FederatedLoadRelationFixture.LoadSearchShapeId,
                    [
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchIdAssignmentId,
                            FederatedLoadRelationFixture.SearchIdPath,
                            Expr.Field(
                                FederatedLoadRelationFixture.LoadBinding,
                                FederatedLoadRelationFixture.LoadIdPath)),
                        new ProjectionAssignment(
                            FederatedLoadRelationFixture.SearchCustomerNameAssignmentId,
                            FederatedLoadRelationFixture.SearchCustomerNamePath,
                            Expr.Field(
                                FederatedLoadRelationFixture.CustomerBinding,
                                FederatedLoadRelationFixture.CustomerNamePath))
                    ])
            ]),
            [new RowsQueryResultDefinition(FederatedLoadRelationFixture.RowsResultId, project)]);
        return RelationQueryDocument.FromDefinition(definition);
    }
}
