using Microsoft.Extensions.DependencyInjection;
using Cohesive.Host.Composition;
using Cohesive.Adapters.Postgres.Infra;
using Cohesive.Adapters.Postgres.Services;
using AspireFirst.Orders;
using Cohesive.Adapters.Aspire.Postgres;
using Cohesive.Relations.Infra;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AspireFirst;
using Cohesive.Adapters.Aspire;
using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using static Cohesive.Infra.InfrastructureTargetImplementation;

namespace Cohesive.Adapters.Aspire.Tests;

public sealed class AspireInfrastructureAssociationTests
{
    static readonly FulfillmentInfrastructure ExampleApp = TestModels.Infrastructure();

    static readonly SourceReference Source = SourceReference.Create("test", "aspire-first");
    static InfrastructureTargetImplementation Storage => ResourceImplementation(new("test/storage"),
        ExampleApp.Definition.Definition.Definition.Resources.Single(resource => resource.Id == FulfillmentInfrastructure.Store).Requirements.Select(requirement => requirement.Capability).Select((capability, index) => new InfrastructureCapabilityEvidence(
            new($"test/storage/{index}"), capability, CapabilityRealizationKind.Native, sourceReferences: [Source])).ToArray());
    static InfrastructureTargetImplementation Process => WorkloadImplementation(new("test/process"),
        [new(new("test/process/evidence"), FulfillmentInfrastructure.ApplicationExecution, CapabilityRealizationKind.Native, sourceReferences: [Source])]);
    static IDistributedApplicationBuilder Builder() => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        Args = [], DisableDashboard = true,
        AssemblyName = typeof(AspireInfrastructureAssociationTests).Assembly.GetName().Name,
        ProjectDirectory = AppContext.BaseDirectory
    });
    static AspireInfrastructureAssociation Attach(IDistributedApplicationBuilder builder,
        Action<AspireInfrastructureAssociationBuilder> configure) => AspireInfrastructureAssociation.Attach(
            builder, ExampleApp.Definition, new("test/association"), new("development"), configure);

    [Fact]
    public void Inferred_preparation_matches_explicit_requirements_and_is_reused_for_association()
    {
        var model = TestModels.Fulfillment();
        var inferred = Infrastructure.Define(new("test/prepared"), new("1"), new("test/prepared/bindings"),
            infra => infra.Resource(FulfillmentInfrastructure.Store).Persistent()
                .Postgres(model.Storage.Bindings, model.Endpoints.Plan));
        var evidence = inferred.RequireResourcePreparation<PostgresPreparedQueryEvidence>(FulfillmentInfrastructure.Store);
        var explicitEvidence = model.Storage.Bindings.PrepareService(model.Endpoints.Plan,
            new("postgres/resources/orders/implementation"));
        var explicitDefinition = Infrastructure.Define(new("test/prepared"), new("1"), new("test/prepared/bindings"), infra =>
        {
            var storage = infra.Resource(FulfillmentInfrastructure.Store).Persistent();
            foreach (var requirement in explicitEvidence.Requirements) storage.Requires(requirement);
        });
        Assert.Equal(explicitDefinition.Definition.Fingerprint, inferred.Definition.Fingerprint);
        var builder = Builder();
        var db = builder.AddPostgres("postgres").AddDatabase("orders");
        var implicitAssociation = builder.AttachCohesive(inferred, new("test/native"), new("dev"), associations =>
        {
            Assert.Same(evidence, associations.RequireResourcePreparation<PostgresPreparedQueryEvidence>(FulfillmentInfrastructure.Store));
            associations.Resource(FulfillmentInfrastructure.Store, db, authority: new("test/owner"));
        });
        var explicitAssociation = AspireInfrastructureAssociation.Attach(builder, explicitDefinition, new("test/native"), new("dev"),
            associations => associations.Resource(FulfillmentInfrastructure.Store, db, explicitEvidence, new("test/owner")));
        Assert.True(implicitAssociation.Deployment.IsComplete);
        Assert.Equal(explicitAssociation.Deployment.Manifest.Fingerprint, implicitAssociation.Deployment.Manifest.Fingerprint);
        Assert.Same(evidence, inferred.RequireResourcePreparation<PostgresPreparedQueryEvidence>(FulfillmentInfrastructure.Store));
        using var app = builder.Build();
    }

    [Fact]
    public void Implicit_database_association_rejects_missing_preparation()
    {
        var definition = Infrastructure.Define(new("test/no-preparation"), new("1"), new("test/bindings"),
            infra => infra.Resource(FulfillmentInfrastructure.Store).Persistent());
        var builder = Builder();
        var db = builder.AddPostgres("postgres").AddDatabase("orders");
        var error = Assert.Throws<InvalidOperationException>(() => builder.AttachCohesive(definition,
            new("test/native"), new("dev"), associations => associations.Resource(FulfillmentInfrastructure.Store, db)));
        Assert.Contains(FulfillmentInfrastructure.Store.Value, error.Message);
        using var app = builder.Build();
    }

    [Fact]
    public void Prepared_evidence_checks_actual_database_name_before_association()
    {
        var builder = Builder();
        var server = builder.AddPostgres("postgres");
        var wrong = server.AddDatabase("orders-label", "inventory");
        Assert.Throws<ArgumentException>(() => Attach(builder, associations =>
            associations.Resource(FulfillmentInfrastructure.Store, wrong, authority: new("test/authority"))));
        var correct = server.AddDatabase("different-label", "orders");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        Assert.True(Attach(builder, associations => associations
            .Resource(FulfillmentInfrastructure.Store, correct, authority: new("test/authority"))
            .Workload(FulfillmentInfrastructure.Worker, worker, Process)).Deployment.IsComplete);
        using var app = builder.Build();
    }

    [Fact]
    public void Project_inference_requires_native_metadata_and_does_not_claim_durable_execution()
    {
        var builder = Builder();
        var configured = TestModels.Infrastructure().Configure(builder);
        var project = builder.CreateResourceBuilder((ProjectResource)configured.Resources[FulfillmentInfrastructure.Worker]);
        var definition = Infrastructure.Define(new("test/project"), new("1"), new("test/project/bindings"), infra =>
            infra.Workload(FulfillmentInfrastructure.Worker).Requires(AspireInfrastructure.DotNetProjectExecution)
                .Requires(new InfrastructureCapabilityId("test/durable-execution")));
        var inferred = AspireInfrastructureAssociation.Attach(builder, definition, new("test/project/native"), new("test"),
            a => a.Workload(FulfillmentInfrastructure.Worker, project, sources: [Source]));
        Assert.False(inferred.Deployment.IsComplete);
        Assert.Contains(inferred.Deployment.Diagnostics, item => item.Code == InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable
            && item.SchemaLocation == "test/durable-execution");
        // Facility selection is atomic: an unsupported extra requirement rejects the entire selection.
        // Prove the inferred execution capability independently instead of treating incompleteness as evidence.
        var executionOnly = Infrastructure.Define(new("test/execution"), new("1"), new("test/execution/bindings"),
            infra => infra.Workload(FulfillmentInfrastructure.Worker).Requires(AspireInfrastructure.DotNetProjectExecution));
        var supported = AspireInfrastructureAssociation.Attach(builder, executionOnly, new("test/execution/native"), new("test"),
            a => a.Workload(FulfillmentInfrastructure.Worker, project, sources: [Source]));
        Assert.True(supported.Deployment.IsComplete);
        var execution = Assert.Single(supported.Deployment.FacilityPlan.CapabilityClosure.Decisions);
        Assert.Equal(AspireInfrastructure.DotNetProjectExecution, execution.Capability);
        Assert.True(execution.IsAdmissible);
        Assert.Equal(CapabilityRealizationKind.Native, execution.Realization);
        var metadata = project.Resource.GetProjectMetadata();
        project.Resource.Annotations.Remove(metadata);
        Assert.Throws<InvalidOperationException>(() => AspireInfrastructureAssociation.Attach(builder, definition,
            new("test/project/missing-metadata"), new("test"), a => a.Workload(FulfillmentInfrastructure.Worker, project)));
        project.Resource.Annotations.Add(metadata);
        using var app = builder.Build();
    }

    [Fact]
    public void Omitted_selection_sources_are_deterministic_and_match_explicit_convention()
    {
        var builder = Builder();
        var database = builder.AddContainer("storage", "postgres", "17");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        var inferred = Attach(builder, a => a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"))
            .Workload(FulfillmentInfrastructure.Worker, worker, Process));
        var explicitSources = Attach(builder, a => a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"),
                [SourceReference.Create("aspire-native-association-v1", "test/association/resources/orders/storage")])
            .Workload(FulfillmentInfrastructure.Worker, worker, Process,
                [SourceReference.Create("aspire-native-association-v1", "test/association/workloads/orders/worker")]));
        Assert.True(inferred.Deployment.IsComplete);
        Assert.Equal(explicitSources.Deployment.Manifest.Fingerprint, inferred.Deployment.Manifest.Fingerprint);
        Assert.Throws<ArgumentException>(() => Attach(builder,
            a => a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [])));
        using var app = builder.Build();
    }

    [Fact]
    public void Runnable_example_uses_generated_project_metadata_and_existing_native_database()
    {
        var builder = Builder();
        var association = TestModels.Infrastructure().Configure(builder);
        Assert.True(association.Deployment.IsComplete,
            string.Join(Environment.NewLine, association.Deployment.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, association.Resources.Count);
        Assert.Same(builder.Resources.Single(r => r.Name == "orders"), association.Resources[FulfillmentInfrastructure.Store]);
        Assert.IsType<ProjectResource>(association.Resources[FulfillmentInfrastructure.Worker]);
        Assert.Same(builder.Resources.Single(r => r.Name == "worker"), association.Resources[FulfillmentInfrastructure.Worker]);
        Assert.Contains(builder.Resources, r => r.Name == "native-only");
        Assert.DoesNotContain(association.Resources.Values, r => r.Name == "native-only");
        Assert.DoesNotContain(association.Resources.Values, r => r.Name == "postgres");
        using var app = builder.Build(); // model build only; no containers or projects are started
    }

    [Fact]
    public void Association_preserves_native_model_annotations_and_canonical_fingerprint()
    {
        var builder = Builder();
        var database = builder.AddContainer("storage", "postgres", "17").WithEnvironment("EXAMPLE", "preserved");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info").WaitFor(database);
        var before = builder.Resources.ToArray();
        var annotations = before.Select(r => r.Annotations.ToArray()).ToArray();
        var association = Attach(builder, a => a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [Source])
            .Workload(FulfillmentInfrastructure.Worker, worker, Process, [Source]));
        Assert.True(association.Deployment.IsComplete);
        Assert.Equal(before, builder.Resources);
        for (var i = 0; i < before.Length; i++) Assert.Equal(annotations[i], before[i].Annotations);
        Assert.Same(database.Resource, association.Resources[FulfillmentInfrastructure.Store]);
        var direct = InfrastructureTargetDeployments.Define(new("test/association"), ExampleApp.Definition.Definition,
            new("test/association/facilities"), new("test/association/profile"), AspireInfrastructureAssociation.Target,
            new("development"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], d => d
                .ResourceUsing(FulfillmentInfrastructure.Store, Storage, new("aspire/resource/storage"), new("aspire/test"), [Source])
                .WorkloadUsing(FulfillmentInfrastructure.Worker, Process, new("aspire/resource/worker"), [Source]));
        Assert.Equal(direct.Fingerprint, association.Deployment.Manifest.Fingerprint);
        using var app = builder.Build();
    }

    [Fact]
    public void Missing_association_remains_a_canonical_coverage_diagnostic()
    {
        var builder = Builder();
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        var association = Attach(builder, a => a.Workload(FulfillmentInfrastructure.Worker, worker, Process, [Source]));
        Assert.False(association.Deployment.IsComplete);
        Assert.Contains(association.Deployment.Diagnostics,
            d => d.Code == InfrastructureTargetDeploymentCompiler.DiagnosticCodes.ResourceMissing);
        using var app = builder.Build();
    }

    [Fact]
    public void Resource_type_does_not_invent_missing_capability_evidence()
    {
        var builder = Builder();
        var database = builder.AddContainer("postgres", "postgres", "17");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        var incompatible = ResourceImplementation(new("test/other"),
            [new(new("test/other/evidence"), new("test/unrelated"), CapabilityRealizationKind.Native, sourceReferences: [Source])]);
        var association = Attach(builder, a => a.Resource(FulfillmentInfrastructure.Store, database, incompatible, new("aspire/test"), [Source])
            .Workload(FulfillmentInfrastructure.Worker, worker, Process, [Source]));
        Assert.False(association.Deployment.IsComplete);
        Assert.NotEmpty(association.Deployment.Diagnostics);
        using var app = builder.Build();
    }

    [Fact]
    public void Duplicate_foreign_and_wrong_kind_associations_are_rejected()
    {
        var builder = Builder();
        var other = Builder();
        var local = builder.AddContainer("store", "postgres", "17");
        var foreign = other.AddContainer("store", "postgres", "17");
        Assert.Throws<ArgumentException>(() => Attach(builder,
            a => a.Resource(FulfillmentInfrastructure.Store, foreign, Storage, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder,
            a => a.Resource(FulfillmentInfrastructure.Store, local, Process, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder, a => a
            .Resource(FulfillmentInfrastructure.Store, local, Storage, new("aspire/test"), [Source])
            .Resource(FulfillmentInfrastructure.Store, local, Storage, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder, a =>
            a.Resource(FulfillmentInfrastructure.Store, builder.AddContainer("too-late", "postgres", "17"), Storage, new("aspire/test"), [Source])));
        using var app = builder.Build();
        using var otherApp = other.Build();
    }

    [Fact]
    public void Completed_or_failed_authoring_cannot_mutate_associations()
    {
        var builder = Builder();
        var database = builder.AddContainer("store", "postgres", "17");
        AspireInfrastructureAssociationBuilder? saved = null;
        _ = Attach(builder, a => { saved = a; a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [Source]); });
        Assert.Throws<InvalidOperationException>(() => saved!.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [Source]));
        Assert.Throws<InvalidOperationException>(() => Attach(builder, a => { saved = a; throw new InvalidOperationException("user callback"); }));
        Assert.Throws<InvalidOperationException>(() => saved!.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [Source]));
        Assert.Throws<ArgumentException>(() => Attach(builder, a =>
        {
            a.Resource(FulfillmentInfrastructure.Store, database, Storage, new("aspire/test"), [Source]);
            builder.Resources.Remove(database.Resource);
        }));
        using var app = builder.Build();
    }
}
