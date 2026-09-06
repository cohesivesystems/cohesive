using System.Text.Json;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Infra.Tests;

public sealed class InfrastructureTargetDeploymentCompilerTests
{
    static readonly InfrastructureCapabilityId Https = new("test/workload/https");
    static readonly InfrastructureCapabilityId Storage = new("test/resource/storage");
    static readonly InfrastructureTargetFacilityId AppService = new("test/app-service");
    static readonly InfrastructureTargetFacilityId ObjectStore = new("test/object-store");
    static readonly InfrastructureCapabilityVariantId Variant = new("test/production");
    static readonly InfrastructureNodeId Api = new("workloads/api");
    static readonly InfrastructureNodeId State = new("resources/state");
    static readonly InfrastructurePhysicalResourceId ApiPhysical = new("test/app-service/sites/api");
    static readonly InfrastructurePhysicalResourceId StatePhysical = new("test/object-store/buckets/state");
    static readonly InfrastructureLifecycleAuthorityId Authority = new("test/state/production");
    static readonly InfrastructureTargetId Aspire = new("aspire/13.1");
    static readonly InfrastructureTargetId DockerCompose = new("docker-compose/2.30");
    static readonly InfrastructureOperatingBoundaryId DisposableBoundary = new("test/boundaries/disposable");
    static readonly InfrastructureOperatingBoundaryId PrivateNetworkBoundary = new("test/boundaries/private-network");
    static readonly SourceReference Source = SourceReference.Create("test-adapter", "production");
    static readonly SourceReference PolicySource = SourceReference.Create("test-policy", "disposable");

