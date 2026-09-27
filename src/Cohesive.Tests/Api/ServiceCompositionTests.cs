using Cohesive.Api;
using Cohesive.Api.Execution.Services;
using Cohesive.Api.Services;
using Cohesive.Execution;
using Cohesive.Identity;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Authoring;
using Cohesive.Processes.Compilation;
using Cohesive.Processes.Execution;
using Cohesive.Processes.IR;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Authoring;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Execution;
using Cohesive.Relations.IR;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Compilation;
using Cohesive.Transitions.IR;

namespace Cohesive.Tests.Api;

/// <summary>A non-review domain qualifying all service operation families against native execution boundaries.</summary>
public sealed class ServiceCompositionTests
{
    [Fact]
    public async Task PublishingService_RetainsAcquisitionAndComputationAcrossEntityHandoffCrash()
    {
        var fixture = await Fixture.Create();
        var context = Context();
        var before = await fixture.Repository.TryGet(context, "a", EntityReadOptions.Full);
        var revised = await fixture.Service.InvokeAsync(context, "revise", "a", before!.ConcurrencyToken,
            new("direct/revise"), PortableValue.Concrete(fixture.Transition.Definition.Input,
                ObservationValue.FromObject(new Update("a", "tenant-a", "revised", 0))));
        Assert.Equal(ApiResultKind.Success, revised.Kind);

        var visible = await fixture.Service.EvaluateAsync(context, "search", new("direct/search"), new Dictionary<QueryParameterId, ObservationValue>());
        Assert.Equal(new[] { "a", "b" }, visible.Outcome!.Result!.QueryResults.Single().Rows
            .Select(row => row.Value.GetProperty("Id").String).Order());
        Assert.Equal(1, fixture.Reader.Reads);

        var admitted = await fixture.Service.StartAsync(context, "publish", fixture.StartRequest());
        Assert.Equal(ApiResultKind.Success, admitted.Kind);
        var continuation = admitted.Outcome!.Admission!.Continuation;
        var acquire = fixture.Activation("acquire", ProcessActivationCause.Start);
        var captured = await fixture.Runtime().ActivateAsync(context, fixture.Plan, continuation, acquire);
        Assert.Equal(ProcessActivationDisposition.DurableCut, captured.Decision!.Disposition);
        Assert.Equal(2, captured.Snapshot!.Checkpoint.Operations.Length);
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Equal(1, fixture.Computations);

        var publish = fixture.Activation("publish", ProcessActivationCause.Continue);
        await Assert.ThrowsAsync<HandoffCrash>(() => fixture.Runtime(crashAfterFirstCommit: true)
            .ActivateAsync(context, fixture.Plan, continuation, publish));
        var interrupted = (await fixture.Store.LoadAsync(context, continuation.ProcessInstanceId))!.Checkpoint;
        Assert.Single(interrupted.Activations); // Only the acquisition cut committed.
        Assert.Equal(2, interrupted.Operations.Length);
        Assert.Equal(2, (await fixture.Repository.TryGet(context, "a", EntityReadOptions.Full))!.Entity.Version);
        Assert.Equal(0, (await fixture.Repository.TryGet(context, "b", EntityReadOptions.Full))!.Entity.Version);

        // Recreate the runtime and host. Only durable evidence may bridge the interruption.
        var recovered = await fixture.Runtime().ActivateAsync(context, fixture.Plan, continuation, publish);
        Assert.Equal(ProcessActivationDisposition.Completed, recovered.Decision!.Disposition);
        Assert.True(Assert.IsType<PortableValue>(recovered.Decision.State.Terminal.Detail!.Value).Value!.Value.Bool);
        Assert.Equal(4, recovered.Snapshot!.Checkpoint.Operations.Length);
        Assert.Equal(2, recovered.Snapshot.Checkpoint.Activations.Length);
        var first = (await fixture.Repository.TryGet(context, "a", EntityReadOptions.Full))!;
        var second = (await fixture.Repository.TryGet(context, "b", EntityReadOptions.Full))!;
        Assert.Equal("REVISED", first.Entity.Observation.GetField("Text").GetString());
        Assert.Equal("BETA", second.Entity.Observation.GetField("Text").GetString());
        Assert.Equal(2, first.Entity.Version);
        Assert.Equal(1, second.Entity.Version);
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Equal(1, fixture.Computations);
        Assert.Equal("private", (await fixture.Repository.TryGet(context, "foreign", EntityReadOptions.Full))!
            .Entity.Observation.GetField("Text").GetString());

        var replay = await fixture.Runtime().ActivateAsync(context, fixture.Plan, continuation, publish);
        Assert.Equal(ProcessDurableRuntimeDisposition.Replayed, replay.Disposition);
        Assert.Equal(2, fixture.Reader.Reads);
        Assert.Equal(1, fixture.Computations);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("b")]
    public async Task PublishingService_RejectsNewerEditsWithoutClaimingCrossEntityRollback(string editedId)
    {
        var fixture = await Fixture.Create();
        var context = Context();
        var admitted = await fixture.Service.StartAsync(context, "publish", fixture.StartRequest());
        var continuation = admitted.Outcome!.Admission!.Continuation;
        var cut = await fixture.Runtime().ActivateAsync(context, fixture.Plan, continuation,
            fixture.Activation("acquire", ProcessActivationCause.Start));
        Assert.Equal(ProcessActivationDisposition.DurableCut, cut.Decision!.Disposition);
        var current = (await fixture.Repository.TryGet(context, editedId, EntityReadOptions.Full))!;
        var changed = await fixture.Service.InvokeAsync(context, "revise", editedId, current.ConcurrencyToken,
            new("concurrent/edit"), PortableValue.Concrete(fixture.Transition.Definition.Input,
                ObservationValue.FromObject(new Update(editedId, "tenant-a", "newer edit", 0))));
        Assert.Equal(ApiResultKind.Success, changed.Kind);

        var result = await fixture.Runtime().ActivateAsync(context, fixture.Plan, continuation,
            fixture.Activation("publish", ProcessActivationCause.Continue));

        Assert.Equal(ProcessActivationDisposition.Completed, result.Decision!.Disposition);
        Assert.False(Assert.IsType<PortableValue>(result.Decision.State.Terminal.Detail!.Value).Value!.Value.Bool);
        var edited = (await fixture.Repository.TryGet(context, editedId, EntityReadOptions.Full))!;
        Assert.Equal("newer edit", edited.Entity.Observation.GetField("Text").GetString());
        Assert.Equal(1, edited.Entity.Version);
        var otherId = editedId == "a" ? "b" : "a";
        var other = (await fixture.Repository.TryGet(context, otherId, EntityReadOptions.Full))!;
        // A first rejection stops the batch. A later rejection retains the earlier committed update.
        Assert.Equal(editedId == "a" ? 0 : 1, other.Entity.Version);
        Assert.Equal(editedId == "a" ? "beta" : "ALPHA", other.Entity.Observation.GetField("Text").GetString());
        Assert.Equal(editedId == "a" ? 3 : 4, result.Snapshot!.Checkpoint.Operations.Length);
        Assert.Equal(1, fixture.Reader.Reads);
        Assert.Equal(1, fixture.Computations);
    }

