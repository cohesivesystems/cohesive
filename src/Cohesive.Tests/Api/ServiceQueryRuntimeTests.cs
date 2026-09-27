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

    static Fixture Create()
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
            return evaluator;
        });
        var document = ServiceDefinitionDocuments.Create(new("notes"), new("v1"),
            new([new ServiceQueryOperation("search", binding.Reference, tenant.Id, [new("notes.read")])]),
            new(new("tests"), new("tests/services"), DocumentOrigin.Generated));
        fixture.Binding = binding;
        fixture.Runtime = new(document, [binding], new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
        return fixture;
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
