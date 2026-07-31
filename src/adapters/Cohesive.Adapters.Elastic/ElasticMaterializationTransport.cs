using System.Collections.Immutable;

namespace Cohesive.Adapters.Elastic;

/// <summary>
/// Internal physical Elasticsearch seam used by the materialization target. The seam deliberately owns only
/// provider operations and bounded wire representations; generation lifecycle semantics remain in the target.
/// </summary>
internal interface IElasticMaterializationTransport
{
    ValueTask<ElasticDocumentReadResult> GetDocumentAsync(
        string index,
        string id,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticDocumentWriteResult> CreateDocumentAsync(
        string index,
        string id,
        ElasticJsonObject source,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticDocumentWriteResult> ReplaceDocumentAsync(
        string index,
        string id,
        ElasticJsonObject source,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticDocumentWriteResult> DeleteDocumentAsync(
        string index,
        string id,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticIndexCreateResult> CreateIndexAsync(
        string index,
        ElasticJsonObject body,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<bool> IndexExistsAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticAcknowledgedResult> AddWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticAcknowledgedResult> RemoveWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticAcknowledgedResult> RefreshAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticAcknowledgedResult> DeleteIndexAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticOwnedIndexDeleteResult> DeleteOwnedIndexAsync(
        string index,
        string ownerAlias,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticMultiGetResult> MultiGetAsync(
        string index,
        ImmutableArray<string> ids,
        ElasticMultiGetSourceProjection sourceProjection,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticBulkResult> BulkAsync(
        ImmutableArray<ElasticBulkOperation> operations,
        long maximumWireBytes,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticScanPage> ScanAsync(
        ElasticScanRequest request,
        CancellationToken cancellationToken);

    ValueTask<ElasticCountResult> CountAsync(
        string index,
        ElasticJsonObject query,
        int maximumResponseBytes,
        CancellationToken cancellationToken);

    ValueTask<ElasticAliasCasResult> CompareExchangeAliasAsync(
        ElasticAliasCasRequest request,
        CancellationToken cancellationToken);

    ValueTask<ElasticAliasSnapshot> InspectAliasesAsync(
        ImmutableArray<string> aliases,
        int maximumResponseBytes,
        CancellationToken cancellationToken);
}

internal readonly record struct ElasticDocumentConcurrencyToken(long SequenceNumber, long PrimaryTerm)
{
    internal void Validate(string parameterName)
    {
        if (SequenceNumber < 0 || PrimaryTerm <= 0)
        {
            throw new ArgumentException(
                "An Elasticsearch concurrency token requires a nonnegative sequence number and positive primary term.",
                parameterName);
        }
    }
}

internal sealed record ElasticDocumentReadResult(
    bool Found,
    byte[] Source,
    ElasticDocumentConcurrencyToken? ConcurrencyToken,
    long? ExternalVersion);

internal enum ElasticDocumentWriteDisposition
{
    Applied = 0,
    Conflict = 1,
    NotFound = 2
}

internal sealed record ElasticDocumentWriteResult(
    ElasticDocumentWriteDisposition Disposition,
    int StatusCode,
    ElasticDocumentConcurrencyToken? ConcurrencyToken,
    long? ExternalVersion);

internal enum ElasticIndexCreateDisposition
{
    Created = 0,
    AlreadyExists = 1
}

internal sealed record ElasticIndexCreateResult(
    ElasticIndexCreateDisposition Disposition,
    int StatusCode,
    bool Acknowledged,
    bool ShardsAcknowledged,
    string Index);

internal enum ElasticAcknowledgedDisposition
{
    Applied = 0,
    NotFound = 1
}

internal sealed record ElasticAcknowledgedResult(
    ElasticAcknowledgedDisposition Disposition,
    int StatusCode,
    bool Acknowledged);

internal enum ElasticOwnedIndexDeleteDisposition
{
    Applied = 0,
    OwnershipConflict = 1
}

internal sealed record ElasticOwnedIndexDeleteResult(
    ElasticOwnedIndexDeleteDisposition Disposition,
    int StatusCode,
    bool Acknowledged);

internal sealed record ElasticMultiGetDocument(
    string Id,
    bool Found,
    byte[] Source,
    ElasticDocumentConcurrencyToken? ConcurrencyToken,
    long? ExternalVersion);

internal sealed record ElasticMultiGetResult(ImmutableArray<ElasticMultiGetDocument> Documents);

internal enum ElasticMultiGetSourceProjection
{
    Full = 0,
    MaterializationMetadata = 1
}

internal enum ElasticBulkOperationKind
{
    Index = 0,
    Delete = 1
}

internal sealed record ElasticBulkOperation
{
    internal ElasticBulkOperation(
        ElasticBulkOperationKind kind,
        string index,
        string id,
        long externalVersion,
        ElasticJsonObject? source = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported Elasticsearch bulk operation kind.");
        }

        Index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        Id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        if (externalVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(externalVersion),
                externalVersion,
                "An external Elasticsearch document version must be positive.");
        }

        if ((kind == ElasticBulkOperationKind.Index && source is null)
            || (kind == ElasticBulkOperationKind.Delete && source is not null))
        {
            throw new ArgumentException(
                "An index operation requires a JSON source and a delete operation must omit it.",
                nameof(source));
        }

        Kind = kind;
        ExternalVersion = externalVersion;
        Source = source;
    }

    internal ElasticBulkOperationKind Kind { get; }

    internal string Index { get; }

    internal string Id { get; }

    internal long ExternalVersion { get; }

    internal ElasticJsonObject? Source { get; }
}

internal sealed record ElasticBulkItemResult(
    int Ordinal,
    ElasticBulkOperationKind Kind,
    string Index,
    string Id,
    int StatusCode,
    string? Result,
    string? ErrorType,
    string? ErrorReason,
    long? ExternalVersion,
    ElasticDocumentConcurrencyToken? ConcurrencyToken);

internal sealed record ElasticBulkResult(
    long WireBytes,
    long TookMilliseconds,
    bool Errors,
    ImmutableArray<ElasticBulkItemResult> Items);

internal sealed record ElasticScanRequest
{
    internal ElasticScanRequest(
        string index,
        ElasticJsonObject query,
        string sortField,
        string? afterSortValue,
        int maximumItems,
        int maximumResponseBytes)
    {
        Index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        Query = query ?? throw new ArgumentNullException(nameof(query));
        SortField = ElasticMaterializationPhysicalNames.RequireValue(sortField, nameof(sortField));
        AfterSortValue = afterSortValue is null
            ? null
            : ElasticMaterializationPhysicalNames.RequireValue(afterSortValue, nameof(afterSortValue));
        if (maximumItems <= 0 || maximumItems == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumItems),
                maximumItems,
                "A scan page limit must be positive and leave room for one look-ahead item.");
        }

        MaximumItems = maximumItems;
        MaximumResponseBytes = ElasticMaterializationPhysicalNames.RequirePositive(
            maximumResponseBytes,
            nameof(maximumResponseBytes));
    }

    internal string Index { get; }

    internal ElasticJsonObject Query { get; }

    internal string SortField { get; }

    internal string? AfterSortValue { get; }

    internal int MaximumItems { get; }

    internal int MaximumResponseBytes { get; }
}

internal sealed record ElasticScanHit(string Id, string SortValue, byte[] Source);

internal sealed record ElasticScanPage(
    ImmutableArray<ElasticScanHit> Hits,
    string? NextAfterSortValue,
    long TookMilliseconds);

internal sealed record ElasticCountResult(long Count, long TookMilliseconds);

internal sealed record ElasticAliasCasRequest
{
    internal ElasticAliasCasRequest(
        string markerIndex,
        string expectedMarkerAlias,
        string nextMarkerAlias,
        string? readAlias,
        string? expectedReadIndex,
        string? nextReadIndex,
        int maximumResponseBytes,
        ElasticJsonObject? readAliasFilter = null,
        string? routing = null,
        string? searchRouting = null,
        string? indexRouting = null,
        bool? isWriteIndex = null,
        string? expectedNextOwnerAlias = null)
    {
        MarkerIndex = ElasticMaterializationPhysicalNames.RequireConcreteIndex(markerIndex, nameof(markerIndex));
        ExpectedMarkerAlias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(
            expectedMarkerAlias,
            nameof(expectedMarkerAlias));
        NextMarkerAlias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(
            nextMarkerAlias,
            nameof(nextMarkerAlias));
        if (string.Equals(ExpectedMarkerAlias, NextMarkerAlias, StringComparison.Ordinal))
        {
            throw new ArgumentException("A marker compare-and-swap must advance to a distinct alias.", nameof(nextMarkerAlias));
        }

        var hasReadPublication = readAlias is not null;
        if (hasReadPublication != (nextReadIndex is not null)
            || !hasReadPublication
            && (expectedReadIndex is not null
                || readAliasFilter is not null
                || routing is not null
                || searchRouting is not null
                || indexRouting is not null
                || isWriteIndex is not null))
        {
            throw new ArgumentException(
                "A marker-only compare-and-swap omits every read-alias field; publication requires a read alias and next index.",
                nameof(readAlias));
        }

        ReadAlias = readAlias is null
            ? null
            : ElasticMaterializationPhysicalNames.RequireConcreteAlias(readAlias, nameof(readAlias));
        ExpectedReadIndex = expectedReadIndex is null
            ? null
            : ElasticMaterializationPhysicalNames.RequireConcreteIndex(expectedReadIndex, nameof(expectedReadIndex));
        NextReadIndex = nextReadIndex is null
            ? null
            : ElasticMaterializationPhysicalNames.RequireConcreteIndex(nextReadIndex, nameof(nextReadIndex));
        MaximumResponseBytes = ElasticMaterializationPhysicalNames.RequirePositive(
            maximumResponseBytes,
            nameof(maximumResponseBytes));
        ReadAliasFilter = readAliasFilter;
        Routing = OptionalValue(routing, nameof(routing));
        SearchRouting = OptionalValue(searchRouting, nameof(searchRouting));
        IndexRouting = OptionalValue(indexRouting, nameof(indexRouting));
        IsWriteIndex = isWriteIndex;
        ExpectedNextOwnerAlias = expectedNextOwnerAlias is null
            ? null
            : ElasticMaterializationPhysicalNames.RequireConcreteAlias(
                expectedNextOwnerAlias,
                nameof(expectedNextOwnerAlias));
        if (ExpectedNextOwnerAlias is not null && NextReadIndex is null)
        {
            throw new ArgumentException(
                "An atomic next-generation ownership fence requires a published next read index.",
                nameof(expectedNextOwnerAlias));
        }

        if (ReadAlias is not null
            && (string.Equals(ReadAlias, ExpectedMarkerAlias, StringComparison.Ordinal)
                || string.Equals(ReadAlias, NextMarkerAlias, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "The stable read alias must be distinct from both compare-and-swap marker aliases.",
                nameof(readAlias));
        }
    }

    static string? OptionalValue(string? value, string parameterName) =>
        value is null ? null : ElasticMaterializationPhysicalNames.RequireValue(value, parameterName);

    internal string MarkerIndex { get; }

    internal string ExpectedMarkerAlias { get; }

    internal string NextMarkerAlias { get; }

    internal string? ReadAlias { get; }

    internal string? ExpectedReadIndex { get; }

    internal string? NextReadIndex { get; }

    internal int MaximumResponseBytes { get; }

    internal ElasticJsonObject? ReadAliasFilter { get; }

    internal string? Routing { get; }

    internal string? SearchRouting { get; }

    internal string? IndexRouting { get; }

    internal bool? IsWriteIndex { get; }

    internal string? ExpectedNextOwnerAlias { get; }
}

internal enum ElasticAliasCasDisposition
{
    Applied = 0,
    Conflict = 1
}

internal sealed record ElasticAliasCasResult(
    ElasticAliasCasDisposition Disposition,
    int StatusCode,
    bool Acknowledged);

internal sealed record ElasticAliasBinding(
    string Alias,
    string Index,
    bool? IsHidden,
    bool? IsWriteIndex,
    string? Routing,
    string? SearchRouting,
    string? IndexRouting,
    byte[] Filter);

internal sealed record ElasticAliasSnapshot(ImmutableArray<ElasticAliasBinding> Bindings);

/// <summary>Sanitized bounded failure from Elasticsearch materialization transport I/O or response validation.</summary>
/// <remarks>
/// This exception never exposes provider response bodies or credentials. <see cref="Retryable"/> classifies whether
/// retrying the exact operation may succeed; callers must still honor operation idempotency and ownership fences.
/// </remarks>
public sealed class ElasticMaterializationTransportException : Exception
{
    internal ElasticMaterializationTransportException(
        int? statusCode,
        string errorType,
        bool retryable,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ErrorType = ElasticMaterializationPhysicalNames.RequireValue(errorType, nameof(errorType));
        Retryable = retryable;
    }

    /// <summary>Gets the HTTP status code, or <see langword="null"/> when no trustworthy response status exists.</summary>
    public int? StatusCode { get; }

    /// <summary>Gets the sanitized stable provider or adapter error classification.</summary>
    public string ErrorType { get; }

    /// <summary>Gets whether retrying the exact idempotent operation may succeed.</summary>
    public bool Retryable { get; }
}

internal static class ElasticMaterializationRetryPolicy
{
    internal static bool IsRetryableStatus(int statusCode) =>
        statusCode is 408 or 425 or 429 or 500 or 502 or 503 or 504;
}

internal static class ElasticMaterializationPhysicalNames
{
    internal static string RequireConcreteIndex(string value, string parameterName)
        => RequireConcreteName(value, parameterName, "index");

    internal static string RequireConcreteAlias(string value, string parameterName)
        => RequireConcreteName(value, parameterName, "alias");

    static string RequireConcreteName(string value, string parameterName, string kind)
    {
        value = RequireValue(value, parameterName);
        if (value is "_all" || value.IndexOfAny(['*', '?', ',']) >= 0)
        {
            throw new ArgumentException(
                $"A materialization transport operation requires one explicit concrete Elasticsearch {kind}.",
                parameterName);
        }

        return value;
    }

    internal static string RequireValue(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An Elasticsearch physical value cannot be empty or white space.", parameterName);
        }

        return value;
    }

    internal static int RequirePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "A byte bound must be positive.");
        }

        return value;
    }
}