    static readonly ExecutionProvenance Provenance = new(new("tests/publishing", "1"),
        new("tests/services/composition"), DocumentOrigin.Generated);
    static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    static OperationContext Context()
    {
        var actor = new PrincipalRef("editor", PrincipalKind.User);
        var scope = new ScopeRef("tenant-a", "tenant", PartitionKey: "shared");
        return OperationContext.Create(timeProvider: new FixedClock()).WithIdentityContext(new IdentityContext(actor,
            EffectiveScope: new([scope], ScopeSelectionMode.Single, ScopeSelectionSource.Ambient),
            Grants: [new(actor, scope, ["documents.read", "documents.revise", "documents.publish"], "tests")]));
    }

    sealed class Fixture
    {
        public required InMemoryEntityOutboxRepository Repository { get; init; }
        public required CountingReader Reader { get; init; }
        public required CompiledTransitionPlan Transition { get; init; }
        public required CompiledProcessPlan Plan { get; init; }
        public required InteractionContractCatalog Contracts { get; init; }
        public required ProcessRelationHandlerCatalog Handlers { get; init; }
        public InMemoryProcessDurableStore Store { get; } = new();
        public ServiceRuntime Service { get; set; } = null!;
        public int Computations { get; set; }

        public ProcessDurableRuntime Runtime(bool crashAfterFirstCommit = false)
        {
            var adapter = new EntityTransitionProcessOperationAdapter(invocation =>
                invocation.Definition == Transition.DefinitionReference
                && invocation.Input.Value!.Value.GetProperty("Tenant").String == invocation.Context.AuthorityScope.Tenant
                    ? new(Transition, Repository, Contracts) : null);
            var crashPending = crashAfterFirstCommit;
            var host = new RegisteredAsyncProcessReferenceHost(Handlers, async (context, invocation) =>
            {
                var result = await adapter.ExecuteAsync(context, invocation);
                if (crashPending)
                {
                    crashPending = false;
                    throw new HandoffCrash();
                }
                return result;
            });
            return new(Store, host, new("worker/publishing", TimeSpan.FromMinutes(5)));
        }

        public ProcessStartRequest StartRequest() => new(ProcessStartRequest.CurrentSchemaVersion, Plan.DefinitionReference,
            new(new("start/publishing"), new("start/publishing"), new("publishing/1"),
                new("untrusted", new("untrusted", "tenant-b"), "untrusted"), Now, Provenance),
            new(new("publishing/1"), new("attempt/1")),
            PortableValue.Concrete(Plan.Definition.Input, ObservationValue.FromString("batch")));

