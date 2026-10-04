using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Pulumi;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Tests;

public sealed class PulumiDeploymentProjectionTests
{
    static readonly InfrastructureNodeId Store = new("resources/store");
    static readonly InfrastructureCapabilityId Capability = new("test/storage");
    static readonly SourceReference Source = SourceReference.Create("test", "native-association");
    static InfrastructureTargetImplementation Implementation => new(new("test/store"), InfrastructureNodeKind.Resource,
        [new(new("test/storage/evidence"), Capability, CapabilityRealizationKind.Native, sourceReferences: [Source])]);
    static InfrastructureAuthoringResult Definition() => Infrastructure.Define(new("test/app"), new("1"), new("test/bindings"),
        infra => infra.Resource(Store).Persistent().Requires(Capability));
    static InfrastructureTargetDeploymentManifest Manifest(Action<InfrastructureTargetDeploymentManifestBuilder> configure) =>
        InfrastructureTargetDeployments.Define(new("test/deployment"), Definition().Definition,
            new("test/facilities"), new("test/profile"), new("test/pulumi"), new("test/variant"),
            [InfrastructureDefinitionDocument.CurrentSchemaVersion], configure);

    [Fact]
    public async Task Existing_native_resource_and_unmodeled_resource_remain_native()
    {
        var projection = PulumiDeploymentProjection<Resource>.Define(Manifest, p => p.Group()
            .Resource(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source])
            .UseExisting(native => native));
        var plan = InfrastructureTargetDeploymentCompiler.Compile(Definition(), projection.Manifest);
        Assert.True(plan.IsComplete);
        await Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, () =>
        {
            var existing = new TestResource("existing");
            _ = new TestResource("pulumi-only");
            var result = projection.Execute(plan, existing);
            Assert.Same(existing, result.NativeResources[Store]);
            Assert.Single(result.NativeResources);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Authoring_and_compilation_do_not_construct_native_resources()
    {
        var calls = 0;
        var projection = PulumiDeploymentProjection<object>.Define(Manifest, p => p.Group()
            .Resource(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source])
            .Create<object>((_, context) => { calls++; throw new InvalidOperationException("factory failure"); }));
        var plan = InfrastructureTargetDeploymentCompiler.Compile(Definition(), projection.Manifest);
        Assert.True(plan.IsComplete);
        Assert.Equal(0, calls);
        Assert.Equal("factory failure", Assert.Throws<InvalidOperationException>(() => projection.Execute(plan, new())).Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Missing_factory_is_rejected_at_authoring_boundary()
    {
        Assert.Throws<InvalidOperationException>(() => PulumiDeploymentProjection<object>.Define(Manifest, p => p.Group()
            .Resource(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source])));
    }

    [Fact]
    public void Inline_and_separate_authoring_produce_identical_semantics()
    {
        var inline = Manifest(d => d.ResourceUsing(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source]));
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities"), new("test/profile"),
            new("test/pulumi"), new("test/variant"), [InfrastructureDefinitionDocument.CurrentSchemaVersion],
            f => f.Resource(Implementation.Facility.Id).Provides(Implementation.Evidence[0]));
        var separate = InfrastructureTargetDeployments.Define(new("test/deployment"), Definition().Definition, facilities,
            d => d.Resource(Store, Implementation.Facility.Id, new("test/store/physical"), new("pulumi/test"), [Source]));
        Assert.Equal(separate.Fingerprint, inline.Fingerprint);
        Assert.Equal(separate.TargetFacilities.Fingerprint, inline.TargetFacilities.Fingerprint);
    }

    [Fact]
    public void Conflicting_implementation_evidence_and_wrong_node_kind_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Manifest(d =>
        {
            d.ResourceUsing(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source]);
            var conflicting = new InfrastructureTargetImplementation(Implementation.Facility.Id, InfrastructureNodeKind.Resource,
                [new(new("test/different-evidence"), Capability, CapabilityRealizationKind.Native, sourceReferences: [Source])]);
            d.ResourceUsing(new("resources/other"), conflicting, new("test/other"), new("pulumi/test"), [Source]);
        }));
        Assert.Throws<ArgumentException>(() => Manifest(d => d.WorkloadUsing(Store, Implementation, new("test/store"), [Source])));
    }

    [Fact]
    public void Completed_authoring_cannot_be_mutated_or_rebound_to_a_different_manifest()
    {
        PulumiDeploymentFactory<object>? saved = null;
        var projection = PulumiDeploymentProjection<object>.Define(Manifest, p =>
        {
            saved = p.Group().Resource(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source])
                .Create((_, _) => new object());
        });
        Assert.Throws<InvalidOperationException>(() => saved!.After(Store, "late mutation"));
        Assert.Throws<InvalidOperationException>(() => saved!.Resource(new("resources/late"), Implementation,
            new("test/late"), new("pulumi/test"), [Source]));
        var other = Manifest(d => d.ResourceUsing(Store, Implementation, new("test/other/physical"), new("pulumi/test"), [Source]));
        var plan = InfrastructureTargetDeploymentCompiler.Compile(Definition(), other);
        Assert.Throws<ArgumentException>(() => projection.Execute(plan, new()));
    }

    [Fact]
    public void A_group_cannot_gain_placements_after_its_existing_resource_is_selected()
    {
        Assert.Throws<InvalidOperationException>(() => PulumiDeploymentProjection<Resource>.Define(Manifest, p => p.Group()
            .Resource(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source])
            .UseExisting(native => native)
            .Resource(new("resources/late"), Implementation, new("test/late"), new("pulumi/test"), [Source])));
    }

    [Fact]
    public async Task Fluent_placement_preserves_manifest_and_existing_native_identity()
    {
        var implementation = InfrastructureTargetImplementation.ResourceImplementation(new("test/store"), Implementation.Evidence.ToArray());
        var fluent = PulumiDeploymentProjection<Resource>.Define(Manifest, p => p.Resource(Store)
            .Using(implementation).At(new("test/store/physical")).OwnedBy(new("pulumi/test"))
            .SourcedFrom(Source).UseExisting(native => native));
        var direct = Manifest(d => d.ResourceUsing(Store, Implementation, new("test/store/physical"), new("pulumi/test"), [Source]));
        Assert.Equal(direct.Fingerprint, fluent.Manifest.Fingerprint);
        await Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, () =>
        {
            var native = new TestResource("fluent-existing");
            var result = fluent.Execute(InfrastructureTargetDeploymentCompiler.Compile(Definition(), fluent.Manifest), native);
            Assert.Same(native, result.NativeResources[Store]);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Fluent_placement_rejects_missing_fields_wrong_kind_and_late_mutation()
    {
        Assert.Throws<InvalidOperationException>(() => PulumiDeploymentProjection<object>.Define(Manifest,
            p => p.Resource(Store).Using(Implementation).Create((_, _) => new object())));
        Assert.Throws<InvalidOperationException>(() => PulumiDeploymentProjection<object>.Define(Manifest,
            p => p.Resource(Store).Using(Implementation).At(new("test/store"))));
        Assert.Throws<ArgumentException>(() => PulumiDeploymentProjection<object>.Define(Manifest,
            p => p.Workload(Store).Using(Implementation)));
        PulumiDeploymentPlacement<object>? saved = null;
        _ = PulumiDeploymentProjection<object>.Define(Manifest, p =>
        {
            saved = p.Resource(Store).Using(Implementation).At(new("test/store/physical"))
                .OwnedBy(new("pulumi/test")).SourcedFrom(Source);
            saved.Create((_, _) => new object());
        });
        Assert.Throws<InvalidOperationException>(() => saved!.At(new("test/late")));
        Assert.Throws<InvalidOperationException>(() => saved!.Create((_, _) => new object()));
        Assert.Throws<ArgumentException>(() => InfrastructureTargetImplementation.ResourceImplementation(new("test/empty")));
    }

    [Fact]
    public void Fluent_workload_preserves_manifest_and_does_not_construct_during_authoring()
    {
        var node = new InfrastructureNodeId("workloads/worker");
        var definition = Infrastructure.Define(new("test/worker"), new("1"), new("test/bindings"),
            d => d.Workload(node).Requires(Capability));
        var implementation = InfrastructureTargetImplementation.WorkloadImplementation(new("test/worker"), Implementation.Evidence.ToArray());
        InfrastructureTargetDeploymentManifest Define(Action<InfrastructureTargetDeploymentManifestBuilder> configure) =>
            InfrastructureTargetDeployments.Define(new("test/deployment"), definition.Definition,
                new("test/facilities"), new("test/profile"), new("test/pulumi"), new("test/variant"),
                [InfrastructureDefinitionDocument.CurrentSchemaVersion], configure);
        var calls = 0;
        var fluent = PulumiDeploymentProjection<object>.Define(Define, p => p.Workload(node)
            .Using(implementation).At(new("test/worker/physical")).SourcedFrom(Source)
            .Create((_, _) => { calls++; return new object(); }));
        var direct = Define(d => d.WorkloadUsing(node, implementation, new("test/worker/physical"), [Source]));
        Assert.Equal(direct.Fingerprint, fluent.Manifest.Fingerprint);
        Assert.True(InfrastructureTargetDeploymentCompiler.Compile(definition, fluent.Manifest).IsComplete);
        Assert.Equal(0, calls);
        Assert.Throws<InvalidOperationException>(() => PulumiDeploymentProjection<object>.Define(Define,
            p => p.Workload(node).OwnedBy(new("test/owner"))));
    }

    sealed class TestResource(string name) : CustomResource("test:index:Resource", name, new Args(), null, null);
    sealed class Args : ResourceArgs;
    sealed class Mocks : IMocks
    {
        public Task<object> CallAsync(MockCallArgs args) => Task.FromResult<object>(args.Args);
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args) =>
            Task.FromResult<(string?, object)>((args.Name, args.Inputs));
    }
}
