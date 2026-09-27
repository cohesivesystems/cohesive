using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Cohesive.Adapters.AspNet.Services;
using Cohesive.Api;
using Cohesive.Api.Services;
using Cohesive.Api.Execution.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.Api;

[Collection(Cohesive.Tests.Observability.OperationTelemetryEmitterTestCollection.Name)]
public sealed class ServiceRuntimeTests
{
    [Fact]
    public async Task DeclaredOperationLoadsOnceAndCommitsWithExactReviewedToken()
    {
        var fixture = await Fixture.Create();
        Assert.Equal(0, fixture.Resolutions);
        var result = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Success, result.Kind);
        Assert.NotEqual(fixture.Initial.ConcurrencyToken, result.ConcurrencyToken);
        var retained = await fixture.Repository.Inner.TryGet(OperationContext.Create(), "note-1", EntityReadOptions.Full.WithPartitionKey("tenant-a"));
        Assert.Equal("after", retained!.Entity.Observation.GetField("Text").GetString());
        Assert.Equal(fixture.Initial.Entity.Version + 1, retained.Entity.Version);
        Assert.Equal(1, fixture.Repository.Reads);
        Assert.Equal(1, fixture.Repository.Writes);
        Assert.Equal(1, fixture.Authority.ResourceChecks);
        Assert.Equal(1, fixture.Resolutions);
        Assert.NotNull(result.Outcome);
        Assert.NotNull(result.TransitionTrace);
        Assert.Equal(new[] { "authorityAdmitted", "subjectLoaded", "resourceAuthorized", "transitionDecided", "commitCompleted" },
            result.Trace.Events.Select(e => e.Kind));
        Assert.Equal("notes", result.Trace.Definition.DefinitionId.Value);
        var traceJson = ExecutionTraceJsonSerializer.Serialize(result.Trace);
        Assert.DoesNotContain("tenant-a", traceJson);
        Assert.DoesNotContain("note-1", traceJson);
        Assert.DoesNotContain("after", traceJson);
    }

    [Fact]
    public async Task StaleInvocationRejectsBeforeDecisionOrCommit()
    {
        var fixture = await Fixture.Create();
        Assert.Equal(ApiResultKind.Success, (await fixture.Invoke()).Kind);
        var stale = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Conflict, stale.Kind);
        Assert.Equal("services.concurrency.stale", Assert.Single(stale.Diagnostics).Code);
        Assert.Null(stale.Outcome);
        Assert.Null(stale.TransitionTrace);
        Assert.DoesNotContain(stale.Trace.Events, e => e.Kind == "commitCompleted");
        Assert.Equal("services.concurrency.stale", stale.Trace.Events[^1].Detail);
        Assert.Equal(1, fixture.Repository.Writes);
    }

    [Fact]
    public async Task DeniedAdmissionDoesNotResolveOrReadRepository()
    {
        var fixture = await Fixture.Create();
        fixture.Authority.AllowAdmission = false;
        var denied = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal(0, fixture.Resolutions);
        Assert.Equal(0, fixture.Repository.Reads);
        Assert.Equal(0, fixture.Repository.Writes);
    }

    [Fact]
    public async Task LogicalResourceAuthorizationIsIndependentOfPhysicalReadRouting()
    {
        var fixture = await Fixture.Create();
        fixture.Authority.AllowResource = false;
        var denied = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal("services.authorization.resourceDenied", Assert.Single(denied.Diagnostics).Code);
        Assert.Equal(1, fixture.Repository.Reads);
        Assert.Equal(0, fixture.Repository.Writes);
    }

    [Fact]
    public async Task PersistedDeclarationRoundTripsAndRejectsMissingOrInexactBindings()
    {
        var fixture = await Fixture.Create();
        var json = ExecutionDefinitionJsonSerializer.Serialize(fixture.Document);
        var read = ExecutionDefinitionJsonSerializer.TryDeserialize(json, out var restored);
        Assert.True(read.IsValid);
        Assert.True(ServiceDefinitionDocuments.ValidateAndProject(restored!, out var definition).IsValid);
        Assert.Equal("revise", Assert.Single(definition!.Operations).Id);
        Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(restored!, [], fixture.Authority));
        var wrong = new ServiceTransitionBinding("unknown", fixture.Plan, fixture.Repository.EntityDefinition, _ => fixture.Repository);
        Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(restored!, [wrong], fixture.Authority));
        Assert.Equal(0, fixture.Resolutions);
    }

    [Fact]
    public async Task TransitionOnlyExecutorRejectsOtherDeclaredOperationFamiliesBeforeResolvingInfrastructure()
    {
        var fixture = await Fixture.Create();
        var document = ServiceDefinitionDocuments.Create(new("notes"), new("v2"),
            new([new ServiceQueryOperation("revise", fixture.Plan.DefinitionReference, new("tenant"))]),
            fixture.Document.Metadata.Provenance);
        var binding = new ServiceTransitionBinding("revise", fixture.Plan, fixture.Repository.EntityDefinition, _ =>
            throw new InvalidOperationException("Unsupported operations must never resolve infrastructure."));
        var exception = Assert.Throws<ServiceBindingValidationException>(() =>
            new ServiceRuntime(document, [binding], fixture.Authority));
        Assert.Equal("services.binding.operationUnsupported", Assert.Single(exception.Validation.Diagnostics).Code);
        Assert.Equal(0, fixture.Resolutions);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionDoesNotResolveRepository()
    {
        var fixture = await Fixture.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Runtime.InvokeAsync(
            OperationContext.Create(cancellationToken: cancellation.Token), "revise", "note-1",
            fixture.Initial.ConcurrencyToken, new("cancelled"), fixture.Input));
        Assert.Equal(0, fixture.Resolutions);
    }

    [Fact]
    public async Task CommitRaceReturnsDifferentEvidenceFromStaleLoad()
    {
        var fixture = await Fixture.Create();
        fixture.Repository.RejectWrite = true;
        var result = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Conflict, result.Kind);
        Assert.Equal("/commit", Assert.Single(result.Diagnostics).Location);
        Assert.Contains(result.Trace.Events, e => e.Kind == "transitionDecided");
        Assert.DoesNotContain(result.Trace.Events, e => e.Kind == "commitCompleted");
    }

    [Fact]
    public async Task PartialSnapshotsCannotBecomeTrustedTransitionState()
    {
        var fixture = await Fixture.Create();
        fixture.Repository.ReturnPartial = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Invoke());
        Assert.Equal(0, fixture.Repository.Writes);
        Assert.Equal(0, fixture.Authority.ResourceChecks);
    }

    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 403)]
    public async Task HttpUsesSameRuntimeAuthorityAndConditionalCommit(bool allowed, int expectedStatus)
    {
        var fixture = await Fixture.Create();
        fixture.Authority.AllowAdmission = allowed;
        var http = await InvokeHttp<Note.ReviseInput, bool>(fixture.Runtime, fixture.Initial.ConcurrencyToken,
            "{\"Text\":\"after\"}", () => Assert.Equal(0, fixture.Resolutions));
        Assert.Equal(expectedStatus, http.Response.StatusCode);
        Assert.Equal(allowed ? 1 : 0, fixture.Repository.Writes);
        Assert.Equal(allowed ? 1 : 0, fixture.Repository.Reads);
        if (allowed)
        {
            http.Response.Body.Position = 0;
            Assert.Equal("true", await new StreamReader(http.Response.Body).ReadToEndAsync());
            Assert.NotEqual(fixture.Initial.ConcurrencyToken.Value, http.Response.Headers["X-Expected-Concurrency-Token"].ToString());
        }
    }

    [Fact]
    public async Task ApiProjectionCannotRedefineTheDeclaredInputContract()
    {
        var fixture = await Fixture.Create();
        Assert.Throws<ArgumentException>(() => fixture.Runtime.Project<string, bool>("revise"));
        var projected = fixture.Runtime.Project<Note.ReviseInput, bool>("revise");
        Assert.Equal("service/notes/operation/revise", projected.Id.Value);
        Assert.Equal(fixture.Plan.DefinitionReference, projected.Operation.TransitionReference);
        Assert.Equal("notes.revise", Assert.Single(projected.Operation.AuthorizationRequirements).Id);
        Assert.Equal(0, fixture.Resolutions);
    }

    [Theory]
    [InlineData("valid", true, 1)]
    [InlineData("selectedPlacement", true, 1)]
    [InlineData("noGrant", false, 0)]
    [InlineData("expired", false, 0)]
    [InlineData("otherActor", false, 0)]
    [InlineData("missingCapability", false, 0)]
    [InlineData("otherScope", false, 0)]
    [InlineData("sharedPartition", false, 1)]
    public async Task IdentityPolicyRequiresExplicitLiveActorGrantsAndLogicalOwnership(string scenario, bool allowed, int reads)
    {
        var fixture = await Fixture.Create();
        var actor = new PrincipalRef("alice", PrincipalKind.User);
        var scope = new ScopeRef(scenario == "sharedPartition" ? "tenant-b" : "tenant-a", "tenant", PartitionKey: "tenant-a");
        var grant = new IdentityScopeGrant(scenario == "otherActor" ? new("bob", PrincipalKind.User) : actor,
            scenario == "otherScope" ? scope with { Id = "tenant-b" } : scope,
            scenario == "missingCapability" ? [] : ["notes.revise"], "test",
            scenario == "expired" ? DateTimeOffset.MinValue : null);
        var identity = new IdentityContext(actor, EffectiveScope: new(
            [scenario == "selectedPlacement" ? scope with { PartitionKey = "untrusted-selection" } : scope],
            ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: scenario == "noGrant" ? [] : [grant]);
        var runtime = new ServiceRuntime(fixture.Document,
            [new ServiceTransitionBinding("revise", fixture.Plan, fixture.Repository.EntityDefinition, _ => fixture.Repository)],
            new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        var result = await runtime.InvokeAsync(OperationContext.Create().WithIdentityContext(identity), "revise", "note-1",
            fixture.Initial.ConcurrencyToken, new("identity-test"), fixture.Input);
        Assert.Equal(allowed ? ApiResultKind.Success : ApiResultKind.Forbidden, result.Kind);
        Assert.Equal(reads, fixture.Repository.Reads);
        Assert.Equal(allowed ? 1 : 0, fixture.Repository.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstrumentationObservesOneInvocationWithoutChangingItsResult(bool observerFails)
    {
        var fixture = await Fixture.Create();
        var completed = new List<Activity>();
        using ActivityListener listener = new()
        {
            ShouldListenTo = source => source.Name == ExecutionTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                completed.Add(activity);
                if (observerFails) throw new InvalidOperationException("observer failure");
            }
        };
        ActivitySource.AddActivityListener(listener);
        var result = await fixture.Invoke();
        Assert.Equal(ApiResultKind.Success, result.Kind);
        Assert.Single(completed);
        Assert.Equal(1, fixture.Repository.Writes);
        Assert.Equal(ExecutionTraceFingerprinter.ComputeSemantic(result.Trace).Value,
            completed[0].GetTagItem(ExecutionTelemetry.TraceFingerprintTagName));
    }

    [Fact]
    public async Task OperationAndRequirementOrderingDoesNotChangeCanonicalIdentity()
    {
        var fixture = await Fixture.Create();
        var entity = fixture.Repository.EntityDefinition.StateShape.QualifiedId;
        var first = new ServiceTransitionOperation("a", entity, fixture.Plan.DefinitionReference, [new("read"), new("write")]);
        var reordered = new ServiceTransitionOperation("a", entity, fixture.Plan.DefinitionReference, [new("write"), new("read")]);
        var second = new ServiceTransitionOperation("b", entity, fixture.Plan.DefinitionReference);
        var provenance = fixture.Document.Metadata.Provenance;
        var left = ServiceDefinitionDocuments.Create(new("canonical"), new("v1"), new([first, second]), provenance);
        var right = ServiceDefinitionDocuments.Create(new("canonical"), new("v1"), new([second, reordered]), provenance);
        Assert.Equal(left.Metadata.Fingerprint, right.Metadata.Fingerprint);
        Assert.Equal(new ServiceDefinition([first, second]), new ServiceDefinition([second, reordered]));
        Assert.Throws<ArgumentException>(() => new ServiceDefinition([first, first]));
    }

    [Fact]
    public void EmissionRequirementIsRejectedBeforeAnyRepositoryResolution()
    {
        var plan = Cohesive.ExecutionKernel.TestFixtures.Storage.RunControlFixture.Start.Compile().Plan!;
        var entity = Cohesive.ExecutionKernel.TestFixtures.Storage.RunControlFixture.Entity;
        var service = ServiceDefinitionDocuments.Create(new("runs"), new("v1"),
            new([new ServiceTransitionOperation("start", entity.StateShape.QualifiedId, plan.DefinitionReference)]), plan.Document.Metadata.Provenance);
        var resolutions = 0;
        var binding = new ServiceTransitionBinding("start", plan, entity, _ =>
        {
            resolutions++;
            return new InMemoryEntityOutboxRepository(entity, _ => "tenant");
        });
        var failure = Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(service, [binding], new Authority()));
        Assert.Equal("services.binding.capabilityUnsupported", Assert.Single(failure.Validation.Diagnostics).Code);
        Assert.Equal(0, resolutions);
    }

    [Fact]
    public async Task HttpOutcomeUsesNativeNamingAndLosslessByteEncoding()
    {
        var fixture = await Fixture.Create();
        var entity = fixture.Repository.EntityDefinition;
        var authored = TransitionAuthoring.Create<Note, JsonReply, JsonReply>(entity.Shape,
            new(new("reply"), new("v1"), new("body"), fixture.Document.Metadata.Provenance),
            transition => transition.Return(new("reply"), TransitionOutcomeDisposition.Applied, (_, input) => input));
        var plan = authored.Compile().Plan!;
        var declaration = ServiceDefinitionDocuments.Create(new("reply-service"), new("v1"),
            new([new ServiceTransitionOperation("revise", entity.StateShape.QualifiedId, plan.DefinitionReference, [new("notes.revise")])]),
            fixture.Document.Metadata.Provenance);
        var runtime = new ServiceRuntime(declaration, [new ServiceTransitionBinding("revise", plan, entity, _ => fixture.Repository)], fixture.Authority);
        var http = await InvokeHttp<JsonReply, JsonReply>(runtime, fixture.Initial.ConcurrencyToken, "{\"label\":\"hello\",\"data\":\"AQID\"}");
        Assert.Equal(200, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        using var document = System.Text.Json.JsonDocument.Parse(body);
        Assert.Equal("hello", document.RootElement.GetProperty("label").GetString());
        Assert.Equal("AQID", document.RootElement.GetProperty("data").GetString());
    }

    sealed record JsonReply([property: System.Text.Json.Serialization.JsonPropertyName("label")] string Text, byte[] Data);

    static async Task<DefaultHttpContext> InvokeHttp<TInput, TOutcome>(ServiceRuntime runtime,
        EntityConcurrencyToken token, string body, Action? afterMapping = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(OperationContext.Create());
        await using var app = builder.Build();
        app.MapServiceTransition<TInput, TOutcome>(runtime, "revise", "/notes/{id}/revise",
            authorizationPolicyResolver: (_, requirement) => requirement.Id);
        afterMapping?.Invoke();
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().Single();
        Assert.Same(runtime.Declaration, endpoint.Metadata.GetMetadata<ExecutionDefinitionDocument>());
        var bytes = Encoding.UTF8.GetBytes(body);
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = bytes.Length;
        http.Request.Body = new MemoryStream(bytes);
        http.Request.RouteValues["id"] = "note-1";
        http.Request.Headers["X-Expected-Concurrency-Token"] = token.Value;
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        return http;
    }

    [Fact]
    public async Task InvalidInputDiagnosticsDoNotDisclosePayloadOrDiagnosticEvidence()
    {
        var fixture = await Fixture.Create();
        var result = await fixture.Runtime.InvokeAsync(OperationContext.Create(), "revise", "note-1",
            fixture.Initial.ConcurrencyToken, new("invalid-input"), PortableValue.Concrete(fixture.Plan.Definition.Input,
                ObservationValue.FromObject(new { Text = new { Secret = "private-payload" } })));
        Assert.NotEqual(ApiResultKind.Success, result.Kind);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Null(diagnostic.Evidence);
            Assert.Equal("Transition invocation failed its declared contract.", diagnostic.Message);
        });
        Assert.DoesNotContain("private-payload", ExecutionTraceJsonSerializer.Serialize(result.Trace));
        Assert.Equal(0, fixture.Repository.Writes);
    }

    sealed class Fixture
    {
        public required CompiledTransitionPlan Plan { get; init; }
        public required ExecutionDefinitionDocument Document { get; init; }
        public required RecordingRepository Repository { get; init; }
        public required EntitySnapshot Initial { get; init; }
        public Authority Authority { get; } = new();
        public ServiceRuntime Runtime { get; private set; } = null!;
        public int Resolutions { get; private set; }
        public PortableValue Input => PortableValue.Concrete(Plan.Definition.Input,
            ObservationValue.FromObject(new Note.ReviseInput("after")));
        public Task<ServiceInvocationResult> Invoke() => Runtime.InvokeAsync(OperationContext.Create(), "revise", "note-1",
            Initial.ConcurrencyToken, new("tests/services/revise"), Input);

        public static async Task<Fixture> Create()
        {
            var entity = Note.Instance.Definition;
            var authored = TransitionAuthoring.Create<Note, Note.ReviseInput, bool>(entity.Shape,
                new(new("tests/services/revise"), new("v1"), new("body"),
                    new(new(TransitionAuthoring.Producer), new("tests/services"), DocumentOrigin.Generated)),
                transition => transition.Set(new("set-text"), note => note.Text, (_, input) => input.Text)
                    .Return(new("applied"), TransitionOutcomeDisposition.Applied, true));
            var compiled = authored.Compile();
            Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics.Select(d => d.Message)));
            var plan = compiled.Plan!;
            var declaration = new ServiceDefinition([new ServiceTransitionOperation("revise", entity.StateShape.QualifiedId, plan.DefinitionReference,
                [new("notes.revise")])]);
            var document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"), declaration,
                new(new("tests"), new("tests/services"), DocumentOrigin.Generated));
            var inner = new InMemoryEntityOutboxRepository(entity, _ => "tenant-a");
            var initial = await inner.Upsert(OperationContext.Create(), new(entity.CreateState("note-1",
                new { Id = "note-1", Tenant = "tenant-a", Text = "before" }).Snapshot));
            var fixture = new Fixture { Plan = plan, Document = document, Repository = new(inner), Initial = initial };
            fixture.Runtime = new(document, [new ServiceTransitionBinding("revise", plan, entity, _ =>
            {
                fixture.Resolutions++;
                return fixture.Repository;
            })], fixture.Authority);
            return fixture;
        }
    }

    sealed class Authority : IServiceInvocationAuthorization
    {
        public bool AllowAdmission { get; set; } = true;
        public bool AllowResource { get; set; } = true;
        public int ResourceChecks { get; private set; }
        public ValueTask<ScopeRef?> AdmitAsync(OperationContext context, ServiceOperation operation)
        {
            Assert.Equal("notes.revise", Assert.Single(operation.AuthorizationRequirements).Id);
            return ValueTask.FromResult<ScopeRef?>(AllowAdmission ? new("tenant-a", "tenant") : null);
        }
        public ValueTask<bool> AuthorizeResourceAsync(OperationContext context, ServiceTransitionOperation operation, EntitySnapshot snapshot)
        {
            ResourceChecks++;
            return ValueTask.FromResult(AllowResource && snapshot.Entity.Observation.GetField("Tenant").GetString() == "tenant-a");
        }
    }

    sealed class RecordingRepository(IEntityRepository inner) : IEntityRepository
    {
        public IEntityRepository Inner => inner;
        public Cohesive.Transitions.Model.EntityDefinition EntityDefinition => inner.EntityDefinition;
        public bool RejectWrite { get; set; }
        public bool ReturnPartial { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null)
        {
            Reads++;
            return inner.TryGet(context, id, ReturnPartial ? EntityReadOptions.ForFields("Text").WithPartitionKey("tenant-a") : options);
        }
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write)
        {
            Writes++;
            if (RejectWrite) throw new ObservationConcurrencyConflictException("Competing write");
            return inner.Upsert(context, write);
        }
    }

    sealed class Note : Entity<Note>
    {
        public sealed record ReviseInput(string Text);
        public Note()
        {
            Id = WriteOnceField<string>(nameof(Id));
            Tenant = WriteOnceField<string>(nameof(Tenant));
            Text = MutableField<string>(nameof(Text));
        }
        public Field<string> Id { get; }
        public Field<string> Tenant { get; }
        public Field<string> Text { get; }
    }
}
