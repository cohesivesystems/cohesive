using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cohesive.Storage.Materialization;
using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using ElasticHttpMethod = Elastic.Transport.HttpMethod;

namespace Cohesive.Adapters.Elastic;

/// <summary>Bounded low-level realization of the physical Elasticsearch materialization seam.</summary>
internal sealed class ElasticsearchMaterializationTransport : IElasticMaterializationTransport
{
    const string ProtocolErrorType = "cohesive.elasticsearch.protocol";
    const string ResponseLimitErrorType = "cohesive.elasticsearch.response.limitExceeded";
    const string ProviderErrorType = "elasticsearch.error";
    const string TransportErrorType = "cohesive.elasticsearch.transport";
    const int MaximumErrorTypeLength = 128;
    const int MaximumErrorTextLength = 1024;
    readonly ElasticsearchClient client;

    internal ElasticsearchMaterializationTransport(ElasticElasticsearchRuntimeBinding runtimeBinding)
        : this((runtimeBinding ?? throw new ArgumentNullException(nameof(runtimeBinding))).Client)
    {
    }

    internal ElasticsearchMaterializationTransport(ElasticsearchClient client) =>
        this.client = client ?? throw new ArgumentNullException(nameof(client));

    public async ValueTask<ElasticDocumentReadResult> GetDocumentAsync(
        string index,
        string id,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        var response = await SendAsync(
            ElasticHttpMethod.GET,
            $"/{Escape(index)}/_doc/{Escape(id)}",
            body: null,
            maximumResponseBytes,
            "get-control-document",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == 404 && IsDocumentNotFoundResponse(response.Body))
        {
            return new(false, [], ConcurrencyToken: null, ExternalVersion: null);
        }

        RequireSuccess(response, "get-control-document");
        using var document = ParseObject(response.Body, "get-control-document response");
        var root = document.RootElement;
        if (root.TryGetProperty("found", out var foundElement)
            && foundElement.ValueKind == JsonValueKind.False)
        {
            return new(false, [], ConcurrencyToken: null, ExternalVersion: null);
        }

        var source = RequireProperty(root, "_source", "get-control-document response");
        return new(
            Found: true,
            Source: JsonBytes(source),
            ConcurrencyToken: ReadConcurrencyToken(root, required: true, "get-control-document response"),
            ExternalVersion: ReadInt64(root, "_version", required: false, "get-control-document response"));
    }

    public ValueTask<ElasticDocumentWriteResult> CreateDocumentAsync(
        string index,
        string id,
        ElasticJsonObject source,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        WriteDocumentAsync(
            ElasticHttpMethod.PUT,
            $"/{Escape(ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index)))}/_create/{Escape(ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id)))}",
            source,
            maximumResponseBytes,
            allowNotFound: false,
            cancellationToken);

    public ValueTask<ElasticDocumentWriteResult> ReplaceDocumentAsync(
        string index,
        string id,
        ElasticJsonObject source,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        expected.Validate(nameof(expected));
        var path = $"/{Escape(index)}/_doc/{Escape(id)}?if_seq_no={Invariant(expected.SequenceNumber)}&if_primary_term={Invariant(expected.PrimaryTerm)}";
        return WriteDocumentAsync(
            ElasticHttpMethod.PUT,
            path,
            source,
            maximumResponseBytes,
            allowNotFound: true,
            cancellationToken);
    }

    public async ValueTask<ElasticDocumentWriteResult> DeleteDocumentAsync(
        string index,
        string id,
        ElasticDocumentConcurrencyToken expected,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        id = ElasticMaterializationPhysicalNames.RequireValue(id, nameof(id));
        expected.Validate(nameof(expected));
        var response = await SendAsync(
            ElasticHttpMethod.DELETE,
            $"/{Escape(index)}/_doc/{Escape(id)}?if_seq_no={Invariant(expected.SequenceNumber)}&if_primary_term={Invariant(expected.PrimaryTerm)}",
            body: null,
            maximumResponseBytes,
            "delete-control-document",
            cancellationToken).ConfigureAwait(false);
        return ParseDocumentWrite(response, allowNotFound: true, "delete-control-document");
    }

    public async ValueTask<ElasticIndexCreateResult> CreateIndexAsync(
        string index,
        ElasticJsonObject body,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ArgumentNullException.ThrowIfNull(body);
        var response = await SendAsync(
            ElasticHttpMethod.PUT,
            $"/{Escape(index)}",
            body.Bytes,
            maximumResponseBytes,
            "create-index",
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode is 400 or 409
            && string.Equals(ReadError(response.Body).Type, "resource_already_exists_exception", StringComparison.Ordinal))
        {
            return new(
                ElasticIndexCreateDisposition.AlreadyExists,
                response.StatusCode,
                Acknowledged: false,
                ShardsAcknowledged: false,
                index);
        }

        RequireSuccess(response, "create-index");
        using var document = ParseObject(response.Body, "create-index response");
        var root = document.RootElement;
        return new(
            ElasticIndexCreateDisposition.Created,
            response.StatusCode,
            ReadBoolean(root, "acknowledged", defaultValue: false, "create-index response"),
            ReadBoolean(root, "shards_acknowledged", defaultValue: false, "create-index response"),
            ReadString(root, "index", required: false, "create-index response") ?? index);
    }

    public async ValueTask<bool> IndexExistsAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        var response = await SendAsync(
            ElasticHttpMethod.HEAD,
            $"/{Escape(index)}",
            body: null,
            maximumResponseBytes,
            "inspect-index-existence",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == 404)
        {
            return false;
        }

        RequireSuccess(response, "inspect-index-existence");
        return true;
    }

