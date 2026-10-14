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
    static readonly SourceReference Source = SourceReference.Create("test", "aspire-first");
    static InfrastructureTargetImplementation Storage => ResourceImplementation(new("test/storage"),
        OrdersApp.StorageQueries.Requirements.Select((capability, index) => new InfrastructureCapabilityEvidence(
            new($"test/storage/{index}"), capability, CapabilityRealizationKind.Native, sourceReferences: [Source])).ToArray());
    static InfrastructureTargetImplementation Process => WorkloadImplementation(new("test/process"),
        [new(new("test/process/evidence"), OrdersApp.ApplicationExecution, CapabilityRealizationKind.Native, sourceReferences: [Source])]);
    static IDistributedApplicationBuilder Builder() => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        Args = [], DisableDashboard = true,
        AssemblyName = typeof(AspireInfrastructureAssociationTests).Assembly.GetName().Name,
        ProjectDirectory = AppContext.BaseDirectory
    });
    static AspireInfrastructureAssociation Attach(IDistributedApplicationBuilder builder,
        Action<AspireInfrastructureAssociationBuilder> configure) => AspireInfrastructureAssociation.Attach(
            builder, OrdersApp.Definition, new("test/association"), new("development"), configure);

    [Fact]
    public void Prepared_evidence_checks_actual_database_name_before_association()
    {
        var builder = Builder();
        var server = builder.AddPostgres("postgres");
        var wrong = server.AddDatabase("orders-label", "inventory");
        Assert.Throws<ArgumentException>(() => Attach(builder, associations =>
            associations.Resource(OrdersApp.Store, wrong, OrdersApp.StorageQueries, new("test/authority"))));
        var correct = server.AddDatabase("different-label", "orders");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        Assert.True(Attach(builder, associations => associations
            .Resource(OrdersApp.Store, correct, OrdersApp.StorageQueries, new("test/authority"))
            .Workload(OrdersApp.Worker, worker, Process)).Deployment.IsComplete);
        using var app = builder.Build();
    }

    [Fact]
    public void Project_inference_requires_native_metadata_and_does_not_claim_durable_execution()
    {
        var builder = Builder();
        var configured = OrdersApp.Configure(builder);
        var project = builder.CreateResourceBuilder((ProjectResource)configured.Resources[OrdersApp.Worker]);
        var definition = Infrastructure.Define(new("test/project"), new("1"), new("test/project/bindings"), infra =>
            infra.Workload(OrdersApp.Worker).Requires(AspireInfrastructure.DotNetProjectExecution)
                .Requires(new InfrastructureCapabilityId("test/durable-execution")));
        var inferred = AspireInfrastructureAssociation.Attach(builder, definition, new("test/project/native"), new("test"),
            a => a.Workload(OrdersApp.Worker, project, sources: [Source]));
        Assert.False(inferred.Deployment.IsComplete);
        Assert.Contains(inferred.Deployment.Diagnostics, item => item.Code == InfrastructureCapabilityDiagnosticCodes.RequirementUnavailable
            && item.SchemaLocation == "test/durable-execution");
        // Facility selection is atomic: an unsupported extra requirement rejects the entire selection.
        // Prove the inferred execution capability independently instead of treating incompleteness as evidence.
        var executionOnly = Infrastructure.Define(new("test/execution"), new("1"), new("test/execution/bindings"),
            infra => infra.Workload(OrdersApp.Worker).Requires(AspireInfrastructure.DotNetProjectExecution));
        var supported = AspireInfrastructureAssociation.Attach(builder, executionOnly, new("test/execution/native"), new("test"),
            a => a.Workload(OrdersApp.Worker, project, sources: [Source]));
        Assert.True(supported.Deployment.IsComplete);
        var execution = Assert.Single(supported.Deployment.FacilityPlan.CapabilityClosure.Decisions);
        Assert.Equal(AspireInfrastructure.DotNetProjectExecution, execution.Capability);
        Assert.True(execution.IsAdmissible);
        Assert.Equal(CapabilityRealizationKind.Native, execution.Realization);
        var metadata = project.Resource.GetProjectMetadata();
        project.Resource.Annotations.Remove(metadata);
        Assert.Throws<InvalidOperationException>(() => AspireInfrastructureAssociation.Attach(builder, definition,
            new("test/project/missing-metadata"), new("test"), a => a.Workload(OrdersApp.Worker, project)));
        project.Resource.Annotations.Add(metadata);
        using var app = builder.Build();
    }

    [Fact]
    public void Omitted_selection_sources_are_deterministic_and_match_explicit_convention()
    {
        var builder = Builder();
        var database = builder.AddContainer("storage", "postgres", "17");
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        var inferred = Attach(builder, a => a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"))
            .Workload(OrdersApp.Worker, worker, Process));
        var explicitSources = Attach(builder, a => a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"),
                [SourceReference.Create("aspire-native-association-v1", "test/association/resources/orders/storage")])
            .Workload(OrdersApp.Worker, worker, Process,
                [SourceReference.Create("aspire-native-association-v1", "test/association/workloads/orders/worker")]));
        Assert.True(inferred.Deployment.IsComplete);
        Assert.Equal(explicitSources.Deployment.Manifest.Fingerprint, inferred.Deployment.Manifest.Fingerprint);
        Assert.Throws<ArgumentException>(() => Attach(builder,
            a => a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [])));
        using var app = builder.Build();
    }

    [Fact]
    public void Runnable_example_uses_generated_project_metadata_and_existing_native_database()
    {
        var builder = Builder();
        var association = OrdersApp.Configure(builder);
        Assert.True(association.Deployment.IsComplete,
            string.Join(Environment.NewLine, association.Deployment.Diagnostics.Select(d => d.Message)));
        Assert.Equal(2, association.Resources.Count);
        Assert.Same(builder.Resources.Single(r => r.Name == "orders"), association.Resources[OrdersApp.Store]);
        Assert.IsType<ProjectResource>(association.Resources[OrdersApp.Worker]);
        Assert.Same(builder.Resources.Single(r => r.Name == "worker"), association.Resources[OrdersApp.Worker]);
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
        var association = Attach(builder, a => a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [Source])
            .Workload(OrdersApp.Worker, worker, Process, [Source]));
        Assert.True(association.Deployment.IsComplete);
        Assert.Equal(before, builder.Resources);
        for (var i = 0; i < before.Length; i++) Assert.Equal(annotations[i], before[i].Annotations);
        Assert.Same(database.Resource, association.Resources[OrdersApp.Store]);
        var direct = InfrastructureTargetDeployments.Define(new("test/association"), OrdersApp.Definition.Definition,
            new("test/association/facilities"), new("test/association/profile"), AspireInfrastructureAssociation.Target,
            new("development"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], d => d
                .ResourceUsing(OrdersApp.Store, Storage, new("aspire/resource/storage"), new("aspire/test"), [Source])
                .WorkloadUsing(OrdersApp.Worker, Process, new("aspire/resource/worker"), [Source]));
        Assert.Equal(direct.Fingerprint, association.Deployment.Manifest.Fingerprint);
        using var app = builder.Build();
    }

    [Fact]
    public void Missing_association_remains_a_canonical_coverage_diagnostic()
    {
        var builder = Builder();
        var worker = builder.AddExecutable("worker", "dotnet", ".", "--info");
        var association = Attach(builder, a => a.Workload(OrdersApp.Worker, worker, Process, [Source]));
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
        var association = Attach(builder, a => a.Resource(OrdersApp.Store, database, incompatible, new("aspire/test"), [Source])
            .Workload(OrdersApp.Worker, worker, Process, [Source]));
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
            a => a.Resource(OrdersApp.Store, foreign, Storage, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder,
            a => a.Resource(OrdersApp.Store, local, Process, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder, a => a
            .Resource(OrdersApp.Store, local, Storage, new("aspire/test"), [Source])
            .Resource(OrdersApp.Store, local, Storage, new("aspire/test"), [Source])));
        Assert.Throws<ArgumentException>(() => Attach(builder, a =>
            a.Resource(OrdersApp.Store, builder.AddContainer("too-late", "postgres", "17"), Storage, new("aspire/test"), [Source])));
        using var app = builder.Build();
        using var otherApp = other.Build();
    }

    [Fact]
    public void Completed_or_failed_authoring_cannot_mutate_associations()
    {
        var builder = Builder();
        var database = builder.AddContainer("store", "postgres", "17");
        AspireInfrastructureAssociationBuilder? saved = null;
        _ = Attach(builder, a => { saved = a; a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [Source]); });
        Assert.Throws<InvalidOperationException>(() => saved!.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [Source]));
        Assert.Throws<InvalidOperationException>(() => Attach(builder, a => { saved = a; throw new InvalidOperationException("user callback"); }));
        Assert.Throws<InvalidOperationException>(() => saved!.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [Source]));
        Assert.Throws<ArgumentException>(() => Attach(builder, a =>
        {
            a.Resource(OrdersApp.Store, database, Storage, new("aspire/test"), [Source]);
            builder.Resources.Remove(database.Resource);
        }));
        using var app = builder.Build();
    }
}
