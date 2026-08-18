using System.Collections.Immutable;
using Cohesive.Execution;
using Cohesive.MaterializationHarness.Model;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Compilation;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

/// <summary>Determinism and capability coverage for the real-container harness semantic authority.</summary>
public sealed class FreightOrderHarnessModelTests
{
    [Fact]
    public void CanonicalModelIsDeterministicAndRequiresCrossEntityHydration()
    {
        var first = FreightOrderMaterializationModel.Create();
        var second = FreightOrderMaterializationModel.Create();

        Assert.Equal(first.DefinitionFingerprint, second.DefinitionFingerprint);
        Assert.Equal(
            "8b2162bdcec768183420694d056c703ed814c972e5560a9a70f7bfbe37339805",
            first.DefinitionFingerprint.Value);
        Assert.Equal(MaterializationSynchronizationMode.Rebuild, first.Definition.UpdatePolicy.SupportedModes);
        Assert.Equal(MaterializationConsistencyKind.Reconciliation, first.Definition.UpdatePolicy.Consistency);
        Assert.Single(first.Plan.InputContract.Sources);
        Assert.Equal(3, first.Plan.InputContract.Traversals.Length);
        Assert.Equal(4, first.Definition.Sources.Length);
        Assert.All(
            first.Definition.Sources,
            static source => Assert.Contains(
                source.Capabilities,
                capability => capability.Capability == MaterializationCapabilityKind.SourceContinuation));
        Assert.Equal(FreightOrderMaterializationModel.OrderShapeId, first.Root.Shape);
        Assert.Equal(FreightOrderMaterializationModel.OrderSearchDocumentShapeId, first.Output.Shape);
        Assert.All(
            new[]
            {
                first.Storage.Order,
                first.Storage.CustomerAccount,
                first.Storage.OrderStop,
                first.Storage.Location
            },
            static entity => Assert.True(entity.Shape.HasRole(Cohesive.Model.ShapeRoles.Entity)));
        Assert.Contains(first.Storage.Order.Fields, static field => field.Name.Value == "createdAt");
        Assert.Contains(first.Storage.OrderStop.Fields, static field => field.Name.Value == "scheduledStart");
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
            .WithFreshnessPolicy(new(maximumLagMilliseconds: 30_000))
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
                return new MaterializationSourceRequirement(
                    input,
                    [
                        new(
                            new($"{input.Value}/read"),
                            readCapability,
                            [
                                MaterializationGuaranteeKind.StableOrdering,
                                MaterializationGuaranteeKind.RequestLocalCompleteness,
                                MaterializationGuaranteeKind.Reconciliation
                            ],
                            [
                                new(MaterializationLimitKind.ReadItems, maximumReadItems),
                                new(MaterializationLimitKind.ReadBytes, maximumReadBytes)
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
            })
        ];
        ImmutableArray<MaterializationCapabilityRequirement> target =
        [
            Target("target/isolation", MaterializationCapabilityKind.TargetGenerationIsolation),
            Target("target/upsert", MaterializationCapabilityKind.TargetBulkUpsert),
            Target("target/outcomes", MaterializationCapabilityKind.TargetPerItemOutcomes),
            Target("target/seal", MaterializationCapabilityKind.TargetSeal),
            Target("target/validation", MaterializationCapabilityKind.TargetValidation),
            Target("target/promotion", MaterializationCapabilityKind.TargetFencedPromotion),
            Target("target/abandonment", MaterializationCapabilityKind.TargetGenerationAbandonment),
            Target("target/retirement", MaterializationCapabilityKind.TargetRetirement),
            Target("target/cleanup", MaterializationCapabilityKind.TargetCleanup)
        ];
        return new(
            new("freight/order-search"),
            MaterializationRelationReference.From(semantics.CompilationRequest, semantics.Output.Id),
            sources,
            target,
            new(
                MaterializationSynchronizationMode.Rebuild,
                MaterializationConsistencyKind.Reconciliation,
                MaterializationIdempotencyKind.StableOutputIdentityAndVersion),
            new(maximumAttempts: 3, MaterializationFailureDisposition.Stop),
            new(maximumLagMilliseconds: 30_000),
            [],
            Provenance("freight-order-search"));

        static MaterializationCapabilityRequirement Target(
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
                    new(MaterializationLimitKind.WriteItems, maximumWriteItems),
                    new(MaterializationLimitKind.WriteBytes, maximumWriteBytes)
                ]
                : [],
            MaterializationSynchronizationMode.Rebuild);
    }

    static ExecutionProvenance Provenance(string source) => new(
        new("cohesive-materialization-harness", "1"),
        new($"eng/materialization-harness/model/{source}"),
        DocumentOrigin.Generated);
}
