using Cohesive.Execution;
using Cohesive.ExecutionKernel.TestFixtures.Storage;
using Cohesive.Model;
using Cohesive.Storage;
using Cohesive.Storage.Commits;

namespace Cohesive.Tests.Storage.Conformance;

// Linked into the SQLite test assembly. These are behavioral tests, not another commit implementation.
public enum StorageCommitProbe { RoundTripAndReplay, Rollback, CompetingWriters, ReceiptRace, IdentityConflict, LostAcknowledgment, Placement, QueryGuard }

public static class StorageCommitConformance
{
    public const string Target = "synthetic-items";
    public const string Partition = "tenant/a";
    public static readonly OperationContext Context = OperationContext.Create();
    public static IEnumerable<object[]> Cases => Enum.GetValues<StorageCommitProbe>().Select(probe => new object[] { probe });
    public static StorageCommitAddress Address(string id, string partition = Partition, string target = Target) => new(target, partition, id);
    public static PortableValue Value(string value) => PortableValue.Concrete(RunControlFixture.StringContract, ObservationValue.FromString(value));
    public static StorageCommitIntent Intent(string operation, params StorageCommitWrite[] writes) => new(Address(operation), [.. writes], Value("accepted/" + operation));

    public static async Task Verify(IStorageCommitExecutor executor,
        Func<StorageCommitAddress, ValueTask<StorageCommitItem?>> read,
        Func<Task<int>> countActive, StorageCommitProbe probe)
    {
        var item = Address("z-state");
        var create = Intent("create", new StorageCommitWrite(item, Value("initial")));
        Assert.Equal(StorageCommitDisposition.Unknown, (await executor.ReconcileAsync(Context, create.Reference)).Disposition);
        var initial = await executor.CommitAsync(Context, create);
        Assert.Equal(StorageCommitDisposition.Committed, initial.Disposition);
        var snapshot = Assert.IsType<StorageCommitItem>(await read(item));
        Assert.Equal(create.Fingerprint, snapshot.Token.Value);
        Assert.Equal(Value("initial"), snapshot.Value);
        switch (probe)
        {
            case StorageCommitProbe.RoundTripAndReplay:
                var restored = StorageCommitJson.Deserialize(StorageCommitJson.Serialize(create));
                Assert.Equal(create.Fingerprint, restored.Fingerprint);
                await Commit(executor, Intent("later", new StorageCommitWrite(item, Value("later"), snapshot.Token)));
                var replay = await executor.CommitAsync(Context, restored);
                Assert.Equal(StorageCommitDisposition.Replayed, replay.Disposition);
                Assert.Equal(initial.Receipt, replay.Receipt);
                Assert.Equal(Value("later"), (await read(item))!.Value);
                break;
            case StorageCommitProbe.Rollback:
                // Canonical order applies this new item BEFORE the stale replacement on SQLite.
                var prefix = Address("a-prefix");
                var failed = Intent("failed", new StorageCommitWrite(prefix, Value("must-not-survive")),
                    new(item, Value("wrong"), new EntityConcurrencyToken("stale")));
                Assert.Equal(StorageCommitDisposition.PreconditionFailed, (await executor.CommitAsync(Context, failed)).Disposition);
                Assert.Null(await read(prefix));
                Assert.Equal(snapshot, await read(item));
                Assert.Equal(StorageCommitDisposition.Unknown, (await executor.ReconcileAsync(Context, failed.Reference)).Disposition);
                var duplicateCreate = Intent("duplicate", new StorageCommitWrite(prefix, Value("also-rolled-back")), new(item, Value("wrong")));
                Assert.Equal(StorageCommitDisposition.PreconditionFailed, (await executor.CommitAsync(Context, duplicateCreate)).Disposition);
                Assert.Null(await read(prefix));
                break;
            case StorageCommitProbe.CompetingWriters:
                var a = Intent("writer-a", new StorageCommitWrite(item, Value("a"), snapshot.Token), new(Address("a-only"), Value("a")));
                var b = Intent("writer-b", new StorageCommitWrite(item, Value("b"), snapshot.Token), new(Address("b-only"), Value("b")));
                var results = await Race(executor, a, b);
                Assert.Single(results, result => result.Disposition == StorageCommitDisposition.Committed);
                Assert.Single(results, result => result.Disposition == StorageCommitDisposition.PreconditionFailed);
                var winner = results[0].Disposition == StorageCommitDisposition.Committed ? a : b;
                Assert.Equal(winner.Fingerprint, (await read(item))!.Token.Value);
                Assert.NotNull(await read(Address(winner == a ? "a-only" : "b-only")));
                Assert.Null(await read(Address(winner == a ? "b-only" : "a-only")));
                break;
            case StorageCommitProbe.ReceiptRace:
                var exact = Intent("same-operation", new StorageCommitWrite(Address("exact-item"), Value("exact")));
                var exactRace = await Race(executor, exact, exact);
                Assert.Single(exactRace, result => result.Disposition == StorageCommitDisposition.Committed);
                Assert.Single(exactRace, result => result.Disposition == StorageCommitDisposition.Replayed);
                Assert.Equal(exactRace[0].Receipt, exactRace[1].Receipt);
                // Disjoint item writes can only conflict at the shared receipt. The losing item must roll back.
                var first = Intent("conflicting-operation", new StorageCommitWrite(Address("first"), Value("first")));
                var second = Intent("conflicting-operation", new StorageCommitWrite(Address("second"), Value("second")));
                var receiptRace = await Race(executor, first, second);
                Assert.Single(receiptRace, result => result.Disposition == StorageCommitDisposition.Committed);
                Assert.Single(receiptRace, result => result.Disposition == StorageCommitDisposition.IdentityConflict);
                var firstWon = receiptRace[0].Disposition == StorageCommitDisposition.Committed;
                Assert.NotNull(await read(Address(firstWon ? "first" : "second")));
                Assert.Null(await read(Address(firstWon ? "second" : "first")));
                break;
            case StorageCommitProbe.IdentityConflict:
                var conflicting = new StorageCommitIntent(create.ReceiptAddress, create.Writes, Value("different-result"));
                Assert.Equal(StorageCommitDisposition.IdentityConflict, (await executor.CommitAsync(Context, conflicting)).Disposition);
                Assert.Equal(StorageCommitDisposition.IdentityConflict, (await executor.ReconcileAsync(Context, conflicting.Reference)).Disposition);
                Assert.Equal(snapshot, await read(item));
                // Same ID as a receipt is still a distinct ordinary item identity.
                await Commit(executor, Intent("separate-namespace", new StorageCommitWrite(create.ReceiptAddress, Value("ordinary-item"))));
                Assert.Equal(initial.Receipt, (await executor.ReconcileAsync(Context, create.Reference)).Receipt);
                break;
            case StorageCommitProbe.LostAcknowledgment:
                var lost = Intent("lost", new StorageCommitWrite(item, Value("committed-before-crash"), snapshot.Token));
                await Assert.ThrowsAsync<IOException>(async () =>
                {
                    await Commit(executor, lost);
                    throw new IOException("Synthetic transport loss after native commit and before caller acknowledgment.");
                });
                var retained = await executor.ReconcileAsync(Context, lost.Reference);
                Assert.Equal(StorageCommitDisposition.Replayed, retained.Disposition);
                await Commit(executor, Intent("after-lost", new StorageCommitWrite(item, Value("newer-state"), new(lost.Fingerprint))));
                Assert.Equal(retained.Receipt, (await executor.CommitAsync(Context, lost)).Receipt);
                Assert.Equal(Value("newer-state"), (await read(item))!.Value);
                break;
            case StorageCommitProbe.Placement:
                var crossPartition = Intent("cross-partition", new StorageCommitWrite(Address("a", partition: "tenant/b"), Value("b")), new(Address("a"), Value("a")));
                var placement = await executor.CommitAsync(Context, crossPartition);
                Assert.Equal(executor.Capabilities.SupportsMultiplePartitions ? StorageCommitDisposition.Committed : StorageCommitDisposition.Unsupported, placement.Disposition);
                if (!executor.Capabilities.SupportsMultiplePartitions) Assert.Null(await read(Address("a")));
                var crossTarget = Intent("cross-target", new StorageCommitWrite(Address("foreign", target: "another-container"), Value("foreign")), new(Address("local"), Value("local")));
                Assert.Equal(executor.Capabilities.Target is null ? StorageCommitDisposition.Committed : StorageCommitDisposition.Unsupported,
                    (await executor.CommitAsync(Context, crossTarget)).Disposition);
                if (executor.Capabilities.Target is not null) Assert.Null(await read(Address("local")));
                break;
            case StorageCommitProbe.QueryGuard:
                var guard = Address("a-active-guard");
                var seed = Intent("seed-guard", new StorageCommitWrite(guard, Value("guard")));
                await Commit(executor, seed);
                // Read the guard BEFORE the predicate. A new matching row does not change z-state's ETag.
                var guardBeforeQuery = (await read(guard))!.Token;
                Assert.Equal(0, await countActive());
                await Commit(executor, Intent("activate-other", new StorageCommitWrite(guard, Value("guard"), guardBeforeQuery), new(Address("other"), Value("active"))));
                Assert.Equal(1, await countActive());
                Assert.Equal(snapshot.Token, (await read(item))!.Token);
                var decision = new StorageCommitIntent(Address("query-decision"),
                    [new(guard, Value("guard"), guardBeforeQuery), new(item, Value("active"), snapshot.Token)], Value("accepted"),
                    [new("synthetic-query/v1/count-active-equals-zero", guard)]);
                Assert.Equal(executor.Capabilities.SupportsQueryGuards ? StorageCommitDisposition.PreconditionFailed : StorageCommitDisposition.Unsupported,
                    (await executor.CommitAsync(Context, decision)).Disposition);
                Assert.Equal(1, await countActive());
                // Even a correct item ETag cannot turn an unprotected query into a safe decision.
                var unprotected = new StorageCommitIntent(Address("unprotected"), [new(item, Value("active"), snapshot.Token)], Value("accepted"),
                    [new("synthetic-query/v1/count-active-equals-zero")]);
                Assert.Equal(StorageCommitDisposition.Unsupported, (await executor.CommitAsync(Context, unprotected)).Disposition);
                // The actual native guard CAS is also tested when the emulator cannot qualify query consistency.
                var staleGuard = new StorageCommitIntent(Address("stale-guard"), decision.Writes, decision.Result);
                Assert.Equal(StorageCommitDisposition.PreconditionFailed, (await executor.CommitAsync(Context, staleGuard)).Disposition);
                if (executor.Capabilities.SupportsQueryGuards)
                {
                    var currentGuard = (await read(guard))!.Token;
                    Assert.Equal(1, await countActive());
                    var accepted = new StorageCommitIntent(Address("guarded-success"),
                        [new(guard, Value("guard"), currentGuard), new(item, Value("active"), snapshot.Token)], Value("accepted"),
                        [new("synthetic-query/v1/count-active-equals-one", guard)]);
                    await Commit(executor, accepted);
                    Assert.Equal(2, await countActive());
                    Assert.Equal(StorageCommitDisposition.Replayed, (await executor.CommitAsync(Context, accepted)).Disposition);
                }
                break;
            default: throw new ArgumentOutOfRangeException(nameof(probe));
        }
    }

    static async Task<StorageCommitResult[]> Race(IStorageCommitExecutor executor, params StorageCommitIntent[] intents)
    {
        using var start = new Barrier(intents.Length);
        return await Task.WhenAll(intents.Select(intent => Task.Run(async () =>
        {
            Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(30)));
            return await executor.CommitAsync(Context, intent);
        })));
    }

    static async Task Commit(IStorageCommitExecutor executor, StorageCommitIntent intent) =>
        Assert.Equal(StorageCommitDisposition.Committed, (await executor.CommitAsync(Context, intent)).Disposition);
}
