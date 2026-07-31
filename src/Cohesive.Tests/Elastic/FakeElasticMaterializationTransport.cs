using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Adapters.Elastic;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Elastic;

internal sealed class FakeElasticMaterializationTransport : IElasticMaterializationTransport
{
    internal const string GetDocumentOperation = "get-document";
    internal const string CreateDocumentOperation = "create-document";
    internal const string ReplaceDocumentOperation = "replace-document";
    internal const string DeleteDocumentOperation = "delete-document";
    internal const string CreateIndexOperation = "create-index";
    internal const string IndexExistsOperation = "index-exists";
    internal const string AddWriteBlockOperation = "add-write-block";
    internal const string RemoveWriteBlockOperation = "remove-write-block";
    internal const string RefreshOperation = "refresh";
    internal const string DeleteIndexOperation = "delete-index";
    internal const string DeleteOwnedIndexOperation = "delete-owned-index";
    internal const string MultiGetOperation = "multi-get";
    internal const string BulkOperation = "bulk";
    internal const string ScanOperation = "scan";
    internal const string CountOperation = "count";
    internal const string CompareExchangeAliasOperation = "compare-exchange-alias";
    internal const string InspectAliasesOperation = "inspect-aliases";

    const string ProtocolErrorType = "cohesive.elasticsearch.protocol";
    const string ResponseLimitErrorType = "cohesive.elasticsearch.response.limitExceeded";
    const string TransportErrorType = "cohesive.elasticsearch.transport";
    const long PrimaryTerm = 1;

    readonly object gate = new();
    readonly Dictionary<string, FakeIndex> indexes = new(StringComparer.Ordinal);
    readonly Dictionary<BulkFaultKey, Queue<BulkFault>> bulkFaults = [];
    readonly Dictionary<BulkOrdinalFaultKey, BulkFault> ordinalBulkFaults = [];
    readonly Dictionary<string, int> createDocumentFaults = new(StringComparer.Ordinal);
    readonly List<FakeElasticMaterializationCall> calls = [];
    readonly List<ImmutableArray<ElasticBulkOperation>> bulkRequests = [];
    readonly List<ElasticScanRequest> scanRequests = [];
    readonly List<FakeElasticCountRequest> countRequests = [];
    readonly List<ElasticAliasCasRequest> aliasRequests = [];
    int ambiguousAliasApplications;
    int scanResponseLimitFailures;
    TaskCompletionSource? nextBulkEntered;
    TaskCompletionSource? nextBulkRelease;

    internal ImmutableArray<FakeElasticMaterializationCall> Calls
    {
        get
        {
            lock (gate)
            {
                return [.. calls];
            }
        }
    }

    internal ImmutableArray<ImmutableArray<ElasticBulkOperation>> BulkRequests
    {
        get
        {
            lock (gate)
            {
                return [.. bulkRequests.Select(CloneBulkOperations)];
            }
        }
    }

    internal ImmutableArray<ElasticScanRequest> ScanRequests
    {
        get
        {
            lock (gate)
            {
                return [.. scanRequests.Select(CloneScanRequest)];
            }
        }
    }

    internal ImmutableArray<FakeElasticCountRequest> CountRequests
    {
        get
        {
            lock (gate)
            {
                return [.. countRequests.Select(static request => request.Copy())];
            }
        }
    }

    internal ImmutableArray<ElasticAliasCasRequest> AliasRequests
    {
        get
        {
            lock (gate)
            {
                return [.. aliasRequests.Select(CloneAliasRequest)];
            }
        }
    }

    internal void EnqueueRetryableBulkItemFailure(string index, string id, int occurrences = 1) =>
        EnqueueBulkItemFailure(
            index,
            id,
            new(429, "es_rejected_execution_exception", "Injected deterministic retryable bulk rejection."),
            occurrences);

    internal void EnqueuePermanentBulkItemFailure(string index, string id, int occurrences = 1) =>
        EnqueueBulkItemFailure(
            index,
            id,
            new(400, "mapper_parsing_exception", "Injected deterministic permanent bulk rejection."),
            occurrences);

    internal void EnqueueRetryableBulkItemFailure(int itemOrdinal) =>
        EnqueueBulkItemFailure(
            itemOrdinal,
            new(429, "es_rejected_execution_exception", "Injected deterministic retryable bulk rejection."));

    internal void EnqueuePermanentBulkItemFailure(int itemOrdinal) =>
        EnqueueBulkItemFailure(
            itemOrdinal,
            new(400, "mapper_parsing_exception", "Injected deterministic permanent bulk rejection."));

    internal void EnqueueBulkItemFailure(int itemOrdinal, int statusCode, string errorType) =>
        EnqueueBulkItemFailure(
            itemOrdinal,
            new(
                statusCode,
                ElasticMaterializationPhysicalNames.RequireValue(errorType, nameof(errorType)),
                "Injected deterministic bulk rejection."));

