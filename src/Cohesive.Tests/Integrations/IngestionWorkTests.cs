using System.Text;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model.Serialization;

namespace Cohesive.Tests.Integrations;

public sealed class IngestionWorkTests
{
    static readonly DateTimeOffset Now = new(2026, 9, 18, 14, 0, 0, TimeSpan.Zero);
    static readonly ExecutionProvenance Provenance = new(new("work-tests", "1"), new("tests/work"), DocumentOrigin.Generated);
    static readonly IngestionLedgerAddress Address = new("flow/a", "source/a", "sink/a", "partition/a");
    static readonly byte[] Bytes = Encoding.UTF8.GetBytes("exact retained bytes\n");
    static IngestionContentReference Content(string locator) => IngestionContentReference.Describe(locator, Bytes, "application/json");
    static ExecutionDefinitionReference Reference(ExecutionDefinitionDocument doc) => new(doc.Metadata.DefinitionId, doc.Metadata.RevisionId, doc.Metadata.Fingerprint);
    static readonly ExecutionDefinitionReference Definition = Reference(ExecutionDefinitionDocument.Create(new("test.flow"), new("flow"), new("v1"), Address, Provenance));
    static IngestionAcquisitionRequest Request(string operation = "operation/1", long? expected = 0) =>
        new(Address, Definition, Definition, operation, "attempt/1", Content("selection"), expected, Now);
    static ExecutionDefinitionDocument Document(IngestionWorkItem item) => IngestionWorkDocuments.Create(item, Provenance);
    static ExecutionDefinitionDocument Reopen(ExecutionDefinitionDocument document)
    {
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        var validation = IngestionWorkDocuments.TryDeserialize(json, out var reopened, out _);
        Assert.True(validation.IsValid, string.Join(";", validation.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.Equal(json, ExecutionDefinitionJsonSerializer.Serialize(reopened!));
        return reopened!;
    }
    static (ExecutionDefinitionDocument Request, ExecutionDefinitionDocument Acquisition, ExecutionDefinitionDocument Prepared) Chain(
        string operation = "operation/1", long? expected = 0, IngestionPosition? position = null)
    {
        var request = Document(Request(operation, expected));
        var acquired = Document(new IngestionAcquisitionReceipt(Reference(request), Content("page"), Now.AddSeconds(1)));
        var prepared = Document(new IngestionPreparedPublication(Reference(acquired), Definition, Content("write"),
            expected is null ? null : position ?? new IngestionDateRangePosition(new(2026, 9, 18), new(2026, 9, 19)), Now.AddSeconds(2)));
        return (request, acquired, prepared);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryBoundaryReopensCanonicallyWithoutInferringACommitProfile(bool separateLedger)
    {
        var chain = Chain(expected: separateLedger ? 0 : null);
        Assert.True(IngestionWorkDocuments.ValidateChain(Reopen(chain.Request)).IsValid);
        Assert.True(IngestionWorkDocuments.ValidateChain(Reopen(chain.Request), Reopen(chain.Acquisition)).IsValid);
        Assert.True(IngestionWorkDocuments.ValidateChain(Reopen(chain.Request), Reopen(chain.Acquisition), Reopen(chain.Prepared)).IsValid);
        Assert.False(IngestionWorkDocuments.ValidateChain(chain.Request, prepared: chain.Prepared).IsValid);
    }

    [Fact]
    public void BytesAreVerifiedWithoutTrustingTheLocatorOrDigestAlone()
    {
        var content = Content("page");
        Assert.True(content.Matches(Bytes));
        Assert.False(content.Matches(Encoding.UTF8.GetBytes("Exact retained bytes\n")));
        Assert.False(content.Matches(Bytes.AsSpan(1)));
        Assert.False((content with { Length = Bytes.Length + 1 }).Matches(Bytes));
        Assert.Throws<ArgumentException>(() => (content with { Sha256 = content.Sha256.ToUpperInvariant() }).Matches(Bytes));
        Assert.True(IngestionContentReference.Describe("empty", [], "application/octet-stream").Matches([]));
        Assert.Throws<ArgumentException>(() => Document(Request() with { Selection = content with { MediaType = "" } }));
        Assert.Throws<ArgumentException>(() => Document(Request() with { Selection = content with { Length = -1 } }));
    }

    [Fact]
    public void ChangedRequestMetadataCannotReuseAnAcquisitionReceipt()
    {
        var chain = Chain();
        IngestionAcquisitionRequest[] changes = [
            Request() with { ExpectedLedgerRevision = 1 },
            Request() with { AttemptId = "attempt/2" },
            Request() with { Definition = new(Definition.DefinitionId, new("v2"), Definition.Fingerprint) },
            Request() with { Transformation = new(Definition.DefinitionId, new("v2"), Definition.Fingerprint) },
            Request() with { Selection = Content("different-locator") },
            Request() with { Selection = Content("selection") with { MediaType = "application/octet-stream" } },
            Request() with { Address = Address with { Destination = "other" } },
            Request() with { SelectedAtUtc = Now.AddSeconds(-1) }
        ];
        foreach (var change in changes)
            Assert.False(IngestionWorkDocuments.ValidateChain(Document(change), chain.Acquisition, chain.Prepared).IsValid);
        Assert.True(IngestionWorkDocuments.ValidateChain(Document(Request()), chain.Acquisition, chain.Prepared).IsValid);
    }

    [Fact]
    public void ChangedAcquisitionCannotReusePreparedWorkEvenWhenBytesMatch()
    {
        var chain = Chain();
        var changed = Document(new IngestionAcquisitionReceipt(Reference(chain.Request), Content("page"), Now.AddMilliseconds(1500)));
        Assert.Equal(chain.Acquisition.Metadata.DefinitionId, changed.Metadata.DefinitionId);
        Assert.False(IngestionWorkDocuments.ValidateChain(chain.Request, changed, chain.Prepared).IsValid);
    }

    [Fact]
    public void PreparationCannotSwitchThePinnedTransformation()
    {
        var chain = Chain();
        var changed = Document(new IngestionPreparedPublication(Reference(chain.Acquisition),
            new(Definition.DefinitionId, new("changed-transform"), Definition.Fingerprint), Content("write"),
            new IngestionDateRangePosition(new(2026, 9, 18), new(2026, 9, 19)), Now.AddSeconds(2)));
        var result = IngestionWorkDocuments.ValidateChain(chain.Request, chain.Acquisition, changed);
        Assert.Equal("integrations.work.transformation", Assert.Single(result.Diagnostics).Code);
    }

    [Fact]
    public void LedgerProfileChronologyAndOpaquePositionValidationAreExplicit()
    {
        var chain = Chain();
        var noLedger = Document(new IngestionPreparedPublication(Reference(chain.Acquisition), Definition, Content("write"), null, Now.AddSeconds(2)));
        Assert.False(IngestionWorkDocuments.ValidateChain(chain.Request, chain.Acquisition, noLedger).IsValid);
        var early = Document(new IngestionAcquisitionReceipt(Reference(chain.Request), Content("page"), Now.AddTicks(-1)));
        Assert.False(IngestionWorkDocuments.ValidateChain(chain.Request, early).IsValid);
        var earlyPrepared = Document(new IngestionPreparedPublication(Reference(chain.Acquisition), Definition, Content("write"),
            new IngestionCursorPosition("v1", " opaque token "), Now));
        Assert.False(IngestionWorkDocuments.ValidateChain(chain.Request, chain.Acquisition, earlyPrepared).IsValid);
        foreach (var position in new IngestionPosition[] { new IngestionCursorPosition("v1", " opaque token "), new IngestionCursorPosition("v1", null) })
        {
            var cursor = Chain(position: position);
            Assert.True(IngestionWorkDocuments.ValidateChain(cursor.Request, cursor.Acquisition, Reopen(cursor.Prepared)).IsValid);
        }
        Assert.Throws<ArgumentException>(() => Chain(position: new IngestionCursorPosition("v1", "")));
        Assert.Throws<ArgumentException>(() => Document(Request() with { SelectedAtUtc = Now.ToOffset(TimeSpan.FromHours(1)) }));
        Assert.Throws<ArgumentException>(() => Document(Request(expected: long.MaxValue)));
        Assert.Throws<ArgumentException>(() => Document(Request(expected: -1)));
    }

    [Fact]
    public async Task LostLedgerAcknowledgmentReplaysOriginalFrozenWorkAfterLaterProgress()
    {
        var first = Chain();
        var receipt = new IngestionPublicationReceipt("operation/1", Content("write").Sha256, "sink/receipt/1");
        var advance = IngestionWorkDocuments.CreateLedgerAdvance(Reopen(first.Request), Reopen(first.Acquisition), Reopen(first.Prepared), receipt, Provenance);
        var ledger = new InMemoryIngestionLedger();
        var committed = await ledger.AdvanceAsync(advance, CancellationToken.None); // simulate losing this reply
        var later = Chain("operation/2", 1, new IngestionDateRangePosition(new(2026, 9, 19), new(2026, 9, 20)));
        var next = IngestionWorkDocuments.CreateLedgerAdvance(later.Request, later.Acquisition, later.Prepared,
            receipt with { PublicationId = "operation/2", ReceiptReference = "sink/receipt/2" }, Provenance);
        Assert.Equal(IngestionLedgerDisposition.Advanced, (await ledger.AdvanceAsync(next, CancellationToken.None)).Disposition);
        var retry = IngestionWorkDocuments.CreateLedgerAdvance(Reopen(first.Request), Reopen(first.Acquisition), Reopen(first.Prepared), receipt, Provenance);
        Assert.Equal(ExecutionDefinitionJsonSerializer.Serialize(advance), ExecutionDefinitionJsonSerializer.Serialize(retry));
        var replay = await ledger.AdvanceAsync(retry, CancellationToken.None);
        Assert.Equal(IngestionLedgerDisposition.Replayed, replay.Disposition);
        Assert.Equal(committed.Receipt, replay.Receipt);
        Assert.Equal(2, (await ledger.ReadAsync(Address, CancellationToken.None))!.Revision);
        var stale = Chain("operation/stale", 0);
        var rejected = IngestionWorkDocuments.CreateLedgerAdvance(stale.Request, stale.Acquisition, stale.Prepared,
            receipt with { PublicationId = "operation/stale" }, Provenance);
        Assert.Equal(IngestionLedgerDisposition.Conflict, (await ledger.AdvanceAsync(rejected, CancellationToken.None)).Disposition);
    }

    [Fact]
    public void UnrelatedPublicationReceiptCannotAuthorizeSourceProgress()
    {
        var chain = Chain();
        var receipt = new IngestionPublicationReceipt("operation/1", Content("write").Sha256, "receipt");
        Assert.Throws<ArgumentException>(() => IngestionWorkDocuments.CreateLedgerAdvance(chain.Request, chain.Acquisition, chain.Prepared,
            receipt with { PublicationId = "different" }, Provenance));
        Assert.Throws<ArgumentException>(() => IngestionWorkDocuments.CreateLedgerAdvance(chain.Request, chain.Acquisition, chain.Prepared,
            receipt with { ContentFingerprint = Content("write").Sha256 + "changed" }, Provenance));
        var atomic = Chain(expected: null);
        Assert.Throws<ArgumentException>(() => IngestionWorkDocuments.CreateLedgerAdvance(atomic.Request, atomic.Acquisition, atomic.Prepared, receipt, Provenance));
    }

    [Fact]
    public void RecomputedEnvelopeCannotDisguiseAnArbitraryBoundaryIdentity()
    {
        var request = Document(Request());
        var forged = ExecutionDefinitionDocument.Create(request.Kind, new("unrelated"), request.Metadata.RevisionId, (IngestionWorkItem)Request(), Provenance);
        Assert.False(IngestionWorkDocuments.TryRead(forged, out _).IsValid);
        var json = ExecutionDefinitionJsonSerializer.Serialize(request);
        Assert.False(IngestionWorkDocuments.TryDeserialize(json.Replace("attempt/1", "attempt/2", StringComparison.Ordinal), out _, out _).IsValid);
    }
}
