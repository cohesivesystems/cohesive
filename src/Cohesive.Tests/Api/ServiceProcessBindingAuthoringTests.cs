using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Identity;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.IR;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;

namespace Cohesive.Tests.Api;

public sealed class ServiceProcessBindingAuthoringTests
{
    public sealed record Input(string Token);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Service_definition_hosts_token_fenced_transitions_in_both_binding_forms(bool stale, bool prepared)
    {
        var entity = RunControlFixture.Entity;
        var native = new InMemoryEntityOutboxRepository(entity, EntityPartitionKeyPolicy.FromField(nameof(RunControl.Tenant)));
        var repository = new TypedEntityRepository<RunControl>(native);
        var actor = new PrincipalRef("tester", PrincipalKind.User);
        var scope = new ScopeRef("tenant/a", "tenant", PartitionKey: "tenant/a");
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient), Grants: [new(actor, scope, ["run"], "tests")]));
        var original = await native.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial()));
        var transition = TransitionAuthoring.Create<RunControl, Input, string>(entity.Shape,
            id: new("token-fence"), revision: new("1"), body => body.Set(new("status"), item => item.Status, "processed").Return("done"));
        var document = ProcessDefinitionDocuments.Create(new("token-process"), new("1"),
            new(transition.Definition.Input, transition.Definition.Outcome, new("invoke"), [
                new InvokeTransitionProcessNode(new("invoke"), transition.Reference, Expr.Const("run/1"), Expr.BoundValue(ProcessBindingIds.Input),
                    new(new(new("next"), new("return")))),
                new ReturnProcessNode(new("return"), Expr.Const("done"))], ProcessRecoveryPolicy.ContinueAttempt), RunControlFixture.Provenance);
        var process = ProcessAuthoring.Project<Input, string>(document);
        var hosted = Service.Define(new("service"), new("1"), RunControlFixture.Provenance).Operation("run").Require(new("run"))
            .Run(process, bindings =>
            {
                if (prepared)
                    bindings.Transition(new ProcessTransitionOperationBinding(transition.Compile().Plan!, repository,
                        RunControlFixture.Contracts(), expectedConcurrencyTokenField: nameof(Input.Token), partitionKey: "tenant/a"));
                else bindings.Transition(transition, repository, expectedConcurrencyTokenField: nameof(Input.Token));
            }).Build("tests", TimeSpan.FromSeconds(2), new IdentityServiceInvocationAuthorization("tenant", new(nameof(RunControl.Tenant))));
        if (stale) await native.Upsert(context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "newer" }));
        var before = await native.TryGet(context, "run/1");
        var result = await hosted.Runtime.ExecuteProcessAsync(context, "run", new(new("run"), new("attempt")),
            ObservationValue.FromObject(new Input(original.ConcurrencyToken.Value)));
        Assert.Equal(stale ? ApiResultKind.DomainError : ApiResultKind.Success, result.Kind);
        var after = await native.TryGet(context, "run/1");
        if (stale) Assert.Equal(before, after);
        else Assert.Equal("processed", after!.Entity.Observation.GetField(nameof(RunControl.Status)).GetRequiredString());
    }
}
