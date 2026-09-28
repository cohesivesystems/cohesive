using System.Text.Json;
using Cohesive.Adapters.AspNet.Services;
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
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage;

namespace Cohesive.Tests.Api;

public sealed class ServiceQueryRuntimeTests
{
    [Fact]
    public void QueryReferenceProjectionRequiresNoEvaluatorAndRetainsExactRevision()
    {
        var fixture = Create();
        var reference = ServiceQueryBinding.GetReference(new("v1"), fixture.Binding.Compilation);
        Assert.Equal(fixture.Binding.Reference, reference);
        Assert.NotEqual(reference, ServiceQueryBinding.GetReference(new("v2"), fixture.Binding.Compilation));
        Assert.Equal(0, fixture.Resolutions);
        Assert.Equal(0, fixture.Reader.Reads);
        Assert.Throws<ArgumentException>(() => ServiceQueryBinding.GetReference(default, fixture.Binding.Compilation));
    }

    [Fact]
    public async Task DeferredQueryDoesNotPrepareUnusedOperationAndStillIsolatesInvocations()
    {
        var fixture = Create();
        var document = ServiceDefinitionDocuments.Create(new("deferred-notes"), new("v1"), new([
            new ServiceQueryOperation("search", fixture.Binding.Reference, new("tenant"), [new("notes.read")]),
            new ServiceQueryOperation("unused", fixture.Binding.Reference, new("tenant"), [new("notes.read")])]),
            fixture.Runtime.Declaration.Metadata.Provenance);
        var prepared = 0;
        var unused = 0;
        var factories = new Dictionary<string, Func<ServiceBinding>>
        {
            ["search"] = () => { prepared++; return fixture.Binding; },
            ["unused"] = () => { unused++; throw new InvalidOperationException("unused construction failed"); }
        };
        var runtime = ServiceRuntime.CreateDeferred(document, factories, new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        factories.Clear();
        Assert.Equal(0, prepared);
        Assert.Equal(0, unused);
        foreach (var tenant in new[] { "tenant-a", "tenant-b" })
        {
            var result = await runtime.EvaluateAsync(Context(tenant), "search", new(tenant), new Dictionary<QueryParameterId, ObservationValue>());
            Assert.Equal(ApiResultKind.Success, result.Kind);
            Assert.Equal(tenant, Assert.Single(Assert.Single(result.Outcome!.Result!.QueryResults).Rows).Value.GetProperty("Tenant").String);
        }
        Assert.Equal(1, prepared);
        Assert.Equal(0, unused);
        Assert.Equal(2, fixture.Reader.Reads);
        var denied = await runtime.EvaluateAsync(OperationContext.Create(), "search", new("denied"), new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Throws<InvalidOperationException>(() => runtime.ValidateBindings());
        Assert.Throws<InvalidOperationException>(() => runtime.ValidateBindings());
        Assert.Equal(1, unused);
    }

    [Fact]
    public async Task DeferredBindingPreparationIsOnceUnderConcurrency()
    {
        var fixture = Create();
        var prepared = 0;
        var runtime = ServiceRuntime.CreateDeferred(fixture.Runtime.Declaration,
            new Dictionary<string, Func<ServiceBinding>> { ["search"] = () =>
                { Interlocked.Increment(ref prepared); return fixture.Binding; } },
            new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(runtime.ValidateBindings)));
        Assert.Equal(1, prepared);
        Assert.Equal(0, fixture.Reader.Reads);
    }

    [Fact]
    public void DeferredBindingCoverageIsImmediateButExactBindingValidationIsRetainedOnFirstUse()
    {
        var fixture = Create();
        var authorization = new IdentityServiceInvocationAuthorization("tenant", new("Tenant"));
        Assert.Throws<ServiceBindingValidationException>(() => ServiceRuntime.CreateDeferred(fixture.Runtime.Declaration,
            new Dictionary<string, Func<ServiceBinding>>(), authorization));
        var prepared = 0;
        var runtime = ServiceRuntime.CreateDeferred(fixture.Runtime.Declaration,
            new Dictionary<string, Func<ServiceBinding>> { ["search"] = () =>
            {
                prepared++;
                return new ServiceQueryBinding("wrong-operation", new("v1"), fixture.Binding.Compilation,
                    (_, _) => throw new InvalidOperationException());
            } }, authorization);
        Assert.Equal(0, prepared);
        Assert.Throws<ServiceBindingValidationException>(() => runtime.ValidateBindings());
        Assert.Throws<ServiceBindingValidationException>(() => runtime.ValidateBindings());
        Assert.Equal(1, prepared);
        Assert.Equal(0, fixture.Reader.Reads);
    }

    [Theory]
    [InlineData(true, false, 200)]
    [InlineData(false, false, 403)]
    [InlineData(true, true, 400)]
    [InlineData(true, false, 400, true)]
    public async Task QueryHttpUsesDeclaredScopeAndDoesNotReadOnAdmissionFailure(bool authorized, bool spoof, int status, bool failEvaluation = false)
    {
        var fixture = Create(failEvaluation);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(authorized ? Context("tenant-a") : OperationContext.Create());
        await using var app = builder.Build();
        var resolutions = 0;
        var projections = 0;
        var scope = new ApiScopePolicy("tenant", ApiScopeCardinality.Single, ApiScopeBinding.Header,
            ApiScopeAccess.RequireSelected, singleScopeParameterName: "X-Tenant-Id");
        app.MapServiceQuery<QueryRequest, QueryResponse>(fixture.Runtime.Declaration,
            _ => { resolutions++; return fixture.Runtime; }, "search",
            new("POST", "/notes/search", [], new(typeof(QueryRequest))), request => request.Spoof
                ? new Dictionary<QueryParameterId, ObservationValue> { [new("tenant")] = ObservationValue.FromString("tenant-b") }
                : new Dictionary<QueryParameterId, ObservationValue>(),
            outcome =>
            {
                projections++;
                return new(outcome.IsSuccessful,
                    outcome.Result?.QueryResults.SelectMany(result => result.Rows).Select(row => row.Value.GetProperty("Id").String!).ToArray() ?? [], outcome.Diagnostics.Select(diagnostic => diagnostic.Code).ToArray());
            }, (_, requirement) => requirement.Id, [scope]);
        Assert.Equal(0, resolutions);
        Assert.Equal(0, fixture.Resolutions);
        var endpoint = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().Single();
        Assert.Same(scope, endpoint.Metadata.GetMetadata<ApiScopePolicy>());
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new QueryRequest(spoof), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var http = new DefaultHttpContext { RequestServices = app.Services };
        http.TraceIdentifier = "query-request";
        http.Request.Method = "POST";
        http.Request.ContentType = "application/json";
        http.Request.ContentLength = bytes.Length;
        http.Request.Body = new MemoryStream(bytes);
        http.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(http);
        Assert.Equal(status, http.Response.StatusCode);
        Assert.Equal(1, resolutions);
        Assert.Equal(status == 200 || failEvaluation ? 1 : 0, projections);
        Assert.Equal(status == 200 ? 1 : 0, fixture.Reader.Reads);
        http.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(http.Response.Body);
        if (status == 200) Assert.Equal("a", Assert.Single(json.RootElement.GetProperty("ids").EnumerateArray()).GetString());
        if (failEvaluation) Assert.Equal("tests.query.preflight", Assert.Single(json.RootElement.GetProperty("diagnostics").EnumerateArray()).GetString());
        if (spoof) Assert.Equal("services.query.scopeOverride", json.RootElement.GetProperty("code").GetString());
    }

    sealed record QueryRequest(bool Spoof);
    sealed record QueryResponse(bool Successful, string[] Ids, string[] Diagnostics);

    [Fact]
    public async Task NativeQueryUsesTrustedScopeAndOnePhysicalReadPerInvocationInSharedPartition()
    {
        var fixture = Create();
        Assert.Equal(0, fixture.Resolutions);
        var first = await fixture.Runtime.EvaluateAsync(Context("tenant-a"), "search", new("first"), new Dictionary<QueryParameterId, ObservationValue>());
        var second = await fixture.Runtime.EvaluateAsync(Context("tenant-b"), "search", new("second"), new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(ApiResultKind.Success, first.Kind);
        Assert.Equal(ApiResultKind.Success, second.Kind);
        Assert.Equal("a", Assert.Single(Assert.Single(first.Outcome!.Result!.QueryResults).Rows).Value.GetProperty("Id").String);
        Assert.Equal("b", Assert.Single(Assert.Single(second.Outcome!.Result!.QueryResults).Rows).Value.GetProperty("Id").String);
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Equal(2, fixture.Resolutions);
        Assert.Same(first.Outcome.Evaluation.Compilation, second.Outcome.Evaluation.Compilation);
        Assert.Equal(new[] { "authorityAdmitted", "parametersBound", "queryEvaluated" }, first.Trace.Events.Select(e => e.Kind));
        Assert.DoesNotContain("tenant-a", ExecutionTraceJsonSerializer.Serialize(first.Trace));
    }

    [Fact]
    public async Task DenialAndScopeSpoofingNeverResolveOrReadTheBackend()
    {
        var fixture = Create();
        var denied = await fixture.Runtime.EvaluateAsync(OperationContext.Create(), "search", new("denied"), new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(ApiResultKind.Forbidden, denied.Kind);
        Assert.Null(denied.Outcome);
        var spoof = await fixture.Runtime.EvaluateAsync(Context("tenant-a"), "search", new("spoof"),
            new Dictionary<QueryParameterId, ObservationValue> { [new("tenant")] = ObservationValue.FromString("tenant-b") });
        Assert.Equal(ApiResultKind.ValidationFailed, spoof.Kind);
        Assert.Equal("services.query.scopeOverride", Assert.Single(spoof.Diagnostics).Code);
        Assert.Equal(0, fixture.Resolutions);
        Assert.Equal(0, fixture.Reader.Reads);
    }

    [Fact]
    public async Task UnknownParametersAndCancellationNeverReadTheBackend()
    {
        var fixture = Create();
        var unknown = await fixture.Runtime.EvaluateAsync(Context("tenant-a"), "search", new("unknown"),
            new Dictionary<QueryParameterId, ObservationValue> { [new("unknown")] = ObservationValue.FromString("value") });
        Assert.Equal(ApiResultKind.ValidationFailed, unknown.Kind);
        Assert.Equal("services.query.parameterUnknown", Assert.Single(unknown.Diagnostics).Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await fixture.Runtime.EvaluateAsync(
            Context("tenant-a").WithCancellationToken(cancelled.Token), "search", new("cancelled"), new Dictionary<QueryParameterId, ObservationValue>()));
        Assert.Equal(0, fixture.Resolutions);
        Assert.Equal(0, fixture.Reader.Reads);
    }

    [Fact]
    public void ScopeParameterAndExactQueryAreVerifiedAtBindingWithoutResolvingTheEvaluator()
    {
        var fixture = Create();
        var operation = new ServiceQueryOperation("search", fixture.Binding.Reference, new("missing"));
        var document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"), new([operation]),
            new(new("tests"), new("tests/services"), DocumentOrigin.Generated));
        var policy = new IdentityServiceInvocationAuthorization("tenant", new("Tenant"));
        var missing = Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(document, [fixture.Binding], policy));
        Assert.Equal("services.binding.scopeParameter", Assert.Single(missing.Validation.Diagnostics).Code);
        var reference = fixture.Binding.Reference;
        var wrong = new ServiceQueryOperation("search", new(reference.DefinitionId, new("other"), reference.Fingerprint), new("tenant"));
        document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"), new([wrong]), document.Metadata.Provenance);
        var inexact = Assert.Throws<ServiceBindingValidationException>(() => new ServiceRuntime(document, [fixture.Binding], policy));
        Assert.Equal("services.binding.inexact", Assert.Single(inexact.Validation.Diagnostics).Code);
        Assert.Equal(0, fixture.Resolutions);
    }

    static OperationContext Context(string tenant)
    {
        var actor = new PrincipalRef("alice", PrincipalKind.User);
        var scope = new ScopeRef(tenant, "tenant", PartitionKey: "shared");
        return OperationContext.Create().WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["notes.read"], "tests")]));
    }

    static Fixture Create(bool failEvaluation = false)
    {
        var shape = RelationQuery.Expression().Clr.Shape<Row>();
        var author = RelationQuery.Structural();
        var tenant = author.Parameter(new ScalarTypeRef(ScalarTypeKind.String), id: new("tenant"));
        var source = author.Source(shape.Id);
        var filtered = author.Filter(source.Node, Expr.Eq(source.Binding.Field("Tenant"), tenant.Expression));
        var rows = author.Rows(filtered, id: new("rows"));
        var query = author.BuildQuery(new("notes/by-tenant"), new("NotesByTenant"), [rows]);
        var compilation = new RelationQueryCompilationRequest(query.CreateDocument(), [shape.Document]);
        var canonicalShape = shape.Document.Graph.GetShape(shape.Id);
        var entityShape = new Shape(canonicalShape.Id, canonicalShape.Fields, canonicalShape.Constraints,
            canonicalShape.Annotations, role: ShapeRoles.Entity);
        var entity = new EntityDefinition(new("note"), new EntityShapeGraphBinding(shape.Id,
            ShapeGraphDocument.FromGraph(new(shape.Id.GraphId, [entityShape]))));
        var snapshots = new[] { new Row("a", "tenant-a", "Alpha"), new Row("b", "tenant-b", "Beta") }
            .Select(row => new EntitySnapshot(entity.CreateState(row.Id, row).Snapshot, "shared", new("seed/" + row.Id))).ToArray();
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "shared", snapshots);
        var registered = EntityRelationQuerySourceRegistration.InMemory(shape.Id, repository, RelationQueryLogicalPartitionIdentity.WholeSource);
        var reader = new CountingReader(registered.Reader);
        var catalog = new EntityRelationQuerySourceCatalog([new(shape.Id, registered.Source, reader)]);
        var evaluator = catalog.CreateEvaluator(new(new("tests/services/query"), "tests/v1", 100, 1000, 1000, 100, 100, 4));
        var fixture = new Fixture { Reader = reader };
        var binding = new ServiceQueryBinding("search", new("v1"), compilation, (_, scope) =>
        {
            Assert.Equal("shared", scope.ResolvePartitionKey());
            fixture.Resolutions++;
            return failEvaluation ? new FailedPreflightEvaluator() : evaluator;
        });
        var document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"),
            new([new ServiceQueryOperation("search", binding.Reference, tenant.Id, [new("notes.read")])]),
            new(new("tests"), new("tests/services"), DocumentOrigin.Generated));
        fixture.Binding = binding;
        fixture.Runtime = new(document, [binding], new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        return fixture;
    }

    sealed class FailedPreflightEvaluator : IRelationQueryEvaluator
    {
        public ValueTask<RelationQueryEvaluationOutcome> EvaluateAsync(RelationQueryEvaluation evaluation,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(new RelationQueryEvaluationOutcome(
                evaluation, RelationQueryStaticCompiler.Compile(evaluation.Compilation),
                diagnostics: [new("tests.query.preflight", DiagnosticSeverity.Error, "Synthetic attributable preflight failure.")]));
    }

    sealed record Row(string Id, string Tenant, string Text);
    sealed class Fixture
    {
        public ServiceRuntime Runtime { get; set; } = null!;
        public ServiceQueryBinding Binding { get; set; } = null!;
        public required CountingReader Reader { get; init; }
        public int Resolutions { get; set; }
    }
    sealed class CountingReader(IRelationQuerySourceReader inner) : IRelationQuerySourceReader
    {
        public int Reads { get; private set; }
        public RelationQuerySourceReaderDescriptor Descriptor => inner.Descriptor;
        public ValueTask<RelationQuerySourceReadResult> ReadAsync(RelationQuerySourceReadRequest request, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadAsync(request, cancellationToken);
        }
    }
}
