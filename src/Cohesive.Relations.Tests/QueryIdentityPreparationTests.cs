using System.Collections.Immutable;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;

namespace Cohesive.Relations.Tests;

public sealed class QueryIdentityPreparationTests
{
    [Fact]
    public void ProvenanceRetainsCanonicalStorageAndNormalizesReverseInput()
    {
        ImmutableArray<QueryNodeId> nodes = [new("a"), new("b")];
        var canonical = new RelationQueryPhysicalStageProvenance(nodes: nodes);
        var reversed = new RelationQueryPhysicalStageProvenance(nodes: [new("b"), new("a")]);
        Assert.True(nodes.Equals(canonical.Nodes));
        Assert.Equal(nodes.ToArray(), reversed.Nodes.ToArray());
        Assert.False(canonical.Inputs.IsDefault);
        Assert.Empty(canonical.Inputs);
        var duplicate = Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalStageProvenance(nodes: [new("b"), new("a"), new("b")]));
        Assert.Equal("nodes", duplicate.ParamName);
        Assert.Contains("duplicate identities", duplicate.Message);
        var invalid = Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalStageProvenance(nodes: [new("a"), new("a"), default]));
        Assert.Contains("null or default", invalid.Message);
        var noAttribution = Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalStageProvenance());
        Assert.Contains("requires semantic", noAttribution.Message);
    }

    [Fact]
    public void ProvenanceStillRejectsNullAndDuplicateQualifiedEvidence()
    {
        var evidence = new RelationQueryPhysicalCapabilityEvidenceReference(new("source"), new("target"), new("profile"), new("evidence"));
        Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalStageProvenance(nodes: [new("a")], capabilityEvidence: [null!]));
        Assert.Throws<ArgumentException>(() => new RelationQueryPhysicalStageProvenance(nodes: [new("a")], capabilityEvidence: [evidence, evidence]));
    }

    [Fact]
    public void CanonicalProvenanceHasFixedAllocationAt128Nodes()
    {
        var nodes = Enumerable.Range(0, 128).Select(i => new QueryNodeId($"node/{i:D4}")).ToImmutableArray();
        for (var i = 0; i < 20; i++) GC.KeepAlive(new RelationQueryPhysicalStageProvenance(nodes: nodes));
        var samples = new long[7];
        for (var i = 0; i < samples.Length; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var result = new RelationQueryPhysicalStageProvenance(nodes: nodes);
            samples[i] = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(result);
        }
        Array.Sort(samples);
        Assert.InRange(samples[3], 0, 256);
    }

    [Theory]
    [InlineData(32, "fc89dc8a3c3985fee430f823cd3b8d5c15731efcb3d279aafb6ac0d1b5f41f38", "79ac2eb160fcb0094dcf5e21c5da494135fdd0b306c5b64a7db08f22530a3ee2", "a0b1c3edd93bf5fc521a3607efa5f2f2656493dadfb5d836b50d456144a0684e")]
    [InlineData(1024, "fc89dc8a3c3985fee430f823cd3b8d5c15731efcb3d279aafb6ac0d1b5f41f38", "f4b997eee540fef5e804fc7947dcabd972b7672d18ba086e4c19b648892e6dbe", "16f70bfa68821aebc87e94368bb6881528d2979caa74fc1c72754262e76826d6")]
    public void FederatedPhysicalArtifactsKeepPreChangeDigests(int rows, string realization, string placement, string physical)
    {
        var fixture = FederatedLoadPhysicalExecutionFixture.Create(FederatedLoadRelationFixture.RelationDocument,
            maximumBatchSize: 32, maximumLocalRows: rows, maximumReferenceKeysPerObservation: 1,
            customerMaximumBufferedRows: rows);
        var policy = FederatedLoadPhysicalExecutionFixture.CreatePolicy(maximumBatchSize: 32,
            maximumLocalRows: rows, maximumReferenceKeysPerObservation: 1);
        var plan = RelationQueryPhysicalPlanner.Compile(fixture.Plan, fixture.Realization, fixture.Placement, policy).Plan!;
        Assert.Equal(realization, plan.Realization.Value);
        Assert.Equal(placement, plan.Placement.Fingerprint.Value);
        Assert.Equal(physical, plan.Fingerprint.Value);
        Assert.Equal(physical, RelationQueryPhysicalPlanFingerprinter.Compute(plan).Value);
    }
}
