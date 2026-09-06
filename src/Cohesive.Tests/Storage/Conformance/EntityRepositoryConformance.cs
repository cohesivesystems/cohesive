using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Cohesive.Storage;
using Cohesive.Transitions.Execution;

namespace Cohesive.Tests.Storage.Conformance;

// Linked into the separate SQLite test assembly: one set of assertions, no test framework in production packages.
public enum RepositoryProbe { ScalarRoundTrip, CasFence, OrderedPartialBatch, AtomicBatch, DirectOutbox, OperationReceipt }

public static class EntityRepositoryConformance
{
    static readonly OperationContext Context = OperationContext.Create();
    public static IEnumerable<object[]> AllCases => Enum.GetValues<RepositoryProbe>().Select(probe => new object[] { probe });
    public static IEnumerable<object[]> BasicCases => AllCases.Where(row => (RepositoryProbe)row[0] is not RepositoryProbe.DirectOutbox);

    public static Task Verify(IEntityRepository repository, RepositoryProbe probe) => probe switch
    {
        RepositoryProbe.ScalarRoundTrip => ScalarRoundTrip(repository),
        RepositoryProbe.CasFence => CasFence(repository),
        RepositoryProbe.OrderedPartialBatch => OrderedPartialBatch(repository),
        RepositoryProbe.AtomicBatch => AtomicBatch(repository),
        RepositoryProbe.DirectOutbox => DirectOutbox(Assert.IsAssignableFrom<IEntityOutboxRepository>(repository)),
        RepositoryProbe.OperationReceipt => OperationReceipt(repository),
        _ => throw new ArgumentOutOfRangeException(nameof(probe))
    };

    static async Task ScalarRoundTrip(IEntityRepository repository)
    {
        var write = RunControlFixture.Write(RunControlFixture.Initial(), version: 17);
        var saved = await repository.Upsert(Context, write);
        var read = Assert.IsType<EntitySnapshot>(await repository.TryGet(Context, "run/1",
            new(expectedVersion: 17, expectedConcurrencyToken: saved.ConcurrencyToken, partitionKey: "tenant/a")));
        Assert.Equal(write.Entity.Observation.ShapeId, read.Entity.Observation.ShapeId);
        Assert.Equal(write.Entity.Observation.ToCanonicalJsonUtf8(), read.Entity.Observation.ToCanonicalJsonUtf8());
        Assert.Equal(17, read.Entity.Version);
        Assert.Equal("tenant/a", read.PartitionKey);
        Assert.Equal(saved.ConcurrencyToken, read.ConcurrencyToken);
        Assert.Null(await repository.TryGet(Context, "absent", new(partitionKey: "tenant/a")));
    }

