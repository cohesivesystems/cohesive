using System.Text.Json;
using Cohesive.Infra.Configuration;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureTargetCompilerTests
{
    static readonly InfrastructureCapabilityId Https = new("test/workload/https");
    static readonly InfrastructureCapabilityId Storage = new("test/resource/storage");
    static readonly InfrastructureTargetFacilityId AppService = new("test/app-service");
    static readonly InfrastructureTargetFacilityId ObjectStore = new("test/object-store");
    static readonly InfrastructureCapabilityVariantId Variant = new("test/production");

    [Fact]
    public void Fluent_and_direct_manifests_materialize_the_same_canonical_ir()
    {
        var https = Native("test/evidence/https", Https);
        var storage = Native("test/evidence/storage", Storage);
        var profile = Profile([https, storage]);
        var direct = new InfrastructureTargetFacilityManifest(
            InfrastructureTargetFacilityManifest.CurrentSchemaVersion,
            new("test/target-facilities/v1"),
            profile,
            Variant,
            [
                new(AppService, InfrastructureNodeKind.Workload, [https.Id]),
                new(ObjectStore, InfrastructureNodeKind.Resource, [storage.Id])
            ]);

        var fluent = InfrastructureTargetFacilities.Define(
            new("test/target-facilities/v1"),
            profile.Id,
            profile.Target,
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(https);
                target.Resource(ObjectStore).Provides(storage);
            });

        Assert.Equal(direct.Fingerprint, fluent.Fingerprint);
        Assert.Equal(direct.ToReference(), fluent.ToReference());
        Assert.True(direct.Facilities.SequenceEqual(fluent.Facilities));
        Assert.Empty(direct.SourceMap.Entries);
        Assert.Equal(4, fluent.SourceMap.Entries.Length);
    }

    [Fact]
    public void Compiler_selects_facilities_for_every_compatible_definition_node()
    {
        InfrastructureNodeId api = new("workloads/api");
        InfrastructureNodeId reports = new("workloads/reports");
        InfrastructureNodeId state = new("resources/state");
        var semantic = Infrastructure.Define(
            new("test/facility-selection"),
            new("1"),
            infrastructure =>
            {
                infrastructure.Workload(api).Requires(Https);
                infrastructure.Workload(reports).Requires(Https);
                infrastructure.Resource(state).Persistent().Requires(Storage);
            });

        var manifest = Manifest();
        var first = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty);
        var second = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty);

        Assert.True(first.IsComplete);
        Assert.Empty(first.Diagnostics);
        Assert.True(first.CapabilityClosure.IsClosed);
        Assert.Equal(AppService, first.FindDecision(api).Facility);
        Assert.Equal(AppService, first.FindDecision(reports).Facility);
        Assert.Equal(ObjectStore, first.FindDecision(state).Facility);
        Assert.Equal(first, second);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void Unsupported_nodes_produce_structured_diagnostics_instead_of_throwing()
    {
        InfrastructureNodeId worker = new("workloads/gpu-worker");
        InfrastructureCapabilityId gpu = new("test/workload/gpu");
        var semantic = Infrastructure.Define(
            new("test/facility-unavailable"),
            new("1"),
            infrastructure => infrastructure.Workload(worker).Requires(gpu));

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            Manifest(),
            InfrastructureBindingElaborationProfile.Empty);

        Assert.False(plan.IsComplete);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureTargetCompiler.DiagnosticCodes.FacilityUnavailable);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(worker.Value, diagnostic.Evidence?.Subject);
        Assert.Equal(gpu.Value, diagnostic.Evidence?.Expected);
        Assert.NotEmpty(diagnostic.Evidence!.ResolutionOptions);
        Assert.Contains(
            diagnostic.Evidence.SourceReferences,
            static reference => reference.StartsWith("csharp://", StringComparison.Ordinal));
        Assert.Contains(
            diagnostic.Evidence.SourceReferences,
            static reference => reference.StartsWith(
                "infrastructure-target-facility-manifest://",
                StringComparison.Ordinal));
        Assert.Throws<KeyNotFoundException>(() => plan.FindDecision(worker));
    }

    [Fact]
    public void Facility_eligibility_uses_capability_composition_over_owned_leaf_evidence()
    {
        InfrastructureNodeId api = new("workloads/api");
        InfrastructureCapabilityId identity = new("test/workload/identity");
        InfrastructureCapabilityId authenticatedHttps = new("test/workload/authenticated-https");
        var semantic = Infrastructure.Define(
            new("test/facility-composition"),
            new("1"),
            infrastructure => infrastructure.Workload(api).Requires(authenticatedHttps));
        var manifest = InfrastructureTargetFacilities.Define(
            new("test/composed-target-facilities/v1"),
            new("test/composed-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService)
                    .Provides(Native("test/evidence/https", Https))
                    .Provides(Native("test/evidence/identity", identity));
                target.Composes(new(
                    new("test/rules/authenticated-https/v1"),
                    authenticatedHttps,
                    [Https, identity],
                    sourceReferences: [SourceReference.Create("test", "authenticated-https")]));
            });

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty);

        Assert.True(plan.IsComplete);
        Assert.Equal(AppService, plan.FindDecision(api).Facility);
        var decision = Assert.Single(plan.CapabilityClosure.Decisions);
        Assert.Equal(CapabilityRealizationKind.Composed, decision.Realization);
        Assert.Equal(2, decision.Evidence.Length);
        Assert.Single(decision.Rules);
    }

    [Fact]
    public void Ambiguous_facilities_report_every_candidate_without_selecting_one()
    {
        InfrastructureNodeId api = new("workloads/api");
        var semantic = Infrastructure.Define(
            new("test/facility-ambiguous"),
            new("1"),
            infrastructure => infrastructure.Workload(api).Requires(Https));
        var first = Native("test/evidence/https/app-service", Https);
        var second = Native("test/evidence/https/container-app", Https);
        var profile = Profile([first, second]);
        var manifest = new InfrastructureTargetFacilityManifest(
            InfrastructureTargetFacilityManifest.CurrentSchemaVersion,
            new("test/ambiguous-target-facilities/v1"),
            profile,
            Variant,
            [
                new(new("test/app-service"), InfrastructureNodeKind.Workload, [first.Id]),
                new(new("test/container-app"), InfrastructureNodeKind.Workload, [second.Id])
            ]);

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty);

        Assert.False(plan.IsComplete);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureTargetCompiler.DiagnosticCodes.FacilityAmbiguous);
        Assert.Equal("test/app-service,test/container-app", diagnostic.Evidence?.Observed);
        Assert.Throws<KeyNotFoundException>(() => plan.FindDecision(api));
    }

    [Fact]
    public void Attributable_convention_selection_resolves_an_ambiguous_facility_set()
    {
        InfrastructureNodeId api = new("workloads/api");
        InfrastructureTargetFacilityId containerApp = new("test/container-app");
        var semantic = Infrastructure.Define(
            new("test/facility-convention"),
            new("1"),
            infrastructure => infrastructure.Workload(api).Requires(Https));
        var appServiceEvidence = Native("test/evidence/https/app-service", Https);
        var containerAppEvidence = Native("test/evidence/https/container-app", Https);
        var profile = Profile([appServiceEvidence, containerAppEvidence]);
        var manifest = new InfrastructureTargetFacilityManifest(
            InfrastructureTargetFacilityManifest.CurrentSchemaVersion,
            new("test/conventional-target-facilities/v1"),
            profile,
            Variant,
            [
                new(AppService, InfrastructureNodeKind.Workload, [appServiceEvidence.Id]),
                new(containerApp, InfrastructureNodeKind.Workload, [containerAppEvidence.Id])
            ]);
        var convention = new InfrastructureConventionProfile(
            new("test/target-selection/v1"),
            [
                new(
                    InfrastructureTargetFacilitySelection.Subject(api),
                    InfrastructureTargetFacilitySelection.Setting,
                    containerApp.Value,
                    EffectiveConfigurationOrigin.ScopedProfile,
                    "test/application-profile/v1")
            ]);

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty,
            [convention]);

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(containerApp, plan.FindDecision(api).Facility);
        var effective = Assert.Single(plan.Configuration.Configuration);
        Assert.Equal(EffectiveConfigurationOrigin.ScopedProfile, effective.Attribution.Origin);
        Assert.Equal("test/application-profile/v1", effective.Attribution.Authority);
    }

    [Fact]
    public void Incompatible_convention_selection_produces_an_attributable_diagnostic()
    {
        InfrastructureNodeId api = new("workloads/api");
        var semantic = Infrastructure.Define(
            new("test/facility-convention-invalid"),
            new("1"),
            infrastructure => infrastructure.Workload(api).Requires(Https));
        var convention = new InfrastructureConventionProfile(
            new("test/invalid-target-selection/v1"),
            [
                new(
                    InfrastructureTargetFacilitySelection.Subject(api),
                    InfrastructureTargetFacilitySelection.Setting,
                    ObjectStore.Value,
                    EffectiveConfigurationOrigin.Explicit,
                    "test/explicit-selection")
            ]);

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            Manifest(),
            InfrastructureBindingElaborationProfile.Empty,
            [convention]);

        Assert.False(plan.IsComplete);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureTargetCompiler.DiagnosticCodes.FacilitySelectionInvalid);
        Assert.Equal(api.Value, diagnostic.Evidence?.Subject);
        Assert.Contains(AppService.Value, diagnostic.Evidence?.Expected, StringComparison.Ordinal);
        Assert.Contains("test/explicit-selection", diagnostic.Evidence?.Observed, StringComparison.Ordinal);
        Assert.Throws<KeyNotFoundException>(() => plan.FindDecision(api));
    }

    [Fact]
    public void Boundary_acceptance_is_fenced_to_the_manifest_profile_while_facilities_restrict_evidence()
    {
        InfrastructureNodeId scheduler = new("resources/scheduler");
        InfrastructureCapabilityId scheduling = new("test/resource/process-lifetime-scheduling");
        InfrastructureOperatingBoundaryId processLifetime = new("test/boundary/process-lifetime");
        var semantic = Infrastructure.Define(
            new("test/facility-boundary-acceptance"),
            new("1"),
            infrastructure => infrastructure.Resource(scheduler).Persistent().Requires(scheduling));
        var constrained = new InfrastructureCapabilityEvidence(
            new("test/evidence/process-lifetime-scheduling"),
            scheduling,
            CapabilityRealizationKind.Constrained,
            operatingBoundaries: [processLifetime],
            sourceReferences: [SourceReference.Create("test", "scheduler")]);
        var boundary = new InfrastructureOperatingBoundary(
            processLifetime,
            "Scheduling remains durable only for the lifetime of the target process.",
            [SourceReference.Create("test", "scheduler-boundary")]);
        var manifest = InfrastructureTargetFacilities.Define(
            new("test/constrained-target-facilities/v1"),
            new("test/constrained-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Resource(new("test/process-lifetime-scheduler")).Provides(constrained);
                target.Within(boundary);
            });
        var requirement = Assert.Single(Assert.Single(semantic.Definition.Resources).Requirements);
        var policy = InfrastructureBoundaryAcceptancePolicy.Create(
            new("test/policies/process-lifetime/v1"),
            semantic,
            manifest.Profile,
            InfrastructureBindingElaborationProfile.Empty,
            Variant,
            [
                new(
                    requirement.Id,
                    processLifetime,
                    "The local environment explicitly accepts process-lifetime durability.",
                    [SourceReference.Create("test-policy", "local")])
            ]);

        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty,
            policy);

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.Diagnostics);
        Assert.Same(manifest.Profile, plan.CapabilityProfile);
        Assert.Equal(manifest.Profile.ToReference(), plan.CapabilityClosure.Profile);
        Assert.Equal(policy.ToReference(), plan.CapabilityClosure.BoundaryAcceptancePolicy);
        Assert.True(Assert.Single(plan.CapabilityClosure.Decisions).IsAdmissible);
    }

    [Fact]
    public void Manifest_and_plan_round_trip_without_changing_fingerprints()
    {
        var manifest = Manifest();
        var semantic = Infrastructure.Define(
            new("test/facility-round-trip"),
            new("1"),
            infrastructure =>
            {
                infrastructure.Workload(new("workloads/api")).Requires(Https);
                infrastructure.Resource(new("resources/state")).Persistent().Requires(Storage);
            });
        var plan = InfrastructureTargetCompiler.Compile(
            semantic,
            manifest,
            InfrastructureBindingElaborationProfile.Empty);
        var options = StrictDocumentJson.CreateOptions();

        var restoredManifest = Assert.IsType<InfrastructureTargetFacilityManifest>(
            JsonSerializer.Deserialize<InfrastructureTargetFacilityManifest>(
                JsonSerializer.Serialize(manifest, options),
                options));
        var restoredPlan = Assert.IsType<InfrastructureTargetFacilityPlan>(
            JsonSerializer.Deserialize<InfrastructureTargetFacilityPlan>(
                JsonSerializer.Serialize(plan, options),
                options));

        Assert.Equal(manifest, restoredManifest);
        Assert.Equal(manifest.Fingerprint, restoredManifest.Fingerprint);
        Assert.Equal(plan, restoredPlan);
        Assert.Equal(plan.Fingerprint, restoredPlan.Fingerprint);
    }

    static InfrastructureTargetFacilityManifest Manifest()
    {
        var https = Native("test/evidence/https", Https);
        var storage = Native("test/evidence/storage", Storage);
        return InfrastructureTargetFacilities.Define(
            new("test/target-facilities/v1"),
            new("test/capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(https);
                target.Resource(ObjectStore).Provides(storage);
            });
    }

    static InfrastructureCapabilityProfile Profile(
        System.Collections.Immutable.ImmutableArray<InfrastructureCapabilityEvidence> evidence) => new(
            InfrastructureCapabilityProfile.CurrentSchemaVersion,
            new("test/capabilities/v1"),
            new("test-target/1"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            [new(Variant, evidence)]);

    static InfrastructureCapabilityEvidence Native(string id, InfrastructureCapabilityId capability) => new(
        new(id),
        capability,
        CapabilityRealizationKind.Native,
        sourceReferences: [SourceReference.Create("test", id)]);
}
