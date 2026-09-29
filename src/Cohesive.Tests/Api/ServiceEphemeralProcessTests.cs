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
                new(ServiceProcessLifetime.Ephemeral, ServiceProcessCompletion.Terminal, TimeSpan.FromSeconds(2)))]), Provenance);
        Assert.Equal(ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(direct),
            ExecutionDefinitionFingerprinter.GetNormalizedSemanticBytes(document));
        Assert.Equal(direct.Metadata.Fingerprint, document.Metadata.Fingerprint);
        Assert.True(ExecutionDefinitionJsonSerializer.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(document), out var restored).IsValid);
        Assert.True(ServiceDefinitionDocuments.ValidateAndProject(restored!, out var definition).IsValid);
        Assert.Equal(TimeSpan.FromSeconds(2), Assert.IsType<ServiceProcessOperation>(Assert.Single(definition!.Operations)).Execution!.Timeout);
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
        Assert.Equal(PortableValue.Concrete(Text, ObservationValue.FromString("private")), success.Outcome!.State.Terminal.Detail?.Value);
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
        Assert.Throws<ArgumentException>(() => new ServiceProcessExecution(ServiceProcessLifetime.Ephemeral, ServiceProcessCompletion.Admission));
        Assert.Throws<ArgumentException>(() => new ServiceProcessExecution(ServiceProcessLifetime.Ephemeral, ServiceProcessCompletion.Terminal));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ServiceProcessExecution(ServiceProcessLifetime.Ephemeral, ServiceProcessCompletion.Terminal, TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => Service.Define(new("notes"), new("1"), Provenance).Operation("echo").ExecuteEphemerally(TimeSpan.FromSeconds(1)));
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
