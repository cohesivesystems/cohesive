using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Execution;
using Cohesive.Integrations;

namespace Cohesive.Tests.Integrations;

public class IngestionLedgerTests
{
    static readonly IngestionLedgerAddress Address = new("flow/a", "source/a", "destination/a", "partition/a");
    static readonly ExecutionProvenance Provenance = new(new("ledger-tests", "1"), new("tests/ledger"), DocumentOrigin.Generated);
    static ExecutionDefinitionReference Definition()
    {
        var document = ExecutionDefinitionDocument.Create(new("test.flow"), new("flow/a"), new("revision/1"), Address, Provenance);
        return new(document.Metadata.DefinitionId, document.Metadata.RevisionId, document.Metadata.Fingerprint);
    }

    static IngestionLedgerAdvance Advance(string operation = "operation/1", long expected = 0, int day = 1,
        IngestionLedgerAddress? address = null, IngestionPosition? position = null) =>
        new(address ?? Address, Definition(), expected, position ?? new IngestionDateRangePosition(new(2026, 9, day), new(2026, 9, day + 1)),
            new(operation, "publisher-fingerprint/" + operation, "receipt/" + operation));
    static ExecutionDefinitionDocument Document(IngestionLedgerAdvance advance) => IngestionLedgerDocuments.Create(advance, Provenance);

    [Fact]
    public void ResultsRoundTripForDurableRequestOutcomesWithoutLosingReceiptOrDisposition()
    {
        var document = Document(Advance());
        var committed = IngestionLedgerReduction.Evaluate(null, null, document);
        IngestionLedgerResult[] results = [committed, IngestionLedgerReduction.Evaluate(null, committed.Receipt, document),
            IngestionLedgerReduction.Evaluate(committed.Receipt!.Entry, null, Document(Advance("other"))), IngestionLedgerResult.Unknown()];
        var options = StrictDocumentJson.CreateOptions();
        foreach (var result in results)
        {
            var json = JsonSerializer.Serialize(result, options);
            var restored = JsonSerializer.Deserialize<IngestionLedgerResult>(json, options);
            Assert.Equal(result, restored);
            Assert.Equal(json, JsonSerializer.Serialize(restored, options));
        }
    }

    [Fact]
    public void ResultWireRejectsContradictoryEvidence()
    {
        var result = IngestionLedgerResult.Unknown();
        var options = StrictDocumentJson.CreateOptions();
        var json = JsonSerializer.Serialize(result, options);
        var forged = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        forged["disposition"] = JsonSerializer.SerializeToNode(IngestionLedgerDisposition.Advanced, options);
        Assert.Throws<ArgumentException>(() => JsonSerializer.Deserialize<IngestionLedgerResult>(forged.ToJsonString(), options));
    }

    [Fact]
    public void PositionsRoundTripThroughCanonicalDocumentsWithDistinctMeanings()
    {
        IngestionPosition[] positions = [new IngestionDateRangePosition(new(2026, 9, 1), new(2026, 9, 2)),
            new IngestionCursorPosition("provider-format/v1", " opaque+/token== ", DateTimeOffset.Parse("2026-09-15T00:00:00Z")),
            new IngestionCursorPosition("provider-format/v1", null)];
        foreach (var position in positions)
        {
            var document = Document(Advance(position: position));
            var json = ExecutionDefinitionJsonSerializer.Serialize(document);
            var validation = IngestionLedgerDocuments.TryDeserialize(json, out var reopened, out var advance);
            Assert.True(validation.IsValid, string.Join(";", validation.Diagnostics.Select(x => x.Message)));
            Assert.Equal(position, advance!.Position);
            Assert.Equal(json, ExecutionDefinitionJsonSerializer.Serialize(reopened!));
        }
    }

