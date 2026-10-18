using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Relations.Authoring;

namespace Cohesive.Tests;

public sealed class GeneratedProcessDependencyTests
{
    public sealed record Item(string Id, string Partition);
    internal static readonly DomainEntity<Item> Entity = new DomainModelBuilder().Entity<Item>("dependency/item");
    internal static Transition<Item, string, string> Transition(string id = "dependency/change") =>
        Entity.Transition<string, string>(new(id), new("1"), builder => builder.Return("changed"));
    internal static EntityPointRead<Item, string, string> Read() => Entity.PointRead(
        id: new("dependency/read"), revision: new("1"), result: item => item?.Id ?? "missing");

    [Fact]
    public void Direct_point_read_retains_call_site_policy_in_its_fingerprint()
    {
        var plain = new DirectPointReadProcess(Entity);
        var controlled = new PolicyPointReadProcess(Entity);
        var node = Assert.Single(controlled.Definition.Definition.Nodes.OfType<EvaluateRelationProcessNode>());
        Assert.Equal(3, node.Policy!.MaxAttempts);
        Assert.Equal(ProcessReadProgress.Durable, node.Policy.Progress);
        Assert.Equal(TimeSpan.FromSeconds(2), node.Policy.Timeout);
        Assert.Equal("storage.busy", Assert.Single(node.Policy.RetryableFailureCodes));
        Assert.Equal(Entity.PointRead().Reference, node.Relation);
        Assert.NotEqual(plain.Definition.Reference.Fingerprint, controlled.Definition.Reference.Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_read_binds_the_repository_and_encodes_presence_or_absence(bool present)
    {
        var snapshots = present ? new[] { new Cohesive.Storage.EntitySnapshot(
            Entity.Definition.CreateState("sku", new Item("sku", "local")).Snapshot, "local", new("seed")) } : [];
        var repository = new Cohesive.Storage.InMemoryEntityOutboxRepository(Entity.Definition,
            Cohesive.Storage.EntityPartitionKeyPolicy.Fixed("local", nameof(Item.Partition)), snapshots);
        var process = new DirectPointReadProcess(Entity);
        var prepared = Service.Define(new("read-service"), new("1"), RunControlFixture.Provenance)
            .Use(new ServiceDependencies().Process(process)).Operation("read").Run(process.Definition)
            .ExecuteEphemerally(TimeSpan.FromSeconds(1)).Bind(process.Definition, _ => repository).Prepare();
        var read = Entity.PointRead();
        var evaluation = ExecutionKernel.ProcessRelationHandlerCatalogTests.Evaluation(read.Reference, read.Query.InputContract, "sku");
        var result = await prepared.Host.EvaluateRelationAsync(OperationContext.Create(), evaluation);
        Assert.True(result.IsSuccessful, result.Failure?.Message);
        var observed = result.Value!.Value!.Value.GetProperty("Entity");
        Assert.Equal(!present, observed.Kind == ObservationValueKind.Null);
        if (present) Assert.Equal("sku", observed.GetProperty(nameof(Item.Id)).GetRequiredString());
    }

    public sealed record RevisedItem(string Id, string Partition, int Count);

    [Fact]
    public void Entity_shape_changes_derive_a_new_read_revision_without_changing_logical_identity()
    {
        var revised = new DomainModelBuilder().Entity<RevisedItem>(Entity.Definition.Name.Value).PointRead();
        Assert.Equal(Entity.PointRead().Reference.DefinitionId, revised.Reference.DefinitionId);
        Assert.NotEqual(Entity.PointRead().Reference.RevisionId, revised.Reference.RevisionId);
    }

    [Fact]
    public void Derived_read_warm_preparation_does_not_allocate()
    {
        _ = MeasureDerivedRead();
        Assert.Equal(0, MeasureDerivedRead());
    }

    static long MeasureDerivedRead()
    {
        var expected = Entity.PointRead();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++)
            if (!ReferenceEquals(expected, Entity.PointRead())) throw new InvalidOperationException();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Direct_point_read_derives_and_reuses_the_entity_contract()
    {
        var read = Entity.PointRead();
        Assert.Same(read, Entity.PointRead());
        Assert.Null(read.Result(null).Entity);
        var item = new Item("sku", "local");
        Assert.Same(item, read.Result(item).Entity);
        var process = new DirectPointReadProcess(Entity);
        Assert.Single(process.Dependencies);
        Assert.Equal(read.Reference, Assert.Single(process.Definition.Definition.Nodes.OfType<EvaluateRelationProcessNode>()).Relation);
        _ = new ServiceDependencies().Process(process);
    }

    [Fact]
    public void Entity_point_read_preserves_identity_absence_and_revision()
    {
        var read = Read();
        Assert.Same(Entity, read.Entity);
        Assert.Equal("item-1", read.Key("item-1"));
        Assert.Equal("missing", read.Result(null));
        Assert.Equal("item-1", read.Result(new Item("item-1", "local")));
        Assert.Equal(Read().Reference, read.Reference);
        var revised = Entity.PointRead(new("dependency/read"), new("2"), item => item?.Id ?? "missing");
        Assert.NotEqual(read.Reference, revised.Reference);
        Assert.Equal("2", revised.Query.Implementation.Version);
    }

    [Fact]
    public void Entity_point_read_rejects_missing_projection_at_setup() =>
        Assert.Throws<ArgumentNullException>(() => Entity.PointRead<Item, string>(
            new("dependency/read"), new("1"), null!));

    [Fact]
    public void Generated_attachments_follow_actual_typed_invocations_and_retain_authorities()
    {
        var process = new GeneratedDependencyProcess(Transition(), Read());
        var nodes = process.Definition.Definition.Nodes;
        Assert.Equal(2, nodes.OfType<InvokeTransitionProcessNode>().Count());
        Assert.Single(nodes.OfType<EvaluateRelationProcessNode>());
        Assert.Equal(2, process.Dependencies.Length);
        var receiver = new Receiver();
        foreach (var dependency in process.Dependencies) dependency.Contribute(receiver);
        Assert.Same(process.Change, receiver.TransitionAuthority);
        Assert.Same(process.Read, receiver.ReadAuthority);
        Assert.Empty(new ServiceDependencies().Process(process).Queries);
    }

    [Fact]
    public void Changed_attachment_cannot_bind_a_previously_prepared_graph()
    {
        var process = new GeneratedDependencyProcess(Transition(), Read());
        _ = process.Definition;
        process.Change = Transition("dependency/different");
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceDependencies().Process(process));
        Assert.Equal(ServiceBindingDiagnosticCodes.GeneratedDependencyMismatch, Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Generated_attachments_are_prepared_once_without_warm_allocation()
    {
        var process = new GeneratedDependencyProcess(Transition(), Read());
        _ = MeasureWarmReads(process);
        Assert.Equal(0L, MeasureWarmReads(process));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static long MeasureWarmReads(GeneratedDependencyProcess process)
    {
        var expected = process.Dependencies;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10_000; i++)
            if (process.Dependencies != expected) throw new InvalidOperationException();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Generated_and_explicit_registrations_cannot_silently_overwrite_each_other()
    {
        var process = new GeneratedDependencyProcess(Transition(), Read());
        Assert.Throws<ArgumentException>(() => new ServiceDependencies().Transition(process.Change).Process(process));
    }

    [Fact]
    public void Raw_reference_requires_an_explicit_binding_and_fails_before_repository_io()
    {
        var query = Read().Query;
        var process = new RawDependencyProcess(query.Reference);
        Assert.Empty(process.Dependencies);
        var error = Assert.Throws<ServiceBindingValidationException>(() => Bind(new ServiceDependencies().Process(process), process));
        Assert.Equal(ServiceBindingDiagnosticCodes.QueryMissing, Assert.Single(error.Validation.Diagnostics).Code);
        _ = new ServiceDependencies().Lookup(query, Entity, input => input, item => item?.Id ?? "missing").Process(process);
    }

    [Fact]
    public void Statement_transitions_attach_their_exact_authority()
    {
        var transition = Transition();
        var process = new StatementTransitionProcess(transition, transition);
        Assert.Equal(2, process.Definition.Definition.Nodes.OfType<InvokeTransitionProcessNode>().Count());
        Assert.All(process.Dependencies, dependency => Assert.Equal(transition.Reference, dependency.Reference));
        Assert.Equal(2, process.Dependencies.Length);
        _ = new ServiceDependencies().Process(process);
    }

    [Fact]
    public void Competing_transition_authorities_for_one_exact_reference_are_rejected()
    {
        var first = Transition();
        var second = Transition();
        Assert.Equal(first.Reference, second.Reference);
        var error = Assert.Throws<ServiceBindingValidationException>(() =>
            new ServiceDependencies().Process(new StatementTransitionProcess(first, second)));
        Assert.Equal(ServiceBindingDiagnosticCodes.GeneratedDependencyAmbiguous, Assert.Single(error.Validation.Diagnostics).Code);
    }

    [Fact]
    public void Missing_raw_transition_is_rejected_at_binding()
    {
        var transition = Transition();
        var process = new RawTransitionProcess(transition.Reference);
        var error = Assert.Throws<ServiceBindingValidationException>(() => Bind(new ServiceDependencies().Process(process), process));
        Assert.Equal(ServiceBindingDiagnosticCodes.TransitionMissing, Assert.Single(error.Validation.Diagnostics).Code);
        _ = new ServiceDependencies().Transition(transition).Process(process);
    }

    [Fact]
    public void Repeated_aliases_to_one_query_recipe_are_idempotent()
    {
        var read = Read();
        var process = new TwoReadProcess(read, read);
        _ = new ServiceDependencies().Process(process);
    }

    [Fact]
    public void Competing_recipes_for_one_exact_query_are_rejected()
    {
        var first = Read();
        var second = new EntityPointRead<Item, string, string>(first.Query, Entity, input => input, _ => "different");
        var process = new TwoReadProcess(first, second);
        var error = Assert.Throws<ServiceBindingValidationException>(() => new ServiceDependencies().Process(process));
        Assert.Equal(ServiceBindingDiagnosticCodes.GeneratedDependencyAmbiguous, Assert.Single(error.Validation.Diagnostics).Code);
    }

    static void Bind(ServiceDependencies dependencies, ProcessModel<string, string> process)
    {
        var service = Service.Define(new("dependency/service"), new("1"), RunControlFixture.Provenance)
            .Use(dependencies).Operation("run").Run(process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(1));
        _ = service.Bind(process.Definition, _ => throw new RepositoryReachedException());
    }

    sealed class RepositoryReachedException : Exception;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_lookup_registration_is_order_independent(bool processFirst)
    {
        var read = Read();
        var process = new RawDependencyProcess(read.Reference);
        var dependencies = new ServiceDependencies();
        if (processFirst) dependencies = dependencies.Process(process);
        dependencies = dependencies.Lookup(read.Query, Entity, read.Key, read.Result);
        if (!processFirst) dependencies = dependencies.Process(process);
        Assert.Throws<RepositoryReachedException>(() => Bind(dependencies, process));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Another_process_can_supply_a_raw_reference_in_either_order(bool rawFirst)
    {
        var transition = Transition();
        var raw = new RawTransitionProcess(transition.Reference);
        var typed = new StatementTransitionProcess(transition, transition);
        var dependencies = rawFirst
            ? new ServiceDependencies().Process(raw).Process(typed)
            : new ServiceDependencies().Process(typed).Process(raw);
        Assert.Throws<RepositoryReachedException>(() => Bind(dependencies, raw));
    }

    [Fact]
    public void Binding_reports_every_missing_exact_reference_before_repository_io()
    {
        var transition = Transition();
        var read = Read();
        var process = new RawMixedProcess(transition.Reference, read.Reference);
        var error = Assert.Throws<ServiceBindingValidationException>(() => Bind(new ServiceDependencies().Process(process), process));
        Assert.Equal(2, error.Validation.Diagnostics.Length);
        foreach (var reference in new[] { transition.Reference, read.Reference })
        {
            var diagnostic = Assert.Single(error.Validation.Diagnostics.Where(d => d.Message.Contains(reference.DefinitionId.Value)));
            Assert.Contains(reference.RevisionId.Value, diagnostic.Message);
            Assert.Contains(reference.Fingerprint.Value, diagnostic.Message);
            Assert.Contains(Uri.EscapeDataString(reference.DefinitionId.Value), diagnostic.Location);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Host_attaches_transitions_in_canonical_reference_order(bool reverseRegistration)
    {
        var domain = new DomainModelBuilder();
        var a = domain.Entity<Item>("ordered/a");
        var z = domain.Entity<Item>("ordered/z");
        var first = a.Transition<string, string>(new("ordered/a"), new("1"), builder => builder.Return("a"));
        var last = z.Transition<string, string>(new("ordered/z"), new("1"), builder => builder.Return("z"));
        var process = new StatementTransitionProcess(last, first);
        var dependencies = reverseRegistration
            ? new ServiceDependencies().Transition(last).Transition(first)
            : new ServiceDependencies().Transition(first).Transition(last);
        var service = Service.Define(new("ordered/service"), new("1"), RunControlFixture.Provenance)
            .Use(dependencies).Operation("run").Run(process.Definition).ExecuteEphemerally(TimeSpan.FromSeconds(1));
        var attached = new List<Cohesive.Model.ValueContract>();
        _ = service.Bind(process.Definition, contract =>
        {
            attached.Add(contract);
            var entity = contract == first.Definition.Observation ? a : z;
            return new Cohesive.Storage.InMemoryEntityOutboxRepository(entity.Definition,
                Cohesive.Storage.EntityPartitionKeyPolicy.FromField(nameof(Item.Partition)));
        });
        Assert.Equal(new[] { first.Definition.Observation, last.Definition.Observation }, attached);
    }

    sealed class Receiver : IProcessDependencyReceiver
    {
        public object? TransitionAuthority { get; private set; }
        public object? ReadAuthority { get; private set; }
        public void Transition<TEntity, TInput, TResult>(Transition<TEntity, TInput, TResult> transition) where TEntity : notnull => TransitionAuthority = transition;
        public void PointRead<TEntity, TInput, TResult>(EntityPointRead<TEntity, TInput, TResult> read)
            where TEntity : class where TInput : notnull where TResult : notnull => ReadAuthority = read;
    }
}

[Process(id: "dependency/process", revision: "1")]
public sealed partial class GeneratedDependencyProcess : ProcessModel<string, string>
{
    public Transition<GeneratedProcessDependencyTests.Item, string, string> Change { get; set; }
    public EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> Read { get; }
    public GeneratedDependencyProcess(Transition<GeneratedProcessDependencyTests.Item, string, string> change,
        EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> read) { Change = change; Read = read; }
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        var observed = await process.Query(Read, input);
        var first = await process.Transition(Change, input, observed);
        var second = await process.Transition(Change, input, first);
        return second;
    }
}

[Process(id: "dependency/raw", revision: "1")]
public sealed partial class RawDependencyProcess : ProcessModel<string, string>
{
    readonly ExecutionDefinitionReference query;
    public RawDependencyProcess(ExecutionDefinitionReference query) => this.query = query;
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        var result = await process.Query<string>(query, input);
        return result;
    }
}

[Process(id: "dependency/two-reads", revision: "1")]
public sealed partial class TwoReadProcess : ProcessModel<string, string>
{
    readonly EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> first;
    readonly EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> second;
    public TwoReadProcess(EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> first,
        EntityPointRead<GeneratedProcessDependencyTests.Item, string, string> second) { this.first = first; this.second = second; }
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        var observed = await process.Query(first, input);
        var result = await process.Query(second, observed);
        return result;
    }
}

[Process(id: "dependency/statements", revision: "1")]
public sealed partial class StatementTransitionProcess : ProcessModel<string, string>
{
    readonly Transition<GeneratedProcessDependencyTests.Item, string, string> first;
    readonly Transition<GeneratedProcessDependencyTests.Item, string, string> second;
    public StatementTransitionProcess(Transition<GeneratedProcessDependencyTests.Item, string, string> first,
        Transition<GeneratedProcessDependencyTests.Item, string, string> second) { this.first = first; this.second = second; }
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        await process.Transition(first, input, input);
        await process.Transition(second, input, input);
        return input;
    }
}

[Process(id: "dependency/raw-transition", revision: "1")]
public sealed partial class RawTransitionProcess : ProcessModel<string, string>
{
    readonly ExecutionDefinitionReference transition;
    public RawTransitionProcess(ExecutionDefinitionReference transition) => this.transition = transition;
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        await process.Transition(transition, input, input);
        return input;
    }
}

[Process(id: "dependency/raw-mixed", revision: "1")]
public sealed partial class RawMixedProcess : ProcessModel<string, string>
{
    readonly ExecutionDefinitionReference transition;
    readonly ExecutionDefinitionReference query;
    public RawMixedProcess(ExecutionDefinitionReference transition, ExecutionDefinitionReference query)
    { this.transition = transition; this.query = query; }
    protected override async ProcessTask<string> Execute(ProcessContext process, string input)
    {
        await process.Transition(transition, input, input);
        await process.Transition(transition, input, input);
        var result = await process.Query<string>(query, input);
        return result;
    }
}

[Process(id: "dependency/direct-read", revision: "1")]
internal sealed partial class DirectPointReadProcess : ProcessModel<string, bool>
{
    readonly DomainEntity<GeneratedProcessDependencyTests.Item> entity;
    public DirectPointReadProcess(DomainEntity<GeneratedProcessDependencyTests.Item> entity) => this.entity = entity;
    protected override async ProcessTask<bool> Execute(ProcessContext process, string input)
    {
        var found = await process.PointRead(entity, input);
        return found.Entity != null;
    }
}

[Process(id: "dependency/direct-read", revision: "1")]
internal sealed partial class PolicyPointReadProcess : ProcessModel<string, bool>
{
    readonly DomainEntity<GeneratedProcessDependencyTests.Item> entity;
    public PolicyPointReadProcess(DomainEntity<GeneratedProcessDependencyTests.Item> entity) => this.entity = entity;
    protected override async ProcessTask<bool> Execute(ProcessContext process, string input)
    {
        var found = await process.PointRead(entity, input, policy: new ProcessReadPolicy(
            timeout: TimeSpan.FromSeconds(2), maxAttempts: 3,
            retryableFailureCodes: ["storage.busy"], progress: ProcessReadProgress.Durable));
        return found.Entity != null;
    }
}
