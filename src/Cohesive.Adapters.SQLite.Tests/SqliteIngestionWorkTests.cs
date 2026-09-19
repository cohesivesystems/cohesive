using System.Diagnostics;
using System.Text;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model.Serialization;
using Cohesive.Storage.Commits;

namespace Cohesive.Adapters.SQLite.Tests;

public sealed class SqliteIngestionWorkTests
{
    static readonly OperationContext Context = OperationContext.Create();
    static SqliteDatabase Database(string path) => new(new(path, durability: SqliteDurability.Full));
    static SqliteIngestionWorkStore Store(DatabaseFixture file)
    {
        var database = Database(file.Path);
        SqliteIngestionWorkStore.Schema.Apply(database);
        return new(database);
    }

    [Fact]
    public async Task ReopenedPrefixPreservesExactEvidenceAndRejectsDifferentContent()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        foreach (var boundary in IngestionWorkFixture.Chain())
        {
            Assert.Equal(StorageCommitDisposition.Committed, (await store.RetainAsync(Context, boundary.Document, boundary.Content)).Disposition);
            store = new(Database(file.Path));
            var reopened = await store.ReadAsync(Context, boundary.Document.Metadata.DefinitionId, boundary.Document.Metadata.RevisionId);
            Assert.NotNull(reopened);
            Assert.Equal(ExecutionDefinitionJsonSerializer.Serialize(boundary.Document), ExecutionDefinitionJsonSerializer.Serialize(reopened.Value.Document));
            Assert.Equal(boundary.Content, reopened.Value.Content.ToArray());
            Assert.Equal(StorageCommitDisposition.Replayed, (await store.RetainAsync(Context, boundary.Document, boundary.Content)).Disposition);
        }
        var changed = IngestionWorkFixture.Chain(selection: "changed-selection")[0];
        Assert.Equal(StorageCommitDisposition.IdentityConflict, (await store.RetainAsync(Context, changed.Document, changed.Content)).Disposition);
        var first = IngestionWorkFixture.Chain()[0];
        Assert.Equal(first.Content, (await store.ReadAsync(Context, first.Document.Metadata.DefinitionId, first.Document.Metadata.RevisionId))!.Value.Content.ToArray());
        Assert.Equal(StorageCommitDisposition.Replayed, (await store.RetainAsync(Context, first.Document, first.Content)).Disposition);
    }

    [Fact]
    public async Task OrphanAndChangedPredecessorsCannotAuthorizeRetention()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var chain = IngestionWorkFixture.Chain();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RetainAsync(Context, chain[1].Document, chain[1].Content));
        await store.RetainAsync(Context, chain[0].Document, chain[0].Content);
        var changed = IngestionWorkFixture.Chain(selection: "changed");
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RetainAsync(Context, changed[1].Document, changed[1].Content));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RetainAsync(Context, chain[2].Document, chain[2].Content));
    }

    [Fact]
    public async Task InvalidBytesCancellationAndBoundsWriteNothing()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var first = IngestionWorkFixture.Chain()[0];
        await Assert.ThrowsAsync<ArgumentException>(async () => await store.RetainAsync(Context, first.Document, new byte[] { 1 }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.RetainAsync(Context.WithCancellationToken(new(true)), first.Document, first.Content));
        var bounded = new SqliteIngestionWorkStore(Database(file.Path), maximumContentBytes: 1);
        await Assert.ThrowsAsync<ArgumentException>(async () => await bounded.RetainAsync(Context, first.Document, first.Content));
        Assert.Null(await store.ReadAsync(Context, first.Document.Metadata.DefinitionId, first.Document.Metadata.RevisionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentCandidatesHaveOneOriginalOwner(bool identical)
    {
        using var file = new DatabaseFixture();
        Store(file);
        var candidates = Enumerable.Range(0, 6).Select(i => IngestionWorkFixture.Chain(selection: "selection/" + (identical ? 0 : i))[0]).ToArray();
        var results = await Task.WhenAll(candidates.Select(candidate => Task.Run(async () =>
            await new SqliteIngestionWorkStore(Database(file.Path)).RetainAsync(Context, candidate.Document, candidate.Content))));
        Assert.Single(results, r => r.Disposition == StorageCommitDisposition.Committed);
        Assert.Equal(5, results.Count(r => r.Disposition == (identical ? StorageCommitDisposition.Replayed : StorageCommitDisposition.IdentityConflict)));
    }

    [Theory]
    [InlineData("content")]
    [InlineData("receipt")]
    [InlineData("bytes")]
    public async Task CorruptionNeverBecomesSuccessfulReplay(string kind)
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var first = IngestionWorkFixture.Chain()[0];
        await store.RetainAsync(Context, first.Document, first.Content);
        using (var connection = Database(file.Path).OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = kind switch
            {
                "content" => "UPDATE __cohesive_storage_commits_v1 SET token = 'corrupt' WHERE kind = 'item' AND id LIKE '%/content'",
                "bytes" => "UPDATE __cohesive_storage_commits_v1 SET payload = replace(payload, 'c2VsZWN0aW9u', 'Y29ycnVwdGVk') WHERE kind = 'item' AND id LIKE '%/content'",
                _ => "DELETE FROM __cohesive_storage_commits_v1 WHERE kind = 'receipt'"
            };
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadAsync(Context, first.Document.Metadata.DefinitionId, first.Document.Metadata.RevisionId));
        if (kind != "receipt")
            await Assert.ThrowsAsync<InvalidDataException>(async () => await store.RetainAsync(Context, first.Document, first.Content));
    }

    [Fact]
    public async Task ReopeningPreparedEvidenceAlsoVerifiesItsRetainedPredecessors()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var chain = IngestionWorkFixture.Chain();
        foreach (var boundary in chain) await store.RetainAsync(Context, boundary.Document, boundary.Content);
        using (var connection = Database(file.Path).OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM __cohesive_storage_commits_v1 WHERE partition = $request";
            command.Parameters.AddWithValue("$request", chain[0].Document.Metadata.DefinitionId.Value);
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidDataException>(async () => await store.ReadAsync(Context, chain[2].Document.Metadata.DefinitionId, chain[2].Document.Metadata.RevisionId));
    }

    [Fact]
    public async Task NativeRowLimitRejectsOversizeBeforeManagedJsonDecodingIncludingEmbeddedNul()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var first = IngestionWorkFixture.Chain()[0];
        await store.RetainAsync(Context, first.Document, first.Content);
        using (var connection = Database(file.Path).OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE __cohesive_storage_commits_v1 SET payload = char(0) || CAST(zeroblob(300000) AS TEXT) WHERE kind = 'item'";
            command.ExecuteNonQuery();
        }
        var bounded = new SqliteIngestionWorkStore(Database(file.Path), maximumContentBytes: 1, maximumDocumentBytes: 1);
        var error = await Assert.ThrowsAsync<InvalidDataException>(async () => await bounded.ReadAsync(Context, first.Document.Metadata.DefinitionId, first.Document.Metadata.RevisionId));
        Assert.Contains("per-row byte limit", error.Message);
    }

    [Fact]
    public async Task KillBeforeCommitLeavesTheOriginalRetainedPrefixIntact()
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var first = IngestionWorkFixture.Chain()[0];
        await store.RetainAsync(Context, first.Document, first.Content);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SqliteCrashWorker).Assembly.Location);
        start.ArgumentList.Add("--sqlite-crash-worker");
        start.ArgumentList.Add(file.Path);
        start.ArgumentList.Add("DELETE FROM __cohesive_storage_commits_v1;");
        using var worker = Process.Start(start)!;
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("uncommitted", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var reopened = new SqliteIngestionWorkStore(Database(file.Path));
            Assert.Equal(first.Content, (await reopened.ReadAsync(Context, first.Document.Metadata.DefinitionId, first.Document.Metadata.RevisionId))!.Value.Content.ToArray());
            Assert.Equal(StorageCommitDisposition.Replayed, (await reopened.RetainAsync(Context, first.Document, first.Content)).Disposition);
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
            Assert.Equal("", await errors);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task KillAfterEachCommittedBoundaryReopensAndReplaysWithoutRebuildingEvidence(int retainedCount)
    {
        using var file = new DatabaseFixture();
        var store = Store(file);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(SqliteCrashWorker).Assembly.Location);
        start.ArgumentList.Add("--ingestion-retain-worker");
        start.ArgumentList.Add(file.Path);
        start.ArgumentList.Add(retainedCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var worker = Process.Start(start)!;
        var errors = worker.StandardError.ReadToEndAsync();
        try
        {
            Assert.Equal("retained", await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)));
            worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(0, worker.ExitCode);
            var chain = IngestionWorkFixture.Chain();
            for (var i = 0; i < chain.Length; i++)
            {
                var reopened = await store.ReadAsync(Context, chain[i].Document.Metadata.DefinitionId, chain[i].Document.Metadata.RevisionId);
                if (i >= retainedCount) { Assert.Null(reopened); continue; }
                Assert.NotNull(reopened);
                Assert.Equal(chain[i].Content, reopened.Value.Content.ToArray());
                Assert.Equal(StorageCommitDisposition.Replayed, (await store.RetainAsync(Context, reopened.Value.Document, reopened.Value.Content)).Disposition);
            }
        }
        finally
        {
            if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync();
            Assert.Equal("", await errors);
        }
    }
}

