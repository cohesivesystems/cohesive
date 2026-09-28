using System.Diagnostics;
using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Adapters.OpenApi;
using Cohesive.Api.CodeGen;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Relations.Execution;
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

[Collection(Cohesive.Tests.Observability.OperationTelemetryEmitterTestCollection.Name)]
public sealed class ServiceProcessEntityResultTests
{
    [Fact]
    public async Task DeclarationProjectionPreservesRuntimeEndpointContractsWithoutResolvingRepositories()
    {
        var fixture = await Create();
        var declaration = fixture.Runtime.Declaration;
        var http = new HttpBinding("GET", "/results/{instanceId}", [], null);
        var declared = ServiceApiProjection.ProjectCommittedEntityResult<string>(declaration, "result", http);
        var bound = fixture.Runtime.ProjectCommittedEntityResult<string>("result", http);
        Assert.Equal(bound.Id, declared.Id);
        Assert.Equal(bound.Operation.AuthorizationRequirements, declared.Operation.AuthorizationRequirements);
        Assert.Equal(bound.Operation.Results.Select(result => (result.Kind, result.BodyType, result.Http!.StatusCode)),
            declared.Operation.Results.Select(result => (result.Kind, result.BodyType, result.Http!.StatusCode)));
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
        Assert.Throws<ArgumentException>(() => ServiceApiProjection.ProjectCommittedEntityResult<string>(
            declaration, "missing", http));
        Assert.Throws<ArgumentException>(() => ServiceApiProjection.ProjectCommittedEntityResult<string>(
            declaration, "result", new("POST", "/result", [], new(typeof(string)))));
    }

