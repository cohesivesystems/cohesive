using System.Diagnostics;
using System.Collections.Concurrent;
using Cohesive.Execution;
using Cohesive.Model.Authoring;
using Cohesive.Model.Serialization;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Cohesive.Transitions.Authoring;
using Cohesive.Transitions.IR;
using Cohesive.Transitions.Model;

namespace Cohesive.Tests.ExecutionKernel;

public sealed class EntityTransitionCapturedTokenTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CapturedToken_RejectsLaterStateAndPreservesExactReceiptReplay(bool ignoreReadPrecondition, bool commitDuringRead)
    {
        var entity = ObjectEntityDefinition.For<Document>(new("document"));
        var provenance = new ExecutionProvenance(new("tests", "1"), new("captured-token"), DocumentOrigin.Generated);
        var compiled = TransitionAuthoring.Create<Document, Update, bool>(entity.Shape,
            new(new("document/update"), new("1"), new("body"), provenance), t => t
                .Set(new("text"), state => state.Text, (_, input) => input.Text)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true)).Compile();
        Assert.True(compiled.IsSuccessful, string.Join("; ", compiled.Validation.Diagnostics));
        var plan = compiled.Plan!;
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var context = OperationContext.Create();
        var repository = new InMemoryEntityOutboxRepository(entity, _ => "shared");
        var initial = await repository.Upsert(context, new(entity.CreateState("one", new Document("one", "before")).Snapshot));
        var boundRepository = new ReadBoundaryRepository(repository, ignoreReadPrecondition);
        var binding = new ProcessTransitionOperationBinding(plan, boundRepository, contracts!,
            expectedConcurrencyTokenField: nameof(Update.Token));
        Assert.Throws<ArgumentException>(() => new ProcessTransitionOperationBinding(plan, repository, contracts!,
            expectedConcurrencyTokenField: "Missing"));
        var adapter = new EntityTransitionProcessOperationAdapter(_ => binding);
        var invocation = new ProcessTransitionInvocation(
            ProcessDurabilityTestFixture.DefinitionReference("process/update", '1'), plan.DefinitionReference,
            ProcessDurabilityTestFixture.StringValue("one"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Update(initial.ConcurrencyToken.Value, "prepared"))),
            new(new("instance"), new("attempt")), new("activation"), new("token"), new("node"), 0,
            DateTimeOffset.UnixEpoch, new(new("authority", "tenant"), new("correlation"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), provenance));
        if (commitDuringRead)
        {
            var racingAdapter = new EntityTransitionProcessOperationAdapter(_ => new(plan, repository, contracts!,
                expectedConcurrencyTokenField: nameof(Update.Token)));
            boundRepository.BeforeRead = async () =>
                Assert.True((await racingAdapter.ExecuteAsync(context, invocation)).IsSuccessful);
        }
        var committed = await adapter.ExecuteAsync(context, invocation);
        Assert.True(committed.IsSuccessful, committed.Failure?.ToString());
        var after = (await repository.TryGet(context, "one"))!;
        Assert.Equal("prepared", after.Entity.Observation.GetField("Text").GetString());
        Assert.NotEqual(initial.ConcurrencyToken, after.ConcurrencyToken);
        var replayed = await adapter.ExecuteAsync(context, invocation);
        Assert.True(replayed.IsSuccessful, replayed.Failure?.ToString());
        Assert.Equal(committed.ReceiptReference, replayed.ReceiptReference);
        var options = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        var transported = System.Text.Json.JsonSerializer.Deserialize<ProcessOperationResult>(
            System.Text.Json.JsonSerializer.Serialize(committed, options), options)!;
        var reference = EntityTransitionReceiptReferences.Read(transported.ReceiptReference!);
        var resolved = await repository.ResolveTransitionOperation(context, reference);
        Assert.Equal(after, resolved.Receipt!.Entity);
        Assert.Equal(committed.Value, transported.Value);
        Assert.Equal(after.ConcurrencyToken, (await repository.TryGet(context, "one"))!.ConcurrencyToken);

        var conflictingReplay = await adapter.ExecuteAsync(context, invocation with
        {
            Input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Update(after.ConcurrencyToken.Value, "prepared")))
        });
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.IdentityConflict, conflictingReplay.Failure?.Code);

        var stale = await adapter.ExecuteAsync(context, invocation with { Occurrence = 1 });
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.SubjectChanged, stale.Failure?.Code);
        Assert.Equal(after.ConcurrencyToken, (await repository.TryGet(context, "one"))!.ConcurrencyToken);
        var malformed = await adapter.ExecuteAsync(context, invocation with
        {
            Occurrence = 2,
            Input = PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Update("", "must not commit")))
        });
        Assert.Equal(ProcessTransitionOperationAdapterDiagnosticCodes.CapturedConcurrencyTokenInvalid, malformed.Failure?.Code);
    }

    internal static async Task<(EntityTransitionProcessOperationAdapter Adapter, OperationContext Context,
        ProcessTransitionInvocation Invocation, string Token, Func<EntityTransitionProcessOperationAdapter> CreateAdapter)> CreateFailureFixture()
    {
        var entity = ObjectEntityDefinition.For<Document>(new("document"));
        var provenance = new ExecutionProvenance(new("tests", "1"), new("safe-conflict"), DocumentOrigin.Generated);
        var plan = TransitionAuthoring.Create<Document, Update, bool>(entity.Shape,
            new(new("document/update"), new("1"), new("body"), provenance), body => body
                .Set(new("text"), state => state.Text, (_, input) => input.Text)
                .Return(new("applied"), TransitionOutcomeDisposition.Applied, true)).Compile().Plan!;
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var context = OperationContext.Create(traceContext: new ActivityContext(ActivityTraceId.CreateRandom(), ActivitySpanId.CreateRandom(), ActivityTraceFlags.Recorded));
        var native = new InMemoryEntityOutboxRepository(entity, _ => "shared");
        var initial = await native.Upsert(context, new(entity.CreateState("private-id", new Document("private-id", "before")).Snapshot));
        var repository = new ReadBoundaryRepository(native, false) { FailCommit = true };
        EntityTransitionProcessOperationAdapter CreateAdapter() => new(_ => new(plan, repository, contracts!));
        var adapter = CreateAdapter();
        var invocation = new ProcessTransitionInvocation(
            ProcessDurabilityTestFixture.DefinitionReference("process/update", '1'), plan.DefinitionReference,
            ProcessDurabilityTestFixture.StringValue("private-id"),
            PortableValue.Concrete(plan.Definition.Input, ObservationValue.FromObject(new Update(initial.ConcurrencyToken.Value, "prepared"))),
            new(new("instance"), new("attempt")), new("activation"), new("token"), new("node"), 0,
            DateTimeOffset.UnixEpoch, new(new("authority", "tenant"), new("correlation"),
                new(InteractionDurabilityDemand.Durable, InteractionVisibilityDemand.AfterOriginCommit), provenance));
        return (adapter, context, invocation, initial.ConcurrencyToken.Value, CreateAdapter);
    }

    [Fact]
    public async Task Native_commit_details_do_not_enter_portable_process_failure()
    {
        var fixture = await CreateFailureFixture();
        var diagnostics = new ConcurrentQueue<EntityTransitionFailureDiagnostic>();
        using var subscription = fixture.Adapter.SubscribeTransitionFailures(diagnostics.Enqueue);
        var result = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        Assert.Equal(EntityTransitionOperationDiagnosticCodes.ConcurrencyConflict, result.Failure?.Code);
        Assert.Equal("/write/expectedConcurrencyToken", result.Failure?.Location);
        Assert.Equal("The entity operation could not be committed.", result.Failure?.Message);
        var json = System.Text.Json.JsonSerializer.Serialize(result, ProcessDurableCheckpointJsonSerializer.CreateOptions());
        Assert.DoesNotContain("private-provider-detail", json);
        Assert.DoesNotContain("private-id", json);
        Assert.DoesNotContain(fixture.Token, json);
        var recorded = Assert.Single(diagnostics);
        var detail = Assert.Single(recorded.Result.Diagnostics);
        Assert.Equal(fixture.Context.TraceContext, recorded.TraceContext);
        Assert.Contains("private-provider-detail", detail.Message);
        Assert.Contains("private-id", detail.Message);
        Assert.Contains(fixture.Token, detail.Message);
    }

    [Fact]
    public async Task Subscriptions_are_scoped_to_adapter_even_with_same_repository_and_trace()
    {
        var fixture = await CreateFailureFixture();
        var other = fixture.CreateAdapter();
        var firstCount = 0;
        var otherCount = 0;
        using var firstSubscription = fixture.Adapter.SubscribeTransitionFailures(_ => firstCount++);
        using var otherSubscription = other.SubscribeTransitionFailures(_ => otherCount++);
        await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        Assert.Equal(1, firstCount);
        Assert.Equal(0, otherCount);
        await other.ExecuteAsync(fixture.Context, fixture.Invocation);
        Assert.Equal(1, firstCount);
        Assert.Equal(1, otherCount);
    }

    [Fact]
    public async Task Disposing_subscription_twice_prevents_future_delivery()
    {
        var fixture = await CreateFailureFixture();
        var delivered = 0;
        var subscription = fixture.Adapter.SubscribeTransitionFailures(_ => delivered++);
        await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        subscription.Dispose();
        subscription.Dispose();
        await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public async Task Throwing_observer_does_not_change_result_or_skip_other_observers()
    {
        var fixture = await CreateFailureFixture();
        var expected = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        using var broken = fixture.Adapter.SubscribeTransitionFailures(_ => throw new InvalidOperationException("private sink error"));
        var delivered = 0;
        using var healthy = fixture.Adapter.SubscribeTransitionFailures(_ => delivered++);
        var actual = await fixture.Adapter.ExecuteAsync(fixture.Context, fixture.Invocation);
        Assert.Equal(expected.Failure, actual.Failure);
        Assert.Equal(1, delivered);
    }

    [Fact]
    public void Null_observer_is_rejected() =>
        Assert.Throws<ArgumentNullException>(() => new EntityTransitionProcessOperationAdapter(_ => null)
            .SubscribeTransitionFailures(null!));

    sealed record Document(string Id, string Text);
    sealed record Update(string Token, string Text);

    sealed class ReadBoundaryRepository(InMemoryEntityOutboxRepository inner, bool ignoreReadPrecondition) : IEntityTransitionOperationRepository
    {
        public EntityDefinition EntityDefinition => inner.EntityDefinition;
        public string? IdentityField => inner.IdentityField;
        public EntityTransitionOperationCapabilities TransitionOperationCapabilities => inner.TransitionOperationCapabilities;
        public bool FailCommit { get; set; }
        public Func<Task>? BeforeRead { get; set; }
        public async Task<EntitySnapshot?> TryGet(OperationContext context, string id, EntityReadOptions? options = null)
        {
            if (BeforeRead is { } action)
            {
                BeforeRead = null;
                await action();
            }
            return await inner.TryGet(context, id, ignoreReadPrecondition ? EntityReadOptions.Full : options);
        }
        public Task<EntitySnapshot> Upsert(OperationContext context, EntityWriteRequest write) => inner.Upsert(context, write);
        public Task<EntityTransitionOperationResult> TryGetTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => inner.TryGetTransitionOperation(context, request);
        public Task<EntityTransitionOperationResult> TryGetCreationTransitionOperation(OperationContext context, EntityTransitionOperationRequest request) => inner.TryGetCreationTransitionOperation(context, request);
        public Task<EntityTransitionOperationResult> CommitTransitionOperation(OperationContext context, EntityTransitionOperationCommit commit) => FailCommit
            ? Task.FromResult(EntityTransitionCommitProtocol.Conflict(commit, "private-provider-detail"))
            : inner.CommitTransitionOperation(context, commit);
    }
}
