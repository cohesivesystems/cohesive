using System.Collections.Immutable;
using System.Text;
using Cohesive.Execution;
using Cohesive.Model.Serialization;

namespace Cohesive.Storage.Commits;

/// <summary>Observable evidence from an atomic commit or reconciliation.</summary>
public enum StorageCommitDisposition
{
    /// <summary>No conclusive receipt evidence is available; this does not prove non-commit.</summary>
    Unknown,
    /// <summary>This attempt atomically retained all writes and the result.</summary>
    Committed,
    /// <summary>An exact retained receipt supplies the result without reapplying writes.</summary>
    Replayed,
    /// <summary>This attempt did not apply because a presence or version precondition failed.</summary>
    PreconditionFailed,
    /// <summary>The operation identity is retained for a different intent.</summary>
    IdentityConflict,
    /// <summary>The requested placement or dependency cannot preserve the declared semantics.</summary>
    Unsupported
}

/// <summary>Receipt evidence retaining an exact application result independently of later item state.</summary>
public sealed record StorageCommitReceipt
{
    /// <summary>Creates complete committed evidence.</summary>
    /// <param name="reference">Exact operation identity and fingerprint.</param>
    /// <param name="result">Materialized result retained in the same transaction.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The result is unresolved.</exception>
    public StorageCommitReceipt(StorageCommitReference reference, PortableValue result)
    {
        Reference = reference ?? throw new ArgumentNullException(nameof(reference));
        Result = StorageCommitJson.RequireValue(result);
    }
    /// <summary>Exact committed intent reference.</summary>
    public StorageCommitReference Reference { get; }
    /// <summary>Retained result; never reconstructed from a later snapshot.</summary>
    public PortableValue Result { get; }

    /// <summary>Compares a retained operation before any state preconditions are evaluated.</summary>
    /// <param name="reference">Requested operation identity and content.</param>
    /// <returns>Exact replay or a structured identity conflict.</returns>
    public StorageCommitResult Reconcile(StorageCommitReference reference) => Reference == reference
        ? StorageCommitResult.Success(this, StorageCommitDisposition.Replayed)
        : StorageCommitResult.Rejected(StorageCommitDisposition.IdentityConflict, "storage.commit.identity-conflict",
            "The receipt address is retained for different commit content.", "/receiptAddress");
}

/// <summary>Closed success, uncertainty or diagnostic result.</summary>
public sealed record StorageCommitResult
{
    StorageCommitResult(StorageCommitDisposition disposition, StorageCommitReceipt? receipt,
        ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Disposition = disposition;
        Receipt = receipt;
        Diagnostics = diagnostics;
    }
    /// <summary>Evidence classification, including inconclusive receipt absence.</summary>
    public StorageCommitDisposition Disposition { get; }
    /// <summary>Exact retained evidence on success; otherwise null.</summary>
    public StorageCommitReceipt? Receipt { get; }
    /// <summary>Structured rejection or uncertainty evidence.</summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>Creates a committed or replayed result.</summary>
    /// <param name="receipt">Atomically retained evidence.</param>
    /// <param name="disposition">Committed or replayed.</param>
    /// <returns>A successful result with a receipt.</returns>
    /// <exception cref="ArgumentNullException">The receipt is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The disposition is not a success.</exception>
    public static StorageCommitResult Success(StorageCommitReceipt receipt, StorageCommitDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (disposition is not (StorageCommitDisposition.Committed or StorageCommitDisposition.Replayed))
            throw new ArgumentOutOfRangeException(nameof(disposition));
        return new(disposition, receipt, []);
    }

    /// <summary>Creates uncertainty or rejection without inventing receipt evidence.</summary>
    /// <param name="disposition">Unknown, precondition failure, identity conflict or unsupported.</param>
    /// <param name="code">Stable diagnostic code.</param>
    /// <param name="message">Actionable explanation.</param>
    /// <param name="location">Optional declaration JSON pointer.</param>
    /// <returns>A result without a receipt.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The disposition is invalid or a success.</exception>
    public static StorageCommitResult Rejected(StorageCommitDisposition disposition, string code, string message,
        string? location = null)
    {
        if (!Enum.IsDefined(disposition) || disposition is StorageCommitDisposition.Committed or StorageCommitDisposition.Replayed)
            throw new ArgumentOutOfRangeException(nameof(disposition));
        return new(disposition, null, [new(code, DiagnosticSeverity.Error, message, location)]);
    }

    /// <summary>Reports a rejected attempt whose item presence or version precondition failed.</summary>
    /// <param name="location">Optional pointer to the failed write in the declaration.</param>
    /// <returns>A rejection without a receipt; a different in-flight exact attempt may still commit.</returns>
    public static StorageCommitResult PreconditionFailed(string? location = null) =>
        Rejected(StorageCommitDisposition.PreconditionFailed, "storage.commit.precondition",
            "This attempt failed an item presence or version precondition. Reconcile again if another exact attempt is in flight.", location);

