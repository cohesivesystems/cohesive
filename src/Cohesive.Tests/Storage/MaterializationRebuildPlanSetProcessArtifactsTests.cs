using Cohesive.Execution;
using Cohesive.Processes.IR;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationRebuildPlanSetProcessArtifactsTests
{
    [Fact]
    public void Create_BindsExactPlanSetAndBothBoundedCapacityAwarePhases()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);

        var artifacts = MaterializationRebuildPlanSetProcessFactory.Create(planSet);

        Assert.Equal(MaterializationRebuildPlanSetReference.FromPlanSet(planSet), artifacts.PlanSet);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(artifacts.PlanSet),
            artifacts.ParentPlan.DefinitionReference.DefinitionId);
        Assert.StartsWith(
            MaterializationRebuildPlanSetProcessFactory.ParentDefinitionFamilyId.Value + "/",
            artifacts.ParentPlan.DefinitionReference.DefinitionId.Value,
            StringComparison.Ordinal);
        Assert.Equal(ProcessRecoveryPolicy.ContinueAttempt, artifacts.Leaf.CoordinatorPlan.Definition.RecoveryPolicy);
        Assert.Equal(ProcessRecoveryPolicy.ContinueAttempt, artifacts.PromotionWorkerPlan.Definition.RecoveryPolicy);
        Assert.Equal(ProcessRecoveryPolicy.RestartAttempt, artifacts.ParentPlan.Definition.RecoveryPolicy);

        var build = Assert.IsType<ForEachPartitionProcessNode>(
            artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.BuildLeavesNodeId));
        var promote = Assert.IsType<ForEachPartitionProcessNode>(
            artifacts.ParentPlan.GetNode(MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId));
        Assert.Equal(ProcessPartitionFailurePolicy.AwaitAll, build.Failure);
        Assert.Equal(ProcessPartitionFailurePolicy.AwaitAll, promote.Failure);
        Assert.Equal(planSet.Scheduling.MaximumStartsPerActivation, build.Limits.MaximumStartsPerActivation);
        Assert.Equal(planSet.Scheduling.MaximumParallelism, build.Limits.MaximumParallelism);
        Assert.Equal(build.Limits, promote.Limits);
        Assert.Equal(
            planSet.Placement.CapacityDomains.Select(static domain => domain.Id.Value),
            build.CapacityDomains.Select(static domain => domain.Identity));
        Assert.True(build.CapacityDomains.SequenceEqual(promote.CapacityDomains));
        Assert.NotNull(build.CapacityIdentity);
        Assert.NotNull(promote.CapacityIdentity);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId,
            build.Completed.Target);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.ReadinessBarrierNodeId,
            build.Failed.Target);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.FinalizeNodeId,
            promote.Completed.Target);
        Assert.Equal(
            MaterializationRebuildPlanSetProcessFactory.FinalizeNodeId,
            promote.Failed.Target);
    }

    [Fact]
    public void CanonicalDocuments_RoundTripAndFactoryReproducesFingerprints()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var planSet = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var first = MaterializationRebuildPlanSetProcessFactory.Create(planSet);
        var second = MaterializationRebuildPlanSetProcessFactory.Create(planSet);

        Assert.Equal(
            first.ProcessDocuments.Select(static document => document.Metadata.Fingerprint),
            second.ProcessDocuments.Select(static document => document.Metadata.Fingerprint));
        Assert.Equal(
            first.InteractionDocuments.Select(static document => document.Metadata.Fingerprint),
            second.InteractionDocuments.Select(static document => document.Metadata.Fingerprint));
        foreach (var document in first.ProcessDocuments)
        {
            var json = ExecutionDefinitionJsonSerializer.Serialize(document);
            var validation = ProcessDefinitionDocuments.TryDeserialize(json, out var restored, out _);
            Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
            Assert.Equal(document, restored);
        }
    }

    [Fact]
    public void Create_RealizesProgressivePoliciesSequentiallyAndRejectsAtomicVisibility()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var independent = MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan);
        var progressive = new MaterializationRebuildPlanSet(
            schemaVersion: independent.SchemaVersion,
            request: independent.Request,
            membership: independent.Membership,
            placement: independent.Placement,
            scheduling: independent.Scheduling,
            promotion: new(
                MaterializationRebuildPromotionMode.AllReadyProgressive,
                MaterializationProgressivePromotionFailurePolicy.RetainPromotedAndStop),
            leafPlans: independent.LeafPlans,
            provenance: independent.Provenance);

        var progressiveArtifacts = MaterializationRebuildPlanSetProcessFactory.Create(progressive);
        var promote = Assert.IsType<ForEachPartitionProcessNode>(progressiveArtifacts.ParentPlan.GetNode(
            MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId));

        Assert.Equal(1, promote.Limits.MaximumParallelism);
        Assert.Equal(1, promote.Limits.MaximumStartsPerActivation);
        Assert.Equal(ProcessPartitionFailurePolicy.FailFast, promote.Failure);

        var continueProgressive = new MaterializationRebuildPlanSet(
            schemaVersion: independent.SchemaVersion,
            request: independent.Request,
            membership: independent.Membership,
            placement: independent.Placement,
            scheduling: independent.Scheduling,
            promotion: new(
                MaterializationRebuildPromotionMode.AllReadyProgressive,
                MaterializationProgressivePromotionFailurePolicy.RetainPromotedAndContinue),
            leafPlans: independent.LeafPlans,
            provenance: independent.Provenance);
        var continueArtifacts = MaterializationRebuildPlanSetProcessFactory.Create(continueProgressive);
        Assert.Equal(
            ProcessPartitionFailurePolicy.AwaitAll,
            Assert.IsType<ForEachPartitionProcessNode>(continueArtifacts.ParentPlan.GetNode(
                MaterializationRebuildPlanSetProcessFactory.PromoteLeavesNodeId)).Failure);
        Assert.NotEqual(progressiveArtifacts.PlanSet.PlanSet, continueArtifacts.PlanSet.PlanSet);
        Assert.NotEqual(
            progressiveArtifacts.ParentPlan.DefinitionReference.Fingerprint,
            continueArtifacts.ParentPlan.DefinitionReference.Fingerprint);

        var compensate = new MaterializationRebuildPlanSet(
            schemaVersion: independent.SchemaVersion,
            request: independent.Request,
            membership: independent.Membership,
            placement: independent.Placement,
            scheduling: independent.Scheduling,
            promotion: new(
                MaterializationRebuildPromotionMode.AllReadyProgressive,
                MaterializationProgressivePromotionFailurePolicy.CompensatePromoted),
            leafPlans: independent.LeafPlans,
            provenance: independent.Provenance);
        var compensateArtifacts = MaterializationRebuildPlanSetProcessFactory.Create(compensate);
        Assert.NotNull(compensateArtifacts.CompensationWorkBinding);
        Assert.NotNull(compensateArtifacts.CompensationInvocationBinding);
        Assert.IsType<RequestProcessNode>(compensateArtifacts.ParentPlan.GetNode(
            MaterializationRebuildPlanSetProcessFactory.PrepareCompensationWorkNodeId));
        var compensateLeaves = Assert.IsType<ForEachPartitionProcessNode>(compensateArtifacts.ParentPlan.GetNode(
            MaterializationRebuildPlanSetProcessFactory.CompensateLeavesNodeId));
        Assert.Equal(1, compensateLeaves.Limits.MaximumParallelism);
        Assert.Equal(ProcessPartitionFailurePolicy.AwaitAll, compensateLeaves.Failure);

        var atomic = new MaterializationRebuildPlanSet(
            schemaVersion: independent.SchemaVersion,
            request: independent.Request,
            membership: independent.Membership,
            placement: independent.Placement,
            scheduling: independent.Scheduling,
            promotion: new(MaterializationRebuildPromotionMode.AtomicVisibility),
            leafPlans: independent.LeafPlans,
            provenance: independent.Provenance);
        Assert.Throws<ArgumentException>(() => MaterializationRebuildPlanSetProcessFactory.Create(atomic));
    }

    [Fact]
    public void PlanSetReference_StrictlyRoundTripsAndRejectsOpenDocuments()
    {
        var plan = MaterializationRebuildPlanJsonSerializerTests.CreateControlledPlan([], []);
        var reference = MaterializationRebuildPlanSetReference.FromPlanSet(
            MaterializationRebuildPlanJsonSerializerTests.CreateSinglePlanSet(plan));

        var json = MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(reference);
        var restored = MaterializationRebuildPlanSetReferenceJsonSerializer.Deserialize(json);

        Assert.Equal(reference, restored);
        Assert.Equal(json, MaterializationRebuildPlanSetReferenceJsonSerializer.Serialize(restored));
        var alternateFingerprintProfile = new MaterializationRebuildPlanSetReference(
            schemaVersion: reference.SchemaVersion,
            request: reference.Request,
            planSet: new(
                algorithm: reference.PlanSet.Algorithm,
                canonicalization: reference.PlanSet.Canonicalization + "/alternate",
                value: reference.PlanSet.Value));
        Assert.NotEqual(
            MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(reference),
            MaterializationRebuildPlanSetProcessFactory.GetParentDefinitionId(alternateFingerprintProfile));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            MaterializationRebuildPlanSetReferenceJsonSerializer.Deserialize(
                json[..^1] + ",\"unexpected\":true}"));
    }
}
