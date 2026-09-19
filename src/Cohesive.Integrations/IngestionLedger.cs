using System.Text;
using System.Text.Json.Serialization;
using Cohesive.Execution;

namespace Cohesive.Integrations;

/// <summary>Stable independently advancing flow/source/destination/partition identity. Definition revision is deliberately not part of the address.</summary>
/// <param name="Flow">Application flow identity; distinguishes independent consumers of the same source and destination.</param>
/// <param name="Source">Logical source binding, including tenant scope where applicable.</param>
/// <param name="Destination">Logical sink binding, not a physical table name.</param>
/// <param name="Partition">Explicit source partition; use a stable named singleton for unpartitioned sources.</param>
public sealed record IngestionLedgerAddress(string Flow, string Source, string Destination, string Partition)
{
    internal void Validate()
    {
        Require(Flow); Require(Source); Require(Destination); Require(Partition);
    }

    internal static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static void Require(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (StrictUtf8.GetByteCount(value) > 1024)
            throw new ArgumentException("Ingestion identities must fit within 1024 UTF-8 bytes.");
    }
}

/// <summary>Source progress, distinct from sink data coverage and Process execution state.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(IngestionCursorPosition), "cursor")]
[JsonDerivedType(typeof(IngestionDateRangePosition), "dateRange")]
public abstract record IngestionPosition;

/// <summary>Opaque next-source cursor; values are preserved exactly and never sorted or interpreted.</summary>
/// <param name="Format">Source-owned cursor format/revision identifier.</param>
/// <param name="Token">Next token, or null for explicit source exhaustion; missing ledger state means no committed progress.</param>
/// <param name="ValidUntilUtc">Optional source-declared UTC expiry. Expiry prevents resumption, not historical receipt reconciliation.</param>
public sealed record IngestionCursorPosition(string Format, string? Token, DateTimeOffset? ValidUntilUtc = null) : IngestionPosition;

/// <summary>Last completed forward window. Its end is the next forward-selection boundary, not proof of session coverage.</summary>
/// <param name="StartInclusive">Inclusive source date boundary.</param>
/// <param name="EndExclusive">Exclusive source date boundary; must follow the start.</param>
/// <remarks>Subsequent forward windows must abut. Overlap/lookback reads may occur inside acquisition,
/// but their ledger advancement must describe only the newly completed forward window. Gaps/backfills use a separate flow.</remarks>
public sealed record IngestionDateRangePosition(DateOnly StartInclusive, DateOnly EndExclusive) : IngestionPosition;

/// <summary>Exact sink acknowledgment supplied by a trusted publication adapter, not inferred from a current sink snapshot.</summary>
/// <param name="PublicationId">Stable publication identity within the ledger scope, frozen before the first sink effect.</param>
/// <param name="ContentFingerprint">Exact source work and prepared sink intent fingerprint owned by the publisher.</param>
/// <param name="ReceiptReference">Retained original sink receipt locator, usable throughout the recovery horizon.</param>
/// <remarks>Construction is a declaration, not proof of a commit. The qualified adapter must verify that
/// the receipt binds this publication/content and scope before invoking ledger advancement. Unknown outcomes cannot supply this evidence.</remarks>
public sealed record IngestionPublicationReceipt(string PublicationId, string ContentFingerprint, string ReceiptReference);

/// <summary>Persisted proposed advancement, prepared with an exact expected revision before publication and completed with confirmed sink evidence.</summary>
/// <param name="Address">Independently advancing ledger scope.</param>
/// <param name="Definition">Exact ingestion definition revision and fingerprint; migration never happens implicitly.</param>
/// <param name="ExpectedRevision">Expected current revision; zero requires no previous entry.</param>
/// <param name="Position">Next committed source progress.</param>
/// <param name="Publication">Confirmed exact sink receipt; never an unknown-outcome placeholder.</param>
public sealed record IngestionLedgerAdvance(IngestionLedgerAddress Address, ExecutionDefinitionReference Definition,
    long ExpectedRevision, IngestionPosition Position, IngestionPublicationReceipt Publication);

/// <summary>Committed source progress. The monotonically increasing revision is the ledger CAS token.</summary>
/// <param name="Address">Ledger scope.</param>
/// <param name="Definition">Pinned flow meaning.</param>
/// <param name="Revision">Positive committed ledger revision.</param>
/// <param name="Position">Committed source progress, not sink coverage.</param>
public sealed record IngestionLedgerEntry(IngestionLedgerAddress Address, ExecutionDefinitionReference Definition,
    long Revision, IngestionPosition Position);

