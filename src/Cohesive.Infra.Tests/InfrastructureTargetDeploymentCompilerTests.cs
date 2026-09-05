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
    static readonly SourceReference Source = SourceReference.Create("test-adapter", "production");

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

    static InfrastructureTargetFacilityManifest Facilities() => InfrastructureTargetFacilities.Define(
        new("test/target-facilities/v1"),
        new("test/target-capabilities/v1"),
        new("test-target/1"),
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
