using System.Collections.Immutable;
using Cohesive.Model;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Relations.Serialization;

namespace Cohesive.Relations.TestFixtures;

static class FederatedLoadPhysicalExecutionFixture
{
    public static readonly RelationQuerySourceInstanceId LoadsSource = new("fake/loads");
    public static readonly RelationQuerySourceInstanceId CustomersSource = new("fake/customers");
    public static readonly RelationQuerySourceInstanceId EquipmentSource = new("fake/equipment");

    public static Compilation Create(
        RelationQueryDocument document,
        RelationQueryCompilationDemand? demand = null,
        long maximumBatchSize = 2,
        long maximumLocalRows = 100,
        long maximumReferenceKeysPerObservation = 100,
        long customerMaximumBufferedRows = 100,
        long maximumBufferedRows = 100)
    {
        var compilation = RelationQueryStaticCompiler.Compile(new(
            document,
            FederatedLoadRelationFixture.ShapeGraphDocuments,
            FederatedLoadRelationFixture.RelationshipCatalogDocument,
            demand));
        if (!compilation.IsSuccessful || compilation.Plan is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                compilation.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code} {diagnostic.Location}: {diagnostic.Message}")));
        }

        return Create(
            compilation.Plan,
            maximumBatchSize,
            maximumLocalRows,
            maximumReferenceKeysPerObservation,
            customerMaximumBufferedRows,
            maximumBufferedRows);
    }

    public static Compilation Create(
        CompiledRelationQueryPlan plan,
        long maximumBatchSize = 2,
        long maximumLocalRows = 100,
        long maximumReferenceKeysPerObservation = 100,
        long customerMaximumBufferedRows = 100,
        long maximumBufferedRows = 100)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var realization = RelationQueryInMemoryInterpreter.Default.Realize(plan);
        var placement = CreatePlacement(
            plan,
            maximumBatchSize,
            customerMaximumBufferedRows,
            maximumBufferedRows);
        var policy = CreatePolicy(
            maximumBatchSize,
            maximumLocalRows,
            maximumReferenceKeysPerObservation,
            maximumBufferedRows);
        var physicalResult = RelationQueryPhysicalPlanner.Compile(
            plan,
            realization,
            placement,
            policy);
        if (!physicalResult.IsSuccessful || physicalResult.Plan is null)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                physicalResult.Diagnostics.Select(static diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
        }

        return new(plan, realization, placement, physicalResult.Plan);
    }

    public static RelationQueryPhysicalPlanningPolicy CreatePolicy(
        long maximumBatchSize = 2,
        long maximumLocalRows = 100,
        long maximumReferenceKeysPerObservation = 100,
        long maximumBufferedRows = 100) =>
        new(
            new($"tests/federated-execution-policy/batch-{maximumBatchSize}/v1"),
            conventionSetVersion: "tests/federated-execution-conventions/v1",
            maximumBatchSize,
            maximumBufferedRows,
            maximumLocalRows,
            maximumFanOut: 100,
            maximumReferenceKeysPerObservation,
            maximumConcurrency: 4);

    public static RelationQuerySourceInstance Source(
        Compilation compilation,
        RelationQuerySourceInstanceId source) =>
        compilation.Placement.SourceInstances.Single(candidate => candidate.Id == source);

    public static ImmutableArray<RelationQueryCapabilityEvidence> AvailableCapabilities(
        CompiledRelationQueryPlan plan) =>
    [
        .. plan.RequirementGraph.Inputs
            .OfType<RelationQueryCapabilityInput>()
            .Select(static input => new RelationQueryCapabilityEvidence(
                input.Id,
                RelationQueryCapabilityEvidenceState.Available,
                "tests/in-memory-capability"))
    ];

    public static RelationQuerySourcePlacement CreatePlacement(
        CompiledRelationQueryPlan plan,
        long maximumBatchSize = 2,
        long customerMaximumBufferedRows = 100,
        long maximumBufferedRows = 100,
        IReadOnlyDictionary<QualifiedShapeId, RelationQuerySourceInstanceId>? sourceAliases = null)
    {
        List<RelationQuerySourcePlacementBinding> bindings = [];
        foreach (var source in plan.InputContract.Sources)
        {
            var sourceId = SourceForShape(source.Shape, sourceAliases);
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
                fields: FieldBindings(source.Fields)));
        }

        foreach (var traversal in plan.InputContract.Traversals)
        {
            bindings.Add(new(
                new($"placement/{Uri.EscapeDataString(traversal.Input.Id.Value)}"),
                traversal.Input.Id,
                traversal.Input.Traversal,
                traversal.Result,
                traversal.ResultShape,
                SourceForShape(traversal.ResultShape, sourceAliases),
                RelationQuerySourcePlacementBindingKind.RelationshipTraversal,
                RelationQuerySourceAcquisitionKind.BoundedLookup,
                RelationQuerySourcePlacementOrigin.Explicit,
                new(traversal.ResultShape, "$identity"),
                FieldBindings(traversal.Fields),
                traversal.Input.Direction == RelationshipTraversalDirection.Inverse
                    ? [new(traversal.Input.Id, traversal.Definition.SourceReference, "$relationship")]
                    : []));
        }

        var sources = bindings
            .Select(static binding => binding.Source)
            .Distinct()
            .Select(source => new RelationQuerySourceInstance(
                source,
                new($"domain/{source.Value}"),
                PrimitiveProfile(source),
                new(
                    maximumBatchSize,
                    maximumBufferedRows: source == CustomersSource
                        ? customerMaximumBufferedRows
                        : maximumBufferedRows,
                    maximumFanOut: 100,
                    maximumConcurrency: 4)))
            .ToImmutableArray();

        return new(
            RelationQuerySourcePlacement.CurrentSchemaVersion,
            RelationQueryCompiledPlanReference.From(plan),
            conventionSetVersion: "tests/federated-execution-placement/v1",
            sources,
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
        RelationQuerySourceInstanceId source)
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
        return new(
            new($"fake/{source.Value}"),
            new($"fake/{source.Value}/profile-v1"),
            [RelationQueryDocument.CurrentSchemaVersion],
            [RelationQueryCompilationProvenance.CurrentCompilerProfile],
            [
                .. capabilities.Select(capability => new RelationQueryTargetCapabilityEvidence(
                    new($"evidence/{(int)capability}"),
                    new PrimitiveRelationQueryCapability(capability)))
            ]);
    }

    static RelationQuerySourceInstanceId SourceForShape(
        QualifiedShapeId shape,
        IReadOnlyDictionary<QualifiedShapeId, RelationQuerySourceInstanceId>? sourceAliases)
    {
        if (sourceAliases?.TryGetValue(shape, out var source) == true)
            return source;
        if (shape == FederatedLoadRelationFixture.LoadShapeId)
            return LoadsSource;
        if (shape == FederatedLoadRelationFixture.CustomerShapeId)
            return CustomersSource;
        if (shape == FederatedLoadRelationFixture.EquipmentShapeId)
            return EquipmentSource;

        throw new InvalidOperationException(
            $"No fake physical source is configured for '{shape}'.");
    }

    public sealed record Compilation(
        CompiledRelationQueryPlan Plan,
        RelationQueryRealizationReport Realization,
        RelationQuerySourcePlacement Placement,
        CompiledRelationQueryPhysicalPlan PhysicalPlan);
}
