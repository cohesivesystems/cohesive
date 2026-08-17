using Cohesive.MaterializationHarness.Model;
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
            "956e59762ba24a3dc459baafa8849681a415a7f3580d79a58cacfabe39fbb3ab",
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
        Assert.Equal(
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(first.Plan)),
            RelationQueryCompiledPlanReferenceFingerprinter.Compute(
                RelationQueryCompiledPlanReference.From(second.Plan)));
    }
}