    [Fact]
    public void Fluent_and_direct_manifests_materialize_the_same_canonical_ir()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        var direct = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            new("test/deployments/production/v1"),
            semantic.Definition.ToReference(),
            facilities,
            [new(Api, AppService, ApiPhysical, [Source])],
            [new(State, ObjectStore, StatePhysical, Authority, [Source])]);

        var fluent = Deployment(semantic, facilities);

        Assert.Equal(direct.Fingerprint, fluent.Fingerprint);
        Assert.Equal(direct.ToReference(), fluent.ToReference());
        Assert.True(direct.Workloads.SequenceEqual(fluent.Workloads));
        Assert.True(direct.Resources.SequenceEqual(fluent.Resources));
        Assert.Empty(direct.SourceMap.Entries);
        Assert.Equal(2, fluent.SourceMap.Entries.Length);
        Assert.All(fluent.SourceMap.Entries, static entry =>
        {
            Assert.StartsWith("csharp://", entry.Source.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("/private/", entry.Source.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(":\\", entry.Source.Value, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Compiler_derives_facility_discharge_lifecycle_placements_and_physical_witnesses()
    {
        var semantic = Semantic();
        var manifest = Deployment(semantic, Facilities());

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        var repeated = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.Diagnostics);
        Assert.Null(plan.BoundaryAcceptancePolicy);
        Assert.Equal(plan.FacilityPlan, repeated.FacilityPlan);
        Assert.Equal(plan.Realization, repeated.Realization);
        Assert.Equal(ApiPhysical, manifest.FindWorkload(Api).PhysicalResource);
        Assert.Equal(StatePhysical, manifest.FindResource(State).PhysicalResource);
        Assert.Equal(AppService, plan.FacilityPlan.FindDecision(Api).Facility);
        Assert.Equal(ObjectStore, plan.FacilityPlan.FindDecision(State).Facility);
        var realization = Assert.IsType<InfrastructureRealization>(plan.Realization);
        var placement = Assert.Single(realization.WorkloadPlacements);
        Assert.Equal(Api, placement.Workload);
        Assert.Equal(ApiPhysical, placement.PhysicalResource);
        var lifecycle = Assert.Single(realization.Lifecycle.Bindings);
        Assert.Equal(State, lifecycle.Resource);
        Assert.Equal(StatePhysical, lifecycle.PhysicalResource);
        Assert.Equal(Authority, lifecycle.Authority);
        Assert.Equal(InfrastructureLifecycleDisposition.Managed, lifecycle.Disposition);
        Assert.Equal(2, realization.CapabilityWitnesses.Length);
        Assert.All(realization.WitnessDecisions, static decision => Assert.True(decision.IsComplete));
    }

    [Fact]
    public void Fluent_foreign_management_compiles_to_one_manager_and_one_selected_target_reference()
    {
        var semantic = Semantic();
        var facilities = Facilities(Aspire);
        var fluent = InfrastructureTargetDeployments.Define(
            new("test/deployments/compose-managed-aspire-consumed/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.ReferencedResource(
                    State,
                    ObjectStore,
                    StatePhysical,
                    DockerCompose,
                    Authority,
                    [Source]);
            });
        var direct = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            fluent.Id,
            semantic.Definition.ToReference(),
            facilities,
            [new(Api, AppService, ApiPhysical, [Source])],
            [new(State, ObjectStore, StatePhysical, Authority, [Source], DockerCompose)]);

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, fluent);
        var realization = Assert.IsType<InfrastructureRealization>(plan.Realization);
        var options = StrictDocumentJson.CreateOptions();
        var roundTrip = Assert.IsType<InfrastructureTargetDeploymentManifest>(
            JsonSerializer.Deserialize<InfrastructureTargetDeploymentManifest>(
                JsonSerializer.Serialize(fluent, options),
                options));

        Assert.True(plan.IsComplete);
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(direct.Fingerprint, fluent.Fingerprint);
        Assert.True(direct.Resources.SequenceEqual(fluent.Resources));
        Assert.Equal(fluent, roundTrip);
        Assert.Equal(2, realization.Lifecycle.Bindings.Length);
        var manager = Assert.Single(
            realization.Lifecycle.Bindings,
            static binding => binding.Disposition == InfrastructureLifecycleDisposition.Managed);
        Assert.Equal(DockerCompose, manager.Interpreter);
        Assert.Equal(Authority, manager.Authority);
        var reference = Assert.Single(
            realization.Lifecycle.Bindings,
            static binding => binding.Disposition == InfrastructureLifecycleDisposition.Referenced);
        Assert.Equal(Aspire, reference.Interpreter);
        Assert.Equal(Authority, reference.Authority);
    }

    [Fact]
    public void Invalid_foreign_manager_combinations_produce_structured_diagnostics()
    {
        InfrastructureNodeId externalState = new("resources/external-state");
        var semantic = Infrastructure.Define(
            new("test/target-deployment-invalid-manager"),
            new("1"),
            new("test/target-deployment-invalid-manager/bindings/v1"),
            infrastructure =>
            {
                infrastructure.Workload(Api).Requires(Https);
                infrastructure.Resource(State).Persistent().Requires(Storage);
                infrastructure.Resource(externalState).External().Requires(Storage);
            });
        var facilities = Facilities(Aspire);
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/invalid-manager/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.ReferencedResource(State, ObjectStore, StatePhysical, Aspire, Authority, [Source]);
                deployment.ReferencedResource(
                    externalState,
                    ObjectStore,
                    new("test/object-store/buckets/external"),
                    DockerCompose,
                    new("external/owner"),
                    [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.Realization);
        Assert.Contains(
            plan.Diagnostics,
            static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ResourceManagerSelfReference
                && diagnostic.Evidence!.Observed == Aspire.Value);
        Assert.Contains(
            plan.Diagnostics,
            static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ExternalResourceManager
                && diagnostic.Evidence!.Observed == DockerCompose.Value);
        Assert.All(
            plan.Diagnostics.Where(static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ResourceManagerSelfReference
                || diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ExternalResourceManager),
            static diagnostic =>
            {
                Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
                Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
                Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
            });
    }

    [Fact]
    public void Shared_physical_resource_ownership_conflicts_are_surfaced_as_a_deployment_diagnostic()
    {
        InfrastructureNodeId mirror = new("resources/mirror");
        InfrastructureNodeId externalAlias = new("resources/external-alias");
        InfrastructureLifecycleAuthorityId otherAuthority = new("test/state/other");
        var semantic = Infrastructure.Define(
            new("test/target-deployment-conflicting-physical-ownership"),
            new("1"),
            new("test/target-deployment-conflicting-physical-ownership/bindings/v1"),
            infrastructure =>
            {
                infrastructure.Resource(State).Persistent().Requires(Storage);
                infrastructure.Resource(mirror).Persistent().Requires(Storage);
                infrastructure.Resource(externalAlias).External().Requires(Storage);
            });
        var facilities = Facilities(Aspire);
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/conflicting-physical-ownership/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.ReferencedResource(
                    State,
                    ObjectStore,
                    StatePhysical,
                    DockerCompose,
                    Authority,
                    [Source]);
                deployment.Resource(mirror, ObjectStore, StatePhysical, otherAuthority, [Source]);
                deployment.Resource(externalAlias, ObjectStore, StatePhysical, Authority, [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.Realization);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ResourceLifecycleInvalid);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("several lifecycle authorities", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(manifest.Id.Value, diagnostic.Evidence!.Subject);
        Assert.NotEmpty(diagnostic.Evidence.SourceReferences);
        Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
    }

    [Fact]
    public void Compiler_materializes_exact_boundary_policy_from_declarative_target_acceptance()
    {
        var semantic = Semantic();
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/constrained-target-facilities/v1"),
            new("test/constrained-target-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(new(
                    new("test/evidence/constrained-https"),
                    Https,
                    CapabilityRealizationKind.Constrained,
                    operatingBoundaries: [DisposableBoundary, PrivateNetworkBoundary],
                    sourceReferences: [Source]));
                target.Resource(ObjectStore).Provides(Native("test/evidence/storage", Storage));
                target.Within(new(
                    DisposableBoundary,
                    "The target is suitable only for disposable tests.",
                    [Source]));
                target.Within(new(
                    PrivateNetworkBoundary,
                    "The target endpoint is exposed only on the private test network.",
                    [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/constrained/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
                deployment.AcceptBoundary(
                    PrivateNetworkBoundary,
                    "This deployment runs only on the isolated test network.",
                    [PolicySource]);
                deployment.AcceptBoundary(
                    DisposableBoundary,
                    "This deployment is used only for disposable test runs.",
                    [PolicySource]);
            });
        var direct = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            manifest.Id,
            semantic.Definition.ToReference(),
            facilities,
            [new(Api, AppService, ApiPhysical, [Source])],
            [new(State, ObjectStore, StatePhysical, Authority, [Source])],
            boundaryAcceptances:
            [
                new(
                    DisposableBoundary,
                    "This deployment is used only for disposable test runs.",
                    [PolicySource]),
                new(
                    PrivateNetworkBoundary,
                    "This deployment runs only on the isolated test network.",
                    [PolicySource])
            ]);

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        var repeated = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        var restored = JsonSerializer.Deserialize<InfrastructureTargetDeploymentManifest>(
            JsonSerializer.Serialize(manifest),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(plan.IsComplete, string.Join(Environment.NewLine, plan.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(direct.Fingerprint, manifest.Fingerprint);
        Assert.Equal(manifest, restored);
        var policy = Assert.IsType<InfrastructureBoundaryAcceptancePolicy>(plan.BoundaryAcceptancePolicy);
        Assert.Equal(policy, repeated.BoundaryAcceptancePolicy);
        Assert.Equal(policy.ToReference(), plan.FacilityPlan.CapabilityClosure.BoundaryAcceptancePolicy);
        var decision = Assert.Single(
            plan.FacilityPlan.CapabilityClosure.Decisions,
            static decision => decision.Capability == Https);
        Assert.All(policy.Acceptances, acceptance => Assert.Equal(decision.Requirement, acceptance.Requirement));
        Assert.Equal<InfrastructureOperatingBoundaryId>(
            [DisposableBoundary, PrivateNetworkBoundary],
            policy.Acceptances.Select(static acceptance => acceptance.Boundary));
        Assert.Equal(
            "This deployment is used only for disposable test runs.",
            policy.FindAcceptance(decision.Requirement, DisposableBoundary)?.Rationale);
        Assert.Equal(
            "This deployment runs only on the isolated test network.",
            policy.FindAcceptance(decision.Requirement, PrivateNetworkBoundary)?.Rationale);
        Assert.All(policy.Acceptances, static acceptance =>
            Assert.Equal<SourceReference>([PolicySource], acceptance.SourceReferences));
        Assert.Equal<InfrastructureOperatingBoundaryId>(
            [DisposableBoundary, PrivateNetworkBoundary],
            decision.AcceptedOperatingBoundaries);
        Assert.Empty(decision.MissingOperatingBoundaries);
    }

    [Fact]
    public void Unknown_and_unused_target_boundary_acceptances_are_diagnostic()
    {
        InfrastructureOperatingBoundaryId unknown = new("test/boundaries/unknown");
        var semantic = Semantic();
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/native-with-unused-boundary-facilities/v1"),
            new("test/native-with-unused-boundary-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(Native("test/evidence/https", Https));
                target.Resource(ObjectStore).Provides(Native("test/evidence/storage", Storage));
                target.Within(new(
                    DisposableBoundary,
                    "An available boundary that no selected proof uses.",
                    [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/invalid-boundaries/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
                deployment.AcceptBoundary(DisposableBoundary, "Stale acceptance.", [PolicySource]);
                deployment.AcceptBoundary(unknown, "Unknown acceptance.", [PolicySource]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.Realization);
        Assert.Empty(Assert.IsType<InfrastructureBoundaryAcceptancePolicy>(plan.BoundaryAcceptancePolicy).Acceptances);
        var unknownDiagnostic = Assert.Single(
            plan.Diagnostics,
            static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.BoundaryAcceptanceUnknown);
        Assert.Equal(DiagnosticSeverity.Error, unknownDiagnostic.Severity);
        Assert.Equal(unknown.Value, unknownDiagnostic.Evidence?.Subject);
        var unusedDiagnostic = Assert.Single(
            plan.Diagnostics,
            static diagnostic =>
                diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.BoundaryAcceptanceUnused);
        Assert.Equal(DiagnosticSeverity.Warning, unusedDiagnostic.Severity);
        Assert.Equal(DisposableBoundary.Value, unusedDiagnostic.Evidence?.Subject);
        Assert.All([unknownDiagnostic, unusedDiagnostic], static diagnostic =>
        {
            Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
        });
    }

    [Fact]
    public void Compiler_derives_a_complete_subset_from_declarative_non_participation()
    {
        InfrastructureNodeId admin = new("workloads/admin");
        var semantic = Infrastructure.Define(
            new("test/target-deployment-subset"),
            new("1"),
            new("test/target-deployment-subset/bindings/v1"),
            infrastructure =>
            {
                infrastructure.Workload(Api).Requires(Https);
                infrastructure.Workload(admin).Requires(Https);
                infrastructure.Resource(State).Persistent().Requires(Storage);
            });
        var facilities = Facilities();
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/api-only/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.NonParticipatingWorkload(
                    admin,
                    "The API-only environment does not host the administration workload.",
                    ["profile://local/api-only"]);
                deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
            });
        var direct = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            manifest.Id,
            semantic.Definition.ToReference(),
            facilities,
            [new(Api, AppService, ApiPhysical, [Source])],
            [new(State, ObjectStore, StatePhysical, Authority, [Source])],
            [new(
                admin,
                "The API-only environment does not host the administration workload.",
                ["profile://local/api-only"])]);

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        var realization = Assert.IsType<InfrastructureRealization>(plan.Realization);
        var roundTrip = JsonSerializer.Deserialize<InfrastructureTargetDeploymentManifest>(
            JsonSerializer.Serialize(manifest),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.True(plan.IsComplete, string.Join(Environment.NewLine, plan.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Empty(plan.Diagnostics);
        Assert.Equal(direct.Fingerprint, manifest.Fingerprint);
        Assert.Equal(direct.ToReference(), manifest.ToReference());
        Assert.Equal(3, plan.FacilityPlan.Decisions.Length);
        Assert.Equal(admin, Assert.Single(manifest.NonParticipatingWorkloads).Workload);
        Assert.Equal(manifest.NonParticipatingWorkloads, realization.NonParticipatingWorkloads);
        Assert.DoesNotContain(realization.WitnessDecisions, decision =>
            decision.Subjects.SequenceEqual([admin]));
        Assert.Equal(manifest, roundTrip);
    }

    [Fact]
    public void Missing_physical_declarations_produce_diagnostics_without_application_side_assessment()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/incomplete/v1"),
            semantic.Definition,
            facilities,
            deployment => deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]));

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.Realization);
        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.WorkloadMissing);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(Api.Value, diagnostic.Evidence?.Subject);
        Assert.NotEmpty(diagnostic.Evidence!.ResolutionOptions);
    }

    [Fact]
    public void Target_deployment_rejects_unknown_and_conflicting_non_participation()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/invalid-participation/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.NonParticipatingWorkload(
                    Api,
                    "Conflicts with the physical deployment.",
                    ["profile://local/invalid"]);
                deployment.NonParticipatingWorkload(
                    new("workloads/removed"),
                    "Stale profile entry.",
                    ["profile://local/invalid"]);
                deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.False(plan.IsComplete);
        Assert.Null(plan.Realization);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.WorkloadParticipationConflict
                          && diagnostic.Evidence?.Subject == Api.Value);
        Assert.Contains(
            plan.Diagnostics,
            diagnostic => diagnostic.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.WorkloadNonParticipationUnknown
                          && diagnostic.Evidence?.Subject == "workloads/removed");
        Assert.All(plan.Diagnostics, static diagnostic =>
        {
            Assert.NotNull(diagnostic.Evidence);
            Assert.NotEmpty(diagnostic.Evidence!.SourceReferences);
            Assert.NotEmpty(diagnostic.Evidence.ResolutionOptions);
        });
    }

    [Fact]
    public void Compiler_attributes_composed_evidence_to_subject_and_auxiliary_facilities()
    {
        InfrastructureCapabilityId authentication = new("test/identity/authentication");
        InfrastructureCapabilityId authenticatedHttps = new("test/network/authenticated-https");
        InfrastructureNodeId ui = new("workloads/ui");
        InfrastructureNodeId identity = new("resources/identity");
        InfrastructureTargetFacilityId entra = new("test/entra");
        InfrastructurePhysicalResourceId uiPhysical = new("test/app-service/sites/ui");
        InfrastructurePhysicalResourceId identityPhysical = new("test/entra/tenant/current");
        var semantic = Infrastructure.Define(
            new("test/composed-deployment"),
            new("1"),
            new("test/composed-deployment/bindings/v1"),
            infrastructure =>
            {
                var endpoint = infrastructure
                    .Contract(new("test/contracts/authenticated-endpoint/v1"), new("test/rules/authenticated-endpoint/v1"))
                    .Requires(authenticatedHttps)
                    .SourcedFrom(Source.Value);
                var client = infrastructure.Workload(ui).Requires(Https);
                var server = infrastructure.Workload(Api).Requires(Https);
                _ = infrastructure.Resource(identity).External().Requires(authentication);
                infrastructure.Bind(client).To(server).As(endpoint);
            });
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/composed-target-facilities/v1"),
            new("test/composed-target-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(AppService).Provides(Native("test/evidence/https", Https));
                target.Resource(entra).Provides(Native("test/evidence/authentication", authentication));
                target.Composes(new(
                    new("test/rules/authenticated-https/v1"),
                    authenticatedHttps,
                    [Https, authentication],
                    sourceReferences: [Source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/composed/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(ui, AppService, uiPhysical, [Source]);
                deployment.Workload(Api, AppService, ApiPhysical, [Source]);
                deployment.Resource(identity, entra, identityPhysical, new("external/entra"), [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.True(plan.IsComplete);
        var realization = Assert.IsType<InfrastructureRealization>(plan.Realization);
        var composed = Assert.Single(
            realization.WitnessDecisions,
            decision => decision.Capability == authenticatedHttps);
        Assert.True(composed.IsComplete);
        var identityEvidence = Assert.Single(
            realization.CapabilityWitnesses,
            witness => witness.Requirement == composed.Requirement
                && witness.Evidence == new InfrastructureCapabilityEvidenceId("test/evidence/authentication"));
        Assert.Equal(3, identityEvidence.PhysicalResources.Length);
        Assert.Contains(uiPhysical, identityEvidence.PhysicalResources);
        Assert.Contains(ApiPhysical, identityEvidence.PhysicalResources);
        Assert.Contains(identityPhysical, identityEvidence.PhysicalResources);
    }

    [Fact]
    public void Binding_scoped_evidence_witnesses_every_physical_subject()
    {
        InfrastructureCapabilityId execution = new("test/workload/execution");
        InfrastructureCapabilityId taskHub = new("test/process/task-hub");
        InfrastructureNodeId worker = new("workloads/worker");
        InfrastructureNodeId scheduler = new("resources/scheduler");
        InfrastructureTargetFacilityId workerFacility = new("test/worker-runtime");
        InfrastructureTargetFacilityId schedulerFacility = new("test/durable-task");
        InfrastructurePhysicalResourceId workerPhysical = new("test/worker-runtime/apps/worker");
        InfrastructurePhysicalResourceId schedulerPhysical = new("test/durable-task/hubs/current");
        InfrastructureCapabilityEvidenceId taskHubEvidence = new("test/evidence/task-hub");
        var semantic = Infrastructure.Define(
            new("test/binding-scoped-witness"),
            new("1"),
            new("test/binding-scoped-witness/bindings/v1"),
            infrastructure =>
            {
                var contract = infrastructure
                    .Contract(new("test/contracts/task-hub-client/v1"), new("test/rules/task-hub-client/v1"))
                    .Requires(taskHub)
                    .SourcedFrom(Source.Value);
                var client = infrastructure.Workload(worker).Requires(execution);
                var hub = infrastructure.Resource(scheduler).Persistent().Requires(taskHub);
                infrastructure.Bind(client).To(hub).As(contract);
            });
        var facilities = InfrastructureTargetFacilities.Define(
            new("test/binding-scoped-target-facilities/v1"),
            new("test/binding-scoped-target-capabilities/v1"),
            new("test-target/1"),
            Variant,
            [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            target =>
            {
                target.Workload(workerFacility).Provides(Native("test/evidence/execution", execution));
                target.Resource(schedulerFacility).Provides(Native(taskHubEvidence.Value, taskHub));
            });
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/binding-scoped/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(worker, workerFacility, workerPhysical, [Source]);
                deployment.Resource(scheduler, schedulerFacility, schedulerPhysical, Authority, [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        Assert.True(plan.IsComplete);
        var realization = Assert.IsType<InfrastructureRealization>(plan.Realization);
        var bindingDecision = Assert.Single(
            realization.WitnessDecisions,
            decision => decision.Capability == taskHub && decision.Subjects.Length == 2);
        var witness = Assert.Single(
            realization.CapabilityWitnesses,
            candidate => candidate.Requirement == bindingDecision.Requirement
                && candidate.Evidence == taskHubEvidence);
        Assert.Equal<InfrastructurePhysicalResourceId>([schedulerPhysical, workerPhysical], witness.PhysicalResources);
    }

    [Fact]
    public void Manifest_round_trips_without_changing_its_fingerprint()
    {
        var semantic = Semantic();
        var manifest = Deployment(semantic, Facilities());
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(manifest, options);

        var restored = Assert.IsType<InfrastructureTargetDeploymentManifest>(
            JsonSerializer.Deserialize<InfrastructureTargetDeploymentManifest>(
                json,
                options));

        Assert.DoesNotContain("/private/", json, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", json, StringComparison.Ordinal);
        Assert.Equal(manifest, restored);
        Assert.Equal(manifest.Fingerprint, restored.Fingerprint);
        Assert.Equal(manifest.ToReference(), restored.ToReference());
    }

    [Fact]
    public void Authoring_provenance_is_persisted_but_excluded_from_semantic_fingerprints()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        var first = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            new("test/deployments/source-map/v1"),
            semantic.Definition.ToReference(),
            facilities,
            [new(Api, AppService, ApiPhysical, [Source])],
            [new(State, ObjectStore, StatePhysical, Authority, [Source])],
            sourceMap: new(
            [
                new(
                    InfrastructureSourceReferences.Node(Api),
                    SourceReference.Create("csharp", "test/deployments/source-map/v1#First:L10"))
            ]));
        var moved = new InfrastructureTargetDeploymentManifest(
            InfrastructureTargetDeploymentManifest.CurrentSchemaVersion,
            first.Id,
            first.Definition,
            facilities,
            first.Workloads,
            first.Resources,
            sourceMap: new(
            [
                new(
                    InfrastructureSourceReferences.Node(Api),
                    SourceReference.Create("csharp", "test/deployments/source-map/v1#Moved:L200"))
            ]));

        Assert.NotEqual(first, moved);
        Assert.Equal(first.Fingerprint, moved.Fingerprint);
        Assert.Equal(first.ToReference(), moved.ToReference());
    }

    [Fact]
    public void Unknown_deployments_report_semantic_evidence_and_automatic_authoring_provenance()
    {
        var semantic = Semantic();
        var facilities = Facilities();
        InfrastructureNodeId stale = new("workloads/stale");
        var manifest = InfrastructureTargetDeployments.Define(
            new("test/deployments/stale/v1"),
            semantic.Definition,
            facilities,
            deployment =>
            {
                deployment.Workload(stale, AppService, new("test/app-service/sites/stale"), [Source]);
                deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
            });

        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);

        var diagnostic = Assert.Single(
            plan.Diagnostics,
            static item => item.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.NodeUnknown);
        Assert.Contains(Source.Value, diagnostic.Evidence!.SourceReferences);
        Assert.Contains(
            diagnostic.Evidence.SourceReferences,
            static reference => reference.StartsWith("csharp://", StringComparison.Ordinal));
    }

    static InfrastructureAuthoringResult Semantic() => Infrastructure.Define(
        new("test/target-deployment"),
        new("1"),
        new("test/target-deployment/bindings/v1"),
        infrastructure =>
        {
            infrastructure.Workload(Api).Requires(Https);
            infrastructure.Resource(State).Persistent().Requires(Storage);
        });

    static InfrastructureTargetFacilityManifest Facilities(InfrastructureTargetId? target = null) => InfrastructureTargetFacilities.Define(
        new("test/target-facilities/v1"),
        new("test/target-capabilities/v1"),
        target ?? new("test-target/1"),
        Variant,
        [InfrastructureDefinitionDocument.CurrentSchemaVersion],
        target =>
        {
            target.Workload(AppService).Provides(Native("test/evidence/https", Https));
            target.Resource(ObjectStore).Provides(Native("test/evidence/storage", Storage));
        });

    static InfrastructureTargetDeploymentManifest Deployment(
        InfrastructureAuthoringResult semantic,
        InfrastructureTargetFacilityManifest facilities) => InfrastructureTargetDeployments.Define(
        new("test/deployments/production/v1"),
        semantic.Definition,
        facilities,
        deployment =>
        {
            deployment.Workload(Api, AppService, ApiPhysical, [Source]);
            deployment.Resource(State, ObjectStore, StatePhysical, Authority, [Source]);
        });

    static InfrastructureCapabilityEvidence Native(string id, InfrastructureCapabilityId capability) => new(
        new(id),
        capability,
        CapabilityRealizationKind.Native,
        sourceReferences: [Source]);
}