    [Fact]
    public async Task PendingResultReadIsNotReportedAsRejectedTelemetry()
    {
        var fixture = await Create();
        fixture.Values.Result = ProcessExecutionValueReadResult.InProgress(new(
            fixture.Values.Result.Values!.Definition, fixture.Instance));
        var completed = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ExecutionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => completed.Add(activity)
        };
        ActivitySource.AddActivityListener(listener);
        var result = await fixture.Read();
        Assert.Equal(ApiResultKind.Accepted, result.Kind);
        var span = Assert.Single(completed);
        Assert.Equal("pending", span.GetTagItem(ExecutionTelemetry.OutcomeTagName));
        Assert.NotEqual(ActivityStatusCode.Error, span.Status);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Fact]
    public void ProblemProjectionUsesDeclaredStatusAndBodyContract()
    {
        var endpoint = Cohesive.Api.Api.Define("Problems").Query("Read").Route("GET", "/result")
            .Returns<string>().Result<ApiConflictProblem>(ApiResultKind.Conflict, httpStatusCode: 412).Build();
        var response = ServiceEndpointRouteBuilderExtensions.ProjectServiceProblem(endpoint,
            ApiResultKind.Conflict, [new("review.stale", DiagnosticSeverity.Error, "Refresh the review.", "/token")]);
        Assert.Equal(412, Assert.IsAssignableFrom<IStatusCodeHttpResult>(response).StatusCode);
        Assert.IsType<ApiConflictProblem>(Assert.IsAssignableFrom<IValueHttpResult>(response).Value);
        Assert.Throws<InvalidOperationException>(() => ServiceEndpointRouteBuilderExtensions.ProjectServiceProblem(
            endpoint, ApiResultKind.Success, []));
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 403)]
    [InlineData(true, 400, ApiResultKind.ValidationFailed)]
    public async Task HttpProjectionUsesAuthorizedExactReceiptAndOpaqueToken(bool admitted, int status, ApiResultKind? classification = null)
    {
        var fixture = await Create(classification: classification);
        await fixture.Repository.Upsert(fixture.Context, new(fixture.Repository.EntityDefinition
            .CreateState("note/1", new Note("note/1", "tenant-a", "later"), 2).Snapshot, fixture.Committed.ConcurrencyToken));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(admitted ? fixture.Context : OperationContext.Create());
        await using var app = builder.Build();
        var projections = 0;
        app.MapServiceProcessEntityResult(fixture.Runtime, "result", "/notes/results/{instanceId}", snapshot =>
        {
            projections++;
            return new Response(snapshot.Entity.Observation.GetField("Text").GetRequiredString());
        }, authorizationPolicyResolver: (_, requirement) => requirement.Id);
        Assert.Equal(0, fixture.Values.Reads);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
        Assert.Same(fixture.Runtime.Declaration, endpoint.Metadata.GetMetadata<ExecutionDefinitionDocument>());
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = "GET";
        http.Request.RouteValues["instanceId"] = fixture.Instance.Value;
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, projections);
        Assert.Equal(admitted ? 1 : 0, fixture.Values.Reads);
        if (status == 200)
        {
            Assert.Equal(fixture.Committed.ConcurrencyToken.Value, http.Response.Headers["X-Concurrency-Token"].ToString());
            http.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(http.Response.Body);
            Assert.Equal("committed-private", json.RootElement.GetProperty("text").GetString());
        }
        if (status == 400)
        {
            http.Response.Body.Position = 0;
            using var json = await JsonDocument.ParseAsync(http.Response.Body);
            var issue = Assert.Single(json.RootElement.GetProperty("issues").EnumerateArray());
            Assert.Equal("notes.rejected", issue.GetProperty("code").GetString());
            Assert.Equal("/note", issue.GetProperty("field").GetString());
            Assert.False(http.Response.Headers.ContainsKey("X-Concurrency-Token"));
        }
    }

    [Fact]
    public async Task ResultProjectionEmitsTypedReadContractWithoutStorageAccess()
    {
        var fixture = await Create();
        var endpoint = fixture.Runtime.ProjectCommittedEntityResult<Response>("result",
            new("GET", "/notes/results/{instanceId}",
                [new("instanceId", HttpParameterSource.Route, typeof(string))], body: null));
        Assert.Equal(typeof(Response), endpoint.Operation.ResponseType);
        Assert.Equal(new[] { "notes.result.read" },
            endpoint.Operation.AuthorizationRequirements.Select(requirement => requirement.Id));
        var emission = new OpenApiEmitter().Emit(new ApiCodeGenerationRequest(new ApiDefinition([endpoint.Operation])));
        using var document = JsonDocument.Parse(Assert.Single(emission.Documents).Text);
        var read = document.RootElement.GetProperty("paths").GetProperty("/notes/results/{instanceId}").GetProperty("get");
        Assert.False(read.TryGetProperty("requestBody", out _));
        var parameter = Assert.Single(read.GetProperty("parameters").EnumerateArray());
        Assert.Equal("instanceId", parameter.GetProperty("name").GetString());
        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        foreach (var status in new[] { "200", "202", "400", "403", "404", "500" })
            Assert.True(read.GetProperty("responses").TryGetProperty(status, out _), status);
        Assert.Equal(0, fixture.Values.Reads);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Theory]
    [InlineData(ApiResultKind.ValidationFailed)]
    [InlineData(ApiResultKind.Conflict)]
    [InlineData(ApiResultKind.NotFound)]
    [InlineData(ApiResultKind.Success)]
    public async Task ClassifierSeparatesBusinessRejectionFromMissingCommitEvidence(ApiResultKind classification)
    {
        var fixture = await Create(classification: classification);
        var original = fixture.Values.Result.Values!;
        fixture.Values.Result = ProcessExecutionValueReadResult.Available(new(original.Definition,
            original.ProcessInstanceId, original.Input, original.TerminalOutcome, original.TerminalContinuation, []));
        var denied = await fixture.Runtime.ReadCommittedEntityAsync(OperationContext.Create(), "result", fixture.Instance);
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal(0, fixture.Values.Classifications);
        var result = await fixture.Read();
        Assert.Equal(classification == ApiResultKind.Success ? ApiResultKind.InfrastructureError : classification, result.Kind);
        Assert.Equal(classification == ApiResultKind.Success ? "services.process.receiptUnavailable" : "notes.rejected",
            Assert.Single(result.Diagnostics).Code);
        Assert.Equal(1, fixture.Values.Classifications);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
        Assert.Null(result.Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundedWaitReadsAgainOnlyAfterProviderCompletion(bool completes)
    {
        var fixture = await Create();
        var terminal = fixture.Values.Result;
        fixture.Values.Result = ProcessExecutionValueReadResult.InProgress(new(terminal.Values!.Definition, fixture.Instance));
        fixture.Values.OnWait = () =>
        {
            if (completes) fixture.Values.Result = terminal;
            return completes;
        };
        var denied = await fixture.Runtime.ReadCommittedEntityAsync(OperationContext.Create(), "result", fixture.Instance,
            TimeSpan.FromSeconds(2));
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal(0, fixture.Values.Waits);
        var result = await fixture.Runtime.ReadCommittedEntityAsync(fixture.Context, "result", fixture.Instance,
            TimeSpan.FromSeconds(2));
        Assert.Equal(completes ? ApiResultKind.Success : ApiResultKind.Accepted, result.Kind);
        Assert.Equal(1, fixture.Values.Waits);
        Assert.Equal(completes ? 2 : 1, fixture.Values.Reads);
        Assert.Equal(completes ? 1 : 0, fixture.Values.RepositoryResolutions);
        Assert.Contains(result.Trace.Events, item => item.Kind == (completes ? "completionWaitFinished" : "completionWaitExpired"));
    }

    [Fact]
    public async Task BoundedWaitRechecksRevokedAuthorizationBeforeSecondRead()
    {
        var authorization = new RevocableAuthorization();
        var fixture = await Create(authorization: authorization);
        var terminal = fixture.Values.Result;
        fixture.Values.Result = ProcessExecutionValueReadResult.InProgress(new(terminal.Values!.Definition, fixture.Instance));
        fixture.Values.OnWait = () => { authorization.Revoked = true; fixture.Values.Result = terminal; return true; };
        var result = await fixture.Runtime.ReadCommittedEntityAsync(fixture.Context, "result", fixture.Instance, TimeSpan.FromSeconds(2));
        Assert.Equal(ApiResultKind.Forbidden, result.Kind);
        Assert.Equal(1, fixture.Values.Reads);
        Assert.Equal(1, fixture.Values.Waits);
        Assert.Equal(0, fixture.Values.RepositoryResolutions);
    }

    [Fact]
    public async Task BoundedWaitRejectsWrongInstanceBeforeWaiting()
    {
        var fixture = await Create();
        fixture.Values.Result = ProcessExecutionValueReadResult.InProgress(new(fixture.Values.Result.Values!.Definition, new("other")));
        var result = await fixture.Runtime.ReadCommittedEntityAsync(fixture.Context, "result", fixture.Instance, TimeSpan.FromSeconds(2));
        Assert.Equal(ApiResultKind.NotFound, result.Kind);
        Assert.Equal(0, fixture.Values.Waits);
    }

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

    static async Task<Fixture> Create(string owner = "tenant-a", string resultNode = "commit", ApiResultKind? classification = null, IServiceInvocationAuthorization? authorization = null)
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
        var classifier = classification is null ? null : HostedQuery<bool, ServiceResultClassification>.Create(
            new("notes/classify"), new("1"), new("notes/classify", "1"), "test-policy", provenance,
            evaluationSemantics: HostedQueryEvaluationSemantics.DeterministicComputation);
        var classifierBinding = classifier is null ? null : DeterministicHostedQueryBinding.Create(classifier,
            classifier.Implementation, (_, _, _) =>
            {
                values.Classifications++;
                return new ServiceResultClassification(classification!.Value,
                    classification == ApiResultKind.Success ? [] : [new("notes.rejected", DiagnosticSeverity.Error,
                        "The note was rejected.", "/note")]);
            });
        var service = ServiceDefinitionDocuments.Create(new("notes"), new("1"), new([
            new ServiceProcessEntityResultOperation("result", plan.DefinitionReference, new(resultNode), entity.StateShape.QualifiedId,
                [new("notes.result.read")], resultClassifier: classifier?.Reference)]), provenance);
        var binding = new ServiceProcessEntityResultBinding("result", plan, transition,
            new(entity, _ => { values.RepositoryResolutions++; return repository; }), "notes", values, classifierBinding);
        var runtime = new ServiceRuntime(service, [binding], authorization ?? new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        return new(runtime, repository, values, context, continuation.ProcessInstanceId, (await repository.TryGet(context, "note/1"))!);
    }

    sealed record Response(string Text);
    sealed record Note(string Id, string Tenant, string Text);
    sealed record Update(string Text);
    sealed record Fixture(ServiceRuntime Runtime, InMemoryEntityOutboxRepository Repository, Values Values,
        OperationContext Context, ProcessInstanceId Instance, EntitySnapshot Committed)
    {
        public ValueTask<ServiceOperationResult<EntitySnapshot>> Read() => Runtime.ReadCommittedEntityAsync(Context, "result", Instance);
    }
    sealed class RevocableAuthorization : IServiceInvocationAuthorization
    {
        readonly IdentityServiceInvocationAuthorization inner = new("tenant", new("Tenant"));
        public bool Revoked;
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation) =>
            Revoked ? ValueTask.FromResult<ScopeRef?>(null) : inner.AdmitAsync(context, operation);
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceOperation operation, EntitySnapshot snapshot) =>
            inner.AuthorizeResourceAsync(context, operation, snapshot);
    }
    sealed class Values(ProcessExecutionValueReadResult result) : IProcessExecutionValueRepository, IProcessExecutionCompletionWaiter
    {
        public ProcessExecutionValueReadResult Result = result;
        public int Reads;
        public int Classifications;
        public int Waits;
        public Func<bool>? OnWait;
        public ValueTask<bool> WaitForCompletionAsync(OperationContext context, InteractionAuthorityScope authorityScope,
            ProcessInstanceId processInstanceId, TimeSpan maximumWait)
        {
            Assert.Equal(new("notes", "tenant-a"), authorityScope);
            Assert.Equal(TimeSpan.FromSeconds(2), maximumWait);
            context.ThrowIfCancellationRequested();
            Waits++;
            return ValueTask.FromResult(OnWait!());
        }

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
