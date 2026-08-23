using System.Text.Json;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureCapabilityWitnessTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly InfrastructureCapabilityId DurableStorage = new("durable-storage");
    static readonly InfrastructureCapabilityEvidenceId StorageEvidence = new("evidence/durable-storage");
    static readonly InfrastructureCapabilityVariantId Variant = new("production");

    [Fact]
    public void Ari_scheduler_evidence_for_one_physical_scheduler_cannot_close_an_unrelated_scheduler_demand()
    {
        InfrastructureCapabilityId durableScheduling = new("durable-process-scheduling");
        InfrastructureCapabilityEvidenceId schedulerEvidence = new("evidence/durable-task-scheduler");
        InfrastructureNodeId trainingScheduler = new("resource/training-scheduler");
        InfrastructureNodeId replayScheduler = new("resource/replay-scheduler");
        var trainingRequirement = InfrastructureCapabilityRequirement.ForNode(trainingScheduler, durableScheduling);
        var replayRequirement = InfrastructureCapabilityRequirement.ForNode(replayScheduler, durableScheduling);
        var definition = Definition(
            resources:
            [
                new(trainingScheduler, InfrastructureResourceLifecycle.Persistent, [trainingRequirement]),
                new(replayScheduler, InfrastructureResourceLifecycle.Persistent, [replayRequirement])
            ]);
        var closure = Closure(definition, NativeProfile(durableScheduling, schedulerEvidence));
        var lifecycle = Lifecycle(
            definition,
            (trainingScheduler, "physical/durable-task/training"),
            (replayScheduler, "physical/durable-task/replay"));

        var incomplete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            capabilityWitnesses:
            [
                Witness(trainingRequirement.Id, schedulerEvidence, "physical/durable-task/training")
            ]);

        Assert.True(incomplete.FindWitnessDecision(trainingRequirement.Id)!.IsComplete);
        var replayDecision = incomplete.FindWitnessDecision(replayRequirement.Id)!;
        Assert.False(replayDecision.IsComplete);
        Assert.True(replayDecision.MissingEvidence.SequenceEqual([schedulerEvidence]));
        Assert.Equal(
            ["physical/durable-task/replay"],
            replayDecision.MissingPhysicalResources.Select(static resource => resource.Value));
        Assert.Contains(
            incomplete.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.EvidenceWitnessMissing
                          && diagnostic.Evidence?.Subject == replayRequirement.Id.Value);

        var complete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            capabilityWitnesses:
            [
                Witness(trainingRequirement.Id, schedulerEvidence, "physical/durable-task/training"),
                Witness(replayRequirement.Id, schedulerEvidence, "physical/durable-task/replay")
            ]);

        Assert.True(complete.IsCapabilityWitnessComplete);
        Assert.Empty(complete.Diagnostics);
    }

    [Fact]
    public void Workload_requirement_must_cover_its_selected_deployment_resource()
    {
        InfrastructureNodeId workload = new("workload/api");
        var requirement = InfrastructureCapabilityRequirement.ForNode(workload, DurableStorage);
        var definition = Definition(workloads: [new(workload, [requirement])]);
        var closure = Closure(definition, NativeProfile(DurableStorage, StorageEvidence));
        var lifecycle = Lifecycle(definition);
        InfrastructureWorkloadPlacement placement = new(
            workload,
            new("physical/apps/api"),
            new("aspire"),
            ["aspire://app-host/api"]);

        var wrong = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [placement],
            [Witness(requirement.Id, StorageEvidence, "physical/apps/other")]);

        var wrongDecision = Assert.Single(wrong.WitnessDecisions);
        Assert.Equal(["physical/apps/api"], wrongDecision.MissingPhysicalResources.Select(static item => item.Value));
        Assert.Contains(
            wrong.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.SubjectPhysicalResourceMissing);

        var complete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [placement],
            [Witness(requirement.Id, StorageEvidence, "physical/apps/api")]);

        Assert.True(complete.IsCapabilityWitnessComplete);
    }

    [Fact]
    public void Workload_placements_are_validated_against_the_exact_definition()
    {
        InfrastructureNodeId workload = new("workload/api");
        var definition = Definition(workloads: [new(workload)]);
        var closure = Closure(definition, Profile(new(Variant)));
        var lifecycle = Lifecycle(definition);
        InfrastructureWorkloadPlacement stalePlacement = new(
            new("workload/removed"),
            new("physical/apps/removed"),
            new("aspire"),
            ["aspire://app-host/removed"]);

        var realization = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [stalePlacement]);

        Assert.False(realization.IsCapabilityWitnessComplete);
        Assert.Contains(
            realization.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.WorkloadPlacementUnknown);
        Assert.Contains(
            realization.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.WorkloadPlacementMissing
                          && diagnostic.Evidence?.Subject == workload.Value);
    }

    [Fact]
    public void Binding_obligation_proof_must_cover_both_selected_endpoint_resources()
    {
        InfrastructureNodeId api = new("workload/api");
        InfrastructureNodeId store = new("resource/store");
        InfrastructureBindingId bindingId = new("binding/api/store");
        InfrastructureBindingContractId contract = new("document-read-write");
        var definition = Definition(
            workloads: [new(api)],
            resources: [new(store, InfrastructureResourceLifecycle.Persistent)],
            bindings: [new(bindingId, api, store, contract)]);
        var bindingProfile = new InfrastructureBindingElaborationProfile(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            new("bindings/documents/v1"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new(new("rules/document-read-write/v1"), contract, [DurableStorage], ["spec://document-read-write"])]);
        var closure = InfrastructureCapabilityCompiler.Compile(
            definition,
            NativeProfile(DurableStorage, StorageEvidence),
            Variant,
            bindingProfile);
        var requirement = Assert.Single(closure.BindingElaboration.Obligations).Requirement;
        var lifecycle = Lifecycle(definition, (store, "physical/storage/store"));
        InfrastructureWorkloadPlacement placement = new(
            api,
            new("physical/apps/api"),
            new("terraform"),
            ["terraform://apps/api"]);

        var incomplete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [placement],
            [Witness(requirement.Id, StorageEvidence, "physical/storage/store")]);

        var incompleteDecision = Assert.Single(incomplete.WitnessDecisions);
        Assert.True(incompleteDecision.Subjects.SequenceEqual([store, api]));
        Assert.Equal(["physical/apps/api"], incompleteDecision.MissingPhysicalResources.Select(static item => item.Value));

        var complete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [placement],
            [Witness(requirement.Id, StorageEvidence, "physical/apps/api", "physical/storage/store")]);

        Assert.True(complete.IsCapabilityWitnessComplete);
        Assert.Empty(complete.Diagnostics);
    }

    [Fact]
    public void Composed_proof_requires_a_witness_for_every_transitive_evidence_identity()
    {
        InfrastructureCapabilityId queue = new("durable-queue");
        InfrastructureCapabilityId timer = new("durable-timer");
        InfrastructureCapabilityId scheduler = new("durable-scheduler");
        InfrastructureCapabilityEvidenceId queueEvidence = new("evidence/queue");
        InfrastructureCapabilityEvidenceId timerEvidence = new("evidence/timer");
        InfrastructureNodeId schedulerResource = new("resource/scheduler");
        var requirement = InfrastructureCapabilityRequirement.ForNode(schedulerResource, scheduler);
        var definition = Definition(resources: [new(
            schedulerResource,
            InfrastructureResourceLifecycle.Persistent,
            [requirement])]);
        var profile = Profile(new(
            Variant,
            evidence:
            [
                NativeEvidence(queueEvidence, queue),
                NativeEvidence(timerEvidence, timer)
            ],
            rules: [new(new("rules/scheduler/v1"), scheduler, [queue, timer])]));
        var closure = Closure(definition, profile);
        var lifecycle = Lifecycle(definition, (schedulerResource, "physical/scheduler/main"));

        var incomplete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            capabilityWitnesses:
            [
                Witness(requirement.Id, queueEvidence, "physical/scheduler/main")
            ]);

        Assert.True(Assert.Single(incomplete.WitnessDecisions).MissingEvidence.SequenceEqual([timerEvidence]));

        var complete = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            capabilityWitnesses:
            [
                Witness(requirement.Id, timerEvidence, "physical/scheduler/main"),
                Witness(requirement.Id, queueEvidence, "physical/scheduler/main")
            ]);

        Assert.True(complete.IsCapabilityWitnessComplete);
        Assert.True(
            Assert.Single(complete.WitnessDecisions).WitnessedEvidence.SequenceEqual([queueEvidence, timerEvidence]));
    }

    [Fact]
    public void Realization_normalization_fingerprint_and_json_are_producer_order_independent()
    {
        InfrastructureNodeId api = new("workload/api");
        InfrastructureNodeId jobs = new("workload/jobs");
        var apiRequirement = InfrastructureCapabilityRequirement.ForNode(api, DurableStorage);
        var jobsRequirement = InfrastructureCapabilityRequirement.ForNode(jobs, DurableStorage);
        var definition = Definition(workloads: [new(api, [apiRequirement]), new(jobs, [jobsRequirement])]);
        var closure = Closure(definition, NativeProfile(DurableStorage, StorageEvidence));
        var lifecycle = Lifecycle(definition);
        InfrastructureWorkloadPlacement apiPlacement = new(
            api,
            new("physical/apps/api"),
            new("aspire"),
            ["aspire://api"]);
        InfrastructureWorkloadPlacement jobsPlacement = new(
            jobs,
            new("physical/apps/jobs"),
            new("aspire"),
            ["aspire://jobs"]);
        var apiWitness = Witness(apiRequirement.Id, StorageEvidence, "physical/apps/api");
        var jobsWitness = Witness(jobsRequirement.Id, StorageEvidence, "physical/apps/jobs");

        var first = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [jobsPlacement, apiPlacement],
            [jobsWitness, apiWitness]);
        var second = InfrastructureRealizationCompiler.Compile(
            closure,
            lifecycle,
            [apiPlacement, jobsPlacement],
            [apiWitness, jobsWitness]);
        var roundTrip = JsonSerializer.Deserialize<InfrastructureRealization>(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonOptions);

        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first, roundTrip);
        Assert.Equal(apiRequirement.Id, first.FindWitnessDecision(apiRequirement.Id)?.Requirement);
    }

    [Fact]
    public void Unknown_stale_and_unavailable_witnesses_fail_closed_with_structured_diagnostics()
    {
        InfrastructureNodeId store = new("resource/store");
        var requirement = InfrastructureCapabilityRequirement.ForNode(store, DurableStorage);
        var definition = Definition(resources: [new(
            store,
            InfrastructureResourceLifecycle.Persistent,
            [requirement])]);
        var unavailableClosure = Closure(definition, Profile(new(Variant)));
        var lifecycle = Lifecycle(definition, (store, "physical/storage/store"));
        InfrastructureCapabilityEvidenceId staleEvidence = new("evidence/stale");

        var realization = InfrastructureRealizationCompiler.Compile(
            unavailableClosure,
            lifecycle,
            capabilityWitnesses:
            [
                Witness(requirement.Id, staleEvidence, "physical/storage/store"),
                Witness(new("requirements/unknown"), staleEvidence, "physical/storage/store")
            ]);

        Assert.False(realization.IsCapabilityWitnessComplete);
        Assert.Contains(
            realization.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.WitnessForUnavailableDecision);
        Assert.Contains(
            realization.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityWitnessDiagnosticCodes.RequirementUnknown);
        Assert.All(realization.Diagnostics, static diagnostic =>
        {
            Assert.NotNull(diagnostic.Evidence);
            Assert.Equal("infrastructure-capability-witnessing", diagnostic.Evidence.Stage);
            Assert.NotEmpty(diagnostic.Evidence.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
        });
    }

    static InfrastructureDefinitionDocument Definition(
        InfrastructureWorkloadDefinition[]? workloads = null,
        InfrastructureResourceDefinition[]? resources = null,
        InfrastructureBindingDefinition[]? bindings = null) =>
        InfrastructureDefinitionDocument.FromDefinition(new(
            new("witness-tests"),
            new("v1"),
            workloads: workloads is null ? [] : [.. workloads],
            resources: resources is null ? [] : [.. resources],
            bindings: bindings is null ? [] : [.. bindings]));

    static InfrastructureCapabilityClosureReport Closure(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile) =>
        InfrastructureCapabilityCompiler.Compile(definition, profile, Variant);

    static InfrastructureCapabilityProfile NativeProfile(
        InfrastructureCapabilityId capability,
        InfrastructureCapabilityEvidenceId evidence) =>
        Profile(new(Variant, evidence: [NativeEvidence(evidence, capability)]));

    static InfrastructureCapabilityProfile Profile(InfrastructureCapabilityVariant variant) =>
        new(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/witness-tests/v1"),
            new("test-target"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [variant]);

    static InfrastructureCapabilityEvidence NativeEvidence(
        InfrastructureCapabilityEvidenceId evidence,
        InfrastructureCapabilityId capability) =>
        new(
            evidence,
            capability,
            CapabilityRealizationKind.Native,
            sourceReferences: [$"test://{evidence.Value}"]);

    static InfrastructureLifecyclePlan Lifecycle(
        InfrastructureDefinitionDocument definition,
        params (InfrastructureNodeId Resource, string Physical)[] resources) =>
        new(
            definition,
            [
                .. resources.Select(static resource => new InfrastructureResourceLifecycleBinding(
                    resource.Resource,
                    new(resource.Physical),
                    new("terraform"),
                    new("state/witness-tests"),
                    InfrastructureLifecycleDisposition.Managed))
            ]);

    static InfrastructureCapabilityEvidenceWitness Witness(
        InfrastructureRequirementId requirement,
        InfrastructureCapabilityEvidenceId evidence,
        params string[] physicalResources) =>
        new(
            requirement,
            evidence,
            [.. physicalResources.Select(static resource => new InfrastructurePhysicalResourceId(resource))],
            [$"plan://{requirement.Value}/{evidence.Value}"]);
}