        public ProcessActivation Activation(string id, ProcessActivationCause cause) => new(new(id), cause, Now,
            new(new("documents", "tenant-a"), new("publishing/1"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), Plan.Document.Metadata.Provenance));

        public static async Task<Fixture> Create()
        {
            var entity = ObjectEntityDefinition.For<Document>(new("document"));
            var authored = TransitionAuthoring.Create<Document, Update, bool>(entity.Shape,
                new(new("documents/revise"), new("1"), new("body"), Provenance), transition => transition
                    .Requires(new("exact-source"), (state, input) => state.Id == input.Id
                        && state.Tenant == input.Tenant && state.Revision == input.Revision, (_, _) => false)
                    .Set(new("text"), state => state.Text, (_, input) => input.Text)
                    .Set(new("revision"), state => state.Revision, (state, _) => state.Revision + 1)
                    .Return(new("applied"), TransitionOutcomeDisposition.Applied, true));
            var compiledTransition = authored.Compile();
            Assert.True(compiledTransition.IsSuccessful, Format(compiledTransition.Validation));
            var transition = compiledTransition.Plan!;
            var repository = new InMemoryEntityOutboxRepository(entity, _ => "shared");
            foreach (var row in new[] { new Document("a", "tenant-a", "alpha", 0), new("b", "tenant-a", "beta", 0), new("foreign", "tenant-b", "private", 0) })
                await repository.Upsert(Context(), new(entity.CreateState(row.Id, row).Snapshot));

            var query = RelationQuery.Structural();
            var tenant = query.Parameter(new ScalarTypeRef(ScalarTypeKind.String), id: new("tenant"));
            var source = query.Source(entity.StateShape.QualifiedId);
            var filtered = query.Filter(source.Node, Expr.Eq(source.Binding.Field("Tenant"), tenant.Expression));
            var definition = query.BuildQuery(new("documents/by-tenant"), new("DocumentsByTenant"), [query.Rows(filtered, id: new("documents"))]);
            var compilation = new RelationQueryCompilationRequest(definition.CreateDocument(), [ShapeGraphDocument.FromGraph(entity.StateShape.Graph)]);
            var registered = EntityRelationQuerySourceRegistration.InMemory(entity.StateShape.QualifiedId, repository,
                RelationQueryLogicalPartitionIdentity.WholeSource);
            var reader = new CountingReader(registered.Reader);
            var sources = new EntityRelationQuerySourceCatalog([new(entity.StateShape.QualifiedId, registered.Source, reader)]);
            var evaluator = sources.CreateEvaluator(new(new("tests/publishing"), "tests/v1", 100, 1000, 1000, 100, 100, 4));
            var queryBinding = new ServiceQueryBinding("search", new("1"), compilation, (_, _) => evaluator);
            var acquire = HostedQuery<string, Pair>.Create(new("documents/acquire-pair"), new("1"),
                new("tests.documents.acquire-pair", "1"), new Configuration("pair"), Provenance,
                [new("source", queryBinding.Reference)]);
            var compute = HostedQuery<Pair, Pair>.Create(new("documents/prepare-pair"), new("1"),
                new("tests.documents.prepare-pair", "1"), new Configuration("uppercase"), Provenance,
                evaluationSemantics: HostedQueryEvaluationSemantics.DeterministicComputation);
            Assert.True(acquire.IsValid, Format(acquire.Validation));
            Assert.True(compute.IsValid, Format(compute.Validation));
            Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
            var boolContract = transition.Definition.Outcome;
            var prepared = new ValueBindingId("prepared");
            var acquired = new ValueBindingId("acquired");
            var firstResult = new ValueBindingId("first-result");
            var secondResult = new ValueBindingId("second-result");
            var process = ProcessDefinitionDocuments.Create(new("documents/publish-pair"), new("1"),
                new(acquire.InputContract, boolContract, new("acquire"), [
                    new EvaluateRelationProcessNode(new("acquire"), acquire.Reference, Expr.BoundValue(ProcessBindingIds.Input),
                        new(Edge("acquire", "compute"), new(acquired, acquire.ResultContract))),
                    new EvaluateRelationProcessNode(new("compute"), compute.Reference, Expr.BoundValue(acquired),
                        new(Edge("compute", "cut"), new(prepared, compute.ResultContract))),
                    new DurableCutProcessNode(new("cut"), Edge("cut", "first")),
                    new InvokeTransitionProcessNode(new("first"), transition.DefinitionReference,
                        Expr.Field(prepared, "First.Id"), Expr.Field(prepared, "First"),
                        new(Edge("first", "check-first"), new(firstResult, boolContract))),
                    new ChoiceProcessNode(new("check-first"), CaseSelection.OrderedFirstMatch, BranchCompleteness.Fallback,
                        [new(new("first-applied"), Expr.BoundValue(firstResult), Edge("check-first", "second"))],
                        new(new("first-rejected"), Edge("check-first", "rejected"))),
                    new ReturnProcessNode(new("rejected"), Expr.Const(false)),
                    new InvokeTransitionProcessNode(new("second"), transition.DefinitionReference,
                        Expr.Field(prepared, "Second.Id"), Expr.Field(prepared, "Second"),
                        new(Edge("second", "return"), new(secondResult, boolContract))),
                    new ReturnProcessNode(new("return"), Expr.And(Expr.BoundValue(firstResult), Expr.BoundValue(secondResult)))
                ], ProcessRecoveryPolicy.ContinueAttempt), Provenance);
            var compiled = ProcessStaticCompiler.Compile(process, new ProcessDefinitionValidationContext([
                acquire.CreateProcessDefinitionLink(), compute.CreateProcessDefinitionLink(),
                new(transition.DefinitionReference, ProcessDefinitionLinkKind.Transition, transition.Definition.Input, boolContract)],
                interactionContracts: contracts));
            Assert.True(compiled.IsSuccessful, Format(compiled.Validation));
            Fixture fixture = null!;
            var materialize = ObservationMaterializer.For<Document>(entity.StateShape).Compile();
            var handlers = new ProcessRelationHandlerCatalog([
                ProcessRelationHandlerRegistration.Create(acquire, async (context, evaluation, _) =>
                {
                    // This domain projection selects a pair; filtering remains in the canonical query.
                    var request = compilation.Evaluate(new(evaluation.Activation.Value + "/" + evaluation.Node.Value))
                        .Set(tenant.Id, ObservationValue.FromString(evaluation.Context.AuthorityScope.Tenant), "process/authority").Build();
                    var outcome = await evaluator.EvaluateAsync(request, context.CancellationToken);
                    var documents = outcome.Result!.QueryResults.Single().Rows
                        .Select(row => materialize.Materialize(Observation.Create(entity.StateShape, row.Value)))
                        .OrderBy(row => row.Id, StringComparer.Ordinal).ToArray();
                    Assert.Equal(2, documents.Length);
                    return new Pair(ToUpdate(documents[0]), ToUpdate(documents[1]));
                }),
                ProcessRelationHandlerRegistration.CreateDeterministic(compute, compute.Implementation, (pair, _, cancellation) =>
                {
                    cancellation.ThrowIfCancellationRequested();
                    fixture.Computations++;
                    return new Pair(pair.First with { Text = pair.First.Text.ToUpperInvariant() }, pair.Second with { Text = pair.Second.Text.ToUpperInvariant() });
                })]);
            fixture = new() { Repository = repository, Reader = reader, Transition = transition, Plan = compiled.Plan!, Contracts = contracts!, Handlers = handlers };
            var declaration = ServiceDefinitionDocuments.Create(new("documents"), new("1"), new([
                new ServiceTransitionOperation("revise", entity.StateShape.QualifiedId, transition.DefinitionReference, [new("documents.revise")]),
                new ServiceQueryOperation("search", queryBinding.Reference, tenant.Id, [new("documents.read")]),
                new ServiceProcessOperation("publish", fixture.Plan.DefinitionReference, [new("documents.publish")])]), Provenance);
            var processBinding = new ServiceProcessBinding("publish", fixture.Plan, "documents", async (context, request, invocation) =>
            {
                var decision = new ProcessStartReferenceEvaluator().Evaluate(request, new(), invocation.ObservedAtUtc);
                Assert.True(decision.RequiresPersistence);
                var initialized = await fixture.Runtime().InitializeAsync(context, fixture.Plan, decision.Receipt!);
                Assert.Equal(ProcessDurableRuntimeDisposition.Applied, initialized.Disposition);
                return decision.Result;
            });
            fixture.Service = new(declaration, [new ServiceTransitionBinding("revise", transition, entity, _ => repository), queryBinding, processBinding],
                new IdentityServiceInvocationAuthorization("tenant", new("Tenant")));
            return fixture;
        }
    }

    static Update ToUpdate(Document row) => new(row.Id, row.Tenant, row.Text, row.Revision);
    static ProcessEdge Edge(string from, string to) => new(new(from + "/" + to), new(to));
    static string Format(DocumentValidationResult validation) => string.Join("; ", validation.Diagnostics.Select(d => d.Code + ": " + d.Message));
    public sealed record Document(string Id, string Tenant, string Text, long Revision);
    public sealed record Update(string Id, string Tenant, string Text, long Revision);
    public sealed record Pair(Update First, Update Second);
    public sealed record Configuration(string Policy);
    sealed class HandoffCrash : Exception;
    sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
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