/// <summary>Original advancement evidence retained atomically with progress, independently of subsequent entries.</summary>
/// <param name="Advance">Exact immutable advancement document reference.</param>
/// <param name="Entry">Original resulting entry; do not reconstruct it from current progress.</param>
public sealed record IngestionLedgerReceipt(ExecutionDefinitionReference Advance, IngestionLedgerEntry Entry);

/// <summary>Ledger evidence classification; conflicts and uncertainty do not roll back already-published sink data.</summary>
public enum IngestionLedgerDisposition
{
    /// <summary>Advancement and receipt committed atomically.</summary>
    Advanced,
    /// <summary>Exact prior receipt returned before checking current progress.</summary>
    Replayed,
    /// <summary>Identity, scope, definition, position or revision conflict.</summary>
    Conflict,
    /// <summary>Outcome cannot be established; retry exact work or reconcile, never invent a new identity.</summary>
    Unknown,
}

/// <summary>Ledger outcome with either original committed evidence or an actionable diagnostic.</summary>
public sealed record IngestionLedgerResult
{
    [JsonConstructor]
    private IngestionLedgerResult(IngestionLedgerDisposition disposition, IngestionLedgerReceipt? receipt, string? diagnosticCode)
    {
        if (!Enum.IsDefined(disposition)) throw new ArgumentOutOfRangeException(nameof(disposition));
        var succeeded = disposition is IngestionLedgerDisposition.Advanced or IngestionLedgerDisposition.Replayed;
        if (succeeded ? receipt is null || diagnosticCode is not null : receipt is not null || string.IsNullOrWhiteSpace(diagnosticCode))
            throw new ArgumentException("Successful ledger results require a receipt only; conflict/unknown results require a diagnostic only.");
        Disposition = disposition;
        Receipt = receipt;
        DiagnosticCode = diagnosticCode;
    }
    /// <summary>Outcome classification.</summary>
    public IngestionLedgerDisposition Disposition { get; }
    /// <summary>Original committed evidence on advancement/replay only.</summary>
    public IngestionLedgerReceipt? Receipt { get; }
    /// <summary>Stable diagnostic code on conflict or uncertainty.</summary>
    public string? DiagnosticCode { get; }
    internal static IngestionLedgerResult Success(IngestionLedgerReceipt receipt, bool replayed) =>
        new(replayed ? IngestionLedgerDisposition.Replayed : IngestionLedgerDisposition.Advanced, receipt, null);
    internal static IngestionLedgerResult Conflict(string code) => new(IngestionLedgerDisposition.Conflict, null, "integrations.ledger." + code);
    /// <summary>Represents inconclusive evidence without making a rejection claim.</summary>
    /// <returns>An unknown outcome with no receipt.</returns>
    public static IngestionLedgerResult Unknown() => new(IngestionLedgerDisposition.Unknown, null, "integrations.ledger.unknown");
}

/// <summary>Source progress authority with atomic CAS and independent exact-operation receipts.</summary>
/// <remarks>Adapters must atomically evaluate receipt-before-revision semantics and retain receipts across later advances.
/// Inconclusive visibility yields Unknown, not a conflict. Exceptions or cancellation after dispatch may mean committed work.
/// Sink publication is outside this boundary; call only with confirmed evidence. No automatic rebase is permitted.</remarks>
public interface IIngestionLedger
{
    /// <summary>Observes current source progress for planning a new acquisition.</summary>
    /// <param name="address">Stable ledger scope.</param>
    /// <param name="cancellationToken">Read cancellation.</param>
    /// <returns>Observed entry, or null when no entry is observed; a read is not an advancement guarantee.</returns>
    ValueTask<IngestionLedgerEntry?> ReadAsync(IngestionLedgerAddress address, CancellationToken cancellationToken);

    /// <summary>Advances from exact immutable work, or recovers the original committed result.</summary>
    /// <param name="advance">Validated versioned advancement document. Preserve it across all retries.</param>
    /// <param name="cancellationToken">Cancellation; unknown completion must be reconciled with the same document.</param>
    /// <returns>Original success evidence, conflict, or uncertainty.</returns>
    /// <exception cref="ArgumentException">The document is invalid or unsupported.</exception>
    ValueTask<IngestionLedgerResult> AdvanceAsync(ExecutionDefinitionDocument advance, CancellationToken cancellationToken);
}
