using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Machines;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Machines;

namespace Cohesive.Tests.Api;

public sealed class LogisticsTransitionDiscoveryTests
{
    public sealed record Load(string OrganizationId, string Status, bool DocumentsComplete);
    public sealed record Command(string Reason);

    [Theory]
    [InlineData(true, "loads.release", "release", ServiceTransitionAvailabilityKind.Permitted)]
    [InlineData(false, "loads.release", "release", ServiceTransitionAvailabilityKind.Unavailable)]
    [InlineData(true, "loads.cancel", "cancel", ServiceTransitionAvailabilityKind.Permitted)]
    public async Task RoleCapabilitiesFilterMovesAndMachineGuardsStillApply(bool documentsComplete,
        string capability, string expectedOperation, ServiceTransitionAvailabilityKind expectedAvailability)
    {
        var source = Machine.Define("load.lifecycle", "1");
        var ready = source.State("ready").Initial();
        var released = source.State("released").Final();
        var cancelled = source.State("cancelled").Final();
        var release = source.Edge("release").From(ready).To(released)
            .When((Load load, Command command) => load.DocumentsComplete);
        var cancel = source.Edge("cancel").From(ready).To(cancelled);
        var machine = source.Build();
        var links = new MachineDiscriminatorBinding<Load, string>(machine, load => load.Status)
            .Map(ready, "ready").Map(released, "released").Map(cancelled, "cancelled").Build();
        var entity = ObjectEntityDefinition.For<Load>(new("load"));
        CompiledTransitionPlan Plan(string id, MachineMovement movement)
        {
            var transition = TransitionAuthoring.Create<Load, Command, string>(entity.Shape, new(id), new("1"),
                t => t.Move(movement, rejection: (load, command) => "unavailable")
                    .Return(new("result"), TransitionOutcomeDisposition.Applied, "accepted"));
            var compilation = TransitionStaticCompiler.Compile(transition.Document, machineLinks: links);
            Assert.True(compilation.IsSuccessful);
            return compilation.Plan!;
        }
        var releasePlan = Plan("release", release);
        var cancelPlan = Plan("cancel", cancel);
        var service = ServiceDefinitionDocuments.Create(new("loads"), new("1"), new ServiceDefinition([
            new ServiceTransitionOperation("release", entity.StateShape.QualifiedId, releasePlan.DefinitionReference, [new("loads.release")]),
            new ServiceTransitionOperation("cancel", entity.StateShape.QualifiedId, cancelPlan.DefinitionReference, [new("loads.cancel")])]),
            new(new("tests"), new("logistics/discovery"), DocumentOrigin.User));
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "org-a");
        var original = await repository.Upsert(OperationContext.Create(), new(entity.CreateState("load-1",
            new Load("org-a", "ready", documentsComplete)).Snapshot));
        var runtime = new ServiceRuntime(service, [
            new ServiceTransitionBinding("release", releasePlan, entity, _ => repository),
            new ServiceTransitionBinding("cancel", cancelPlan, entity, _ => repository)],
            new IdentityServiceInvocationAuthorization("organization", new("OrganizationId")));
        var actor = new PrincipalRef("dispatcher", PrincipalKind.User);
        var scope = new ScopeRef("org-a", "organization");
        var identity = new IdentityContext(actor, EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new IdentityScopeGrant(actor, scope, [capability], "role-assignment")]);
        var context = OperationContext.Create().WithIdentityContext(identity);
        var input = ObservationValue.FromObject(new Command("customer request"));
        var result = Assert.Single(await runtime.InspectTransitionsAsync(context, entity.StateShape.QualifiedId, "load-1",
            new Dictionary<string, PortableValue>
            {
                ["release"] = PortableValue.Concrete(releasePlan.Definition.Input, input),
                ["cancel"] = PortableValue.Concrete(cancelPlan.Definition.Input, input)
            }));
        Assert.Equal(expectedOperation, result.OperationId);
        Assert.Equal(expectedAvailability, result.Kind);
        var retained = await repository.TryGet(context, "load-1", EntityReadOptions.Full.WithPartitionKey("org-a"));
        Assert.Equal(original.ConcurrencyToken, retained!.ConcurrencyToken);
        Assert.Equal("ready", retained.Entity.Observation.GetField("Status").GetString());
    }
}
