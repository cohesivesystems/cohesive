using Cohesive.Infra;
using Cohesive.Infra.Realization;
using Cohesive.Model;
using Pulumi;
using Pulumi.Testing;

namespace Cohesive.Adapters.Pulumi.Tests;

public sealed class PulumiGraphProjectionTests
{
    static readonly InfrastructureNodeId Worker = new("workloads/worker");
    static readonly InfrastructureNodeId Store = new("resources/store");
    static readonly InfrastructureNodeId Identity = new("resources/identity");

    [Fact]
    public async Task Traversal_constructs_dependencies_once_and_retains_native_values_and_secret_outputs()
    {
        var order = new List<string>();
        await Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, async () =>
        {
            var projection = new PulumiGraphProjection(Plan());
            projection.Map(Worker, context =>
            {
                order.Add("worker");
                var store = context.Get<TestResource>(Store);
                var worker = new TestResource("worker", store.Secret, new() { DependsOn = { store }, Protect = true });
                context.Associate(Worker, worker);
                return worker;
            });
            projection.Map(Store, context =>
            {
                order.Add("store");
                Assert.Equal("external tenant", context.Get<string>(Identity));
                var store = new TestResource("store", Output.CreateSecret("test-secret"));
                context.Associate(Store, store);
                return store;
            });
            projection.Map(Identity, context =>
            {
                order.Add("identity");
                context.Reference(Identity, "Existing independently owned tenant.");
                return "external tenant";
            });
            var result = projection.Execute();
            Assert.Same(result.Get<TestResource>(Worker), result.NativeResources[Worker]);
            Assert.Equal(2, result.NativeResources.Count);
            Assert.True(await Output.IsSecretAsync(result.Get<TestResource>(Worker).Secret));
            Assert.Single(result.ExternalReferences);
            Assert.Throws<InvalidOperationException>(() => projection.Execute());
            result.Get<TestResource>(Worker).Secret.Apply(value => { Assert.Equal("test-secret", value); return value; });
        });
        Assert.Equal(new[] { "identity", "store", "worker" }, order);
    }

    [Fact]
    public void Missing_handler_fails_before_any_factory_runs()
    {
        var calls = 0;
        var projection = new PulumiGraphProjection(Plan());
        projection.Map(Worker, _ => ++calls);
        var error = Assert.Throws<InvalidOperationException>(() => projection.Execute());
        Assert.Contains(Store.Value, error.Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Duplicate_unknown_and_nonparticipating_handlers_are_rejected()
    {
        var projection = new PulumiGraphProjection(Plan(nonparticipating: true));
        Assert.Throws<ArgumentException>(() => projection.Map(Worker, _ => 1));
        Assert.Throws<ArgumentException>(() => projection.Map(new InfrastructureNodeId("unknown"), _ => 1));
        projection.Map(Store, _ => 1);
        Assert.Throws<ArgumentException>(() => projection.Map(Store, _ => 2));
        Assert.Throws<ArgumentException>(() => projection.Map([Identity, Identity], _ => 1));
    }

    [Fact]
    public void Native_ordering_cycle_fails_before_registration()
    {
        var calls = 0;
        var projection = new PulumiGraphProjection(Plan());
        projection.Map(Worker, _ => ++calls);
        projection.Map(Store, _ => ++calls);
        projection.Map(Identity, _ => ++calls).After(Worker, "Deliberately invalid provider ordering.");
        Assert.Contains("Cyclic", Assert.Throws<InvalidOperationException>(() => projection.Execute()).Message);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void Missing_association_cannot_be_hidden_by_a_successful_factory()
    {
        var projection = new PulumiGraphProjection(Plan());
        projection.Map(new[] { Identity, Store, Worker }, _ => new object());
        Assert.Contains("omitted native association", Assert.Throws<InvalidOperationException>(() => projection.Execute()).Message);
    }

    [Fact]
    public void Persistent_declarations_cannot_be_excused_as_external()
    {
        var projection = new PulumiGraphProjection(Plan());
        projection.Map(new[] { Identity, Store, Worker }, context =>
        {
            context.Reference(Store, "Invalid escape.");
            return new object();
        });
        Assert.Contains("not an external", Assert.Throws<InvalidOperationException>(() => projection.Execute()).Message);
    }

    [Fact]
    public void Undeclared_reads_fail_instead_of_using_incidental_registration_order()
    {
        var projection = new PulumiGraphProjection(Plan());
        projection.Map(Identity, context => context.Get<object>(Worker));
        projection.Map(Store, _ => 1);
        projection.Map(Worker, _ => 1);
        Assert.Contains("Undeclared", Assert.Throws<InvalidOperationException>(() => projection.Execute()).Message);
    }

    [Fact]
    public void Factory_failure_is_not_retried()
    {
        var calls = 0;
        var projection = new PulumiGraphProjection(Plan());
        projection.Map<int>(new[] { Identity, Store, Worker }, _ => { calls++; throw new IOException("provider construction failed"); });
        Assert.Throws<IOException>(() => projection.Execute());
        Assert.Throws<InvalidOperationException>(() => projection.Execute());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Grouped_facility_runs_once_and_excluded_workloads_need_no_factory()
    {
        var calls = 0;
        await Deployment.TestAsync(new Mocks(), new TestOptions { IsPreview = false }, () =>
        {
            var projection = new PulumiGraphProjection(Plan(nonparticipating: true));
            PulumiProjectionContext? retained = null;
            projection.Map(new[] { Identity, Store }, context =>
            {
                retained = context;
                calls++;
                context.Reference(Identity, "Existing tenant.");
                var store = new TestResource("grouped", "value");
                context.Associate(Store, store);
                Assert.Throws<InvalidOperationException>(() => context.Associate(Store, store));
                return store;
            });
            var result = projection.Execute();
            Assert.Same(result.Get<TestResource>(Store), result.Get<TestResource>(Identity));
            Assert.Throws<InvalidOperationException>(() => result.Get<string>(Store));
            Assert.Throws<InvalidOperationException>(() => retained!.Reference(Identity, "Callback has ended."));
            Assert.Throws<InvalidOperationException>(() => projection.Map(Worker, _ => 1));
            return Task.CompletedTask;
        });
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Failed_factory_cannot_retain_a_live_context()
    {
        PulumiProjectionContext? retained = null;
        var projection = new PulumiGraphProjection(Plan());
        projection.Map<int>(new[] { Identity, Store, Worker }, context =>
        {
            retained = context;
            throw new IOException("failure");
        });
        Assert.Throws<IOException>(() => projection.Execute());
        Assert.Throws<InvalidOperationException>(() => retained!.Reference(Identity, "Must be closed."));
    }

    static InfrastructureTargetDeploymentPlan Plan(bool nonparticipating = false)
    {
        InfrastructureCapabilityId capability = new("test/capability");
        InfrastructureCapabilityId execution = new("test/execution");
        var source = SourceReference.Create("test", "projection");
        var semantic = Infrastructure.Define(new("test/projection"), new("1"), new("test/bindings"), infra =>
        {
            var identity = infra.Resource(Identity).External().Requires(capability);
            var store = infra.Resource(Store).Persistent().Requires(capability).RequiresReady(identity);
            var worker = infra.Workload(Worker).Requires(execution);
            var contract = infra.Contract(new("test/read"), new("test/rule")).Requires(capability).SourcedFrom(source.Value);
            infra.Bind(worker).To(store).As(contract);
        });
        var facilities = InfrastructureTargetFacilities.Define(new("test/facilities"), new("test/profile"),
            new("test/pulumi"), new("test/variant"), [InfrastructureDefinitionDocument.CurrentSchemaVersion], f =>
            {
                f.Resource(new("test/resource")).Provides(new(new("test/resource/evidence"), capability, CapabilityRealizationKind.Native, sourceReferences: [source]));
                f.Workload(new("test/workload")).Provides(new(new("test/workload/evidence"), execution, CapabilityRealizationKind.Native, sourceReferences: [source]));
            });
        var manifest = InfrastructureTargetDeployments.Define(new("test/deployment"), semantic.Definition, facilities, d =>
        {
            d.Resource(Identity, new("test/resource"), new("test/tenant"), new("external/tenant"), [source]);
            d.Resource(Store, new("test/resource"), new("test/store"), new("pulumi/test"), [source]);
            if (nonparticipating) d.NonParticipatingWorkload(Worker, "Excluded target workload.", [source.Value]);
            else d.Workload(Worker, new("test/workload"), new("test/worker"), [source]);
        });
        var plan = InfrastructureTargetDeploymentCompiler.Compile(semantic, manifest);
        Assert.True(plan.IsComplete, string.Join(";", plan.Diagnostics.Select(d => d.Message)));
        return plan;
    }

    sealed class TestArgs : ResourceArgs
    {
        [Input("secret")] public Input<string> Secret { get; set; } = null!;
    }
    sealed class TestResource : CustomResource
    {
        public TestResource(string name, Input<string> secret, CustomResourceOptions? options = null)
            : base("test:index:Resource", name, new TestArgs { Secret = secret }, options) { }
        [Output("secret")] public Output<string> Secret { get; private set; } = null!;
    }
    sealed class Mocks : IMocks
    {
        public Task<object> CallAsync(MockCallArgs args) => Task.FromResult<object>(args.Args);
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            var state = args.Inputs.ToDictionary();
            if (state.TryGetValue("secret", out var value)) state["secret"] = Output.CreateSecret(value);
            return Task.FromResult<(string?, object)>((args.Name, state));
        }
    }
}
