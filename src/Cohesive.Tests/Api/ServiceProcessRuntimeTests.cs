using Cohesive.Api;
using Cohesive.Api.Execution;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.IR;
using Cohesive.Tests.ExecutionKernel;

namespace Cohesive.Tests.Api;

public sealed class ServiceProcessRuntimeTests
{
    [Fact]
    public async Task StartUsesNativeAdmissionAndReplayWithServerOwnedAuthority()
    {
        var fixture = Create();
        var request = fixture.Request();
        var accepted = await fixture.Runtime.StartAsync(Context(), "publish", request);
        var replayed = await fixture.Runtime.StartAsync(Context(), "publish", request);
        Assert.Equal(ApiResultKind.Success, accepted.Kind);
        Assert.Equal(ProcessStartDisposition.Accepted, accepted.Outcome!.Disposition);
        Assert.Equal(ProcessStartDisposition.Replayed, replayed.Outcome!.Disposition);
        Assert.Equal(accepted.Outcome.Admission, replayed.Outcome.Admission);
        Assert.Equal(2, fixture.Dispatches);
        Assert.Equal(request.InitialContinuation, accepted.Outcome.Admission!.Continuation);
        Assert.Null(accepted.Trace.Continuation);
        Assert.Equal("alice", fixture.Received!.Context.Authorization.Actor);
        Assert.Equal(new("notes-authority", "tenant-a"), fixture.Received.Context.Authorization.AuthorityScope);
        Assert.NotEqual(request.Context.Authorization, fixture.Received.Context.Authorization);
        Assert.Equal(fixture.Document.Metadata.Provenance, fixture.Received.Context.Provenance);
        Assert.NotEqual(request.Context.IssuedAtUtc, fixture.Received.Context.IssuedAtUtc);
        Assert.Equal(request.Context.CommandId, fixture.Received.Context.CommandId);
        Assert.Equal(request.Context.IdempotencyKey, fixture.Received.Context.IdempotencyKey);
        Assert.Equal(new[] { "authorityAdmitted", "processStartDispatched" }, accepted.Trace.Events.Select(e => e.Kind));
        Assert.DoesNotContain("private-input", ExecutionTraceJsonSerializer.Serialize(accepted.Trace));
    }

    [Fact]
    public async Task DeniedWrongDefinitionAndInvalidInputNeverDispatch()
    {
        var fixture = Create();
        var request = fixture.Request();
        var denied = await fixture.Runtime.StartAsync(OperationContext.Create(), "publish", request);
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        var wrongDefinition = new ProcessStartRequest(request.SchemaVersion,
            new(request.Definition.DefinitionId, new("other"), request.Definition.Fingerprint),
            request.Context, request.InitialContinuation, request.Input);
        var wrong = await fixture.Runtime.StartAsync(Context(), "publish", wrongDefinition);
        Assert.Equal(ApiResultKind.ValidationFailed, wrong.Kind);
        Assert.Equal("services.process.definitionMismatch", Assert.Single(wrong.Diagnostics).Code);
        var invalid = new ProcessStartRequest(request.SchemaVersion, request.Definition,
            request.Context, request.InitialContinuation, PortableValue.Concrete(new(new ScalarTypeRef(ScalarTypeKind.Bool)), ObservationValue.FromBool(true)));
        var badInput = await fixture.Runtime.StartAsync(Context(), "publish", invalid);
        Assert.Equal(ApiResultKind.ValidationFailed, badInput.Kind);
        Assert.Equal("services.process.inputInvalid", Assert.Single(badInput.Diagnostics).Code);
        Assert.Equal(0, fixture.Dispatches);
    }

    static OperationContext Context()
    {
        var actor = new PrincipalRef("alice", PrincipalKind.User);
        var scope = new ScopeRef("tenant-a", "tenant", PartitionKey: "shared");
        return OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["notes.publish"], "tests")]));
    }

    static Fixture Create()
    {
        var provenance = new ExecutionProvenance(new("tests"), new("tests/services"), DocumentOrigin.Generated);
        var contract = new ValueContract(new ScalarTypeRef(ScalarTypeKind.String));
        var process = ProcessDefinitionDocuments.Create(new("notes/publish"), new("v1"),
            new(contract, contract, new("return"), [new ReturnProcessNode(new("return"), Expr.BoundValue(ProcessBindingIds.Input))],
                ProcessRecoveryPolicy.ContinueAttempt), provenance);
        var compiled = ProcessStaticCompiler.Compile(process, new ProcessDefinitionValidationContext());
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
        var plan = compiled.Plan!;
        var catalog = ExecutionControlApiCatalog.Create();
        var adapter = new InMemoryExecutionControlApiAdapter(ProcessControlTestFixture.Create().Catalog, catalog);
        var service = ServiceDefinitionDocuments.Create(new("notes"), new("v1"),
            new([new ServiceProcessOperation("publish", plan.DefinitionReference, [new("notes.publish")])]), provenance);
        var fixture = new Fixture { Plan = plan, Document = service };
        var binding = new ServiceProcessBinding("publish", plan, "notes-authority", async (context, request, invocation) =>
        {
            fixture.Dispatches++;
            fixture.Received = request;
            var result = await adapter.DispatchAsync(context, catalog.Start, request, invocation);
            return Assert.IsType<Cohesive.Execution.ProcessStartResult>(result.Body);
        });
        fixture.Runtime = new(service, [binding], new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        return fixture;
    }

    sealed class Fixture
    {
        public required CompiledProcessPlan Plan { get; init; }
        public required ExecutionDefinitionDocument Document { get; init; }
        public ServiceRuntime Runtime { get; set; } = null!;
        public int Dispatches { get; set; }
        public ProcessStartRequest? Received { get; set; }
        public ProcessStartRequest Request() => new(ProcessStartRequest.CurrentSchemaVersion, Plan.DefinitionReference,
            new(new("command"), new("idempotency"), new("instance"), new("forged-actor", new("forged-authority", "tenant-b"), "forged-evidence"),
                DateTimeOffset.UnixEpoch, new(new("forged"), new("forged/source"), DocumentOrigin.Unknown)),
            new(new("instance"), new("attempt")), PortableValue.Concrete(Plan.Definition.Input, ObservationValue.FromString("private-input")));
    }
}
