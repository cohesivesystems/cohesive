using System.Text.Json;
using Cohesive.Infra.Realization;
using Cohesive.Model;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureBoundaryAcceptancePolicyTests
{
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly InfrastructureCapabilityId DurableScheduler = new("durable-process-scheduling");
    static readonly InfrastructureCapabilityVariantId AspireLocal = new("aspire-local");
    static readonly InfrastructureOperatingBoundaryId ProcessLifetime = new("boundaries/process-lifetime-only");

    [Fact]
    public void Exact_demand_scoped_acceptance_closes_a_constrained_proof()
    {
        InfrastructureNodeId scheduler = new("resource/scheduler");
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        var definition = SchedulerDefinition((scheduler, requirement));
        var profile = ConstrainedProfile();
        var policy = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(requirement, ProcessLifetime));

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            policy);

        Assert.True(report.IsClosed);
        Assert.Empty(report.Diagnostics);
        Assert.Equal(policy.ToReference(), report.BoundaryAcceptancePolicy);
        var decision = Assert.Single(report.Decisions);
        Assert.True(decision.IsAvailable);
        Assert.True(decision.IsAdmissible);
        Assert.True(decision.AcceptedOperatingBoundaries.SequenceEqual([ProcessLifetime]));
        Assert.Empty(decision.MissingOperatingBoundaries);
    }

    [Fact]
    public void Binding_derived_demand_acceptance_is_fenced_to_its_exact_elaboration_profile()
    {
        InfrastructureBindingContractId contract = new("process-client");
        var definition = Infrastructure.Define(new("ari-binding"), new("v1"), infrastructure =>
        {
            var api = infrastructure.Workload(new("workload/api"));
            var scheduler = infrastructure.Resource(new("resource/scheduler")).Persistent();
            infrastructure.Bind(new("binding/api/scheduler"), api).To(scheduler).As(contract);
        });
        var bindingProfile = new InfrastructureBindingElaborationProfile(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            new("bindings/ari-process-client/v1"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [
                new(
                    new("rules/process-client/v1"),
                    contract,
                    [DurableScheduler],
                    ["ari://process-client-contract"])
            ]);
        var obligation = Assert.Single(
            InfrastructureBindingElaborator.Elaborate(definition, bindingProfile).Obligations);
        var profile = ConstrainedProfile();
        var policy = InfrastructureBoundaryAcceptancePolicy.Create(
            new("policies/ari-binding-local/v1"),
            definition,
            profile,
            bindingProfile,
            AspireLocal,
            [Accept(obligation.Requirement.Id, ProcessLifetime)]);

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            bindingProfile,
            policy);

        Assert.True(report.IsClosed);
        Assert.Equal(bindingProfile.ToReference(), report.BoundaryAcceptancePolicy?.BindingProfile);
        Assert.True(Assert.Single(report.Decisions).IsAdmissible);
    }

    [Fact]
    public void Ari_acceptance_for_one_scheduler_cannot_weaken_an_unrelated_scheduler_demand()
    {
        InfrastructureNodeId trainingScheduler = new("resource/training-scheduler");
        InfrastructureNodeId replayScheduler = new("resource/replay-scheduler");
        InfrastructureRequirementId trainingRequirement = new("requirements/training/durable-scheduler");
        InfrastructureRequirementId replayRequirement = new("requirements/replay/durable-scheduler");
        var definition = SchedulerDefinition(
            (trainingScheduler, trainingRequirement),
            (replayScheduler, replayRequirement));
        var profile = ConstrainedProfile();
        var policy = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(trainingRequirement, ProcessLifetime));

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            policy);

        Assert.False(report.IsClosed);
        Assert.True(report.FindDecision(trainingRequirement)!.IsAdmissible);
        var replay = report.FindDecision(replayRequirement)!;
        Assert.True(replay.IsAvailable);
        Assert.False(replay.IsAdmissible);
        Assert.True(replay.MissingOperatingBoundaries.SequenceEqual([ProcessLifetime]));
        var diagnostic = Assert.Single(report.Diagnostics);
        Assert.Equal(InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired, diagnostic.Code);
        Assert.Equal(replayRequirement.Value, diagnostic.Evidence?.Subject);
        Assert.Contains(ProcessLifetime.Value, diagnostic.Evidence?.Expected, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_transitive_boundary_must_be_accepted()
    {
        InfrastructureOperatingBoundaryId workerLifetime = new("boundaries/worker-lifetime-only");
        InfrastructureCapabilityId durableTimer = new("durable-timer");
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        var definition = SchedulerDefinition((new("resource/scheduler"), requirement));
        var profile = Profile(
            new InfrastructureCapabilityVariant(
                AspireLocal,
                evidence:
                [
                    new(
                        new("evidence/process-timer"),
                        durableTimer,
                        CapabilityRealizationKind.Constrained,
                        operatingBoundaries: [ProcessLifetime],
                        sourceReferences: ["aspire://timer"]),
                    new(
                        new("evidence/process-scheduler"),
                        DurableScheduler,
                        CapabilityRealizationKind.Constrained,
                        auxiliaries: [new("evidence/process-timer")],
                        operatingBoundaries: [workerLifetime],
                        sourceReferences: ["aspire://scheduler"])
                ],
                operatingBoundaries:
                [
                    Boundary(ProcessLifetime),
                    Boundary(workerLifetime)
                ]));
        var partialPolicy = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(requirement, ProcessLifetime));

        var partial = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            partialPolicy);

        var partialDecision = Assert.Single(partial.Decisions);
        Assert.False(partial.IsClosed);
        Assert.True(partialDecision.AcceptedOperatingBoundaries.SequenceEqual([ProcessLifetime]));
        Assert.True(partialDecision.MissingOperatingBoundaries.SequenceEqual([workerLifetime]));

        var completePolicy = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(requirement, workerLifetime),
            Accept(requirement, ProcessLifetime));
        var complete = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            completePolicy);

        Assert.True(complete.IsClosed);
        Assert.Empty(Assert.Single(complete.Decisions).MissingOperatingBoundaries);
    }

    [Fact]
    public void Policy_fences_must_match_every_exact_compiler_authority()
    {
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        var definition = SchedulerDefinition((new("resource/scheduler"), requirement));
        var changedDefinition = Infrastructure.Define(new("ari-schedulers"), new("v2"), infrastructure =>
            infrastructure.Resource(new("resource/scheduler"))
                .Persistent()
                .Requires(requirement, DurableScheduler));
        var profile = ConstrainedProfile();
        var changedProfile = new InfrastructureCapabilityProfile(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/aspire-local/changed/v1"),
            profile.Target,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            profile.Variants);
        InfrastructureCapabilityVariantId otherVariant = new("other-local");
        var acceptance = Accept(requirement, ProcessLifetime);

        Assert.Throws<ArgumentException>(() => Policy(definition, profile, otherVariant, acceptance));
        var wrongDefinition = Policy(changedDefinition, profile, AspireLocal, acceptance);
        var wrongProfile = Policy(definition, changedProfile, AspireLocal, acceptance);
        var wrongVariant = new InfrastructureBoundaryAcceptancePolicy(
            schemaVersion: InfrastructureBoundaryAcceptancePolicy.CurrentSchemaVersion,
            id: new("policies/wrong-variant/v1"),
            definition: definition.ToReference(),
            profile: profile.ToReference(),
            bindingProfile: InfrastructureBindingElaborationProfile.Empty.ToReference(),
            target: profile.Target,
            variant: otherVariant,
            acceptances: [acceptance]);
        var wrongTarget = new InfrastructureBoundaryAcceptancePolicy(
            InfrastructureBoundaryAcceptancePolicy.CurrentSchemaVersion,
            new("policies/wrong-target/v1"),
            definition.ToReference(),
            profile.ToReference(),
            InfrastructureBindingElaborationProfile.Empty.ToReference(),
            new("terraform"),
            AspireLocal,
            [acceptance]);
        var changedBindings = new InfrastructureBindingElaborationProfile(
            InfrastructureBindingElaborationProfile.CurrentSchemaVersion,
            new("bindings/changed/v1"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion]);
        var wrongBindings = InfrastructureBoundaryAcceptancePolicy.Create(
            new("policies/wrong-bindings/v1"),
            definition,
            profile,
            changedBindings,
            AspireLocal,
            [acceptance]);

        AssertPolicyMismatch(definition, profile, wrongDefinition);
        AssertPolicyMismatch(definition, profile, wrongProfile);
        AssertPolicyMismatch(definition, profile, wrongVariant);
        AssertPolicyMismatch(definition, profile, wrongTarget);
        AssertPolicyMismatch(definition, profile, wrongBindings);
    }

    [Fact]
    public void Unknown_and_unexpected_acceptances_fail_closed_with_structured_diagnostics()
    {
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        InfrastructureOperatingBoundaryId staleBoundary = new("boundaries/removed");
        InfrastructureRequirementId staleRequirement = new("requirements/removed");
        var definition = SchedulerDefinition((new("resource/scheduler"), requirement));
        var profile = ConstrainedProfile();
        var policy = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(staleRequirement, ProcessLifetime),
            Accept(requirement, staleBoundary));

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            policy);

        Assert.False(report.IsClosed);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptanceRequirementUnknown);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptanceUnexpected);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired);
        Assert.All(report.Diagnostics, static diagnostic =>
        {
            Assert.NotNull(diagnostic.Evidence);
            Assert.Equal("infrastructure-capability-matching", diagnostic.Evidence.Stage);
            Assert.NotEmpty(diagnostic.Evidence.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence.Expected));
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence.Observed));
        });
    }

    [Fact]
    public void Native_proofs_do_not_consume_boundary_acceptance()
    {
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        var definition = SchedulerDefinition((new("resource/scheduler"), requirement));
        var profile = Profile(new(
            AspireLocal,
            evidence:
            [
                new(
                    new("evidence/native-scheduler"),
                    DurableScheduler,
                    CapabilityRealizationKind.Native,
                    sourceReferences: ["test://native-scheduler"])
            ]));
        var emptyPolicy = Policy(definition, profile, AspireLocal);

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            emptyPolicy);

        Assert.True(report.IsClosed);
        var decision = Assert.Single(report.Decisions);
        Assert.Empty(decision.AcceptedOperatingBoundaries);
        Assert.Empty(decision.MissingOperatingBoundaries);
    }

    [Fact]
    public void Unsupported_policy_schema_cannot_accept_a_boundary()
    {
        InfrastructureRequirementId requirement = new("requirements/scheduler/durability");
        var definition = SchedulerDefinition((new("resource/scheduler"), requirement));
        var profile = ConstrainedProfile();
        var current = Policy(
            definition,
            profile,
            AspireLocal,
            Accept(requirement, ProcessLifetime));
        var future = new InfrastructureBoundaryAcceptancePolicy(
            "cohesive.infra.boundary-acceptance/future",
            current.Id,
            current.Definition,
            current.Profile,
            current.BindingProfile,
            current.Target,
            current.Variant,
            current.Acceptances);

        var report = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            future);

        Assert.False(report.IsClosed);
        Assert.Empty(Assert.Single(report.Decisions).AcceptedOperatingBoundaries);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptancePolicySchemaUnsupported);
        Assert.Contains(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.OperatingBoundaryAcceptanceRequired);
    }

    [Fact]
    public void Policy_and_closure_are_canonical_fingerprinted_and_restorable()
    {
        InfrastructureRequirementId training = new("requirements/training/durable-scheduler");
        InfrastructureRequirementId replay = new("requirements/replay/durable-scheduler");
        InfrastructureNodeId trainingResource = new("resource/training-scheduler");
        InfrastructureNodeId replayResource = new("resource/replay-scheduler");
        var definition = SchedulerDefinition(
            (trainingResource, training),
            (replayResource, replay));
        var profile = ConstrainedProfile();
        var trainingAcceptance = Accept(
            training,
            ProcessLifetime,
            sourceReferences: ["policy://local-development", "approval://architecture"]);
        var replayAcceptance = Accept(
            replay,
            ProcessLifetime,
            sourceReferences: ["approval://architecture", "policy://local-development"]);
        var first = Policy(definition, profile, AspireLocal, trainingAcceptance, replayAcceptance);
        var reordered = Policy(definition, profile, AspireLocal, replayAcceptance, trainingAcceptance);

        var restoredPolicy = JsonSerializer.Deserialize<InfrastructureBoundaryAcceptancePolicy>(
            JsonSerializer.Serialize(first, JsonOptions),
            JsonOptions);
        var report = InfrastructureCapabilityCompiler.Compile(definition, profile, AspireLocal, first);
        var restoredReport = JsonSerializer.Deserialize<InfrastructureCapabilityClosureReport>(
            JsonSerializer.Serialize(report, JsonOptions),
            JsonOptions);
        var changedPolicy = Policy(
            definition,
            profile,
            AspireLocal,
            new(
                training,
                ProcessLifetime,
                "A separately approved local-development rationale.",
                ["approval://separate-review"]),
            replayAcceptance);
        var changedReport = InfrastructureCapabilityCompiler.Compile(
            definition,
            profile,
            AspireLocal,
            changedPolicy);
        var lifecycle = new InfrastructureLifecyclePlan(
            definition,
            [
                new(
                    trainingResource,
                    new("physical/schedulers/training"),
                    new("aspire"),
                    new("sessions/ari-local"),
                    InfrastructureLifecycleDisposition.Managed),
                new(
                    replayResource,
                    new("physical/schedulers/replay"),
                    new("aspire"),
                    new("sessions/ari-local"),
                    InfrastructureLifecycleDisposition.Managed)
            ]);
        InfrastructureCapabilityEvidenceId schedulerEvidence = new("evidence/aspire-process-scheduler");
        InfrastructureCapabilityEvidenceWitness[] witnesses =
        [
            new(
                training,
                schedulerEvidence,
                [new("physical/schedulers/training")],
                ["aspire://plan/training"]),
            new(
                replay,
                schedulerEvidence,
                [new("physical/schedulers/replay")],
                ["aspire://plan/replay"])
        ];
        var realization = InfrastructureRealizationCompiler.Compile(
            report,
            lifecycle,
            capabilityWitnesses: [.. witnesses]);
        var changedRealization = InfrastructureRealizationCompiler.Compile(
            changedReport,
            lifecycle,
            capabilityWitnesses: [.. witnesses]);
        var tampered = new InfrastructureBoundaryAcceptancePolicyFingerprint(
            first.Fingerprint.Algorithm,
            first.Fingerprint.Canonicalization,
            "00");

        Assert.Equal(first, reordered);
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.Equal(first, restoredPolicy);
        Assert.Equal(report, restoredReport);
        Assert.NotEqual(first.Fingerprint, changedPolicy.Fingerprint);
        Assert.NotEqual(realization.Fingerprint, changedRealization.Fingerprint);
        Assert.Throws<ArgumentException>(() => new InfrastructureBoundaryAcceptancePolicy(
            first.SchemaVersion,
            first.Id,
            first.Definition,
            first.Profile,
            first.BindingProfile,
            first.Target,
            first.Variant,
            first.Acceptances,
            tampered));
    }

    static InfrastructureDefinitionDocument SchedulerDefinition(
        params (InfrastructureNodeId Resource, InfrastructureRequirementId Requirement)[] schedulers) =>
        InfrastructureDefinitionDocument.FromDefinition(new(
            new("ari-schedulers"),
            new("v1"),
            resources:
            [
                .. schedulers.Select(static scheduler => new InfrastructureResourceDefinition(
                    scheduler.Resource,
                    InfrastructureResourceLifecycle.Persistent,
                    [new(scheduler.Requirement, DurableScheduler)]))
            ]));

    static InfrastructureCapabilityProfile ConstrainedProfile() =>
        Profile(new(
            AspireLocal,
            evidence:
            [
                new(
                    new("evidence/aspire-process-scheduler"),
                    DurableScheduler,
                    CapabilityRealizationKind.Constrained,
                    operatingBoundaries: [ProcessLifetime],
                    sourceReferences: ["ari://AspireHost"])
            ],
            operatingBoundaries: [Boundary(ProcessLifetime)]));

    static InfrastructureCapabilityProfile Profile(InfrastructureCapabilityVariant variant) =>
        new(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("profiles/aspire-local/v1"),
            new("aspire"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [variant]);

    static InfrastructureOperatingBoundary Boundary(InfrastructureOperatingBoundaryId id) =>
        new(
            id,
            $"Constraint '{id.Value}' applies only to explicitly accepted local development demands.",
            ["ari://AspireHost"]);

    static InfrastructureBoundaryAcceptance Accept(
        InfrastructureRequirementId requirement,
        InfrastructureOperatingBoundaryId boundary,
        string[]? sourceReferences = null) =>
        new(
            requirement,
            boundary,
            "Local development explicitly accepts process-lifetime scheduling durability.",
            sourceReferences is null ? ["policy://local-development"] : [.. sourceReferences]);

    static InfrastructureBoundaryAcceptancePolicy Policy(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureCapabilityVariantId variant,
        params InfrastructureBoundaryAcceptance[] acceptances) =>
        InfrastructureBoundaryAcceptancePolicy.Create(
            new("policies/ari-local-development/v1"),
            definition,
            profile,
            InfrastructureBindingElaborationProfile.Empty,
            variant,
            [.. acceptances]);

    static void AssertPolicyMismatch(
        InfrastructureDefinitionDocument definition,
        InfrastructureCapabilityProfile profile,
        InfrastructureBoundaryAcceptancePolicy policy)
    {
        var report = InfrastructureCapabilityCompiler.Compile(definition, profile, AspireLocal, policy);

        Assert.False(report.IsClosed);
        Assert.Null(report.BoundaryAcceptancePolicy);
        var diagnostic = Assert.Single(
            report.Diagnostics,
            static diagnostic => diagnostic.Code == InfrastructureCapabilityDiagnosticCodes.BoundaryAcceptancePolicyFenceMismatch);
        Assert.NotNull(diagnostic.Evidence);
        Assert.Equal("infrastructure-capability-matching", diagnostic.Evidence.Stage);
        Assert.NotEmpty(diagnostic.Evidence.SourceReferences);
        Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence.Expected));
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Evidence.Observed));
    }
}
