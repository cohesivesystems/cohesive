using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cohesive.Adapters.AspNet.Machines;
using Cohesive.Adapters.Cosmos;
using Cohesive.Examples.TenderAcceptance;
using Cohesive.Execution;
using Cohesive.Processes.Execution;
using Cohesive.Storage;
using Cohesive.Storage.Processes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Machines;

public sealed class MachineReactionHostTests
{
    [Theory]
    [InlineData(true, MachineReactionStatus.Completed)]
    [InlineData(false, MachineReactionStatus.Rejected)]
    public async Task Native_host_replays_exact_receipt_and_reauthorizes(bool allowed, MachineReactionStatus expected)
    {
        var f = await Fixture.Create(allowed);
        var first = Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(expected, first.Status);
        Assert.Equal(first, Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt)));
        f.SourceAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host().DeliverAsync(f.Context, f.Receipt));
        f.SourceAllowed = true;
        f.TargetAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Host().DeliverAsync(f.Context, f.Receipt));
    }

    [Fact]
    public async Task Lost_acknowledgement_reuses_committed_operation_after_worker_restart()
    {
        var f = await Fixture.Create();
        var target = new LostResponse(f.Target);
        await Assert.ThrowsAsync<TimeoutException>(() => f.Host(target).DeliverAsync(f.Context, f.Receipt));
        var before = await f.Repository.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test"));
        Assert.Equal(MachineReactionStatus.Completed, Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt)).Status);
        var after = await f.Repository.TryGet(f.Context, "load-1", EntityReadOptions.Full.WithPartitionKey("test"));
        Assert.Equal(before!.ConcurrencyToken, after!.ConcurrencyToken);
    }

    [Theory]
    [InlineData(true, false, MachineReactionStatus.Completed)]
    [InlineData(false, false, MachineReactionStatus.Rejected)]
    [InlineData(true, true, MachineReactionStatus.Completed)]
    public async Task Http_target_roundtrips_exact_receipts_and_reauthorizes_lookup(bool allowed, bool loseResponse, MachineReactionStatus expected)
    {
        var f = await Fixture.Create(allowed);
        await using var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapMachineReactionTarget("/reaction", f.Target, _ => f.Context);
        await app.StartAsync();
        using var client = new HttpClient(new LoseHttpResponseHandler(loseResponse));
        var remote = new HttpMachineReactionTarget(f.Target.Transition, f.Target.Authority, client, new(app.Urls.Single() + "/reaction"));
        if (loseResponse)
            await Assert.ThrowsAsync<HttpRequestException>(() => f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        var first = Assert.Single(await f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(expected, first.Status);
        var replay = Assert.Single(await f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(first.Operation, replay.Operation);
        Assert.Equal(first.Receipt!.Commit.Fingerprint, replay.Receipt!.Commit.Fingerprint);
        Assert.Equal(first.Receipt.Entity.ConcurrencyToken, replay.Receipt.Entity.ConcurrencyToken);
        f.TargetAllowed = false;
        var denied = await Assert.ThrowsAsync<HttpRequestException>(() => f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Http_acceptance_cannot_complete_a_reaction()
    {
        var f = await Fixture.Create();
        using var client = new HttpClient(new AcceptedHandler());
        var remote = new HttpMachineReactionTarget(f.Target.Transition, f.Target.Authority, client, new("https://example.invalid/reaction"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Completed, Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt)).Status);
    }

    [Fact]
    public async Task Http_receipt_for_another_operation_is_not_commitment_evidence()
    {
        var f = await Fixture.Create();
        using var client = new HttpClient(new WrongReceiptHandler(f.Receipt));
        var remote = new HttpMachineReactionTarget(f.Target.Transition, f.Target.Authority, client, new("https://example.invalid/reaction"));
        var error = await Assert.ThrowsAsync<MachineReactionProtocolException>(() => f.Host(remote).DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionProtocolException.ReceiptMismatch, error.Code);
        Assert.Equal(MachineReactionStatus.Completed, Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt)).Status);
    }

    [Fact]
    public async Task Concurrent_delivery_converges_on_one_operation()
    {
        var f = await Fixture.Create();
        async Task Deliver()
        {
            try { await f.Host().DeliverAsync(f.Context, f.Receipt); }
            catch (InvalidOperationException) { /* A held lease requires source replay. */ }
        }
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Deliver)));
        var first = Assert.Single(await f.Host().DeliverAsync(f.Context, f.Receipt));
        Assert.Equal(MachineReactionStatus.Completed, first.Status);
        Assert.Equal("mem:2", first.Receipt!.Entity.ConcurrencyToken!.Value);
    }

    [Fact]
    public async Task Registration_rejects_another_target_revision()
    {
        var f = await Fixture.Create();
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var wrong = MachineReactionTarget.Local(new(f.Model.Confirm.Compile().Plan!, f.Repository, contracts!),
            f.Target.Authority, (_, _) => ValueTask.FromResult(true));
        Assert.Throws<ArgumentException>(() => f.Host(wrong));
    }

    [Fact]
    public async Task Nonmatching_movement_is_ignored_by_preparation_and_host_before_authorization()
    {
        var f = await Fixture.Create();
        var occurrence = Assert.Single(EntityMachineOccurrence.FromReceipt(f.Receipt));
        Assert.Null(MachineReactionInvocation.Prepare(f.Model.ReceiptConfirmation, occurrence,
            new("worker", f.Target.Authority, "test"), DateTimeOffset.UtcNow));
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var target = MachineReactionTarget.Local(new(f.Model.Confirm.Compile().Plan!, f.Repository, contracts!),
            f.Target.Authority, (_, _) => throw new InvalidOperationException("Unrelated target must not authorize"));
        var host = new MachineReactionHost(f.Model.ReceiptConfirmation, f.Store, target,
            (_, _) => throw new InvalidOperationException("Unrelated source must not authorize"), new("worker", TimeSpan.FromMinutes(5)));
        Assert.Empty(await host.DeliverAsync(f.Context, f.Receipt));
    }

    [Fact]
    public async Task Http_protocol_conflicts_are_structured_but_unexpected_failures_remain_server_errors()
    {
        var f = await Fixture.Create();
        var recording = new RecordingTarget(f.Target);
        var progress = Assert.Single(await f.Host(recording).DeliverAsync(f.Context, f.Receipt));
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var other = MachineReactionTarget.Local(new(f.Model.Confirm.Compile().Plan!, f.Repository, contracts!),
            f.Target.Authority, (_, _) => ValueTask.FromResult(true));
        await using var app = WebApplication.CreateBuilder().Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapMachineReactionTarget("/target", other, _ => f.Context);
        app.MapMachineReactionTarget("/broken", new UnexpectedFailureTarget(f.Target), _ => f.Context);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new(app.Urls.Single()) };
        var json = ProcessDurableCheckpointJsonSerializer.CreateOptions();
        using var revision = await client.PostAsJsonAsync("/target", recording.Invocation, json);
        Assert.Equal(HttpStatusCode.Conflict, revision.StatusCode);
        var body = await revision.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MachineReactionProtocolException.RevisionMismatch, body.GetProperty("code").GetString());
        using var receipt = await client.PostAsJsonAsync("/target/receipt", progress.Receipt!.Request.Reference, json);
        Assert.Equal(HttpStatusCode.Conflict, receipt.StatusCode);
        body = await receipt.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(MachineReactionProtocolException.ReceiptMismatch, body.GetProperty("code").GetString());
        using var broken = await client.PostAsJsonAsync("/broken", recording.Invocation, json);
        Assert.Equal(HttpStatusCode.InternalServerError, broken.StatusCode);
    }

    [Theory]
    [InlineData("tests", "another-tenant")]
    [InlineData("another-authority", "test")]
    public async Task Cosmos_mixed_authorities_route_without_authorizing_or_admitting_unrelated_receipts(string authority, string tenant)
    {
        var f = await Fixture.Create();
        var original = f.Receipt.Commit;
        var request = original.Request;
        var foreignScope = new InteractionAuthorityScope(authority, tenant);
        var foreignRequest = new EntityTransitionOperationRequest(request.Operation, foreignScope, request.Transition, request.Subject, request.Input);
        var foreignCommit = new EntityTransitionOperationCommit(foreignRequest, original.Write, original.DecisionKind,
            original.Result, original.GuaranteeDemands, original.Evidence);
        var foreignReceipt = new EntityTransitionOperationReceipt(foreignCommit, f.Receipt.Entity, f.Receipt.CommittedAtUtc);
        using var client = new CosmosClient("https://localhost:8081", Convert.ToBase64String(new byte[64]));
        var repository = new CosmosEntityOutboxRepository(f.Model.Tender.Entity, client.GetContainer("test", "source"),
            partitionKeyPolicy: EntityPartitionKeyPolicy.Fixed("test"));
        var entity = new CosmosObservationContainerDocument("entity", "test", "entity", f.Model.Tender.Entity.Shape.Id.Value,
            f.Receipt.Entity.Entity.EntityId.Value, f.Receipt.Entity.Entity.Version, EntityConcurrencyToken: "retained-token");
        var checkpoints = 0;
        var batch = new CosmosManagedMaterializationProviderBatch("range", "cursor",
            [repository.CreateTransitionReceiptDocument(f.Context, foreignCommit, entity, "test"),
             repository.CreateTransitionReceiptDocument(f.Context, original, entity, "test")],
            () => { checkpoints++; return Task.CompletedTask; });
        await CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context, batch);
        Assert.Equal(1, checkpoints);
        Assert.Equal(1, f.SourceAuthorizations);
        var foreign = MachineReactionInvocation.Prepare(f.Model.PendingAcceptance,
            Assert.Single(EntityMachineOccurrence.FromReceipt(foreignReceipt)), new("worker", foreignScope, "test"), DateTimeOffset.UtcNow)!;
        Assert.Null(await f.Store.LoadAsync(f.Context, foreign.Start.InitialContinuation.ProcessInstanceId));
        f.SourceAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context, batch));
        Assert.Equal(1, checkpoints);
        Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
        var foreignTarget = MachineReactionTarget.Local(new(f.Model.MarkPending.Compile().Plan!, f.Repository, contracts!),
            foreignScope, (_, _) => ValueTask.FromResult(true));
        var leases = client.GetContainer("test", "leases");
        var localSource = new CosmosMachineReactionSource(repository, leases, f.Host(), "same-seed", "worker", DateTimeOffset.UnixEpoch);
        var foreignSource = new CosmosMachineReactionSource(repository, leases, f.Host(foreignTarget), "same-seed", "worker", DateTimeOffset.UnixEpoch);
        Assert.NotEqual(localSource.ProcessorName, foreignSource.ProcessorName);
    }

    [Fact]
    public async Task Cosmos_checkpoints_only_conclusive_batches_and_validates_receipt_content()
    {
        var f = await Fixture.Create();
        using var client = new CosmosClient("https://localhost:8081", Convert.ToBase64String(new byte[64]));
        var repository = new CosmosEntityOutboxRepository(f.Model.Tender.Entity, client.GetContainer("test", "source"),
            partitionKeyPolicy: EntityPartitionKeyPolicy.Fixed("test"));
        var entity = new CosmosObservationContainerDocument("entity", "test", "entity", f.Model.Tender.Entity.Shape.Id.Value,
            f.Receipt.Entity.Entity.EntityId.Value, f.Receipt.Entity.Entity.Version, EntityConcurrencyToken: "retained-token");
        var document = repository.CreateTransitionReceiptDocument(f.Context, f.Receipt.Commit, entity, "test");
        var checkpoints = 0;
        var batch = new CosmosManagedMaterializationProviderBatch("range", "cursor", [document], () => { checkpoints++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<TimeoutException>(() => CosmosMachineReactionSource.DeliverBatchAsync(repository,
            f.Host(new LostResponse(f.Target)), f.Context, batch));
        Assert.Equal(0, checkpoints);
        await CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context, batch);
        Assert.Equal(1, checkpoints);
        await CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context, batch);
        Assert.Equal(2, checkpoints);
        await CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context, batch with { Documents = [entity] });
        Assert.Equal(3, checkpoints);
        await Assert.ThrowsAnyAsync<Exception>(() => CosmosMachineReactionSource.DeliverBatchAsync(repository, f.Host(), f.Context,
            batch with { Documents = [document with { Id = "corrupt" }] }));
        Assert.Equal(3, checkpoints);
        var source = new CosmosMachineReactionSource(repository, client.GetContainer("test", "leases"), f.Host(), "test", "worker", DateTimeOffset.UnixEpoch);
        var other = new CosmosMachineReactionSource(repository, client.GetContainer("test", "leases"), f.Host(), "test", "worker", DateTimeOffset.UnixEpoch.AddDays(1));
        Assert.NotEqual(source.ProcessorName, other.ProcessorName);
    }

    sealed class Fixture
    {
        internal TenderReactionModel Model = null!;
        internal OperationContext Context = null!;
        internal EntityTransitionOperationReceipt Receipt = null!;
        internal InMemoryEntityOutboxRepository Repository = null!;
        internal MachineReactionTarget Target = null!;
        internal readonly InMemoryProcessDurableStore Store = new();
        internal bool SourceAllowed = true;
        internal bool TargetAllowed = true;
        internal int SourceAuthorizations;
        internal MachineReactionHost Host(MachineReactionTarget? target = null) => new(Model.PendingAcceptance, Store, target ?? Target,
            (_, receipt) => { SourceAuthorizations++; return ValueTask.FromResult<ProcessControlAuthorizationContext?>(SourceAllowed
                ? new("worker", receipt.Request.AuthorityScope, "policy/test") : null); }, new("worker", TimeSpan.FromMinutes(5)));

        internal static async Task<Fixture> Create(bool allowed = true)
        {
            var f = new Fixture { Model = new() };
            var (source, context, commit) = await EntityMachineOccurrenceTests.Prepare(f.Model.Tender.Entity,
                f.Model.Tender.AcceptPending, new TenderState("load-1", false, false, false, ""), new SendResult(false));
            f.Context = context;
            f.Receipt = (await source.CommitTransitionOperation(context, commit)).Receipt!;
            f.Repository = new(f.Model.Load, EntityPartitionKeyPolicy.Fixed("test"));
            await f.Repository.Upsert(context, new(f.Model.Load.CreateState("load-1", new LoadAcknowledgementState(LoadAcknowledgementStatus.Ready, allowed)).Snapshot));
            Assert.True(InteractionContractCatalog.TryCreate([], out var contracts).IsValid);
            f.Target = MachineReactionTarget.Local(new(f.Model.MarkPending.Compile().Plan!, f.Repository, contracts!),
                f.Receipt.Request.AuthorityScope, (_, _) => ValueTask.FromResult(f.TargetAllowed));
            return f;
        }
    }

    sealed class LostResponse(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override async ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { await inner.ExecuteAsync(context, invocation); throw new TimeoutException("Lost acknowledgement after commit"); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class RecordingTarget(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        internal ProcessTransitionInvocation? Invocation;
        public override ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
        { Invocation = invocation; return inner.ExecuteAsync(context, invocation); }
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => inner.ResolveAsync(context, reference);
    }

    sealed class UnexpectedFailureTarget(MachineReactionTarget inner) : MachineReactionTarget(inner.Transition, inner.Authority)
    {
        public override ValueTask<ProcessOperationResult> ExecuteAsync(OperationContext context, ProcessTransitionInvocation invocation)
            => throw new InvalidOperationException("Unexpected infrastructure failure");
        public override ValueTask<EntityTransitionOperationReceipt?> ResolveAsync(OperationContext context, EntityTransitionOperationReference reference)
            => throw new InvalidOperationException("Unexpected infrastructure failure");
    }

    sealed class AcceptedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }

    sealed class WrongReceiptHandler(EntityTransitionOperationReceipt receipt) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(receipt, options: ProcessDurableCheckpointJsonSerializer.CreateOptions()) });
    }

    sealed class LoseHttpResponseHandler(bool lose) : DelegatingHandler(new HttpClientHandler())
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (lose)
            {
                lose = false;
                response.Dispose();
                throw new HttpRequestException("Simulated lost response after server commitment");
            }
            return response;
        }
    }
}
