using System.Collections.Immutable;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Processes.Runtime;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.Api;

public sealed class ServiceProcessEntityResultTests
{
    [Fact]
    public async Task ResultReturnsOriginalCommitAfterLaterWrite()
    {
        var fixture = await Create();
        var later = await fixture.Repository.Upsert(fixture.Context, new(fixture.Repository.EntityDefinition
            .CreateState("note/1", new Note("note/1", "tenant-a", "later"), 2).Snapshot, fixture.Committed.ConcurrencyToken));
        var result = await fixture.Read();
        Assert.Equal(ApiResultKind.Success, result.Kind);
        Assert.Equal(fixture.Committed, result.Outcome);
        Assert.NotEqual(later.ConcurrencyToken, result.Outcome!.ConcurrencyToken);
        Assert.Equal(1, fixture.Values.Reads);
        Assert.Equal(1, fixture.Values.RepositoryResolutions);
        Assert.Equal(new[] { "authorityAdmitted", "terminalValuesRead", "commitReceiptResolved", "resourceAuthorized" },
            result.Trace.Events.Select(item => item.Kind));
        Assert.DoesNotContain("committed-private", ExecutionTraceJsonSerializer.Serialize(result.Trace));
    }

    [Fact]
    public async Task DeniedAdmissionNeverReadsOrResolvesRepositories()
    {
        var fixture = await Create();
        var result = await fixture.Runtime.ReadCommittedEntityAsync(OperationContext.Create(), "result", fixture.Instance);
        Assert.Equal(ApiResultKind.Forbidden, result.Kind);
        Assert.Equal(0, fixture.Values.Reads);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Fact]
    public async Task LogicalOwnershipIsCheckedOnRetainedSnapshot()
    {
        var fixture = await Create("tenant-other");
        var result = await fixture.Read();
        Assert.Equal(ApiResultKind.Forbidden, result.Kind);
        Assert.Null(result.Outcome);
        Assert.Equal("services.authorization.resourceDenied", Assert.Single(result.Diagnostics).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("ambiguous")]
    [InlineData("inProgress")]
    public async Task UnavailableEvidenceNeverResolvesEntityRepository(string scenario)
    {
        var fixture = await Create();
        var original = fixture.Values.Result.Values!;
        fixture.Values.Result = scenario == "inProgress"
            ? ProcessExecutionValueReadResult.InProgress(new(original.Definition, original.ProcessInstanceId, original.Input))
            : ProcessExecutionValueReadResult.Available(new(original.Definition, original.ProcessInstanceId, original.Input,
                original.TerminalOutcome, original.TerminalContinuation,
                scenario == "missing" ? default : [.. original.Evidence, .. original.Evidence]));
        var result = await fixture.Read();
        Assert.Equal(scenario == "inProgress" ? ApiResultKind.Accepted : ApiResultKind.InfrastructureError, result.Kind);
        Assert.Null(result.Outcome);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Fact]
    public async Task PriorAttemptCannotSupplyTerminalAttemptReceipt()
    {
        var fixture = await Create();
        var original = fixture.Values.Result.Values!;
        var old = new ProcessContinuationIdentity(fixture.Instance, new("previous-attempt"));
        var earlier = original.Evidence.Select(item => item with
            { Trace = item.Trace.Select(trace => trace with { Continuation = old }).ToImmutableArray() }).ToImmutableArray();
        fixture.Values.Result = ProcessExecutionValueReadResult.Available(new(original.Definition, original.ProcessInstanceId,
            original.Input, original.TerminalOutcome, original.TerminalContinuation, earlier));
        var result = await fixture.Read();
        Assert.Equal(ApiResultKind.InfrastructureError, result.Kind);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Fact]
    public async Task BindingRejectsResultSourceThatIsNotTheExactTransitionNode()
    {
        var error = await Assert.ThrowsAsync<ServiceBindingValidationException>(() => Create(resultNode: "return"));
        Assert.Equal("services.binding.resultSourceMismatch", Assert.Single(error.Validation.Diagnostics).Code);
    }

    static async Task<Fixture> Create(string owner = "tenant-a", string resultNode = "commit")
    {
        var actor = new PrincipalRef("reviewer", PrincipalKind.User);
        var scope = new ScopeRef("tenant-a", "tenant", PartitionKey: "shared");
        var context = OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["notes.result.read"], "tests")]));
        var entity = ObjectEntityDefinition.For<Note>(new("note"));
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "shared");
        await repository.Upsert(context, new(entity.CreateState("note/1", new Note("note/1", owner, "initial")).Snapshot));
        var provenance = new ExecutionProvenance(new("tests", "1"), new("tests/service-result"), DocumentOrigin.Generated);
        var transition = TransitionAuthoring.Create<Note, Update, bool>(entity.Shape,
            new(new("note/update"), new("1"), new("body"), provenance), body => body
                .Set(new("text"), state => state.Text, (_, input) => input.Text)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true)).Compile().Plan!;
        var document = ProcessDefinitionDocuments.Create(new("note/process"), new("1"),
            new(transition.Definition.Input, transition.Definition.Outcome, new("commit"), [
                new InvokeTransitionProcessNode(new("commit"), transition.DefinitionReference,
                    Expr.Const("note/1"), Expr.BoundValue(ProcessBindingIds.Input), new(new(new("commit/return"), new("return")))),
                new ReturnProcessNode(new("return"), Expr.Const(true))], ProcessRecoveryPolicy.ContinueAttempt), provenance);
        var plan = ProcessStaticCompiler.Compile(document, new ProcessDefinitionValidationContext([
            new(transition.DefinitionReference, ProcessDefinitionLinkKind.Transition, transition.Definition.Input, transition.Definition.Outcome)])).Plan!;
        Assert.NotNull(plan);
        InteractionContractCatalog.TryCreate([], out var contracts);
        var adapter = new EntityTransitionProcessOperationAdapter(_ => new(transition, repository, contracts!));
        var continuation = new ProcessContinuationIdentity(new("instance/note"), new("attempt/1"));
        var initial = ProcessReferenceInterpreter.Create(plan, continuation,
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Update("committed-private"))));
        var decision = ProcessReferenceInterpreter.Activate(plan, initial,
            new(new("activation/1"), ProcessActivationCause.Start, context.UtcNow,
                new(new("notes", "tenant-a"), new("correlation"),
                    new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), provenance)),
            new AdapterHost(adapter, context));
        Assert.Equal(ProcessActivationDisposition.Completed, decision.Disposition);
        var values = new Values(ProcessExecutionValueReadResult.Available(new(plan.DefinitionReference, continuation.ProcessInstanceId,
            terminalOutcome: decision.State.Terminal, terminalContinuation: continuation, evidence: [decision.Evidence])));
        var service = ServiceDefinitionDocuments.Create(new("notes"), new("1"), new([
            new ServiceProcessEntityResultOperation("result", plan.DefinitionReference, new(resultNode), entity.StateShape.QualifiedId,
                [new("notes.result.read")])]), provenance);
        var binding = new ServiceProcessEntityResultBinding("result", plan, transition,
            new(entity, _ => { values.RepositoryResolutions++; return repository; }), "notes", values);
        var runtime = new ServiceRuntime(service, [binding], new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        return new(runtime, repository, values, context, continuation.ProcessInstanceId, (await repository.TryGet(context, "note/1"))!);
    }

    sealed record Note(string Id, string Tenant, string Text);
    sealed record Update(string Text);
    sealed record Fixture(ServiceRuntime Runtime, InMemoryEntityOutboxRepository Repository, Values Values,
        OperationContext Context, ProcessInstanceId Instance, EntitySnapshot Committed)
    {
        public ValueTask<ServiceOperationResult<EntitySnapshot>> Read() => Runtime.ReadCommittedEntityAsync(Context, "result", Instance);
    }
    sealed class Values(ProcessExecutionValueReadResult result) : IProcessExecutionValueRepository
    {
        public ProcessExecutionValueReadResult Result = result;
        public int Reads;
        public int RepositoryResolutions;
        public ValueTask<ProcessExecutionValueReadResult> GetValuesAsync(OperationContext context,
            InteractionAuthorityScope authorityScope, ProcessInstanceId processInstanceId)
        {
            Reads++;
            Assert.Equal(new("notes", "tenant-a"), authorityScope);
            return ValueTask.FromResult(Result);
        }
    }
    sealed class AdapterHost(EntityTransitionProcessOperationAdapter adapter, OperationContext context) : IProcessReferenceHost
    {
        public ProcessOperationResult InvokeTransition(ProcessTransitionInvocation invocation) => adapter.ExecuteAsync(context, invocation).GetAwaiter().GetResult();
        public ProcessOperationResult EvaluateRelation(ProcessRelationEvaluation evaluation) => throw new InvalidOperationException();
        public ProcessSignalTargetResult ResolveSignalTarget(ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
