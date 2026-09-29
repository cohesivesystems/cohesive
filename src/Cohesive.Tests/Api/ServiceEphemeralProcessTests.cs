using System.Text;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.IR;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Processes.Authoring;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;

namespace Cohesive.Tests.Api;

public sealed class ServiceEphemeralProcessTests
{
    static readonly ExecutionProvenance Provenance = new(new("tests"), new("tests/services/ephemeral"), DocumentOrigin.Generated);
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));

    [Fact]
    public void FluentPolicyIsByteEquivalentToDirectIrAndRoundTrips()
    {
        var plan = Plan();
        var document = Declare(plan);
        var direct = ServiceDefinitionDocuments.Create(new("notes"), new("1"),
            new([new ServiceProcessOperation("echo", plan.DefinitionReference, [new("notes.read")],
                new(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Terminal, TimeSpan.FromSeconds(2)))]), Provenance);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(direct),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document));
        Assert.Equal(direct.Metadata.Fingerprint, document.Metadata.Fingerprint);
        Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(document), out var restored).IsValid);
        Assert.True(ServiceDefinitionDocuments.ValidateAndProject(restored!, out var definition).IsValid);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<ServiceProcessOperation>(Assert.Single(definition!.Operations)).Execution!.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FluentResultReadDerivesProcessIdentityWithoutInheritingWritePermission(bool classified)
    {
        var process = ProcessAuthoring.Project<string, string>(Plan().Document);
        var classifier = classified ? HostedQuery<string, ServiceResultClassification>.Create(new("classify"), new("1"),
            new("tests.classify", "1"), "v1", Provenance,
            evaluationSemantics: HostedQueryEvaluationSemantics.DeterministicComputation) : null;
        var document = Service.Define(new("notes"), new("1"), Provenance)
            .Operation("write").Require(new("notes.write")).Run(process).ReturnAfterDurableAdmission()
            .Operation("result").Require(new("notes.read")).ReadResultOf(process, classifier).Build();
        var direct = ServiceDefinitionDocuments.Create(new("notes"), new("1"), new([
            new ServiceProcessOperation("write", process.Reference, [new("notes.write")],
                new(ProcessExecutionLifetime.Durable, ServiceProcessCompletion.Admission)),
            new ServiceProcessResultOperation("result", process.Reference, [new("notes.read")], classifier?.Reference)]), Provenance);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(direct),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document));
        Assert.Equal(direct.Metadata.Fingerprint, document.Metadata.Fingerprint);
        if (classifier is not null)
        {
            var fromDocument = Service.Define(new("notes"), new("1"), Provenance)
                .Operation("write").Require(new("notes.write")).Run(process.Document).ReturnAfterDurableAdmission()
                .Operation("result").Require(new("notes.read")).ReadResultOf(process.Document, classifier).Build();
            Assert.Equal(document.Metadata.Fingerprint, fromDocument.Metadata.Fingerprint);
        }
        Assert.Throws<InvalidOperationException>(() => Service.Define(new("notes"), new("1"), Provenance)
            .Operation("ambiguous").Run(process).ReadResultOf(process));
    }

    [Fact]
    public async Task AdmissionAndExactInputPrecedeHostResolutionAndOutputIsInferred()
    {
        var plan = Plan();
        var resolutions = 0;
        var runtime = new ServiceRuntime(Declare(plan), [new ServiceEphemeralProcessBinding("echo", plan, "notes", (_, invocation) =>
        {
            resolutions++;
            Assert.Equal("alice", invocation.Authorization.Actor);
            Assert.Equal("tenant-a", invocation.Authorization.AuthorityScope.Tenant);
            return new NoOperationsHost();
        })], new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        var identity = new ProcessContinuationIdentity(new("instance/echo"), new("attempt/1"));
        var denied = await runtime.ExecuteProcessAsync(OperationContext.Create(), "echo", identity, ObservationValue.FromString("private"));
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal(0, resolutions);
        var invalid = await runtime.ExecuteProcessAsync(Context(), "echo", identity, ObservationValue.FromBool(true));
        Assert.Equal(ApiResultKind.ValidationFailed, invalid.Kind);
        Assert.Equal(0, resolutions);
        var success = await runtime.ExecuteProcessAsync(Context(), "echo", identity, ObservationValue.FromString("private"));
        Assert.Equal(ApiResultKind.Success, success.Kind);
        Assert.Equal(1, resolutions);
        Assert.Equal(PortableValue.Concrete(Text, ObservationValue.FromString("private")), success.Outcome!.Decision.State.Terminal.Detail?.Value);
        Assert.DoesNotContain("private", ExecutionTraceJsonSerializer.Serialize(success.Trace));
    }

    [Fact]
    public void BindingsAndNativeStartProjectionRejectIncompatibleCompletion()
    {
        var plan = Plan();
        var ephemeral = Declare(plan);
        var authorization = new IdentityServiceInvocationAuthorization("tenant", new("Tenant"));
        var mismatch = Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(ephemeral,
            [new ServiceProcessBinding("echo", plan, "notes", (_, _, _) => throw new InvalidOperationException())], authorization));
        Assert.Equal("services.binding.executionUnsupported", Assert.Single(mismatch.Validation.Diagnostics).Code);
        Assert.Throws<ServiceBindingValidationException>(() => ServiceApiProjection.ProjectProcessInput<string>(ephemeral, "echo"));
        var durable = Service.Define(new("notes"), new("1"), Provenance).Operation("echo").Run(plan).ReturnAfterDurableAdmission().Build();
        Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(durable,
            [new ServiceEphemeralProcessBinding("echo", plan, "notes", (_, _) => new NoOperationsHost())], authorization));
    }

    [Fact]
    public void InvalidBudgetsAndEphemeralAdmissionCannotBeDeclared()
    {
        Assert.Throws<ArgumentException>(() => new ServiceProcessExecution(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Admission));
        Assert.Throws<ArgumentException>(() => new ServiceProcessExecution(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Terminal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceProcessExecution(ProcessExecutionLifetime.Ephemeral, ServiceProcessCompletion.Terminal, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => Service.Define(new("notes"), new("1"), Provenance).Operation("echo").ExecuteEphemerally(TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(false, 200, false, true)]
    [InlineData(true, 500, false, true)]
    [InlineData(false, 200, true, true)]
    [InlineData(true, 500, true, true)]
    [InlineData(false, 403, false, false)]
    [InlineData(false, 403, true, false)]
    public async Task HttpTerminalProjectionIsDeferredAndNeverExposesInternalFailure(bool failHost, int expectedStatus, bool mapped, bool authorized)
    {
        var query = new ExecutionDefinitionReference(new("query"), new("1"),
            new(ExecutionDefinitionFingerprinter.Algorithm, ExecutionDefinitionFingerprinter.Canonicalization, new string('a', 64)));
        var process = ProcessAuthoring.Create<string, string>(
            new(new("http-echo"), new("1"), new("query"), ProcessRecoveryPolicy.ContinueAttempt,
                new(new("process-importer"), new("tests/processes/http-echo"), DocumentOrigin.Generated)), builder =>
            {
                var output = builder.Output<string>(new("output"), Text);
                builder.EvaluateRelation(new("query"), query, builder.Input.Value,
                    builder.Continuation(builder.Edge(new("next"), new("return")), output));
                builder.Return(new("return"), output.Value);
            });
        var plan = process.Compile(new(definitions: [new(query, ProcessDefinitionLinkKind.RelationQuery, Text, Text)])).Plan!;
        var document = Declare(plan);
        var resolutions = 0;
        var host = new EchoHost(failHost);
        var runtime = new ServiceRuntime(document,
            [new ServiceEphemeralProcessBinding("echo", plan, "notes", (_, _) => host)],
            new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(authorized ? Context() : OperationContext.Create());
        await using var app = builder.Build();
        var projections = 0;
        if (mapped)
            app.MapServiceEphemeralProcess<EchoRequest, EchoResponse, string, string>(document,
                _ => { resolutions++; return runtime; }, "echo", process,
                new("POST", "/echo/{prefix}", [], new(typeof(EchoRequest))),
                (http, body) => $"{http.Request.RouteValues["prefix"]}{body.Text}",
                output => { projections++; return new(output); }, (_, requirement) => requirement.Id);
        else
            app.MapServiceEphemeralProcess(document, _ => { resolutions++; return runtime; }, "echo", process,
                new("POST", "/echo", [], new(typeof(string))), (_, requirement) => requirement.Id);
        Assert.Equal(0, resolutions);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
        var http = new DefaultHttpContext { RequestServices = app.Services, TraceIdentifier = "invocation/1" };
        var bytes = Encoding.UTF8.GetBytes(mapped ? "{\"text\":\"hello\"}" : "\"hello\"");
        http.Request.RouteValues["prefix"] = "route/";
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = bytes.Length;
        http.Request.Body = new MemoryStream(bytes);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        Assert.Equal(expectedStatus, http.Response.StatusCode);
        Assert.Equal(1, resolutions);
        Assert.Equal(authorized ? 1 : 0, host.Calls);
        var body = Encoding.UTF8.GetString(((MemoryStream)http.Response.Body).ToArray());
        Assert.DoesNotContain("private-backend", body);
        if (!authorized) Assert.Contains("services.authorization.denied", body);
        else if (failHost) Assert.Contains("services.process.incomplete", body);
        else Assert.Equal(mapped ? "{\"text\":\"route/hello\"}" : "\"hello\"", body);
        Assert.Equal(mapped && authorized && !failHost ? 1 : 0, projections);
    }

    public sealed record EchoRequest(string Text);
    public sealed record EchoResponse(string Text);

    [Fact]
    public void MediumProjectionRetainsDeclaredIdentityAndRejectsMismatchedBodyOrProcess()
    {
        var process = ProcessAuthoring.Project<string, string>(Plan().Document);
        var declaration = Declare(Plan());
        var direct = ServiceApiProjection.ProjectEphemeralProcess(declaration, "echo", process);
        var mapped = ServiceApiProjection.ProjectEphemeralProcess<EchoRequest, EchoResponse, string, string>(
            declaration, "echo", process, new("POST", "/echo", [], new(typeof(EchoRequest))));
        Assert.Equal(direct.Operation.Id, mapped.Operation.Id);
        Assert.Equal(typeof(EchoResponse), mapped.Operation.Results.Single(result => result.IsPrimary).BodyType);
        Assert.Throws<ArgumentException>(() =>
            ServiceApiProjection.ProjectEphemeralProcess<EchoRequest, EchoResponse, string, string>(
                declaration, "echo", process, new("POST", "/echo", [], new(typeof(string)))));
        var durable = Service.Define(new("notes"), new("1"), Provenance)
            .Operation("echo").Run(process).ReturnAfterDurableAdmission().Build();
        Assert.Throws<ArgumentException>(() =>
            ServiceApiProjection.ProjectEphemeralProcess<EchoRequest, EchoResponse, string, string>(durable, "echo", process));
    }

    sealed class EchoHost(bool fail) : IAsyncProcessReferenceHost
    {
        public int Calls { get; private set; }
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation)
        {
            Calls++;
            Assert.Equal("alice", evaluation.StartContext?.Authorization.Actor);
            Assert.Equal("tenant-a", evaluation.StartContext?.Authorization.AuthorityScope.Tenant);
            Assert.Equal(evaluation.Continuation.ProcessInstanceId, evaluation.StartContext!.ProcessInstanceId);
            Assert.Equal(Provenance, evaluation.StartContext.Provenance);
            if (fail) throw new IOException("private-backend");
            return ValueTask.FromResult(ProcessOperationResult.Completed(evaluation.Input));
        }
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }

    static ExecutionDefinitionDocument Declare(CompiledProcessPlan plan) => Service.Define(new("notes"), new("1"), Provenance)
        .Require(new("notes.read"))
        .Operation("echo").Run(plan).ExecuteEphemerally(TimeSpan.FromSeconds(2))
        .Build();

    static CompiledProcessPlan Plan()
    {
        var document = ProcessDefinitionDocuments.Create(new("echo"), new("1"),
            new(Text, Text, new("return"), [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
                ProcessRecoveryPolicy.ContinueAttempt), Provenance);
        return ProcessStaticCompiler.Compile(document, new()).Plan!;
    }

    static OperationContext Context()
    {
        var actor = new PrincipalRef("alice", PrincipalKind.User);
        var scope = new ScopeRef("tenant-a", "tenant", PartitionKey: "shared");
        return OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["notes.read"], "tests")]));
    }

    sealed class NoOperationsHost : IAsyncProcessReferenceHost
    {
        public ValueTask<ProcessOperationResult> InvokeTransitionAsync(OperationContext context, ProcessTransitionInvocation invocation) => throw new InvalidOperationException();
        public ValueTask<ProcessOperationResult> EvaluateRelationAsync(OperationContext context, ProcessRelationEvaluation evaluation) => throw new InvalidOperationException();
        public ValueTask<ProcessSignalTargetResult> ResolveSignalTargetAsync(OperationContext context, ProcessSignalTargetResolution resolution) => throw new InvalidOperationException();
    }
}
