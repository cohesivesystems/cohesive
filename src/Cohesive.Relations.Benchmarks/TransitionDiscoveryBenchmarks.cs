using System.Collections.Immutable;
using BenchmarkDotNet.Attributes;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;

namespace Cohesive.Relations.Benchmarks;

[MemoryDiagnoser]
[ShortRunJob]
public class TransitionDiscoveryBenchmarks
{
    [Params(1, 8, 32)] public int Operations { get; set; }
    [Params(false, true)] public bool CompleteInput { get; set; }
    public sealed record Load(string OrganizationId, string Reason);
    public sealed record Command(string Reason);
    ServiceRuntime runtime = null!;
    OperationContext context = null!;
    QualifiedShapeId entity;
    Dictionary<string, PortableValue> inputs = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var definition = ObjectEntityDefinition.For<Load>(new("load"));
        entity = definition.StateShape.QualifiedId;
        var transition = TransitionAuthoring.Create<Load, Command, string>(definition.Shape, new("load.update"), new("1"),
            t => t.Set(new("reason"), load => load.Reason, (load, command) => command.Reason)
                .Return(new("result"), TransitionOutcomeDisposition.Applied, "updated"));
        var compiled = transition.Compile();
        var plan = compiled.Plan ?? throw new InvalidOperationException("Fixture failed compilation.");
        var operations = Enumerable.Range(0, Operations).Select(index => new ServiceTransitionOperation("update-" + index,
            entity, plan.DefinitionReference, [new("loads.update")])).ToArray();
        var service = ServiceDefinitionDocuments.Create(new("loads"), new("1"), new ServiceDefinition([.. operations]),
            new(new("benchmark"), new("discovery"), DocumentOrigin.User));
        var repository = new InMemoryEntityOutboxRepository(definition, _ => "org-a");
        await repository.Upsert(OperationContext.Create(), new(definition.CreateState("load-1", new Load("org-a", "before")).Snapshot));
        runtime = new(service, operations.Select(operation => new ServiceTransitionBinding(operation.Id, plan, definition, _ => repository)),
            new IdentityServiceInvocationAuthorization("organization", new(nameof(Load.OrganizationId))));
        var actor = new PrincipalRef("operator", PrincipalKind.User);
        var scope = new ScopeRef("org-a", "organization");
        context = OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new IdentityScopeGrant(actor, scope, ["loads.update"], "benchmark")]));
        inputs = operations.ToDictionary(operation => operation.Id,
            _ => PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Command("after"))));
        var result = await Inspect();
        if (result.Length != Operations) throw new InvalidOperationException("Fixture operations are not discoverable.");
    }

    [Benchmark]
    public Task<ImmutableArray<ServiceTransitionAvailability>> Inspect() =>
        runtime.InspectTransitionsAsync(context, entity, "load-1", CompleteInput ? inputs : null);
}