    internal void ApplyNextAliasExchangeThenFailAmbiguously(int occurrences = 1)
    {
        if (occurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences), occurrences, "Occurrences must be positive.");
        }

        lock (gate)
        {
            ambiguousAliasApplications = checked(ambiguousAliasApplications + occurrences);
        }
    }

    internal void FailNextControlDocumentCreate(string documentKind, int occurrences = 1)
    {
        documentKind = ElasticMaterializationPhysicalNames.RequireValue(documentKind, nameof(documentKind));
        if (occurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences), occurrences, "Occurrences must be positive.");
        }

        lock (gate)
        {
            createDocumentFaults.TryGetValue(documentKind, out var retained);
            createDocumentFaults[documentKind] = checked(retained + occurrences);
        }
    }

    internal (Task Entered, Action Release) PauseNextBulk()
    {
        lock (gate)
        {
            if (nextBulkRelease is not null)
                throw new InvalidOperationException("A fake Elasticsearch bulk pause is already pending.");
            nextBulkEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            nextBulkRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = nextBulkEntered.Task;
            var release = nextBulkRelease;
            return (entered, () => release.TrySetResult());
        }
    }

    internal void FailNextScanWithResponseLimit(int occurrences = 1)
    {
        if (occurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences), occurrences, "Occurrences must be positive.");
        }

        lock (gate)
        {
            scanResponseLimitFailures = checked(scanResponseLimitFailures + occurrences);
        }
    }

    internal void TamperRemoveAlias(string index, string alias)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        alias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(alias, nameof(alias));
        lock (gate)
        {
            if (!RequireIndex(index).Aliases.Remove(alias))
                throw new InvalidOperationException("The fake alias selected for tampering does not exist.");
        }
    }

    internal void TamperMoveAlias(string alias, string expectedIndex, string nextIndex)
    {
        alias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(alias, nameof(alias));
        expectedIndex = ElasticMaterializationPhysicalNames.RequireConcreteIndex(expectedIndex, nameof(expectedIndex));
        nextIndex = ElasticMaterializationPhysicalNames.RequireConcreteIndex(nextIndex, nameof(nextIndex));
        lock (gate)
        {
            var expected = RequireIndex(expectedIndex);
            var next = RequireIndex(nextIndex);
            if (!expected.Aliases.Remove(alias, out var retained))
                throw new InvalidOperationException("The fake alias selected for tampering does not exist on its expected index.");
            if (!next.Aliases.TryAdd(alias, retained))
                throw new InvalidOperationException("The fake alias selected for tampering already exists on its next index.");
        }
    }

    public ValueTask<ElasticDocumentReadResult> GetDocumentAsync(
        string index,
        string id,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(GetDocumentOperation, index, id, itemCount: 1, maximumRequestBytes: null, maximumResponseBytes);
            var state = RequireIndex(index);
            if (!state.Documents.TryGetValue(id, out var document) || !document.Exists)
            {
                return ValueTask.FromResult(new ElasticDocumentReadResult(
                    Found: false,
                    Source: [],
                    ConcurrencyToken: null,
                    ExternalVersion: null));
            }

            RequireResponseBound(document.Source.LongLength, maximumResponseBytes);
            return ValueTask.FromResult(new ElasticDocumentReadResult(
                Found: true,
                Source: [.. document.Source],
                ConcurrencyToken: document.Token,
                ExternalVersion: document.EffectiveVersion));
        }
    }

    public ValueTask<ElasticDocumentWriteResult> CreateDocumentAsync(
        string index,
        string id,
        ReadOnlyMemory<byte> source,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        var normalized = NormalizeObject(source, nameof(source));
        lock (gate)
        {
            Record(CreateDocumentOperation, index, id, itemCount: 1, source.Length, maximumResponseBytes);
            if (TryConsumeCreateDocumentFault(normalized, out var failedDocumentKind))
            {
                throw new ElasticMaterializationTransportException(
                    statusCode: null,
                    TransportErrorType,
                    retryable: true,
                    $"Injected failure before creating control document kind '{failedDocumentKind}'.");
            }
            var state = RequireIndex(index);
            RequireWritable(state, index);
            if (state.Documents.TryGetValue(id, out var existing) && existing.Exists)
            {
                return ValueTask.FromResult(ConflictDocumentWrite());
            }

            var document = existing ?? new FakeDocument();
            state.Documents[id] = document;
            ApplyDocumentWrite(state, document, normalized, externalVersion: null);
            return ValueTask.FromResult(AppliedDocumentWrite(document, statusCode: 201));
        }
    }

    public ValueTask<ElasticDocumentWriteResult> ReplaceDocumentAsync(
        string index,
        string id,
        ReadOnlyMemory<byte> source,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        expected.Validate(nameof(expected));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        var normalized = NormalizeObject(source, nameof(source));
        lock (gate)
        {
            Record(ReplaceDocumentOperation, index, id, itemCount: 1, source.Length, maximumResponseBytes);
            if (!indexes.TryGetValue(index, out var state))
            {
                return ValueTask.FromResult(NotFoundDocumentWrite());
            }

            RequireWritable(state, index);
            if (!state.Documents.TryGetValue(id, out var document) || !document.Exists)
            {
                return ValueTask.FromResult(NotFoundDocumentWrite());
            }

            if (document.Token != expected)
            {
                return ValueTask.FromResult(ConflictDocumentWrite());
            }

            ApplyDocumentWrite(state, document, normalized, externalVersion: null);
            return ValueTask.FromResult(AppliedDocumentWrite(document, statusCode: 200));
        }
    }

    public ValueTask<ElasticDocumentWriteResult> DeleteDocumentAsync(
        string index,
        string id,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        expected.Validate(nameof(expected));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(DeleteDocumentOperation, index, id, itemCount: 1, maximumRequestBytes: null, maximumResponseBytes);
            if (!indexes.TryGetValue(index, out var state))
            {
                return ValueTask.FromResult(NotFoundDocumentWrite());
            }

            RequireWritable(state, index);
            if (!state.Documents.TryGetValue(id, out var document) || !document.Exists)
            {
                return ValueTask.FromResult(NotFoundDocumentWrite());
            }

            if (document.Token != expected)
            {
                return ValueTask.FromResult(ConflictDocumentWrite());
            }

            ApplyDocumentDelete(state, document, externalVersion: document.LastExternalVersion);
            return ValueTask.FromResult(AppliedDocumentWrite(document, statusCode: 200));
        }
    }

    public ValueTask<ElasticIndexCreateResult> CreateIndexAsync(
        string index,
        ReadOnlyMemory<byte> body,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        var normalized = NormalizeObject(body, nameof(body));
        var prepared = ParseIndex(index, normalized);
        lock (gate)
        {
            Record(CreateIndexOperation, index, id: null, itemCount: 0, body.Length, maximumResponseBytes);
            if (indexes.ContainsKey(index))
            {
                return ValueTask.FromResult(new ElasticIndexCreateResult(
                    ElasticIndexCreateDisposition.AlreadyExists,
                    StatusCode: 400,
                    Acknowledged: false,
                    ShardsAcknowledged: false,
                    index));
            }

            indexes.Add(index, prepared);
            return ValueTask.FromResult(new ElasticIndexCreateResult(
                ElasticIndexCreateDisposition.Created,
                StatusCode: 200,
                Acknowledged: true,
                ShardsAcknowledged: true,
                index));
        }
    }

    public ValueTask<bool> IndexExistsAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(IndexExistsOperation, index, id: null, itemCount: 0, maximumRequestBytes: null, maximumResponseBytes);
            return ValueTask.FromResult(indexes.ContainsKey(index));
        }
    }

    public ValueTask<ElasticAcknowledgedResult> AddWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        SetWriteBlockAsync(index, blocked: true, maximumResponseBytes, cancellationToken);

    public ValueTask<ElasticAcknowledgedResult> RemoveWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        SetWriteBlockAsync(index, blocked: false, maximumResponseBytes, cancellationToken);

    public ValueTask<ElasticAcknowledgedResult> RefreshAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(RefreshOperation, index, id: null, itemCount: 0, maximumRequestBytes: null, maximumResponseBytes);
            if (!indexes.TryGetValue(index, out var state))
            {
                return ValueTask.FromResult(NotFoundAcknowledgement());
            }

            state.Refresh();
            return ValueTask.FromResult(AppliedAcknowledgement());
        }
    }

    public ValueTask<ElasticAcknowledgedResult> DeleteIndexAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(DeleteIndexOperation, index, id: null, itemCount: 0, maximumRequestBytes: null, maximumResponseBytes);
            return ValueTask.FromResult(indexes.Remove(index)
                ? AppliedAcknowledgement()
                : NotFoundAcknowledgement());
        }
    }

    public ValueTask<ElasticOwnedIndexDeleteResult> DeleteOwnedIndexAsync(
        string index,
        string ownerAlias,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ownerAlias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(ownerAlias, nameof(ownerAlias));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(
                DeleteOwnedIndexOperation,
                index,
                id: null,
                itemCount: 0,
                maximumRequestBytes: null,
                maximumResponseBytes);
            if (!indexes.TryGetValue(index, out var state)
                || !state.Aliases.TryGetValue(ownerAlias, out var owner)
                || owner.IsHidden is not true
                || owner.IsWriteIndex is not null
                || owner.Routing is not null
                || owner.SearchRouting is not null
                || owner.IndexRouting is not null
                || owner.Filter.Length != 0)
            {
                return ValueTask.FromResult(new ElasticOwnedIndexDeleteResult(
                    ElasticOwnedIndexDeleteDisposition.OwnershipConflict,
                    StatusCode: 409,
                    Acknowledged: false));
            }

            indexes.Remove(index);
            return ValueTask.FromResult(new ElasticOwnedIndexDeleteResult(
                ElasticOwnedIndexDeleteDisposition.Applied,
                StatusCode: 200,
                Acknowledged: true));
        }
    }

    public ValueTask<ElasticMultiGetResult> MultiGetAsync(
        string index,
        ImmutableArray<string> ids,
        ElasticMultiGetSourceProjection sourceProjection,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        if (!Enum.IsDefined(sourceProjection))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceProjection),
                sourceProjection,
                "Unsupported Elasticsearch multi-get source projection.");
        }
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        foreach (var id in ids.IsDefault ? [] : ids)
        {
            ElasticMaterializationPhysicalNames.RequireValue(id, nameof(ids));
        }

        var normalizedIds = ids.IsDefault ? ImmutableArray<string>.Empty : ids;
        lock (gate)
        {
            Record(MultiGetOperation, index, id: null, normalizedIds.Length, maximumRequestBytes: null, maximumResponseBytes);
            if (normalizedIds.IsEmpty)
            {
                return ValueTask.FromResult(new ElasticMultiGetResult([]));
            }

            var state = RequireIndex(index);
            var builder = ImmutableArray.CreateBuilder<ElasticMultiGetDocument>(normalizedIds.Length);
            long payloadBytes = 0;
            foreach (var id in normalizedIds)
            {
                if (!state.Documents.TryGetValue(id, out var document) || !document.Exists)
                {
                    builder.Add(new(id, Found: false, Source: [], ConcurrencyToken: null, ExternalVersion: null));
                    continue;
                }

                var projectedSource = sourceProjection == ElasticMultiGetSourceProjection.Full
                    ? [.. document.Source]
                    : ProjectMaterializationMetadata(document.Source);
                payloadBytes = checked(payloadBytes + projectedSource.LongLength);
                builder.Add(new(
                    id,
                    Found: true,
                    Source: projectedSource,
                    ConcurrencyToken: document.Token,
                    ExternalVersion: document.EffectiveVersion));
            }

            RequireResponseBound(payloadBytes, maximumResponseBytes);
            return ValueTask.FromResult(new ElasticMultiGetResult(builder.MoveToImmutable()));
        }
    }

    static byte[] ProjectMaterializationMetadata(ReadOnlyMemory<byte> source)
    {
        using var document = JsonDocument.Parse(source);
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(
                    ElasticMaterializationTargetBinding.MetadataField,
                    out var metadata))
            {
                writer.WritePropertyName(ElasticMaterializationTargetBinding.MetadataField);
                metadata.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public async ValueTask<ElasticBulkResult> BulkAsync(
        ImmutableArray<ElasticBulkOperation> operations,
        long maximumWireBytes,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        if (operations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An Elasticsearch bulk request requires at least one operation.", nameof(operations));
        }

        if (operations.Any(static operation => operation is null))
        {
            throw new ArgumentException("An Elasticsearch bulk request cannot contain a null operation.", nameof(operations));
        }

        if (maximumWireBytes <= 0 || maximumWireBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWireBytes),
                maximumWireBytes,
                $"A bulk wire bound must be between 1 and {Array.MaxLength} bytes.");
        }

        TaskCompletionSource? entered;
        TaskCompletionSource? release;
        lock (gate)
        {
            entered = nextBulkEntered;
            release = nextBulkRelease;
            nextBulkEntered = null;
            nextBulkRelease = null;
        }
        if (release is not null)
        {
            entered!.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        var normalized = NormalizeBulkOperations(operations);
        var wireBytes = MeasureBulkWireBytes(normalized);
        if (wireBytes > maximumWireBytes)
        {
            throw new ArgumentException(
                $"Elasticsearch bulk NDJSON exceeded its declared {maximumWireBytes.ToString(CultureInfo.InvariantCulture)}-byte wire bound.");
        }

        lock (gate)
        {
            var requestOrdinal = bulkRequests.Count;
            var captured = CloneBulkOperations(normalized);
            bulkRequests.Add(captured);
            Record(BulkOperation, index: null, id: null, normalized.Length, maximumWireBytes, maximumResponseBytes);
            var builder = ImmutableArray.CreateBuilder<ElasticBulkItemResult>(normalized.Length);
            var errors = false;
            for (var ordinal = 0; ordinal < normalized.Length; ordinal++)
            {
                var operation = normalized[ordinal];
                ElasticBulkItemResult result;
                if (TryTakeBulkFault(requestOrdinal, ordinal, operation.Index, operation.Id, out var fault))
                {
                    result = FailedBulkItem(operation, ordinal, fault.StatusCode, fault.ErrorType, fault.Reason);
                }
                else if (!indexes.TryGetValue(operation.Index, out var state))
                {
                    result = FailedBulkItem(
                        operation,
                        ordinal,
                        statusCode: 404,
                        "index_not_found_exception",
                        $"Index '{operation.Index}' does not exist.");
                }
                else if (state.WriteBlocked)
                {
                    result = FailedBulkItem(
                        operation,
                        ordinal,
                        statusCode: 403,
                        "cluster_block_exception",
                        $"Index '{operation.Index}' is blocked for writes.");
                }
                else
                {
                    result = ApplyBulkItem(state, operation, ordinal);
                }

                errors |= result.StatusCode >= 300;
                builder.Add(result);
            }

            return new ElasticBulkResult(
                wireBytes,
                TookMilliseconds: 0,
                errors,
                builder.MoveToImmutable());
        }
    }

    public ValueTask<ElasticScanPage> ScanAsync(
        ElasticScanRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var query = ParseQuery(request.Query, nameof(request));
        lock (gate)
        {
            scanRequests.Add(CloneScanRequest(request));
            Record(
                ScanOperation,
                request.Index,
                id: null,
                request.MaximumItems,
                request.Query.Length,
                request.MaximumResponseBytes);
            if (scanResponseLimitFailures > 0)
            {
                scanResponseLimitFailures--;
                throw new ElasticMaterializationTransportException(
                    statusCode: null,
                    ResponseLimitErrorType,
                    retryable: false,
                    "Injected bounded-response rejection for scan page reduction.");
            }

            List<SearchHit> matches = [];
            foreach (var candidate in SearchCandidates(request.Index))
            {
                if (!EvaluateQuery(candidate.Source, query)
                    || candidate.AliasFilter is { } aliasFilter && !EvaluateQuery(candidate.Source, aliasFilter))
                {
                    continue;
                }

                var sortValue = ReadSortValue(candidate.Id, candidate.Source, request.SortField);
                if (request.AfterSortValue is { } after
                    && MaterializationSealContentOrder.Compare(new(sortValue), new(after)) <= 0)
                {
                    continue;
                }

                matches.Add(new(candidate.Id, sortValue, candidate.Source));
            }

            matches.Sort(static (left, right) =>
            {
                var bySort = MaterializationSealContentOrder.Compare(
                    new(left.SortValue),
                    new(right.SortValue));
                return bySort != 0 ? bySort : string.CompareOrdinal(left.Id, right.Id);
            });
            for (var index = 1; index < matches.Count; index++)
            {
                if (string.Equals(matches[index - 1].SortValue, matches[index].SortValue, StringComparison.Ordinal))
                {
                    throw Protocol("A fake Elasticsearch scan encountered duplicate stable sort values.");
                }
            }

            var returnedCount = Math.Min(request.MaximumItems, matches.Count);
            var builder = ImmutableArray.CreateBuilder<ElasticScanHit>(returnedCount);
            long payloadBytes = 0;
            for (var index = 0; index < returnedCount; index++)
            {
                var match = matches[index];
                payloadBytes = checked(payloadBytes + match.Source.LongLength);
                builder.Add(new(match.Id, match.SortValue, [.. match.Source]));
            }

            RequireResponseBound(payloadBytes, request.MaximumResponseBytes);
            var page = builder.MoveToImmutable();
            var next = matches.Count > request.MaximumItems ? page[^1].SortValue : null;
            return ValueTask.FromResult(new ElasticScanPage(page, next, TookMilliseconds: 0));
        }
    }

    public ValueTask<ElasticCountResult> CountAsync(
        string index,
        ReadOnlyMemory<byte> query,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        var parsed = ParseQuery(query, nameof(query));
        lock (gate)
        {
            countRequests.Add(new(index, query.ToArray(), maximumResponseBytes));
            Record(CountOperation, index, id: null, itemCount: 0, query.Length, maximumResponseBytes);
            long count = 0;
            foreach (var candidate in SearchCandidates(index))
            {
                if (EvaluateQuery(candidate.Source, parsed)
                    && (candidate.AliasFilter is null || EvaluateQuery(candidate.Source, candidate.AliasFilter.Value)))
                {
                    count++;
                }
            }

            return ValueTask.FromResult(new ElasticCountResult(count, TookMilliseconds: 0));
        }
    }

    public ValueTask<ElasticAliasCasResult> CompareExchangeAliasAsync(
        ElasticAliasCasRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var captured = CloneAliasRequest(request);
        var normalizedFilter = request.ReadAliasFilter.IsEmpty
            ? []
            : NormalizeObject(request.ReadAliasFilter, nameof(request));
        lock (gate)
        {
            aliasRequests.Add(captured);
            Record(
                CompareExchangeAliasOperation,
                request.MarkerIndex,
                id: null,
                itemCount: request.ReadAlias is null ? 1 : 2,
                request.ReadAliasFilter.Length,
                request.MaximumResponseBytes);

            if (request.ReadAlias is { } inspectedReadAlias)
            {
                var ownerCount = 0;
                string? ownerIndex = null;
                FakeAlias? readOwner = null;
                foreach (var index in indexes)
                {
                    if (index.Value.Aliases.TryGetValue(inspectedReadAlias, out var found))
                    {
                        ownerCount++;
                        ownerIndex = index.Key;
                        readOwner = found;
                    }
                }

                var readAliasConflict = request.ExpectedReadIndex is null
                    ? ownerCount != 0
                    : ownerCount != 1
                        || ownerIndex != request.ExpectedReadIndex
                        || readOwner is null
                        || readOwner.IsHidden is true
                        || readOwner.IsWriteIndex != request.IsWriteIndex
                        || readOwner.Routing != request.Routing
                        || readOwner.SearchRouting != request.SearchRouting
                        || readOwner.IndexRouting != request.IndexRouting
                        || !readOwner.Filter.AsSpan().SequenceEqual(normalizedFilter);
                if (readAliasConflict)
                {
                    return ValueTask.FromResult(new ElasticAliasCasResult(
                        ElasticAliasCasDisposition.Conflict,
                        StatusCode: 409,
                        Acknowledged: false));
                }
            }

            if (!indexes.TryGetValue(request.MarkerIndex, out var markerIndex)
                || !markerIndex.Aliases.ContainsKey(request.ExpectedMarkerAlias)
                || request.NextReadIndex is { } nextReadIndex && !indexes.ContainsKey(nextReadIndex)
                || request.ExpectedNextOwnerAlias is { } expectedOwnerAlias
                && (request.NextReadIndex is null
                    || !indexes[request.NextReadIndex].Aliases.TryGetValue(expectedOwnerAlias, out var owner)
                    || owner.IsHidden is not true
                    || owner.IsWriteIndex is not null
                    || owner.Routing is not null
                    || owner.SearchRouting is not null
                    || owner.IndexRouting is not null
                    || owner.Filter.Length != 0)
                || request.ExpectedReadIndex is { } expectedReadIndex
                && (!indexes.TryGetValue(expectedReadIndex, out var expectedIndex)
                    || request.ReadAlias is null
                    || !expectedIndex.Aliases.ContainsKey(request.ReadAlias)))
            {
                return ValueTask.FromResult(new ElasticAliasCasResult(
                    ElasticAliasCasDisposition.Conflict,
                    StatusCode: 404,
                    Acknowledged: false));
            }

            markerIndex.Aliases.Remove(request.ExpectedMarkerAlias);
            if (request.ReadAlias is { } readAlias)
            {
                foreach (var index in indexes.Values)
                {
                    index.Aliases.Remove(readAlias);
                }
            }

            if (request.ReadAlias is { } publishedAlias && request.NextReadIndex is { } publishedIndex)
            {
                indexes[publishedIndex].Aliases[publishedAlias] = new(
                    IsHidden: null,
                    request.IsWriteIndex,
                    request.Routing,
                    request.SearchRouting,
                    request.IndexRouting,
                    normalizedFilter);
            }

            markerIndex.Aliases[request.NextMarkerAlias] = new(
                IsHidden: true,
                IsWriteIndex: null,
                Routing: null,
                SearchRouting: null,
                IndexRouting: null,
                Filter: []);

            if (ambiguousAliasApplications > 0)
            {
                ambiguousAliasApplications--;
                return ValueTask.FromException<ElasticAliasCasResult>(new ElasticMaterializationTransportException(
                    statusCode: null,
                    TransportErrorType,
                    retryable: true,
                    "The fake Elasticsearch alias transaction applied, but its response was lost."));
            }

            return ValueTask.FromResult(new ElasticAliasCasResult(
                ElasticAliasCasDisposition.Applied,
                StatusCode: 200,
                Acknowledged: true));
        }
    }

    public ValueTask<ElasticAliasSnapshot> InspectAliasesAsync(
        ImmutableArray<string> aliases,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        if (aliases.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Alias inspection requires at least one exact alias.", nameof(aliases));
        }

        HashSet<string> requested = new(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            var normalized = ElasticMaterializationPhysicalNames.RequireConcreteAlias(alias, nameof(aliases));
            if (!requested.Add(normalized))
            {
                throw new ArgumentException("Alias inspection cannot repeat an alias.", nameof(aliases));
            }
        }

        lock (gate)
        {
            Record(InspectAliasesOperation, index: null, id: null, aliases.Length, maximumRequestBytes: null, maximumResponseBytes);
            List<ElasticAliasBinding> bindings = [];
            long payloadBytes = 0;
            foreach (var index in indexes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                foreach (var alias in index.Value.Aliases)
                {
                    if (!requested.Contains(alias.Key))
                    {
                        continue;
                    }

                    payloadBytes = checked(payloadBytes + alias.Value.Filter.LongLength);
                    bindings.Add(alias.Value.ToBinding(alias.Key, index.Key));
                }
            }

            RequireResponseBound(payloadBytes, maximumResponseBytes);
            return ValueTask.FromResult(new ElasticAliasSnapshot([
                .. bindings
                    .OrderBy(static binding => binding.Alias, StringComparer.Ordinal)
                    .ThenBy(static binding => binding.Index, StringComparer.Ordinal)
            ]));
        }
    }

    ValueTask<ElasticAcknowledgedResult> SetWriteBlockAsync(
        string index,
        bool blocked,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        lock (gate)
        {
            Record(
                blocked ? AddWriteBlockOperation : RemoveWriteBlockOperation,
                index,
                id: null,
                itemCount: 0,
                maximumRequestBytes: null,
                maximumResponseBytes);
            if (!indexes.TryGetValue(index, out var state))
            {
                return ValueTask.FromResult(NotFoundAcknowledgement());
            }

            state.WriteBlocked = blocked;
            return ValueTask.FromResult(AppliedAcknowledgement());
        }
    }

    void EnqueueBulkItemFailure(
        string index,
        string id,
        BulkFault fault,
        int occurrences)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        if (occurrences <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences), occurrences, "Occurrences must be positive.");
        }

        lock (gate)
        {
            BulkFaultKey key = new(index, id);
            if (!bulkFaults.TryGetValue(key, out var queue))
            {
                queue = new();
                bulkFaults.Add(key, queue);
            }

            for (var occurrence = 0; occurrence < occurrences; occurrence++)
            {
                queue.Enqueue(fault);
            }
        }
    }

    void EnqueueBulkItemFailure(int itemOrdinal, BulkFault fault)
    {
        if (itemOrdinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(itemOrdinal), itemOrdinal, "A bulk item ordinal cannot be negative.");
        }

        lock (gate)
        {
            BulkOrdinalFaultKey key = new(bulkRequests.Count, itemOrdinal);
            if (!ordinalBulkFaults.TryAdd(key, fault))
            {
                throw new InvalidOperationException(
                    "A deterministic failure is already scripted for that item in the next bulk request.");
            }
        }
    }

    bool TryTakeBulkFault(
        int requestOrdinal,
        int itemOrdinal,
        string index,
        string id,
        out BulkFault fault)
    {
        if (ordinalBulkFaults.Remove(new(requestOrdinal, itemOrdinal), out fault))
        {
            return true;
        }

        BulkFaultKey key = new(index, id);
        if (!bulkFaults.TryGetValue(key, out var queue))
        {
            fault = default;
            return false;
        }

        fault = queue.Dequeue();
        if (queue.Count == 0)
        {
            bulkFaults.Remove(key);
        }

        return true;
    }

    ElasticBulkItemResult ApplyBulkItem(FakeIndex state, ElasticBulkOperation operation, int ordinal)
    {
        if (!state.Documents.TryGetValue(operation.Id, out var document))
        {
            document = new();
            state.Documents.Add(operation.Id, document);
        }

        if (document.LastExternalVersion is { } currentVersion
            && operation.ExternalVersion <= currentVersion)
        {
            return FailedBulkItem(
                operation,
                ordinal,
                statusCode: 409,
                "version_conflict_engine_exception",
                $"External version {operation.ExternalVersion.ToString(CultureInfo.InvariantCulture)} does not advance {currentVersion.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (operation.Kind == ElasticBulkOperationKind.Index)
        {
            var created = !document.Exists;
            ApplyDocumentWrite(state, document, operation.Source.ToArray(), operation.ExternalVersion);
            return new(
                ordinal,
                operation.Kind,
                operation.Index,
                operation.Id,
                StatusCode: created ? 201 : 200,
                Result: created ? "created" : "updated",
                ErrorType: null,
                ErrorReason: null,
                ExternalVersion: operation.ExternalVersion,
                document.Token);
        }

        var found = document.Exists;
        ApplyDocumentDelete(state, document, operation.ExternalVersion);
        return new(
            ordinal,
            operation.Kind,
            operation.Index,
            operation.Id,
            StatusCode: found ? 200 : 404,
            Result: found ? "deleted" : "not_found",
            ErrorType: null,
            ErrorReason: null,
            ExternalVersion: operation.ExternalVersion,
            document.Token);
    }

    IEnumerable<SearchCandidate> SearchCandidates(string indexOrAlias)
    {
        if (indexes.TryGetValue(indexOrAlias, out var direct))
        {
            foreach (var document in direct.SearchableDocuments)
            {
                yield return new(document.Key, document.Value.Source, AliasFilter: null);
            }
            yield break;
        }

        var found = false;
        foreach (var index in indexes.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!index.Value.Aliases.TryGetValue(indexOrAlias, out var alias))
            {
                continue;
            }

            found = true;
            JsonElement? filter = alias.Filter.Length == 0 ? null : ParseQuery(alias.Filter, "alias filter");
            foreach (var document in index.Value.SearchableDocuments)
            {
                yield return new(document.Key, document.Value.Source, filter);
            }
        }

        if (!found)
        {
            throw MissingIndex(indexOrAlias);
        }
    }

    FakeIndex RequireIndex(string index) =>
        indexes.TryGetValue(index, out var state) ? state : throw MissingIndex(index);

    void Record(
        string operation,
        string? index,
        string? id,
        int itemCount,
        long? maximumRequestBytes,
        int maximumResponseBytes) =>
        calls.Add(new(
            calls.Count,
            operation,
            index,
            id,
            itemCount,
            maximumRequestBytes,
            maximumResponseBytes));

    static FakeIndex ParseIndex(string index, byte[] normalized)
    {
        using var document = JsonDocument.Parse(normalized);
        Dictionary<string, FakeAlias> aliases = new(StringComparer.Ordinal);
        if (document.RootElement.TryGetProperty("aliases", out var aliasObject))
        {
            if (aliasObject.ValueKind != JsonValueKind.Object)
            {
                throw Protocol("An index aliases declaration must be a JSON object.");
            }

            foreach (var aliasProperty in aliasObject.EnumerateObject())
            {
                var aliasName = ElasticMaterializationPhysicalNames.RequireConcreteAlias(aliasProperty.Name, "body");
                if (aliasProperty.Value.ValueKind != JsonValueKind.Object)
                {
                    throw Protocol("An index alias declaration must be a JSON object.");
                }

                aliases.Add(aliasName, ParseAlias(aliasProperty.Value));
            }
        }

        var blocked = TryReadInitialWriteBlock(document.RootElement);
        return new(index, normalized, aliases, blocked);
    }

    static FakeAlias ParseAlias(JsonElement value) => new(
        ReadOptionalBoolean(value, "is_hidden"),
        ReadOptionalBoolean(value, "is_write_index"),
        ReadOptionalString(value, "routing"),
        ReadOptionalString(value, "search_routing"),
        ReadOptionalString(value, "index_routing"),
        value.TryGetProperty("filter", out var filter) ? NormalizeObject(JsonBytes(filter), "filter") : []);

    static bool TryReadInitialWriteBlock(JsonElement root)
    {
        if (!root.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (settings.TryGetProperty("index.blocks.write", out var flat))
        {
            return RequireBoolean(flat, "index.blocks.write");
        }

        return settings.TryGetProperty("index", out var index)
               && index.ValueKind == JsonValueKind.Object
               && index.TryGetProperty("blocks", out var blocks)
               && blocks.ValueKind == JsonValueKind.Object
               && blocks.TryGetProperty("write", out var write)
               && RequireBoolean(write, "settings.index.blocks.write");
    }

    static bool EvaluateQuery(byte[] source, JsonElement query)
    {
        using var document = JsonDocument.Parse(source);
        return EvaluateClause(document.RootElement, query);
    }

    static bool EvaluateClause(JsonElement source, JsonElement clause)
    {
        if (clause.ValueKind != JsonValueKind.Object || clause.GetRawText() == "{}")
        {
            throw Protocol("A fake Elasticsearch query clause must be a nonempty JSON object.");
        }

        using var properties = clause.EnumerateObject();
        if (!properties.MoveNext())
        {
            throw Protocol("A fake Elasticsearch query clause cannot be empty.");
        }

        var property = properties.Current;
        if (properties.MoveNext())
        {
            throw Protocol("A fake Elasticsearch query clause must contain one query operator.");
        }

        return property.Name switch
        {
            "match_all" => EvaluateMatchAll(property.Value),
            "term" => EvaluateTerm(source, property.Value),
            "bool" => EvaluateBoolean(source, property.Value),
            "exists" => EvaluateExists(source, property.Value),
            _ => throw Protocol($"The fake Elasticsearch transport does not support query operator '{property.Name}'.")
        };
    }

    static bool EvaluateMatchAll(JsonElement matchAll)
    {
        if (matchAll.ValueKind != JsonValueKind.Object)
        {
            throw Protocol("A match_all query must contain an object.");
        }

        return true;
    }

    static bool EvaluateTerm(JsonElement source, JsonElement term)
    {
        if (term.ValueKind != JsonValueKind.Object)
        {
            throw Protocol("A term query must contain one field object.");
        }

        using var fields = term.EnumerateObject();
        if (!fields.MoveNext())
        {
            throw Protocol("A term query must contain one field.");
        }

        var field = fields.Current;
        if (fields.MoveNext())
        {
            throw Protocol("A term query must contain exactly one field.");
        }

        var expected = field.Value;
        if (expected.ValueKind == JsonValueKind.Object && expected.TryGetProperty("value", out var wrapped))
        {
            expected = wrapped;
        }

        return TryResolveField(source, field.Name, out var actual) && ScalarEquals(actual, expected);
    }

    static bool EvaluateBoolean(JsonElement source, JsonElement boolean)
    {
        if (boolean.ValueKind != JsonValueKind.Object)
        {
            throw Protocol("A bool query must contain an object.");
        }

        List<JsonElement> must = [];
        List<JsonElement> filters = [];
        List<JsonElement> mustNot = [];
        List<JsonElement> should = [];
        int? minimumShouldMatch = null;
        foreach (var property in boolean.EnumerateObject())
        {
            switch (property.Name)
            {
                case "must":
                    AddClauses(must, property.Value);
                    break;
                case "filter":
                    AddClauses(filters, property.Value);
                    break;
                case "must_not":
                    AddClauses(mustNot, property.Value);
                    break;
                case "should":
                    AddClauses(should, property.Value);
                    break;
                case "minimum_should_match":
                    minimumShouldMatch = ReadMinimumShouldMatch(property.Value);
                    break;
                default:
                    throw Protocol($"The fake Elasticsearch bool query does not support '{property.Name}'.");
            }
        }

        if (must.Any(clause => !EvaluateClause(source, clause))
            || filters.Any(clause => !EvaluateClause(source, clause))
            || mustNot.Any(clause => EvaluateClause(source, clause)))
        {
            return false;
        }

        var requiredShould = minimumShouldMatch ?? (should.Count > 0 && must.Count == 0 && filters.Count == 0 ? 1 : 0);
        return should.Count(clause => EvaluateClause(source, clause)) >= requiredShould;
    }

    static bool EvaluateExists(JsonElement source, JsonElement exists)
    {
        if (exists.ValueKind != JsonValueKind.Object
            || !exists.TryGetProperty("field", out var field)
            || field.ValueKind != JsonValueKind.String)
        {
            throw Protocol("An exists query requires one string field property.");
        }

        return TryResolveField(source, field.GetString()!, out var value)
               && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    static void AddClauses(List<JsonElement> destination, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            destination.AddRange(value.EnumerateArray().Select(static clause => clause.Clone()));
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Protocol("A bool query clause must be an object or array of objects.");
        }

        destination.Add(value.Clone());
    }

    static int ReadMinimumShouldMatch(JsonElement value)
    {
        if (value.TryGetInt32(out var number) && number >= 0)
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number >= 0)
        {
            return number;
        }

        throw Protocol("The fake Elasticsearch minimum_should_match must be a nonnegative integer.");
    }

    static bool TryResolveField(JsonElement source, string path, out JsonElement value)
    {
        if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty(path, out value))
        {
            return true;
        }

        value = source;
        foreach (var component in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(component, out value))
            {
                value = default;
                return false;
            }
        }

        return true;
    }

    static bool ScalarEquals(JsonElement actual, JsonElement expected)
    {
        if (actual.ValueKind != expected.ValueKind)
        {
            return false;
        }

        return actual.ValueKind switch
        {
            JsonValueKind.String => string.Equals(actual.GetString(), expected.GetString(), StringComparison.Ordinal),
            JsonValueKind.True or JsonValueKind.False => actual.GetBoolean() == expected.GetBoolean(),
            JsonValueKind.Number => decimal.TryParse(actual.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                                    && decimal.TryParse(expected.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
                                    && left == right,
            JsonValueKind.Null => true,
            _ => throw Protocol("A term query value must be a scalar JSON value.")
        };
    }

    static string ReadSortValue(string id, byte[] source, string sortField)
    {
        if (string.Equals(sortField, "_id", StringComparison.Ordinal))
        {
            return id;
        }

        using var document = JsonDocument.Parse(source);
        if (!TryResolveField(document.RootElement, sortField, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            throw Protocol($"Stable sort field '{sortField}' must resolve to one string value.");
        }

        return value.GetString()!;
    }

    static JsonElement ParseQuery(ReadOnlyMemory<byte> query, string parameterName)
    {
        if (query.IsEmpty)
        {
            using var matchAll = JsonDocument.Parse("{\"match_all\":{}}");
            return matchAll.RootElement.Clone();
        }

        var normalized = NormalizeObject(query, parameterName);
        using var document = JsonDocument.Parse(normalized);
        return document.RootElement.Clone();
    }

    static ImmutableArray<ElasticBulkOperation> NormalizeBulkOperations(
        ImmutableArray<ElasticBulkOperation> operations)
    {
        var builder = ImmutableArray.CreateBuilder<ElasticBulkOperation>(operations.Length);
        foreach (var operation in operations)
        {
            builder.Add(operation.Kind == ElasticBulkOperationKind.Index
                ? new(
                    operation.Kind,
                    operation.Index,
                    operation.Id,
                    operation.ExternalVersion,
                    NormalizeObject(operation.Source, "bulk index source"))
                : new(operation.Kind, operation.Index, operation.Id, operation.ExternalVersion));
        }

        return builder.MoveToImmutable();
    }

    static long MeasureBulkWireBytes(ImmutableArray<ElasticBulkOperation> operations)
    {
        ArrayBufferWriter<byte> buffer = new();
        foreach (var operation in operations)
        {
            WriteJsonLine(buffer, writer =>
            {
                writer.WriteStartObject();
                writer.WritePropertyName(operation.Kind == ElasticBulkOperationKind.Index ? "index" : "delete");
                writer.WriteStartObject();
                writer.WriteString("_index", operation.Index);
                writer.WriteString("_id", operation.Id);
                writer.WriteNumber("version", operation.ExternalVersion);
                writer.WriteString("version_type", "external");
                writer.WriteEndObject();
                writer.WriteEndObject();
            });
            if (operation.Kind == ElasticBulkOperationKind.Index)
            {
                using var source = JsonDocument.Parse(operation.Source);
                WriteJsonLine(buffer, source.RootElement.WriteTo);
            }
        }

        return buffer.WrittenCount;
    }

    static void WriteJsonLine(ArrayBufferWriter<byte> buffer, Action<Utf8JsonWriter> write)
    {
        using (Utf8JsonWriter writer = new(buffer))
        {
            write(writer);
        }

        var newline = buffer.GetSpan(1);
        newline[0] = (byte)'\n';
        buffer.Advance(1);
    }

    static byte[] NormalizeObject(ReadOnlyMemory<byte> source, string parameterName)
    {
        if (source.IsEmpty)
        {
            throw new ArgumentException("A JSON object payload cannot be empty.", parameterName);
        }

        try
        {
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Protocol($"{parameterName} must be a JSON object.");
            }

            return JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ElasticMaterializationTransportException(
                statusCode: null,
                ProtocolErrorType,
                retryable: false,
                $"{parameterName} was not valid bounded JSON.",
                exception);
        }
    }

    bool TryConsumeCreateDocumentFault(byte[] source, out string? documentKind)
    {
        using var document = JsonDocument.Parse(source);
        if (!document.RootElement.TryGetProperty("documentKind", out var kindElement)
            || kindElement.ValueKind != JsonValueKind.String
            || kindElement.GetString() is not { } kind
            || !createDocumentFaults.TryGetValue(kind, out var remaining))
        {
            documentKind = null;
            return false;
        }

        if (remaining == 1)
            createDocumentFaults.Remove(kind);
        else
            createDocumentFaults[kind] = remaining - 1;
        documentKind = kind;
        return true;
    }

    static ImmutableArray<ElasticBulkOperation> CloneBulkOperations(
        ImmutableArray<ElasticBulkOperation> operations) =>
        [.. operations.Select(static operation => operation.Kind == ElasticBulkOperationKind.Index
            ? new ElasticBulkOperation(
                operation.Kind,
                operation.Index,
                operation.Id,
                operation.ExternalVersion,
                operation.Source.ToArray())
            : new ElasticBulkOperation(operation.Kind, operation.Index, operation.Id, operation.ExternalVersion))];

    static ElasticScanRequest CloneScanRequest(ElasticScanRequest request) => new(
        request.Index,
        request.Query.ToArray(),
        request.SortField,
        request.AfterSortValue,
        request.MaximumItems,
        request.MaximumResponseBytes);

    static ElasticAliasCasRequest CloneAliasRequest(ElasticAliasCasRequest request) => new(
        request.MarkerIndex,
        request.ExpectedMarkerAlias,
        request.NextMarkerAlias,
        request.ReadAlias,
        request.ExpectedReadIndex,
        request.NextReadIndex,
        request.MaximumResponseBytes,
        request.ReadAliasFilter.ToArray(),
        request.Routing,
        request.SearchRouting,
        request.IndexRouting,
        request.IsWriteIndex,
        request.ExpectedNextOwnerAlias);

    static ElasticBulkItemResult FailedBulkItem(
        ElasticBulkOperation operation,
        int ordinal,
        int statusCode,
        string errorType,
        string reason) => new(
        ordinal,
        operation.Kind,
        operation.Index,
        operation.Id,
        statusCode,
        Result: null,
        errorType,
        reason,
        ExternalVersion: null,
        ConcurrencyToken: null);

    static ElasticDocumentWriteResult AppliedDocumentWrite(FakeDocument document, int statusCode) => new(
        ElasticDocumentWriteDisposition.Applied,
        statusCode,
        document.Token,
        document.EffectiveVersion);

    static ElasticDocumentWriteResult ConflictDocumentWrite() => new(
        ElasticDocumentWriteDisposition.Conflict,
        StatusCode: 409,
        ConcurrencyToken: null,
        ExternalVersion: null);

    static ElasticDocumentWriteResult NotFoundDocumentWrite() => new(
        ElasticDocumentWriteDisposition.NotFound,
        StatusCode: 404,
        ConcurrencyToken: null,
        ExternalVersion: null);

    static ElasticAcknowledgedResult AppliedAcknowledgement() => new(
        ElasticAcknowledgedDisposition.Applied,
        StatusCode: 200,
        Acknowledged: true);

    static ElasticAcknowledgedResult NotFoundAcknowledgement() => new(
        ElasticAcknowledgedDisposition.NotFound,
        StatusCode: 404,
        Acknowledged: false);

    static void ApplyDocumentWrite(
        FakeIndex index,
        FakeDocument document,
        byte[] source,
        long? externalVersion)
    {
        document.Exists = true;
        document.Source = [.. source];
        document.InternalVersion = checked(document.InternalVersion + 1);
        document.SequenceNumber = index.NextSequenceNumber++;
        document.LastExternalVersion = externalVersion;
    }

    static void ApplyDocumentDelete(FakeIndex index, FakeDocument document, long? externalVersion)
    {
        document.Exists = false;
        document.Source = [];
        document.InternalVersion = checked(document.InternalVersion + 1);
        document.SequenceNumber = index.NextSequenceNumber++;
        document.LastExternalVersion = externalVersion;
    }

    static void RequireWritable(FakeIndex index, string indexName)
    {
        if (index.WriteBlocked)
        {
            throw new ElasticMaterializationTransportException(
                statusCode: 403,
                "cluster_block_exception",
                retryable: false,
                $"Index '{indexName}' is blocked for writes.");
        }
    }

    static void RequireResponseBound(long observedPayloadBytes, int maximumResponseBytes)
    {
        if (observedPayloadBytes > maximumResponseBytes)
        {
            throw new ElasticMaterializationTransportException(
                statusCode: null,
                ResponseLimitErrorType,
                retryable: false,
                $"Fake Elasticsearch response payload exceeded its declared {maximumResponseBytes.ToString(CultureInfo.InvariantCulture)}-byte bound.");
        }
    }

    static bool? ReadOptionalBoolean(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return null;
        }

        return RequireBoolean(value, name);
    }

    static bool RequireBoolean(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw Protocol($"Alias or index setting '{name}' must be Boolean.")
    };

    static string? ReadOptionalString(JsonElement owner, string name)
    {
        if (!owner.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : throw Protocol($"Alias setting '{name}' must be a string.");
    }

    static byte[] JsonBytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());

    static ElasticMaterializationTransportException MissingIndex(string index) => new(
        statusCode: 404,
        "index_not_found_exception",
        retryable: false,
        $"Index or alias '{index}' does not exist.");

    static ElasticMaterializationTransportException Protocol(string message) => new(
        statusCode: null,
        ProtocolErrorType,
        retryable: false,
        message);

    readonly record struct BulkFaultKey(string Index, string Id);

    readonly record struct BulkOrdinalFaultKey(int RequestOrdinal, int ItemOrdinal);

    readonly record struct BulkFault(int StatusCode, string ErrorType, string Reason);

    readonly record struct SearchCandidate(string Id, byte[] Source, JsonElement? AliasFilter);

    readonly record struct SearchHit(string Id, string SortValue, byte[] Source);

    sealed class FakeIndex
    {
        internal FakeIndex(
            string name,
            byte[] createBody,
            Dictionary<string, FakeAlias> aliases,
            bool writeBlocked)
        {
            Name = name;
            CreateBody = [.. createBody];
            Aliases = aliases;
            WriteBlocked = writeBlocked;
        }

        internal string Name { get; }

        internal byte[] CreateBody { get; }

        internal Dictionary<string, FakeDocument> Documents { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, FakeDocument> SearchableDocuments { get; } = new(StringComparer.Ordinal);

        internal Dictionary<string, FakeAlias> Aliases { get; }

        internal bool WriteBlocked { get; set; }

        internal long NextSequenceNumber { get; set; }

        internal void Refresh()
        {
            SearchableDocuments.Clear();
            foreach (var document in Documents)
            {
                if (document.Value.Exists)
                {
                    SearchableDocuments.Add(document.Key, document.Value.Clone());
                }
            }
        }
    }

    sealed class FakeDocument
    {
        internal bool Exists { get; set; }

        internal byte[] Source { get; set; } = [];

        internal long SequenceNumber { get; set; } = -1;

        internal long InternalVersion { get; set; }

        internal long? LastExternalVersion { get; set; }

        internal ElasticDocumentConcurrencyToken Token => new(SequenceNumber, PrimaryTerm);

        internal long EffectiveVersion => LastExternalVersion ?? InternalVersion;

        internal FakeDocument Clone() => new()
        {
            Exists = Exists,
            Source = [.. Source],
            SequenceNumber = SequenceNumber,
            InternalVersion = InternalVersion,
            LastExternalVersion = LastExternalVersion
        };
    }

    sealed record FakeAlias(
        bool? IsHidden,
        bool? IsWriteIndex,
        string? Routing,
        string? SearchRouting,
        string? IndexRouting,
        byte[] Filter)
    {
        internal ElasticAliasBinding ToBinding(string alias, string index) => new(
            alias,
            index,
            IsHidden,
            IsWriteIndex,
            Routing,
            SearchRouting,
            IndexRouting,
            [.. Filter]);
    }
}

internal sealed record FakeElasticMaterializationCall(
    int Ordinal,
    string Operation,
    string? Index,
    string? Id,
    int ItemCount,
    long? MaximumRequestBytes,
    int MaximumResponseBytes);

internal sealed record FakeElasticCountRequest(string Index, byte[] Query, int MaximumResponseBytes)
{
    internal FakeElasticCountRequest Copy() => new(Index, [.. Query], MaximumResponseBytes);
}
