using Cohesive.Simulation.Provisioning;
using Cohesive.Simulation.Worlds;

namespace Cohesive.Simulation.Tests;

public sealed class WorldProvisioningTests
{
    [Fact]
    public async Task Provisioning_IsBoundedOrderedAndDeterministicallyAddressed()
    {
        var plan = DemoWorld().Compile();
        RecordingSink firstSink = new("demo/database");
        RecordingSink secondSink = new("demo/database");

        var first = await WorldProvisioner.ProvisionAsync(
            plan,
            rootSeed: long.MinValue,
            firstSink,
            new(batchSize: 2));
        var second = await WorldProvisioner.ProvisionAsync(
            plan,
            rootSeed: long.MinValue,
            secondSink,
            new(batchSize: 2));

        Assert.Equal(first.RunId, second.RunId);
        Assert.Equal(
            firstSink.Batches.Select(static batch => batch.Id),
            secondSink.Batches.Select(static batch => batch.Id));
        Assert.Equal(["customers", "customers", "orders"], firstSink.Batches.Select(static batch => batch.PopulationId));
        Assert.Equal([0L, 2L, 0L], firstSink.Batches.Select(static batch => batch.StartSequenceIndex));
        Assert.All(firstSink.Batches, static batch => Assert.InRange(batch.Items.Length, low: 1, high: 2));
        Assert.Equal(
            [0L, 1L, 2L],
            firstSink.Batches
                .Where(static batch => batch.PopulationId == "customers")
                .SelectMany(static batch => batch.Items)
                .Select(static item => item.Replay.SequenceIndex));
        Assert.Equal(5, first.ItemCount);
        Assert.Equal(3, first.BatchCount);
        Assert.Equal(0, first.AlreadyCommittedBatchCount);
        Assert.Equal(["customers", "orders"], first.Populations.Select(static population => population.PopulationId));
    }

    [Fact]
    public async Task TargetIdentity_ParticipatesInRunAndBatchIdentity()
    {
        var plan = DemoWorld().Compile();
        RecordingSink database = new("demo/database");
        RecordingSink artifact = new("demo/artifact");

        var databaseResult = await WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, database);
        var artifactResult = await WorldProvisioner.ProvisionAsync(plan, rootSeed: 42, artifact);