    [Fact]
    public async Task OldRetryReturnsItsOriginalReceiptAfterLaterProgress()
    {
        var ledger = new InMemoryIngestionLedger();
        var original = Document(Advance());
        var first = await ledger.AdvanceAsync(original, CancellationToken.None);
        await ledger.AdvanceAsync(Document(Advance("operation/2", 1, 2)), CancellationToken.None);
        Assert.True(IngestionLedgerDocuments.TryDeserialize(ExecutionDefinitionJsonSerializer.Serialize(original), out var reopened, out _).IsValid);
        var replay = await ledger.AdvanceAsync(reopened!, CancellationToken.None);
        Assert.Equal(IngestionLedgerDisposition.Replayed, replay.Disposition);
        Assert.Equal(first.Receipt, replay.Receipt);
        Assert.Equal(1, replay.Receipt!.Entry.Revision);
        Assert.Equal(2, (await ledger.ReadAsync(Address, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task SamePublicationIdentityCannotBeReboundToDifferentProgressOrContent()
    {
        var ledger = new InMemoryIngestionLedger();
        var advance = Advance();
        await ledger.AdvanceAsync(Document(advance), CancellationToken.None);
        var result = await ledger.AdvanceAsync(Document(advance with { Publication = advance.Publication with { ContentFingerprint = "different" } }), CancellationToken.None);
        Assert.Equal("integrations.ledger.identity-conflict", result.DiagnosticCode);
        Assert.Equal(1, (await ledger.ReadAsync(Address, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task ConcurrentDifferentPublicationsCannotAdvanceFromTheSameRevision()
    {
        var ledger = new InMemoryIngestionLedger();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            await ledger.AdvanceAsync(Document(Advance("operation/" + i)), CancellationToken.None))));
        Assert.Single(results, x => x.Disposition == IngestionLedgerDisposition.Advanced);
        Assert.Equal(7, results.Count(x => x.DiagnosticCode == "integrations.ledger.revision-conflict"));
    }

    [Fact]
    public async Task FlowSourceDestinationAndPartitionIndependentlyScopeProgress()
    {
        var ledger = new InMemoryIngestionLedger();
        IngestionLedgerAddress[] scopes = [Address, Address with { Flow = "other" }, Address with { Source = "other" },
            Address with { Destination = "other" }, Address with { Partition = "other" }];
        Assert.Equal(scopes.Length, scopes.Select(scope => Document(Advance(address: scope)).Metadata.DefinitionId).Distinct().Count());
        foreach (var scope in scopes)
            Assert.Equal(IngestionLedgerDisposition.Advanced, (await ledger.AdvanceAsync(Document(Advance(address: scope)), CancellationToken.None)).Disposition);
    }

    [Fact]
    public async Task MigrationAndSkippedDateWindowsRequireExplicitResolution()
    {
        var ledger = new InMemoryIngestionLedger();
        await ledger.AdvanceAsync(Document(Advance()), CancellationToken.None);
        var skipped = await ledger.AdvanceAsync(Document(Advance("gap", 1, 3)), CancellationToken.None);
        Assert.Equal("integrations.ledger.position-transition", skipped.DiagnosticCode);
        var migrated = Advance("migration", 1, 2) with { Definition = new(Definition().DefinitionId, new("revision/2"), Definition().Fingerprint) };
        Assert.Equal("integrations.ledger.definition-migration-required", (await ledger.AdvanceAsync(Document(migrated), CancellationToken.None)).DiagnosticCode);
        Assert.Equal(1, (await ledger.ReadAsync(Address, CancellationToken.None))!.Revision);
    }

    [Fact]
    public async Task CursorFormatAndExhaustionAreNotSilentlyReset()
    {
        var ledger = new InMemoryIngestionLedger();
        var first = Document(Advance(position: new IngestionCursorPosition("v1", "z")));
        await ledger.AdvanceAsync(first, CancellationToken.None);
        // Tokens have no lexical ordering: a can follow z.
        Assert.Equal(IngestionLedgerDisposition.Advanced, (await ledger.AdvanceAsync(Document(Advance("op/2", 1, position: new IngestionCursorPosition("v1", "a"))), CancellationToken.None)).Disposition);
        Assert.Equal(IngestionLedgerDisposition.Conflict, (await ledger.AdvanceAsync(Document(Advance("wrong-format", 2, position: new IngestionCursorPosition("v2", "next"))), CancellationToken.None)).Disposition);
        await ledger.AdvanceAsync(Document(Advance("exhausted", 2, position: new IngestionCursorPosition("v1", null))), CancellationToken.None);
        Assert.Equal(IngestionLedgerDisposition.Conflict, (await ledger.AdvanceAsync(Document(Advance("restart", 3, position: new IngestionCursorPosition("v1", "start"))), CancellationToken.None)).Disposition);
        Assert.Equal(IngestionLedgerDisposition.Replayed, (await ledger.AdvanceAsync(first, CancellationToken.None)).Disposition);
    }

    [Fact]
    public async Task CancellationBeforeTheAtomicBoundaryDoesNotAdvance()
    {
        var ledger = new InMemoryIngestionLedger();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ledger.AdvanceAsync(Document(Advance()), new CancellationToken(true)));
        Assert.Null(await ledger.ReadAsync(Address, CancellationToken.None));
    }

    [Fact]
    public void InvalidAndChangedDocumentsCannotBeAdmitted()
    {
        Assert.Throws<ArgumentException>(() => Document(Advance(expected: -1)));
        Assert.Throws<ArgumentException>(() => Document(Advance(expected: long.MaxValue)));
        Assert.Throws<ArgumentException>(() => Document(Advance(position: new IngestionCursorPosition("v1", ""))));
        Assert.Throws<ArgumentException>(() => Document(Advance(position: new IngestionDateRangePosition(new(2026, 9, 2), new(2026, 9, 1)))));
        var json = ExecutionDefinitionJsonSerializer.Serialize(Document(Advance()));
        Assert.False(IngestionLedgerDocuments.TryDeserialize(json.Replace("operation/1", "operation/2", StringComparison.Ordinal), out _, out _).IsValid);
    }

    [Fact]
    public void CorruptStoredEvidenceCannotBecomeSuccessfulReplay()
    {
        var document = Document(Advance());
        var first = IngestionLedgerReduction.Evaluate(null, null, document);
        var receipt = first.Receipt!;
        var corrupt = receipt with { Entry = receipt.Entry with { Revision = 999 } };
        Assert.Equal("integrations.ledger.receipt-integrity", IngestionLedgerReduction.Evaluate(null, corrupt, document).DiagnosticCode);
        Assert.Equal("integrations.ledger.entry-integrity",
            IngestionLedgerReduction.Evaluate(receipt.Entry with { Revision = 0 }, null, document).DiagnosticCode);
    }

    [Fact]
    public void MissingCapabilitiesAndUnknownRetentionRemainDiagnosable()
    {
        var capabilities = new IngestionRecoveryCapabilities(false, false, false, false, null, null, null);
        Assert.Equal(7, capabilities.Validate(TimeSpan.FromDays(7)).Diagnostics.Length);
        var qualified = new IngestionRecoveryCapabilities(true, true, true, true, TimeSpan.FromDays(7), TimeSpan.FromDays(7), TimeSpan.FromDays(7));
        Assert.True(qualified.Validate(TimeSpan.FromDays(7)).IsValid);
        Assert.False(qualified.Validate(TimeSpan.FromDays(8)).IsValid);
        Assert.Equal(IngestionLedgerDisposition.Unknown, IngestionLedgerResult.Unknown().Disposition);
        Assert.Null(IngestionLedgerResult.Unknown().Receipt);
    }
}
