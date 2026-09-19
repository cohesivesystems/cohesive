using System.Text;
using Cohesive.Execution;
using Cohesive.Integrations;
using Cohesive.Model;
using Cohesive.Storage.Commits;

namespace Cohesive.Adapters.SQLite;

/// <summary>Write-once bounded ingestion evidence over the shared SQLite atomic commit executor.</summary>
/// <remarks>Apply Schema before use. This store owns its target namespace exclusively and never updates or
/// deletes evidence. A missing boundary does not prove acquisition was not dispatched. Processes and the
/// source adapter still own dispatch and ambiguity reconciliation. Retention lasts until external deletion;
/// hosts must protect the database throughout their declared recovery horizon.</remarks>
public sealed class SqliteIngestionWorkStore
{
    const string Target = "cohesive.ingestion.work/v1";
    static readonly ValueContract Text = new(new ScalarTypeRef(ScalarTypeKind.String));
    static readonly ValueContract Bytes = new(new ScalarTypeRef(ScalarTypeKind.Bytes));
    readonly SqliteStorageCommitExecutor executor;
    readonly int maximumContentBytes;
    readonly int maximumDocumentBytes;

    /// <summary>Creates a retention adapter without opening or migrating the database.</summary>
    /// <param name="database">Caller-owned FULL-durability database authority.</param>
    /// <param name="maximumContentBytes">Maximum exact payload bytes per boundary, checked before copying; defaults to 4 MiB.</param>
    /// <param name="maximumDocumentBytes">Maximum canonical UTF-8 metadata bytes, default 1 MiB.</param>
    /// <exception cref="ArgumentNullException">Database is null.</exception>
    /// <exception cref="ArgumentException">Database does not use FULL durability.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public SqliteIngestionWorkStore(SqliteDatabase database, int maximumContentBytes = 4 * 1024 * 1024,
        int maximumDocumentBytes = 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumContentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDocumentBytes);
        this.maximumContentBytes = maximumContentBytes;
        this.maximumDocumentBytes = maximumDocumentBytes;
        // JSON escaping can expand metadata sixfold; binary values use base64. Include tagged-value
        // and receipt/address overhead. The native read checks this bound before transferring text.
        var encodedLimit = Math.Max((long)maximumDocumentBytes * 6, ((long)maximumContentBytes + 2) / 3 * 4)
            + 128 * 1024;
        executor = new(database, maximumStoredPayloadBytes: encodedLimit);
    }

    /// <summary>The existing shared commit schema; no feature-local tables or SQL are introduced.</summary>
    public static SqliteSchema Schema => SqliteStorageCommitExecutor.Schema;

    /// <summary>Retains exact metadata and bytes together, or returns the original receipt/conflict.</summary>
    /// <param name="context">Operation context and cancellation. Cancellation around commit may require exact retry.</param>
    /// <param name="document">Canonical request, acquisition or prepared document. Predecessors must already be retained.</param>
    /// <param name="content">Caller-owned content; must remain unchanged until this method returns.</param>
    /// <returns>Shared commit outcome. Success verifies the stored document, content and original receipt.</returns>
    /// <exception cref="ArgumentNullException">Context or document is null.</exception>
    /// <exception cref="ArgumentException">The document, content, or limit is invalid.</exception>
    /// <exception cref="InvalidDataException">Predecessor evidence is missing/different, or retained evidence is corrupt.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed; preserve the exact input for reconciliation.</exception>
    /// <exception cref="System.Text.Json.JsonException">Persisted tagged storage values are malformed.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native storage access fails.</exception>
    public async ValueTask<StorageCommitResult> RetainAsync(OperationContext context, ExecutionDefinitionDocument document,
        ReadOnlyMemory<byte> content)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        var intent = Prepare(document, content);
        await ValidatePredecessors(context, document).ConfigureAwait(false);
        var result = await executor.CommitAsync(context, intent).ConfigureAwait(false);
        if (result.Disposition is StorageCommitDisposition.Committed or StorageCommitDisposition.Replayed)
        {
            var retained = await ReadAsync(context, document.Metadata.DefinitionId, document.Metadata.RevisionId).ConfigureAwait(false);
            if (retained is null || ExecutionDefinitionJsonSerializer.Serialize(retained.Value.Document) != ExecutionDefinitionJsonSerializer.Serialize(document)
                || !retained.Value.Content.Span.SequenceEqual(content.Span))
                throw new InvalidDataException("Retained work differs from the acknowledged boundary.");
        }
        return result;
    }

    /// <summary>Reopens and verifies one immutable boundary, including its original atomic receipt.</summary>
    /// <param name="context">Read cancellation and operation context.</param>
    /// <param name="id">Boundary document identity.</param>
    /// <param name="revision">Original attempt revision.</param>
    /// <returns>Verified document and read-only owned bytes, or null when no document is observed. Do not mutate returned memory.</returns>
    /// <exception cref="ArgumentNullException">Context is null.</exception>
    /// <exception cref="ArgumentException">An identity is invalid.</exception>
    /// <exception cref="InvalidDataException">Stored metadata, payload, size, token, or receipt fails verification.</exception>
    /// <exception cref="OperationCanceledException">Cancellation was observed.</exception>
    /// <exception cref="System.Text.Json.JsonException">Persisted tagged storage values are malformed.</exception>
    /// <exception cref="Microsoft.Data.Sqlite.SqliteException">Native storage access fails.</exception>
    public async ValueTask<(ExecutionDefinitionDocument Document, ReadOnlyMemory<byte> Content)?> ReadAsync(
        OperationContext context, ExecutionDefinitionId id, ExecutionRevisionId revision)
    {
        var retained = await ReadBoundaryAsync(context, id, revision).ConfigureAwait(false);
        if (retained is { } found) await ValidatePredecessors(context, found.Document).ConfigureAwait(false);
        return retained;
    }

    async ValueTask<(ExecutionDefinitionDocument Document, ReadOnlyMemory<byte> Content)?> ReadBoundaryAsync(
        OperationContext context, ExecutionDefinitionId id, ExecutionRevisionId revision)
    {
        var address = Address(id, revision);
        var metadata = await executor.ReadAsync(context, Item(address, "document")).ConfigureAwait(false);
        if (metadata is null) return null;
        var json = metadata.Value.Value?.String;
        if (json is null || Encoding.UTF8.GetByteCount(json) > maximumDocumentBytes
            || !IngestionWorkDocuments.TryDeserialize(json, out var document, out _).IsValid
            || document!.Metadata.DefinitionId != id || document.Metadata.RevisionId != revision)
            throw new InvalidDataException("Retained ingestion metadata is invalid or belongs to another boundary.");
        var payload = await executor.ReadAsync(context, Item(address, "content")).ConfigureAwait(false);
        if (payload?.Value.Value is not { Kind: ObservationValueKind.Bytes } value || value.Bytes.Length > maximumContentBytes)
            throw new InvalidDataException("Retained ingestion content is absent, invalid or oversized.");
        StorageCommitIntent intent;
        try { intent = Prepare(document, value.Bytes); }
        catch (ArgumentException exception) { throw new InvalidDataException("Retained ingestion content failed verification.", exception); }
        if (metadata.Token.Value != intent.Fingerprint || payload.Token.Value != intent.Fingerprint)
            throw new InvalidDataException("Retained evidence does not belong to its original atomic commit.");
        var original = await executor.ReconcileAsync(context, intent.Reference).ConfigureAwait(false);
        if (original.Disposition != StorageCommitDisposition.Replayed || original.Receipt!.Result != intent.Result)
            throw new InvalidDataException("Retained ingestion evidence has no matching original receipt.");
        return (document, value.Bytes);
    }

    async ValueTask ValidatePredecessors(OperationContext context, ExecutionDefinitionDocument document)
    {
        IngestionWorkDocuments.TryRead(document, out var item);
        if (item is IngestionAcquisitionRequest) return;
        var parentReference = item is IngestionAcquisitionReceipt acquired ? acquired.Request : ((IngestionPreparedPublication)item!).Acquisition;
        var parent = await ReadBoundaryAsync(context, parentReference.DefinitionId, parentReference.RevisionId).ConfigureAwait(false)
            ?? throw new InvalidDataException("Retain the original predecessor before dependent evidence.");
        if (item is IngestionAcquisitionReceipt)
        {
            if (!IngestionWorkDocuments.ValidateChain(parent.Document, document).IsValid)
                throw new InvalidDataException("Acquisition differs from its retained request.");
            return;
        }
        if (!IngestionWorkDocuments.TryRead(parent.Document, out var parentItem).IsValid || parentItem is not IngestionAcquisitionReceipt receipt)
            throw new InvalidDataException("Preparation requires retained acquisition evidence.");
        var request = await ReadBoundaryAsync(context, receipt.Request.DefinitionId, receipt.Request.RevisionId).ConfigureAwait(false)
            ?? throw new InvalidDataException("Preparation requires its retained original request.");
        if (!IngestionWorkDocuments.ValidateChain(request.Document, parent.Document, document).IsValid)
            throw new InvalidDataException("Preparation differs from its retained request or acquisition.");
    }

    StorageCommitIntent Prepare(ExecutionDefinitionDocument document, ReadOnlyMemory<byte> content)
    {
        if (content.Length > maximumContentBytes) throw new ArgumentException("Content exceeds the configured byte limit.", nameof(content));
        var validation = IngestionWorkDocuments.TryRead(document, out var item);
        if (!validation.IsValid) throw new ArgumentException("Invalid ingestion work: " + validation.Diagnostics[0].Code, nameof(document));
        var descriptor = item switch
        {
            IngestionAcquisitionRequest request => request.Selection,
            IngestionAcquisitionReceipt acquired => acquired.Content,
            IngestionPreparedPublication prepared => prepared.Content,
            _ => throw new ArgumentException("Unsupported ingestion boundary.", nameof(document))
        };
        if (!descriptor.Matches(content.Span)) throw new ArgumentException("Content does not match its exact descriptor.", nameof(content));
        var json = ExecutionDefinitionJsonSerializer.Serialize(document);
        if (Encoding.UTF8.GetByteCount(json) > maximumDocumentBytes) throw new ArgumentException("Document exceeds the configured byte limit.", nameof(document));
        var address = Address(document.Metadata.DefinitionId, document.Metadata.RevisionId);
        var metadata = PortableValue.Concrete(Text, ObservationValue.FromString(json));
        return new(address, [new(Item(address, "document"), metadata),
            new(Item(address, "content"), PortableValue.Concrete(Bytes, ObservationValue.FromBytes(content)))], metadata);
    }

    static StorageCommitAddress Address(ExecutionDefinitionId id, ExecutionRevisionId revision) => new(Target, id.Value, revision.Value);
    static StorageCommitAddress Item(StorageCommitAddress address, string role) => new(address.Target, address.Partition,
        Uri.EscapeDataString(address.Id) + "/" + role);
}