        Assert.NotEqual(databaseResult.RunId, artifactResult.RunId);
        Assert.NotEqual(database.Batches[0].Id, artifact.Batches[0].Id);
    }

    [Fact]
    public async Task BatchPolicy_ParticipatesInExactRunIdentity()
    {
        var plan = DemoWorld().Compile();
        RecordingSink pairs = new("demo/database");
        RecordingSink triples = new("demo/database");

        var pairResult = await WorldProvisioner.ProvisionAsync(
            plan,
            rootSeed: 42,
            pairs,
            new(batchSize: 2));
        var tripleResult = await WorldProvisioner.ProvisionAsync(
            plan,
            rootSeed: 42,
            triples,
            new(batchSize: 3));

        Assert.NotEqual(pairResult.RunId, tripleResult.RunId);
    }

    [Fact]
    public async Task AlreadyCommittedAcknowledgements_AreRetainedInCompletionEvidence()
    {
        RecordingSink sink = new(
            "demo/idempotent",
            static batch => new(batch.Id, WorldProvisioningBatchDisposition.AlreadyCommitted));

        var result = await WorldProvisioner.ProvisionAsync(
            DemoWorld().Compile(),
            rootSeed: 42,
            sink,
            new(batchSize: 2));

        Assert.Equal(result.BatchCount, result.AlreadyCommittedBatchCount);
        Assert.All(result.Populations, static population =>
            Assert.Equal(population.BatchCount, population.AlreadyCommittedBatchCount));
    }

    [Fact]
    public async Task ExplicitRejection_StopsAtExactBatchWithSinkEvidence()
    {
        RecordingSink sink = new(
            "demo/rejecting",
            batch => batch.Ordinal == 1
                ? new(batch.Id, WorldProvisioningBatchDisposition.Rejected, "capacity exhausted")
                : new(batch.Id, WorldProvisioningBatchDisposition.Committed));

        var exception = await Assert.ThrowsAsync<WorldProvisioningRejectedException>(() =>
            WorldProvisioner.ProvisionAsync(
                DemoWorld().Compile(),
                rootSeed: 42,
                sink,
                new(batchSize: 2)));

        Assert.Equal(2, sink.Batches.Count);
        Assert.Equal(sink.Batches[1], exception.Batch);
        Assert.Equal("capacity exhausted", exception.Receipt.Detail);
    }

    [Fact]
    public async Task MismatchedReceipt_IsRejectedAtTheProtocolBoundary()
    {
        RecordingSink sink = new(
            "demo/broken",
            static _ => new(
                new WorldProvisioningBatchId("csimbatch1_wrong"),
                WorldProvisioningBatchDisposition.Committed));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorldProvisioner.ProvisionAsync(DemoWorld().Compile(), rootSeed: 42, sink));

        Assert.Contains("while batch", exception.Message, StringComparison.Ordinal);
        Assert.Single(sink.Batches);
    }

    [Fact]
    public async Task SinkException_IsPreservedAndNeverRetriedAutomatically()
    {
        var expected = new IOException("commit outcome unknown");
        RecordingSink sink = new("demo/failing", _ => throw expected);

        var observed = await Assert.ThrowsAsync<IOException>(() =>
            WorldProvisioner.ProvisionAsync(DemoWorld().Compile(), rootSeed: 42, sink));

        Assert.Same(expected, observed);
        Assert.Single(sink.Batches);
    }

    [Fact]
    public async Task Cancellation_IsObservedBetweenAcknowledgedBatches()
    {
        using CancellationTokenSource cancellation = new();
        RecordingSink sink = new(
            "demo/cancelled",
            batch =>
            {
                cancellation.Cancel();
                return new(batch.Id, WorldProvisioningBatchDisposition.Committed);
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            WorldProvisioner.ProvisionAsync(
                DemoWorld().Compile(),
                rootSeed: 42,
                sink,
                new(batchSize: 1),
                cancellation.Token));

        Assert.Single(sink.Batches);
    }

    [Fact]
    public void ProtocolValues_RejectInvalidStates()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorldProvisioningOptions(batchSize: 0));
        Assert.Throws<ArgumentException>(() => new WorldProvisioningBatchReceipt(
            default,
            WorldProvisioningBatchDisposition.Committed));
        Assert.Throws<ArgumentException>(() => new WorldProvisioningBatchReceipt(
            new("batch"),
            WorldProvisioningBatchDisposition.Rejected));
    }

    static WorldDefinition DemoWorld()
    {
        var customers = Simulation.Define<ProvisionedCustomer>(customer => customer
            .Member(value => value.Name, Gen.Categorical(
                Gen.Weighted("Ada", weight: 1d),
                Gen.Weighted("Grace", weight: 1d)))
            .Member(value => value.Age, Gen.Int32(minimum: 18, maximum: 90)));
        var orders = Simulation.Define<ProvisionedOrder>(order => order
            .Member(value => value.Number, Gen.Int32(minimum: 1, maximum: 1_000_000)));
        return Simulation.DefineWorld("world/provisioning", "r1", world => world
            .Population("orders", count: 2, orders)
            .Population("customers", count: 3, customers));
    }

    sealed class RecordingSink(
        string targetId,
        Func<WorldProvisioningBatch, WorldProvisioningBatchReceipt>? acknowledge = null)
        : IWorldProvisioningSink
    {
        public string TargetId { get; } = targetId;

        public List<WorldProvisioningBatch> Batches { get; } = [];

        public ValueTask<WorldProvisioningBatchReceipt> CommitAsync(
            WorldProvisioningBatch batch,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Batches.Add(batch);
            return ValueTask.FromResult(
                acknowledge?.Invoke(batch)
                ?? new(batch.Id, WorldProvisioningBatchDisposition.Committed));
        }
    }

    public sealed record ProvisionedCustomer(string Name, int Age);

    public sealed record ProvisionedOrder(int Number);
}