    public ValueTask<ElasticAcknowledgedResult> AddWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        return AcknowledgedAsync(
            ElasticHttpMethod.PUT,
            $"/{Escape(index)}/_block/write",
            maximumResponseBytes,
            allowNotFound: true,
            "add-write-block",
            cancellationToken,
            expectedIndex: index,
            expectedIndexState: "blocked",
            requireShardsAcknowledged: true);
    }

    /// <summary>Removes the write block so a promoted generation can resume incremental writes.</summary>
    /// <remarks>
    /// Elasticsearch 8.x removes the dynamic write block through index settings. The dedicated remove-block endpoint
    /// was introduced in 9.1, while the dedicated add-block endpoint remains necessary for its in-flight-write barrier.
    /// </remarks>
    public ValueTask<ElasticAcknowledgedResult> RemoveWriteBlockAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        AcknowledgedAsync(
            ElasticHttpMethod.PUT,
            $"/{Escape(ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index)))}/_settings",
            maximumResponseBytes,
            allowNotFound: true,
            "remove-write-block",
            cancellationToken,
            ElasticMaterializationWireJson.RemoveWriteBlockBody.Bytes);

    public ValueTask<ElasticAcknowledgedResult> RefreshAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        AcknowledgedAsync(
            ElasticHttpMethod.POST,
            $"/{Escape(ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index)))}/_refresh",
            maximumResponseBytes,
            allowNotFound: true,
            "refresh-index",
            cancellationToken,
            requireAcknowledged: false,
            requireShardEvidence: true);

    public ValueTask<ElasticAcknowledgedResult> DeleteIndexAsync(
        string index,
        int maximumResponseBytes,
        CancellationToken cancellationToken) =>
        AcknowledgedAsync(
            ElasticHttpMethod.DELETE,
            $"/{Escape(ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index)))}",
            maximumResponseBytes,
            allowNotFound: true,
            "delete-index",
            cancellationToken);

    public async ValueTask<ElasticOwnedIndexDeleteResult> DeleteOwnedIndexAsync(
        string index,
        string ownerAlias,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ownerAlias = ElasticMaterializationPhysicalNames.RequireConcreteAlias(ownerAlias, nameof(ownerAlias));
        var body = ElasticMaterializationWireJson.CreateDeleteOwnedIndexBody(index, ownerAlias);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            "/_aliases",
            body.Bytes,
            maximumResponseBytes,
            "delete-owned-index",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is 400 or 404 or 409)
        {
            return new(
                ElasticOwnedIndexDeleteDisposition.OwnershipConflict,
                response.StatusCode,
                Acknowledged: false);
        }

        RequireSuccess(response, "delete-owned-index");
        using var document = ParseObject(response.Body, "owned index deletion response");
        return new(
            ElasticOwnedIndexDeleteDisposition.Applied,
            response.StatusCode,
            ReadBoolean(document.RootElement, "acknowledged", defaultValue: false, "owned index deletion response"));
    }

    public async ValueTask<ElasticMultiGetResult> MultiGetAsync(
        string index,
        ImmutableArray<string> ids,
        ElasticMultiGetSourceProjection sourceProjection,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        if (!Enum.IsDefined(sourceProjection))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceProjection),
                sourceProjection,
                "Unsupported Elasticsearch multi-get source projection.");
        }
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        if (ids.IsDefaultOrEmpty)
        {
            return new([]);
        }

        foreach (var id in ids)
        {
            ElasticMaterializationPhysicalNames.RequireValue(id, nameof(ids));
        }

        var body = ElasticMaterializationWireJson.CreateMultiGetBody(ids, sourceProjection);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            $"/{Escape(index)}/_mget",
            body.Bytes,
            maximumResponseBytes,
            "multi-get",
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(response, "multi-get");

        using var document = ParseObject(response.Body, "multi-get response");
        var docs = RequireProperty(document.RootElement, "docs", "multi-get response");
        if (docs.ValueKind != JsonValueKind.Array || docs.GetArrayLength() != ids.Length)
        {
            throw Protocol("A multi-get response must contain exactly one result per requested identity.");
        }

        var builder = ImmutableArray.CreateBuilder<ElasticMultiGetDocument>(ids.Length);
        var ordinal = 0;
        foreach (var item in docs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Protocol("A multi-get response item must be an object.");
            }

            var expectedId = ids[ordinal++];
            var observedId = ReadString(item, "_id", required: true, "multi-get response item")!;
            if (!string.Equals(expectedId, observedId, StringComparison.Ordinal))
            {
                throw Protocol("A multi-get response did not preserve request identity order.");
            }

            if (item.TryGetProperty("error", out var itemError))
            {
                var error = ReadErrorElement(itemError);
                var status = ReadInt32(item, "status", required: false, "multi-get response item") ?? 500;
                throw RequestFailure(status, error, "multi-get item");
            }

            var found = ReadBoolean(item, "found", defaultValue: false, "multi-get response item");
            builder.Add(found
                ? new(
                    observedId,
                    Found: true,
                    Source: JsonBytes(RequireProperty(item, "_source", "multi-get response item")),
                    ConcurrencyToken: ReadConcurrencyToken(item, required: true, "multi-get response item"),
                    ExternalVersion: ReadInt64(item, "_version", required: false, "multi-get response item"))
                : new(observedId, Found: false, Source: [], ConcurrencyToken: null, ExternalVersion: null));
        }

        return new(builder.MoveToImmutable());
    }

    public async ValueTask<ElasticBulkResult> BulkAsync(
        ImmutableArray<ElasticBulkOperation> operations,
        long maximumWireBytes,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        if (operations.IsDefaultOrEmpty)
        {
            throw new ArgumentException("An Elasticsearch bulk request requires at least one operation.", nameof(operations));
        }

        foreach (var operation in operations)
        {
            if (operation is null)
            {
                throw new ArgumentException(
                    "An Elasticsearch bulk request cannot contain a null operation.",
                    nameof(operations));
            }
        }

        if (maximumWireBytes <= 0 || maximumWireBytes > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumWireBytes),
                maximumWireBytes,
                $"A bulk wire bound must be between 1 and {Array.MaxLength} bytes.");
        }

        var body = ElasticBulkNdjson.Build(operations, maximumWireBytes);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            "/_bulk",
            body,
            maximumResponseBytes,
            "bulk",
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(response, "bulk");
        return ParseBulkResponse(response.Body, operations, body.Length);
    }

    public async ValueTask<ElasticScanPage> ScanAsync(
        ElasticScanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = ElasticMaterializationWireJson.CreateScanBody(request);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            $"/{Escape(request.Index)}/_search",
            body.Bytes,
            request.MaximumResponseBytes,
            "scan",
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(response, "scan");

        using var document = ParseObject(response.Body, "scan response");
        var root = document.RootElement;
        RequireCompleteSearch(root, "scan response");
        var took = ReadInt64(root, "took", required: false, "scan response") ?? 0;
        var hitsObject = RequireProperty(root, "hits", "scan response");
        var hits = RequireProperty(hitsObject, "hits", "scan response");
        if (hits.ValueKind != JsonValueKind.Array || hits.GetArrayLength() > request.MaximumItems + 1)
        {
            throw Protocol("A scan response exceeded its requested item look-ahead bound.");
        }

        var count = Math.Min(request.MaximumItems, hits.GetArrayLength());
        var builder = ImmutableArray.CreateBuilder<ElasticScanHit>(count);
        var ordinal = 0;
        var previousSort = request.AfterSortValue;
        foreach (var hit in hits.EnumerateArray())
        {
            if (ordinal++ >= count)
            {
                break;
            }

            var sort = RequireProperty(hit, "sort", "scan response hit");
            if (sort.ValueKind != JsonValueKind.Array || sort.GetArrayLength() != 1)
            {
                throw Protocol("A scan response hit must contain exactly one sort value.");
            }

            var sortValueElement = sort[0];
            if (sortValueElement.ValueKind != JsonValueKind.String)
            {
                throw Protocol("A materialization scan sort value must be a string.");
            }

            var sortValue = sortValueElement.GetString()!;
            if (previousSort is not null
                && MaterializationSealContentOrder.Compare(new(previousSort), new(sortValue)) >= 0)
            {
                throw Protocol("A materialization scan page was not strictly ordered by its stable sort field.");
            }

            previousSort = sortValue;
            builder.Add(new(
                ReadString(hit, "_id", required: true, "scan response hit")!,
                sortValue,
                JsonBytes(RequireProperty(hit, "_source", "scan response hit"))));
        }

        var page = builder.MoveToImmutable();
        var next = hits.GetArrayLength() > request.MaximumItems ? page[^1].SortValue : null;
        return new(page, next, took);
    }

    public async ValueTask<ElasticCountResult> CountAsync(
        string index,
        ElasticJsonObject query,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        index = ElasticMaterializationPhysicalNames.RequireConcreteIndex(index, nameof(index));
        ArgumentNullException.ThrowIfNull(query);
        var body = ElasticMaterializationWireJson.CreateCountBody(query);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            $"/{Escape(index)}/_count",
            body.Bytes,
            maximumResponseBytes,
            "count",
            cancellationToken).ConfigureAwait(false);
        RequireSuccess(response, "count");
        using var document = ParseObject(response.Body, "count response");
        RequireShardSuccess(document.RootElement, "count response");
        return new(
            ReadInt64(document.RootElement, "count", required: true, "count response")!.Value,
            ReadInt64(document.RootElement, "took", required: false, "count response") ?? 0);
    }

    public async ValueTask<ElasticAliasCasResult> CompareExchangeAliasAsync(
        ElasticAliasCasRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ReadAlias is { } readAlias)
        {
            var aliases = await InspectAliasesAsync(
                [readAlias],
                request.MaximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            if (request.ExpectedReadIndex is null)
            {
                if (!aliases.Bindings.IsDefaultOrEmpty)
                {
                    return new(ElasticAliasCasDisposition.Conflict, StatusCode: 409, Acknowledged: false);
                }
            }
            else if (aliases.Bindings is not [var existing]
                || existing.Alias != readAlias
                || existing.Index != request.ExpectedReadIndex
                || existing.IsHidden is true
                || existing.IsWriteIndex != request.IsWriteIndex
                || existing.Routing != request.Routing
                || existing.SearchRouting != request.SearchRouting
                || existing.IndexRouting != request.IndexRouting
                || !ElasticJsonObject.DeepEquals(existing.Filter, request.ReadAliasFilter?.Bytes ?? default))
            {
                return new(ElasticAliasCasDisposition.Conflict, StatusCode: 409, Acknowledged: false);
            }
        }

        var body = ElasticMaterializationWireJson.CreateAliasBody(request);
        var response = await SendAsync(
            ElasticHttpMethod.POST,
            "/_aliases",
            body.Bytes,
            request.MaximumResponseBytes,
            "alias-compare-exchange",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is 400 or 404 or 409)
        {
            var error = ReadError(response.Body);
            if (IsAliasFenceConflict(error.Type))
            {
                return new(ElasticAliasCasDisposition.Conflict, response.StatusCode, Acknowledged: false);
            }
        }

        RequireSuccess(response, "alias-compare-exchange");
        using var document = ParseObject(response.Body, "alias compare-and-swap response");
        if (ReadBoolean(document.RootElement, "errors", defaultValue: false, "alias compare-and-swap response"))
        {
            return new(ElasticAliasCasDisposition.Conflict, response.StatusCode, Acknowledged: false);
        }
        return new(
            ElasticAliasCasDisposition.Applied,
            response.StatusCode,
            ReadBoolean(document.RootElement, "acknowledged", defaultValue: false, "alias compare-and-swap response"));
    }

    public async ValueTask<ElasticAliasSnapshot> InspectAliasesAsync(
        ImmutableArray<string> aliases,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        if (aliases.IsDefaultOrEmpty)
        {
            throw new ArgumentException("Alias inspection requires at least one exact alias.", nameof(aliases));
        }

        HashSet<string> requested = new(StringComparer.Ordinal);
        foreach (var alias in aliases)
        {
            if (!requested.Add(ElasticMaterializationPhysicalNames.RequireConcreteAlias(alias, nameof(aliases))))
            {
                throw new ArgumentException("Alias inspection cannot repeat an alias.", nameof(aliases));
            }
        }

        var aliasPath = string.Join(",", aliases.Select(Escape));
        var response = await SendAsync(
            ElasticHttpMethod.GET,
            $"/_alias/{aliasPath}?ignore_unavailable=true&allow_no_indices=true&expand_wildcards=all",
            body: null,
            maximumResponseBytes,
            "inspect-aliases",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != 404)
        {
            RequireSuccess(response, "inspect-aliases");
        }
        using var document = ParseObject(response.Body, "alias inspection response");
        List<ElasticAliasBinding> bindings = [];
        foreach (var indexProperty in document.RootElement.EnumerateObject())
        {
            var indexBody = indexProperty.Value;
            if (response.StatusCode == 404
                && indexProperty.Name is "error" or "status"
                && (indexBody.ValueKind != JsonValueKind.Object
                    || !indexBody.TryGetProperty("aliases", out _)))
            {
                continue;
            }

            if (indexBody.ValueKind != JsonValueKind.Object
                || !indexBody.TryGetProperty("aliases", out var aliasesBody)
                || aliasesBody.ValueKind != JsonValueKind.Object)
            {
                throw Protocol("An alias inspection response contained an invalid index entry.");
            }

            foreach (var aliasProperty in aliasesBody.EnumerateObject())
            {
                if (!requested.Contains(aliasProperty.Name))
                {
                    continue;
                }

                var value = aliasProperty.Value;
                if (value.ValueKind != JsonValueKind.Object)
                {
                    throw Protocol("An alias inspection response contained an invalid alias entry.");
                }

                bindings.Add(new(
                    aliasProperty.Name,
                    indexProperty.Name,
                    ReadNullableBoolean(value, "is_hidden", "alias inspection response"),
                    ReadNullableBoolean(value, "is_write_index", "alias inspection response"),
                    ReadString(value, "routing", required: false, "alias inspection response"),
                    ReadString(value, "search_routing", required: false, "alias inspection response"),
                    ReadString(value, "index_routing", required: false, "alias inspection response"),
                    value.TryGetProperty("filter", out var filter) ? JsonBytes(filter) : []));
            }
        }

        return new([
            .. bindings
                .OrderBy(static item => item.Alias, StringComparer.Ordinal)
                .ThenBy(static item => item.Index, StringComparer.Ordinal)
        ]);
    }

    async ValueTask<ElasticDocumentWriteResult> WriteDocumentAsync(
        ElasticHttpMethod method,
        string path,
        ElasticJsonObject source,
        int maximumResponseBytes,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var response = await SendAsync(
            method,
            path,
            source.Bytes,
            maximumResponseBytes,
            "write-control-document",
            cancellationToken).ConfigureAwait(false);
        return ParseDocumentWrite(response, allowNotFound, "write-control-document");
    }

    static ElasticDocumentWriteResult ParseDocumentWrite(
        in RawResponse response,
        bool allowNotFound,
        string operation)
    {
        if (response.StatusCode == 409)
        {
            return new(ElasticDocumentWriteDisposition.Conflict, response.StatusCode, null, null);
        }

        if (allowNotFound
            && response.StatusCode == 404
            && IsDocumentNotFoundResponse(response.Body))
        {
            return new(ElasticDocumentWriteDisposition.NotFound, response.StatusCode, null, null);
        }

        RequireSuccess(response, operation);
        using var document = ParseObject(response.Body, $"{operation} response");
        return new(
            ElasticDocumentWriteDisposition.Applied,
            response.StatusCode,
            ReadConcurrencyToken(document.RootElement, required: true, $"{operation} response"),
            ReadInt64(document.RootElement, "_version", required: false, $"{operation} response"));
    }

    async ValueTask<ElasticAcknowledgedResult> AcknowledgedAsync(
        ElasticHttpMethod method,
        string path,
        int maximumResponseBytes,
        bool allowNotFound,
        string operation,
        CancellationToken cancellationToken,
        ReadOnlyMemory<byte>? body = null,
        string? expectedIndex = null,
        string? expectedIndexState = null,
        bool requireAcknowledged = true,
        bool requireShardsAcknowledged = false,
        bool requireShardEvidence = false)
    {
        var response = await SendAsync(
            method,
            path,
            body,
            maximumResponseBytes,
            operation,
            cancellationToken).ConfigureAwait(false);
        if (allowNotFound && response.StatusCode == 404)
        {
            return new(ElasticAcknowledgedDisposition.NotFound, response.StatusCode, Acknowledged: false);
        }

        RequireSuccess(response, operation);
        using var document = ParseObject(response.Body, $"{operation} response");
        if (requireShardEvidence && !document.RootElement.TryGetProperty("_shards", out _))
        {
            throw Protocol($"{operation} response omitted required shard evidence.");
        }
        RequireShardSuccess(document.RootElement, $"{operation} response");
        var acknowledged = ReadBoolean(
            document.RootElement,
            "acknowledged",
            defaultValue: !requireAcknowledged,
            $"{operation} response");
        var shardsAcknowledged = ReadBoolean(
            document.RootElement,
            "shards_acknowledged",
            defaultValue: !requireShardsAcknowledged,
            $"{operation} response");
        var indexStateAcknowledged = expectedIndex is null
            ? true
            : ReadIndexState(
                document.RootElement,
                expectedIndex,
                expectedIndexState ?? throw new InvalidOperationException("Expected index state was not supplied."),
                $"{operation} response");
        return new(
            ElasticAcknowledgedDisposition.Applied,
            response.StatusCode,
            acknowledged && shardsAcknowledged && indexStateAcknowledged);
    }

    static bool ReadIndexState(
        JsonElement root,
        string expectedIndex,
        string stateProperty,
        string context)
    {
        var indices = RequireProperty(root, "indices", context);
        if (indices.ValueKind != JsonValueKind.Array || indices.GetArrayLength() != 1)
        {
            throw Protocol($"{context} must contain exactly one explicit index result.");
        }

        var index = indices[0];
        if (index.ValueKind != JsonValueKind.Object
            || !string.Equals(
                ReadString(index, "name", required: true, context),
                expectedIndex,
                StringComparison.Ordinal))
        {
            throw Protocol($"{context} did not identify the requested concrete index.");
        }

        if (index.TryGetProperty("exception", out _))
        {
            return false;
        }

        return ReadBoolean(index, stateProperty, defaultValue: false, context);
    }

    async ValueTask<RawResponse> SendAsync(
        ElasticHttpMethod method,
        string path,
        ReadOnlyMemory<byte>? body,
        int maximumResponseBytes,
        string operation,
        CancellationToken cancellationToken)
    {
        ElasticMaterializationPhysicalNames.RequirePositive(maximumResponseBytes, nameof(maximumResponseBytes));
        cancellationToken.ThrowIfCancellationRequested();
        int? responseStatusCode = null;
        try
        {
            using var response = body is null
                ? await client.Transport.RequestAsync<StreamResponse>(method, path, cancellationToken).ConfigureAwait(false)
                : await client.Transport.RequestAsync<StreamResponse>(
                    method,
                    path,
                    PostData.ReadOnlyMemory(body.Value),
                    cancellationToken).ConfigureAwait(false);
            responseStatusCode = response.ApiCallDetails.HttpStatusCode;
            var statusCode = responseStatusCode ?? 0;
            var responseBody = await ReadBoundedAsync(
                response.Body,
                maximumResponseBytes,
                cancellationToken).ConfigureAwait(false);
            return new(statusCode, responseBody);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ElasticMaterializationTransportException exception)
        {
            throw SanitizedFailure(
                operation,
                exception.StatusCode ?? responseStatusCode,
                exception.ErrorType,
                exception.Retryable);
        }
        catch (Exception)
        {
            throw SanitizedFailure(
                operation,
                statusCode: null,
                TransportErrorType,
                retryable: true);
        }
    }

    static async ValueTask<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var initialCapacity = Math.Min(maximumBytes, 16 * 1024);
        using MemoryStream destination = new(initialCapacity);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Min(maximumBytes, 16 * 1024));
        try
        {
            while (true)
            {
                var remaining = maximumBytes - checked((int)destination.Length);
                var requested = remaining >= rented.Length ? rented.Length : remaining + 1;
                var read = await stream.ReadAsync(rented.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return destination.ToArray();
                }

                if (read > remaining)
                {
                    throw new ElasticMaterializationTransportException(
                        statusCode: null,
                        ResponseLimitErrorType,
                        retryable: false,
                        $"Elasticsearch response exceeded its declared {maximumBytes.ToString(CultureInfo.InvariantCulture)}-byte bound.");
                }

                destination.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    static ElasticBulkResult ParseBulkResponse(
        byte[] responseBytes,
        ImmutableArray<ElasticBulkOperation> operations,
        long wireBytes)
    {
        using var document = ParseObject(responseBytes, "bulk response");
        var root = document.RootElement;
        var items = RequireProperty(root, "items", "bulk response");
        if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() != operations.Length)
        {
            throw Protocol("A bulk response must contain exactly one item per operation.");
        }

        var builder = ImmutableArray.CreateBuilder<ElasticBulkItemResult>(operations.Length);
        var ordinal = 0;
        foreach (var item in items.EnumerateArray())
        {
            var operation = operations[ordinal];
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw Protocol("A bulk response item must be an object.");
            }

            using var properties = item.EnumerateObject();
            if (!properties.MoveNext())
            {
                throw Protocol("A bulk response item did not contain an operation result.");
            }

            var property = properties.Current;
            if (properties.MoveNext())
            {
                throw Protocol("A bulk response item contained more than one operation result.");
            }

            var observedKind = property.Name switch
            {
                "index" => ElasticBulkOperationKind.Index,
                "delete" => ElasticBulkOperationKind.Delete,
                _ => throw Protocol("A bulk response contained an unexpected operation kind.")
            };
            if (observedKind != operation.Kind || property.Value.ValueKind != JsonValueKind.Object)
            {
                throw Protocol("A bulk response operation kind did not match its request ordinal.");
            }

            var value = property.Value;
            var index = ReadString(value, "_index", required: true, "bulk response item")!;
            var id = ReadString(value, "_id", required: true, "bulk response item")!;
            if (!string.Equals(index, operation.Index, StringComparison.Ordinal)
                || !string.Equals(id, operation.Id, StringComparison.Ordinal))
            {
                throw Protocol("A bulk response identity did not match its request ordinal.");
            }

            ErrorInfo? error = null;
            if (value.TryGetProperty("error", out var errorElement))
            {
                error = ReadErrorElement(errorElement);
            }

            builder.Add(new(
                ordinal,
                operation.Kind,
                index,
                id,
                ReadInt32(value, "status", required: true, "bulk response item")!.Value,
                ReadString(value, "result", required: false, "bulk response item"),
                error?.Type,
                error?.Reason,
                ReadInt64(value, "_version", required: false, "bulk response item"),
                ReadConcurrencyToken(value, required: false, "bulk response item")));
            ordinal++;
        }

        return new(
            wireBytes,
            ReadInt64(root, "took", required: false, "bulk response") ?? 0,
            ReadBoolean(root, "errors", defaultValue: false, "bulk response"),
            builder.MoveToImmutable());
    }

    static JsonDocument ParseObject(ReadOnlyMemory<byte> value, string context)
    {
        try
        {
            var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw Protocol($"{context} must be a JSON object.");
            }
            return document;
        }
        catch (JsonException)
        {
            throw new ElasticMaterializationTransportException(
                statusCode: null,
                ProtocolErrorType,
                retryable: false,
                $"{context} was not valid bounded JSON.");
        }
    }

    static void RequireSuccess(in RawResponse response, string operation)
    {
        if (response.StatusCode is >= 200 and <= 299)
        {
            return;
        }

        throw RequestFailure(response.StatusCode, ReadError(response.Body), operation);
    }

    static ElasticMaterializationTransportException RequestFailure(
        int statusCode,
        in ErrorInfo error,
        string operation) =>
        SanitizedFailure(
            operation,
            statusCode,
            error.Type,
            ElasticMaterializationRetryPolicy.IsRetryableStatus(statusCode));

    static ElasticMaterializationTransportException SanitizedFailure(
        string operation,
        int? statusCode,
        string? errorType,
        bool retryable)
    {
        var safeType = SanitizeErrorType(errorType);
        var status = statusCode is { } value
            ? $"HTTP {value.ToString(CultureInfo.InvariantCulture)}"
            : "an unavailable HTTP status";
        return new(
            statusCode,
            safeType,
            retryable,
            $"Elasticsearch {operation} failed with {status} ({safeType}).");
    }

    static ErrorInfo ReadError(byte[] body)
    {
        if (body.Length == 0)
        {
            return new("elasticsearch.http", "The response did not include error details.");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                    ? ReadErrorElement(error)
                    : new("elasticsearch.http", "The response did not include structured error details.");
        }
        catch (JsonException)
        {
            return new("elasticsearch.http", "The response did not include valid structured error details.");
        }
    }

    static bool IsDocumentNotFoundResponse(byte[] body)
    {
        if (body.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("error", out _))
            {
                return false;
            }

            return root.TryGetProperty("found", out var found)
                    && found.ValueKind == JsonValueKind.False
                || root.TryGetProperty("result", out var result)
                    && result.ValueKind == JsonValueKind.String
                    && string.Equals(result.GetString(), "not_found", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool IsAliasFenceConflict(string errorType) =>
        errorType is "aliases_not_found_exception" or "alias_not_found_exception";

    static ErrorInfo ReadErrorElement(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return new(ProviderErrorType, SanitizeReason(error.GetString()));
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return new(ProviderErrorType, "Elasticsearch returned an unrecognized error representation.");
        }

        return new(
            SanitizeErrorType(ReadString(error, "type", required: false, "error response")),
            SanitizeReason(ReadString(error, "reason", required: false, "error response")));
    }

    static string SanitizeErrorType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumErrorTypeLength)
        {
            return ProviderErrorType;
        }

        foreach (var character in value)
        {
            if (!(character is >= 'a' and <= 'z'
                    or >= 'A' and <= 'Z'
                    or >= '0' and <= '9'
                    or '.' or '_' or '-'))
            {
                return ProviderErrorType;
            }
        }

        return value;
    }

    static string SanitizeReason(string? value, string fallback = "Elasticsearch request failed.")
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        Span<char> characters = stackalloc char[Math.Min(value.Length, MaximumErrorTextLength)];
        var count = 0;
        foreach (var character in value)
        {
            if (count == characters.Length)
            {
                break;
            }
            characters[count++] = char.IsControl(character) ? ' ' : character;
        }
        var sanitized = new string(characters[..count]).Trim();
        return sanitized.Length == 0 ? fallback : sanitized;
    }

    static void RequireCompleteSearch(JsonElement root, string context)
    {
        if (ReadBoolean(root, "timed_out", defaultValue: false, context))
        {
            throw new ElasticMaterializationTransportException(
                statusCode: 200,
                "elasticsearch.search.timeout",
                retryable: true,
                "Elasticsearch search timed out before producing a complete page.");
        }
        RequireShardSuccess(root, context);
    }

    static void RequireShardSuccess(JsonElement root, string context)
    {
        if (!root.TryGetProperty("_shards", out var shards))
        {
            return;
        }
        if (shards.ValueKind != JsonValueKind.Object)
        {
            throw Protocol($"{context} contained invalid shard evidence.");
        }

        var failed = ReadInt64(shards, "failed", required: false, context) ?? 0;
        var total = ReadInt64(shards, "total", required: false, context);
        var successful = ReadInt64(shards, "successful", required: false, context);
        if (failed > 0 || total is { } totalCount && successful is { } successfulCount && successfulCount < totalCount)
        {
            throw new ElasticMaterializationTransportException(
                statusCode: 200,
                "elasticsearch.shard.incomplete",
                retryable: true,
                $"{context} did not complete successfully on every addressed shard.");
        }
    }

    static JsonElement RequireProperty(JsonElement owner, string propertyName, string context)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(propertyName, out var value))
        {
            throw Protocol($"{context} omitted required property '{propertyName}'.");
        }
        return value;
    }

    static string? ReadString(JsonElement owner, string propertyName, bool required, string context)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            if (required)
            {
                throw Protocol($"{context} omitted required string '{propertyName}'.");
            }
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw Protocol($"{context} property '{propertyName}' was not a string.");
        }
        return value.GetString();
    }

    static long? ReadInt64(JsonElement owner, string propertyName, bool required, string context)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            if (required)
            {
                throw Protocol($"{context} omitted required integer '{propertyName}'.");
            }
            return null;
        }
        if (!value.TryGetInt64(out var result))
        {
            throw Protocol($"{context} property '{propertyName}' was not a 64-bit integer.");
        }
        return result;
    }

    static int? ReadInt32(JsonElement owner, string propertyName, bool required, string context)
    {
        var value = ReadInt64(owner, propertyName, required, context);
        if (value is null)
        {
            return null;
        }
        if (value is < int.MinValue or > int.MaxValue)
        {
            throw Protocol($"{context} property '{propertyName}' exceeded the 32-bit integer range.");
        }
        return (int)value.Value;
    }

    static bool ReadBoolean(JsonElement owner, string propertyName, bool defaultValue, string context)
    {
        var value = ReadNullableBoolean(owner, propertyName, context);
        return value ?? defaultValue;
    }

    static bool? ReadNullableBoolean(JsonElement owner, string propertyName, string context)
    {
        if (!owner.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Protocol($"{context} property '{propertyName}' was not Boolean.")
        };
    }

    static ElasticDocumentConcurrencyToken? ReadConcurrencyToken(
        JsonElement owner,
        bool required,
        string context)
    {
        var sequence = ReadInt64(owner, "_seq_no", required, context);
        var primaryTerm = ReadInt64(owner, "_primary_term", required, context);
        if ((sequence is null) != (primaryTerm is null))
        {
            throw Protocol($"{context} contained incomplete optimistic-concurrency evidence.");
        }
        if (sequence is null)
        {
            return null;
        }

        ElasticDocumentConcurrencyToken token = new(sequence.Value, primaryTerm!.Value);
        token.Validate(context);
        return token;
    }

    static byte[] JsonBytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());

    static string Escape(string value) => Uri.EscapeDataString(value);

    static string Invariant(long value) => value.ToString(CultureInfo.InvariantCulture);

    static ElasticMaterializationTransportException Protocol(string message) =>
        new(statusCode: null, ProtocolErrorType, retryable: false, message);

    readonly record struct RawResponse(int StatusCode, byte[] Body);

    readonly record struct ErrorInfo(string Type, string Reason);
}