internal static class IngestionWorkFixture
{
    internal static (ExecutionDefinitionDocument Document, byte[] Content)[] Chain(string selection = "selection")
    {
        var provenance = new ExecutionProvenance(new("retention-test", "1"), new("synthetic"), DocumentOrigin.Generated);
        var address = new IngestionLedgerAddress("flow", "source", "sink", "partition");
        var definition = Ref(ExecutionDefinitionDocument.Create(new("test.flow"), new("flow"), new("v1"), address, provenance));
        var now = new DateTimeOffset(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);
        byte[] selected = Encoding.UTF8.GetBytes(selection), acquired = Encoding.UTF8.GetBytes("acquired"), prepared = Encoding.UTF8.GetBytes("prepared");
        var request = IngestionWorkDocuments.Create(new IngestionAcquisitionRequest(address, definition, definition, "operation", "attempt",
            IngestionContentReference.Describe("selection", selected, "application/json"), 0, now), provenance);
        var receipt = IngestionWorkDocuments.Create(new IngestionAcquisitionReceipt(Ref(request),
            IngestionContentReference.Describe("source", acquired, "application/json"), now.AddSeconds(1)), provenance);
        var publication = IngestionWorkDocuments.Create(new IngestionPreparedPublication(Ref(receipt), definition,
            IngestionContentReference.Describe("publication", prepared, "application/json"), new IngestionCursorPosition("v1", "opaque"), now.AddSeconds(2)), provenance);
        return [(request, selected), (receipt, acquired), (publication, prepared)];
    }
    static ExecutionDefinitionReference Ref(ExecutionDefinitionDocument document) => new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
}