    /// <summary>Creates inconclusive receipt-absence evidence.</summary>
    /// <returns>Unknown; callers may reconcile again or retry the exact intent.</returns>
    public static StorageCommitResult Unknown() => Rejected(StorageCommitDisposition.Unknown,
        "storage.commit.unknown", "No conclusive receipt evidence is available. Retry the exact intent or reconcile again.");
}

/// <summary>Inspectable native atomic-boundary evidence for the bounded commit profile.</summary>
/// <param name="Target">One supported logical target, or null when all logical targets share this authority.</param>
/// <param name="SupportsMultiplePartitions">Whether one commit can include different logical partitions.</param>
/// <param name="SupportsQueryGuards">Whether the adapter profile supports the declared guard/read protocol.</param>
/// <param name="MaxAtomicItems">Optional native item limit, including the receipt.</param>
/// <param name="MaxSerializedPayloadBytes">Optional conservative adapter budget for serialized documents, including the receipt.</param>
/// <param name="MaxPartitionKeyUtf8Bytes">Optional maximum logical partition length in UTF-8 bytes.</param>
public sealed record StorageCommitCapabilities(string? Target, bool SupportsMultiplePartitions,
    bool SupportsQueryGuards, int? MaxAtomicItems = null, long? MaxSerializedPayloadBytes = null, int? MaxPartitionKeyUtf8Bytes = null)
{
    /// <summary>Validates placement and dependency support without performing I/O.</summary>
    /// <param name="intent">Materialized declaration.</param>
    /// <returns>A structured rejection or null when the profile supports this declaration.</returns>
    /// <exception cref="ArgumentNullException">The intent is null.</exception>
    public StorageCommitResult? Validate(StorageCommitIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        if (ValidateAddress(intent.ReceiptAddress) is { } invalidReceipt) return invalidReceipt;
        for (var index = 0; index < intent.Writes.Length; index++)
        {
            var address = intent.Writes[index].Address;
            if (ValidateAddress(address) is { } invalidWrite) return invalidWrite;
            if (!SupportsMultiplePartitions && address.Partition != intent.ReceiptAddress.Partition)
                return Unsupported("placement", "All writes and the receipt must share one logical partition.", $"/writes/{index}/address");
        }
        if (MaxAtomicItems is { } maximum && intent.Writes.Length >= maximum)
            return Unsupported("item-limit", $"The atomic boundary supports {maximum} items including its receipt.", "/writes");
        foreach (var dependency in intent.QueryDependencies)
        {
            if (!SupportsQueryGuards || dependency.Guard is null
                || !intent.Writes.Any(write => write.Address == dependency.Guard))
                return Unsupported("query-guard", "A query dependency requires a participating guard write and a qualified read-consistency profile.", "/queryDependencies");
        }
        return null;
    }

    /// <summary>Validates an address against the configured logical target.</summary>
    /// <param name="address">Requested item or receipt address.</param>
    /// <returns>A structured target rejection, or null.</returns>
    public StorageCommitResult? ValidateAddress(StorageCommitAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (Target is not null && address.Target != Target)
            return Unsupported("target", $"Bind this operation to the configured target '{Target}'.", "/target");
        return MaxPartitionKeyUtf8Bytes is { } maximum && Encoding.UTF8.GetByteCount(address.Partition) > maximum
            ? Unsupported("partition-limit", $"The partition identity exceeds the {maximum}-byte UTF-8 limit.", "/partition") : null;
    }

    static StorageCommitResult Unsupported(string code, string message, string location) =>
        StorageCommitResult.Rejected(StorageCommitDisposition.Unsupported, "storage.commit." + code, message, location);
}

/// <summary>Executes bounded atomic commits; no ambient transaction, workflow or compensation lifetime.</summary>
public interface IStorageCommitExecutor
{
    /// <summary>Effective guarantees and placement limits used for validation.</summary>
    StorageCommitCapabilities Capabilities { get; }

    /// <summary>Atomically applies conditional writes and retains an exact operation receipt.</summary>
    /// <param name="context">Cancellation and operation context.</param>
    /// <param name="intent">Complete immutable intent; retries must preserve it exactly.</param>
    /// <returns>Committed, replayed, rejected or uncertain evidence.</returns>
    /// <remarks>Transport errors or cancellation after submission can be ambiguous. Reconcile or retry the same
    /// intent. Never automatically re-evaluate a decision or overwrite it with new content under the same identity.</remarks>
    /// <exception cref="OperationCanceledException">Cancellation is observed; submission may already have occurred.</exception>
    ValueTask<StorageCommitResult> CommitAsync(OperationContext context, StorageCommitIntent intent);

    /// <summary>Looks up exact receipt evidence without applying writes or consulting later item state.</summary>
    /// <param name="context">Cancellation and operation context.</param>
    /// <param name="commit">Authority-scoped identity and expected fingerprint.</param>
    /// <returns>Exact replay, identity conflict, unsupported placement or unknown; absence is not proof of rollback.</returns>
    /// <exception cref="OperationCanceledException">Cancellation is observed during lookup.</exception>
    ValueTask<StorageCommitResult> ReconcileAsync(OperationContext context, StorageCommitReference commit);
}