    static async Task CasFence(IEntityRepository repository)
    {
        var initial = RunControlFixture.Initial();
        var first = await repository.Upsert(Context, RunControlFixture.Write(initial));
        var second = await repository.Upsert(Context, RunControlFixture.Write(initial with { Status = "running" }, token: first.ConcurrencyToken));
        Assert.NotEqual(first.ConcurrencyToken, second.ConcurrencyToken);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(Context,
            RunControlFixture.Write(initial with { Status = "stale" }, token: first.ConcurrencyToken)));
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.Upsert(Context,
            RunControlFixture.Write(initial with { Id = "absent" }, token: second.ConcurrencyToken)));
        Assert.Equal(second, await repository.TryGet(Context, initial.Id, new(partitionKey: initial.Tenant)));
    }

    static async Task OrderedPartialBatch(IEntityRepository repository)
    {
        var value = RunControlFixture.Initial();
        var results = await repository.UpsertBatch(Context, new(
            [RunControlFixture.Write(value), RunControlFixture.Write(value with { Status = "running" }, version: 1)], EntityBatchAtomicity.None));
        Assert.Equal(new long[] { 0, 1 }, results.Snapshots.Select(s => s.Entity.Version));
        var stale = results.Snapshots[0];
        var prefix = value with { Id = "prefix" };
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertBatch(Context, new(
            [RunControlFixture.Write(prefix), RunControlFixture.Write(value, token: stale.ConcurrencyToken)], EntityBatchAtomicity.None)));
        Assert.NotNull(await repository.TryGet(Context, prefix.Id, new(partitionKey: prefix.Tenant)));
        Assert.Equal(results.Snapshots[1], await repository.TryGet(Context, value.Id, new(partitionKey: value.Tenant)));
    }

    static async Task AtomicBatch(IEntityRepository repository)
    {
        foreach (var atomicity in new[] { EntityBatchAtomicity.SamePartition, EntityBatchAtomicity.AllOrNothing })
        {
            var value = RunControlFixture.Initial(id: $"target/{atomicity}");
            var stale = await repository.Upsert(Context, RunControlFixture.Write(value));
            var current = await repository.Upsert(Context, RunControlFixture.Write(value with { Status = "running" }));
            var prefix = value with { Id = $"prefix/{atomicity}", Tenant = atomicity == EntityBatchAtomicity.AllOrNothing ? "tenant/b" : value.Tenant };
            var request = new EntityBatchWriteRequest([RunControlFixture.Write(prefix), RunControlFixture.Write(value, token: stale.ConcurrencyToken)], atomicity);
            if (repository.BatchCapabilities.SupportsAtomicity(atomicity))
                await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertBatch(Context, request));
            else
                await Assert.ThrowsAsync<NotSupportedException>(() => repository.UpsertBatch(Context, request));
            Assert.Null(await repository.TryGet(Context, prefix.Id, new(partitionKey: prefix.Tenant)));
            Assert.Equal(current, await repository.TryGet(Context, value.Id, new(partitionKey: value.Tenant)));
        }
    }

    static async Task DirectOutbox(IEntityOutboxRepository repository)
    {
        var initial = await repository.Upsert(Context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(initial);
        var envelopes = RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts(), direct: true);
        var candidate = TransitionStateProjector.Apply(ObservationValue.FromObject(initial.Entity.Observation.Fields), evidence.Decision);
        var write = new EntityWriteRequest(repository.EntityDefinition.CreateState("run/1", candidate.Fields!, version: 1).Snapshot, initial.ConcurrencyToken);
        var commit = new EntityOutboxCommit(write, envelopes);
        var committed = await repository.UpsertWithOutbox(Context, commit);
        var replay = await repository.UpsertWithOutbox(Context, commit);
        Assert.Equal(committed.Entity, replay.Entity);
        Assert.Equal(InteractionEnvelopeJsonSerializer.GetCanonicalBytes(envelopes[0]),
            InteractionEnvelopeJsonSerializer.GetCanonicalBytes(replay.Envelopes[0]));
        var original = Assert.IsType<DomainEventEnvelope>(envelopes[0]);
        var conflicting = new DomainEventEnvelope(original.SchemaVersion, original.Context, original.Contract,
            PortableValue.Concrete(RunControlFixture.StringContract, ObservationValue.FromString("changed")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertWithOutbox(Context, new(write, [conflicting])));
        var unseen = RunControlFixture.Prepare(initial, occurrence: 1);
        var nextEnvelopes = RunControlFixture.Lower(unseen, unseen.Decision, RunControlFixture.Contracts(), direct: true);
        await Assert.ThrowsAsync<ObservationConcurrencyConflictException>(() => repository.UpsertWithOutbox(Context, new(write, nextEnvelopes)));
        Assert.Equal(committed.Entity, await repository.TryGet(Context, "run/1", new(partitionKey: "tenant/a")));
    }

    static async Task OperationReceipt(IEntityRepository repository)
    {
        var initial = await repository.Upsert(Context, RunControlFixture.Write(RunControlFixture.Initial()));
        var evidence = RunControlFixture.Prepare(initial);
        var commit = RunControlFixture.Commit(evidence, evidence.Decision, RunControlFixture.Lower(evidence, evidence.Decision, RunControlFixture.Contracts()));
        var committed = await repository.CommitTransitionOperation(Context, commit);
        if (!repository.TransitionOperationCapabilities.SupportsAtomicStateAndReceipt)
        {
            Assert.Equal(EntityTransitionOperationDisposition.CapabilityInsufficient, committed.Disposition);
            Assert.Equal(initial, await repository.TryGet(Context, "run/1", new(partitionKey: "tenant/a")));
            return;
        }
        Assert.Equal(EntityTransitionOperationDisposition.Committed, committed.Disposition);
        await repository.Upsert(Context, RunControlFixture.Write(RunControlFixture.Initial() with { Status = "completed" }, version: 2));
        var replay = await repository.TryGetTransitionOperation(Context, evidence.Request);
        Assert.Equal(EntityTransitionOperationDisposition.Replayed, replay.Disposition);
        Assert.Equal(committed.Receipt!.Entity, replay.Receipt!.Entity);
        Assert.Equal(commit.Fingerprint, replay.Receipt.Commit.Fingerprint);
        Assert.Equal(EntityTransitionEmissionPublicationAuthority.ProcessOutbox, replay.Receipt.PublicationAuthority);
        var changed = new EntityTransitionOperationRequest(evidence.Request.Operation, evidence.Request.AuthorityScope,
            evidence.Request.Transition, evidence.Request.Subject, PortableValue.Concrete(RunControlFixture.StringContract, ObservationValue.FromString("different")));
        Assert.Equal(EntityTransitionOperationDisposition.IdentityConflict, (await repository.TryGetTransitionOperation(Context, changed)).Disposition);
    }
}
