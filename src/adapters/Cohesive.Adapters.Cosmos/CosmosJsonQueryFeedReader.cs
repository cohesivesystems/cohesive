using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>
/// Reads and owns one Cosmos SDK JSON query iterator while preserving the distinction between provider exhaustion
/// and an adapter-enforced row boundary.
/// </summary>
internal sealed class CosmosJsonQueryFeedReader
{
    const string EvidenceProfile = "cosmos-json-query-feed/v2";
    const string PageEvidenceProfile = "cosmos-json-query-page/v1";

    readonly Func<FeedRange?, QueryDefinition, string?, QueryRequestOptions, FeedIterator<JsonElement>> iteratorFactory;

    /// <summary>Creates a JSON feed reader for one Cosmos container.</summary>
    /// <param name="container">Container that creates each SDK query iterator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is <see langword="null"/>.</exception>
    internal CosmosJsonQueryFeedReader(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        AccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(container.Database.Client.Endpoint);
        DatabaseName = Guard.RequireNotNullOrWhiteSpace(container.Database.Id);
        ContainerName = Guard.RequireNotNullOrWhiteSpace(container.Id);
        iteratorFactory = (feedRange, query, continuationToken, requestOptions) => feedRange is null
            ? container.GetItemQueryIterator<JsonElement>(
                query,
                continuationToken,
                requestOptions)
            : container.GetItemQueryIterator<JsonElement>(
                feedRange,
                query,
                continuationToken,
                requestOptions);
    }

    /// <summary>Creates a JSON feed reader over an explicit SDK iterator factory.</summary>
    /// <param name="accountEndpoint">Absolute Cosmos account endpoint attributed to every read.</param>
    /// <param name="databaseName">Database identity attributed to every read.</param>
    /// <param name="containerName">Container identity attributed to every read.</param>
    /// <param name="iteratorFactory">Factory that creates an SDK iterator for a query and its physical options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="accountEndpoint"/>, <paramref name="databaseName"/>, <paramref name="containerName"/>, or
    /// <paramref name="iteratorFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="accountEndpoint"/> is not a supported absolute account endpoint, or
    /// <paramref name="databaseName"/> or <paramref name="containerName"/> is empty or white space.
    /// </exception>
    internal CosmosJsonQueryFeedReader(
        Uri accountEndpoint,
        string databaseName,
        string containerName,
        Func<QueryDefinition, QueryRequestOptions, FeedIterator<JsonElement>> iteratorFactory)
    {
        AccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(accountEndpoint);
        DatabaseName = Guard.RequireNotNullOrWhiteSpace(databaseName);
        ContainerName = Guard.RequireNotNullOrWhiteSpace(containerName);
        var legacyIteratorFactory = Guard.RequireNotNull(iteratorFactory);
        this.iteratorFactory = (feedRange, query, continuationToken, requestOptions) =>
        {
            if (feedRange is not null || continuationToken is not null)
            {
                throw new NotSupportedException(
                    "The legacy Cosmos query iterator factory supports only an unscoped initial read.");
            }

            return legacyIteratorFactory(query, requestOptions);
        };
    }

    /// <summary>Creates a JSON feed reader over a continuation- and feed-range-aware SDK iterator factory.</summary>
    /// <param name="accountEndpoint">Absolute Cosmos account endpoint attributed to every read.</param>
    /// <param name="databaseName">Database identity attributed to every read.</param>
    /// <param name="containerName">Container identity attributed to every read.</param>
    /// <param name="iteratorFactory">
    /// Factory that creates an SDK iterator for an optional feed range, query, optional continuation, and physical
    /// request options.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="accountEndpoint"/>, <paramref name="databaseName"/>, <paramref name="containerName"/>, or
    /// <paramref name="iteratorFactory"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="accountEndpoint"/> is not a supported absolute account endpoint, or
    /// <paramref name="databaseName"/> or <paramref name="containerName"/> is empty or white space.
    /// </exception>
    internal CosmosJsonQueryFeedReader(
        Uri accountEndpoint,
        string databaseName,
        string containerName,
        Func<FeedRange?, QueryDefinition, string?, QueryRequestOptions, FeedIterator<JsonElement>> iteratorFactory)
    {
        AccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(accountEndpoint);
        DatabaseName = Guard.RequireNotNullOrWhiteSpace(databaseName);
        ContainerName = Guard.RequireNotNullOrWhiteSpace(containerName);
        this.iteratorFactory = Guard.RequireNotNull(iteratorFactory);
    }

    /// <summary>Normalized physical Cosmos account endpoint attributed to this reader.</summary>
    internal Uri AccountEndpoint { get; }

    /// <summary>Physical Cosmos database identity attributed to this reader.</summary>
    internal string DatabaseName { get; }

    /// <summary>Physical Cosmos container identity attributed to this reader.</summary>
    internal string ContainerName { get; }

    /// <summary>Validates and freezes one bound Cosmos SDK query request before iterator creation.</summary>
    /// <param name="query">Bound Cosmos SDK query definition.</param>
    /// <param name="requestOptions">Physical SDK request options for the query.</param>
    /// <param name="requestSizeLimits">Pre-I/O SQL-text and complete-request size boundaries.</param>
    /// <returns>An immutable request that has passed configured size validation.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="query"/>, <paramref name="requestOptions"/>, or <paramref name="requestSizeLimits"/> is
    /// <see langword="null"/>.
    /// </exception>
    /// <exception cref="CosmosQueryRequestSizeLimitException">
    /// The bound query exceeds a configured size boundary or cannot be measured deterministically.
    /// </exception>
    internal PreparedRequest Prepare(
        QueryDefinition query,
        QueryRequestOptions requestOptions,
        CosmosQueryRequestSizeLimits requestSizeLimits)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(requestOptions);
        ArgumentNullException.ThrowIfNull(requestSizeLimits);
        CosmosQueryRequestSizeValidator.RequireWithin(query, requestSizeLimits);
        return new(query, requestOptions);
    }

    /// <summary>Reads exactly one complete Cosmos SDK JSON query response page.</summary>
    /// <param name="request">Previously validated bound Cosmos SDK request.</param>
    /// <param name="feedRange">Optional physical feed range supplied to the SDK iterator.</param>
    /// <param name="continuationToken">
    /// Opaque provider continuation from a previous page, or <see langword="null"/> for the initial page. Empty or
    /// white-space continuations are invalid rather than aliases for the initial position.
    /// </param>
    /// <param name="cancellationToken">Token observed before iterator creation and throughout page acquisition.</param>
    /// <returns>
    /// Every cloned row from one SDK response page together with provider progress, request charge, status, and an
    /// opaque evidence reference whose provider activity identifier is represented only by a hash.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="continuationToken"/> is empty or white space.</exception>
    /// <exception cref="CosmosProviderProtocolException">
    /// The iterator factory or provider returns an invalid iterator, response, status, charge, item, count, or
    /// continuation/progress combination.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// A feed range or continuation is supplied to a reader constructed with the legacy two-argument iterator
    /// factory.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    internal async ValueTask<CosmosJsonQueryFeedPageResult> ReadPageAsync(
        PreparedRequest request,
        FeedRange? feedRange,
        string? continuationToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireContinuationToken(continuationToken, nameof(continuationToken));
        cancellationToken.ThrowIfCancellationRequested();

        using var iterator = iteratorFactory(feedRange, request.Query, continuationToken, request.Options)
            ?? throw Protocol(
                "query-iterator-null",
                "The Cosmos query iterator factory returned null.");
        if (!ReadHasMoreResults(iterator, cancellationToken))
        {
            throw Protocol(
                "query-iterator-without-page",
                "The newly created Cosmos query iterator exposed no response page.");
        }

        var page = await ReadNextResponseAsync(iterator, cancellationToken).ConfigureAwait(false);
        HttpStatusCode? completedStatusCode = null;
        double? completedRequestCharge = null;
        string? completedActivityId = null;
        try
        {
            var statusCode = page.StatusCode;
            completedStatusCode = statusCode;
            var requestCharge = page.RequestCharge;
            RequireValidRequestCharge(
                requestCharge,
                "query-response-charge-invalid",
                statusCode,
                activityId: null);
            completedRequestCharge = requestCharge;
            completedActivityId = page.ActivityId;
            RequireSuccessfulQueryStatus(
                statusCode,
                requestCharge,
                completedActivityId,
                responseChargeAccounted: false);
            ThrowIfCanceledAfterResponse(
                statusCode,
                requestCharge,
                completedActivityId,
                cancellationToken,
                responseChargeAccounted: false);

            var nextContinuationToken = page.ContinuationToken;
            if (nextContinuationToken is not null && string.IsNullOrWhiteSpace(nextContinuationToken))
            {
                throw Protocol(
                    "query-continuation-invalid",
                    "The Cosmos query provider returned an empty or white-space continuation token.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            var hasMoreResults = iterator.HasMoreResults;
            if (hasMoreResults && nextContinuationToken is null)
            {
                throw Protocol(
                    "query-continuation-missing",
                    "The Cosmos query provider reported more results without a durable continuation.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            if (!hasMoreResults && nextContinuationToken is not null)
            {
                throw Protocol(
                    "query-progress-inconsistent",
                    "The Cosmos query provider returned inconsistent iterator and continuation progress.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }

            var providerCount = page.Count;
            if (providerCount < 0)
            {
                throw Protocol(
                    "query-response-count-invalid",
                    "The Cosmos query provider returned a negative response count.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            var resource = page.Resource
                ?? throw Protocol(
                    "query-response-resource-null",
                    "The Cosmos query provider returned a null response resource.",
                    statusCode,
                    requestCharge,
                    completedActivityId);

            var rows = ImmutableArray.CreateBuilder<JsonElement>(providerCount);
            foreach (var row in resource)
            {
                ThrowIfCanceledAfterResponse(
                    statusCode,
                    requestCharge,
                    completedActivityId,
                    cancellationToken,
                    responseChargeAccounted: false);
                if (row.ValueKind == JsonValueKind.Undefined)
                {
                    throw Protocol(
                        "query-response-item-invalid",
                        "The Cosmos query provider returned an undefined JSON item.",
                        statusCode,
                        requestCharge,
                        completedActivityId);
                }
                rows.Add(row.Clone());
            }
            if (rows.Count != providerCount)
            {
                throw Protocol(
                    "query-response-count-mismatch",
                    "The Cosmos query provider response count did not match its resource.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }

            return new(
                rows.Count == rows.Capacity ? rows.MoveToImmutable() : rows.ToImmutable(),
                nextContinuationToken,
                hasMoreResults,
                requestCharge,
                statusCode,
                CreatePageEvidenceReference(
                    completedActivityId,
                    request.Options.ConsistencyLevel));
        }
        catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
            exception,
            cancellationToken))
        {
            throw Protocol(
                "query-response-projection-failed",
                "The Cosmos query provider response could not be projected safely.",
                completedStatusCode,
                completedRequestCharge,
                completedActivityId);
        }
    }

    /// <summary>Reads cloned JSON rows from one validated request until exhaustion or a row boundary.</summary>
    /// <param name="request">Previously validated bound Cosmos SDK request.</param>
    /// <param name="maximumRows">Positive maximum number of JSON rows retained in memory.</param>
    /// <param name="cancellationToken">Token observed before iterator creation and throughout page enumeration.</param>
    /// <param name="completedPageObserver">
    /// Optional synchronous observer invoked once for every completed SDK response with its request charge and
    /// status, including responses completed before a later response fails or cancellation is observed.
    /// </param>
    /// <returns>
    /// Cloned JSON rows, the termination reason, and an opaque digest of physical affinity, command shape, provider
    /// activity correlation, row counts, and termination. Parameter and result values are not incorporated.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maximumRows"/> is not positive or exceeds the maximum runtime array length.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> is canceled.</exception>
    internal async ValueTask<CosmosJsonQueryFeedReadResult> ReadAllAsync(
        PreparedRequest request,
        long maximumRows,
        CancellationToken cancellationToken,
        Action<double, HttpStatusCode>? completedPageObserver = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (maximumRows <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                "A Cosmos JSON feed row boundary must be positive.");
        }
        if (maximumRows > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRows),
                maximumRows,
                $"A Cosmos JSON feed cannot materialize more than {Array.MaxLength.ToString(CultureInfo.InvariantCulture)} rows.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var evidence = CreateEvidenceDigest(request.Query, request.Options);
        using var iterator = iteratorFactory(
            null,
            request.Query,
            null,
            request.Options)
            ?? throw Protocol(
                "query-iterator-null",
                "The Cosmos query iterator factory returned null.");

        var initialCapacity = (int)Math.Min(maximumRows, 256L);
        ImmutableArray<JsonElement>.Builder rows = ImmutableArray.CreateBuilder<JsonElement>(initialCapacity);
        var boundaryStopped = false;
        double requestCharge = 0;
        HttpStatusCode? statusCode = null;

        while (ReadHasMoreResults(iterator, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await ReadNextResponseAsync(iterator, cancellationToken).ConfigureAwait(false);
            HttpStatusCode? statusEvidence = null;
            double? requestChargeEvidence = null;
            string? activityId = null;
            try
            {
                statusEvidence = page.StatusCode;
                var completedRequestCharge = page.RequestCharge;
                RequireValidRequestCharge(
                    completedRequestCharge,
                    "query-response-charge-invalid",
                    statusEvidence.Value,
                    activityId: null);
                requestChargeEvidence = completedRequestCharge;
                activityId = page.ActivityId;
            }
            catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
                exception,
                cancellationToken))
            {
                throw Protocol(
                    "query-response-evidence-invalid",
                    "The Cosmos query provider response evidence could not be read safely.",
                    statusEvidence,
                    requestChargeEvidence,
                    activityId);
            }

            var completedStatusCode = statusEvidence.Value;
            var completedRequestChargeValue = requestChargeEvidence.Value;
            var aggregateRequestCharge = requestCharge + completedRequestChargeValue;
            if (!double.IsFinite(aggregateRequestCharge) || aggregateRequestCharge < 0)
            {
                throw Protocol(
                    "query-response-aggregate-charge-invalid",
                    "The Cosmos query provider returned charges whose aggregate is invalid.",
                    completedStatusCode,
                    completedRequestChargeValue,
                    activityId);
            }
            var responseChargeAccounted = false;
            if (completedPageObserver is not null)
            {
                completedPageObserver(completedRequestChargeValue, completedStatusCode);
                responseChargeAccounted = true;
            }
            RequireSuccessfulQueryStatus(
                completedStatusCode,
                completedRequestChargeValue,
                activityId,
                responseChargeAccounted);
            ThrowIfCanceledAfterResponse(
                completedStatusCode,
                completedRequestChargeValue,
                activityId,
                cancellationToken,
                responseChargeAccounted);
            requestCharge = aggregateRequestCharge;
            statusCode = completedStatusCode;
            try
            {
                var providerCount = page.Count;
                if (providerCount < 0)
                {
                    throw Protocol(
                        "query-response-count-invalid",
                        "The Cosmos query provider returned a negative response count.",
                        completedStatusCode,
                        completedRequestChargeValue,
                        activityId,
                        responseChargeAccounted);
                }
                var resource = page.Resource
                    ?? throw Protocol(
                        "query-response-resource-null",
                        "The Cosmos query provider returned a null response resource.",
                        completedStatusCode,
                        completedRequestChargeValue,
                        activityId,
                        responseChargeAccounted);
                Append(evidence, activityId);
                Append(evidence, providerCount.ToString(CultureInfo.InvariantCulture));
                var enumeratedCount = 0;
                foreach (var row in resource)
                {
                    ThrowIfCanceledAfterResponse(
                        completedStatusCode,
                        completedRequestChargeValue,
                        activityId,
                        cancellationToken,
                        responseChargeAccounted);
                    if (row.ValueKind == JsonValueKind.Undefined)
                    {
                        throw Protocol(
                            "query-response-item-invalid",
                            "The Cosmos query provider returned an undefined JSON item.",
                            completedStatusCode,
                            completedRequestChargeValue,
                            activityId,
                            responseChargeAccounted);
                    }
                    enumeratedCount++;
                    if (rows.Count >= maximumRows)
                    {
                        boundaryStopped = true;
                        continue;
                    }

                    rows.Add(row.Clone());
                }
                if (enumeratedCount != providerCount)
                {
                    throw Protocol(
                        "query-response-count-mismatch",
                        "The Cosmos query provider response count did not match its resource.",
                        completedStatusCode,
                        completedRequestChargeValue,
                        activityId,
                        responseChargeAccounted);
                }
            }
            catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
                exception,
                cancellationToken))
            {
                throw Protocol(
                    "query-response-projection-failed",
                    "The Cosmos query provider response could not be projected safely.",
                    completedStatusCode,
                    completedRequestChargeValue,
                    activityId,
                    responseChargeAccounted);
            }

            if (boundaryStopped)
                break;
        }

        var exhausted = !boundaryStopped && !ReadHasMoreResults(iterator, cancellationToken);
        if (!exhausted && !boundaryStopped)
            boundaryStopped = true;

        var materializedRows = rows.Count == rows.Capacity
            ? rows.MoveToImmutable()
            : rows.ToImmutable();
        return new(
            materializedRows,
            exhausted,
            boundaryStopped,
            requestCharge,
            statusCode,
            CompleteEvidenceReference(evidence, materializedRows.Length, exhausted));
    }

    /// <summary>One bound Cosmos SDK request proven to satisfy configured pre-I/O size boundaries.</summary>
    internal sealed class PreparedRequest
    {
        internal PreparedRequest(QueryDefinition query, QueryRequestOptions options)
        {
            Query = query;
            Options = options;
        }

        /// <summary>Bound SDK query definition.</summary>
        internal QueryDefinition Query { get; }

        /// <summary>Physical SDK request options.</summary>
        internal QueryRequestOptions Options { get; }
    }

    IncrementalHash CreateEvidenceDigest(
        QueryDefinition query,
        QueryRequestOptions requestOptions)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, EvidenceProfile);
        Append(hash, AccountEndpoint.AbsoluteUri);
        Append(hash, DatabaseName);
        Append(hash, ContainerName);
        Append(hash, query.QueryText);
        var parameters = query.GetQueryParameters();
        Append(hash, parameters.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var parameter in parameters.OrderBy(static parameter => parameter.Name, StringComparer.Ordinal))
            Append(hash, parameter.Name);
        Append(hash, requestOptions.PartitionKey is null ? "cross-partition" : "fixed-partition");
        Append(hash, requestOptions.MaxItemCount?.ToString(CultureInfo.InvariantCulture));
        Append(hash, requestOptions.MaxBufferedItemCount?.ToString(CultureInfo.InvariantCulture));
        Append(hash, requestOptions.MaxConcurrency?.ToString(CultureInfo.InvariantCulture));
        Append(hash, requestOptions.ConsistencyLevel is { } consistency
            ? ((int)consistency).ToString(CultureInfo.InvariantCulture)
            : null);
        return hash;
    }

    static string CompleteEvidenceReference(IncrementalHash hash, int rowCount, bool exhausted)
    {
        Append(hash, rowCount.ToString(CultureInfo.InvariantCulture));
        Append(hash, exhausted ? "exhausted" : "boundary-stopped");
        return $"{EvidenceProfile}/sha256/{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static string CreatePageEvidenceReference(
        string? activityId,
        ConsistencyLevel? consistencyLevel)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, PageEvidenceProfile);
        Append(hash, consistencyLevel is { } consistency
            ? ((int)consistency).ToString(CultureInfo.InvariantCulture)
            : null);
        Append(hash, string.IsNullOrWhiteSpace(activityId)
            ? null
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(activityId))));
        return $"{PageEvidenceProfile}/sha256/{Convert.ToHexStringLower(hash.GetHashAndReset())}";
    }

    static bool ReadHasMoreResults(
        FeedIterator<JsonElement> iterator,
        CancellationToken cancellationToken)
    {
        try
        {
            return iterator.HasMoreResults;
        }
        catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
            exception,
            cancellationToken))
        {
            throw Protocol(
                "query-iterator-progress-read-failed",
                "The Cosmos query iterator progress could not be read safely.");
        }
    }

    static async ValueTask<FeedResponse<JsonElement>> ReadNextResponseAsync(
        FeedIterator<JsonElement> iterator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw Protocol(
                    "query-response-null",
                    "The Cosmos query provider returned a null response page.");
        }
        catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
            exception,
            cancellationToken))
        {
            throw Protocol(
                "query-response-read-failed",
                "The Cosmos query provider failed before returning a response page.");
        }
    }

    static void RequireValidRequestCharge(
        double requestCharge,
        string reason,
        HttpStatusCode statusCode,
        string? activityId)
    {
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw Protocol(
                reason,
                "The Cosmos query provider returned a non-finite or negative request charge.",
                statusCode,
                requestCharge: null,
                activityId);
        }
    }

    static void RequireSuccessfulQueryStatus(
        HttpStatusCode statusCode,
        double requestCharge,
        string? activityId,
        bool responseChargeAccounted)
    {
        if (statusCode != HttpStatusCode.OK)
        {
            throw Protocol(
                "query-response-status-invalid",
                "The Cosmos query provider returned a status other than OK as a query page.",
                statusCode,
                requestCharge,
                activityId,
                responseChargeAccounted);
        }
    }

    static void ThrowIfCanceledAfterResponse(
        HttpStatusCode statusCode,
        double requestCharge,
        string? activityId,
        CancellationToken cancellationToken,
        bool responseChargeAccounted)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new CosmosProviderResponseCanceledException(
                statusCode,
                requestCharge,
                activityId,
                cancellationToken,
                responseChargeAccounted);
        }
    }

    static CosmosProviderProtocolException Protocol(
        string reason,
        string message,
        HttpStatusCode? statusCode = null,
        double? requestCharge = null,
        string? activityId = null,
        bool responseChargeAccounted = false) => new(
            reason,
            message,
            statusCode,
            requestCharge,
            activityId,
            responseChargeAccounted);

    static void RequireContinuationToken(string? continuationToken, string parameterName)
    {
        if (continuationToken is not null && string.IsNullOrWhiteSpace(continuationToken))
        {
            throw new ArgumentException(
                "A Cosmos query continuation token must be null or contain a non-white-space provider value.",
                parameterName);
        }
    }

    static void Append(IncrementalHash hash, string? value)
    {
        var framed = string.Concat(
            value?.Length.ToString(CultureInfo.InvariantCulture) ?? "-1",
            ":",
            value,
            ";");
        hash.AppendData(Encoding.UTF8.GetBytes(framed));
    }
}

/// <summary>Immutable physical result of one complete Cosmos SDK JSON query response page.</summary>
internal sealed record CosmosJsonQueryFeedPageResult
{
    /// <summary>Creates one complete physical query-page result.</summary>
    /// <param name="rows">Every cloned JSON row from the SDK response, in provider order.</param>
    /// <param name="nextContinuationToken">Opaque provider continuation for a subsequent page, or <see langword="null"/>.</param>
    /// <param name="hasMoreResults">Whether the SDK iterator reported another response page after this page.</param>
    /// <param name="requestCharge">Request units charged for this SDK response.</param>
    /// <param name="statusCode">HTTP status reported for this SDK response.</param>
    /// <param name="providerEvidenceReference">
    /// Opaque non-sensitive reference derived from the provider activity identifier, or <see langword="null"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> is default, <paramref name="nextContinuationToken"/> or
    /// <paramref name="providerEvidenceReference"/> is empty or white space, or <paramref name="requestCharge"/> is
    /// negative or non-finite.
    /// </exception>
    internal CosmosJsonQueryFeedPageResult(
        ImmutableArray<JsonElement> rows,
        string? nextContinuationToken,
        bool hasMoreResults,
        double requestCharge,
        HttpStatusCode statusCode,
        string? providerEvidenceReference)
    {
        if (rows.IsDefault)
            throw new ArgumentException("A Cosmos JSON query page requires a materialized row collection.", nameof(rows));
        if (nextContinuationToken is not null && string.IsNullOrWhiteSpace(nextContinuationToken))
        {
            throw new ArgumentException(
                "A Cosmos JSON query page continuation cannot be empty or white space.",
                nameof(nextContinuationToken));
        }
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw new ArgumentException(
                "A Cosmos JSON query page request charge must be finite and non-negative.",
                nameof(requestCharge));
        }
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
        {
            throw new ArgumentException(
                "A Cosmos JSON query page evidence reference cannot be empty or white space.",
                nameof(providerEvidenceReference));
        }

        Rows = rows;
        NextContinuationToken = nextContinuationToken;
        HasMoreResults = hasMoreResults;
        RequestCharge = requestCharge;
        StatusCode = statusCode;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    /// <summary>Every cloned JSON row from the SDK response, in provider order.</summary>
    internal ImmutableArray<JsonElement> Rows { get; }

    /// <summary>Opaque provider continuation for a subsequent page, or <see langword="null"/>.</summary>
    internal string? NextContinuationToken { get; }

    /// <summary>Whether the SDK iterator reported another response page after this page.</summary>
    internal bool HasMoreResults { get; }

    /// <summary>Request units charged for this SDK response.</summary>
    internal double RequestCharge { get; }

    /// <summary>HTTP status reported for this SDK response.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>Opaque non-sensitive provider evidence reference, when an activity identifier was available.</summary>
    internal string? ProviderEvidenceReference { get; }
}

/// <summary>Immutable physical result of one bounded Cosmos SDK JSON feed read.</summary>
internal sealed record CosmosJsonQueryFeedReadResult
{
    /// <summary>Creates a bounded physical feed result.</summary>
    /// <param name="rows">Cloned JSON rows in provider order.</param>
    /// <param name="exhausted">Whether the provider reported that the feed was exhausted.</param>
    /// <param name="boundaryStopped">Whether reading stopped at the caller's row boundary.</param>
    /// <param name="requestCharge">Request units aggregated across completed SDK responses.</param>
    /// <param name="statusCode">HTTP status from the final completed SDK response, when one was read.</param>
    /// <param name="providerEvidenceReference">Opaque non-sensitive feed evidence reference, when available.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> is default, or the termination facts are equal rather than mutually exclusive.
    /// </exception>
    internal CosmosJsonQueryFeedReadResult(
        ImmutableArray<JsonElement> rows,
        bool exhausted,
        bool boundaryStopped,
        double requestCharge,
        HttpStatusCode? statusCode,
        string? providerEvidenceReference)
    {
        if (rows.IsDefault)
            throw new ArgumentException("A Cosmos JSON feed result requires a materialized row collection.", nameof(rows));
        if (exhausted == boundaryStopped)
        {
            throw new ArgumentException(
                "A Cosmos JSON feed result must be either exhausted or boundary-stopped.",
                nameof(exhausted));
        }
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw new ArgumentException(
                "A Cosmos JSON feed aggregate request charge must be finite and non-negative.",
                nameof(requestCharge));
        }
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
        {
            throw new ArgumentException(
                "A Cosmos JSON feed evidence reference cannot be empty.",
                nameof(providerEvidenceReference));
        }

        Rows = rows;
        Exhausted = exhausted;
        BoundaryStopped = boundaryStopped;
        RequestCharge = requestCharge;
        StatusCode = statusCode;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    /// <summary>Cloned JSON rows in provider order.</summary>
    internal ImmutableArray<JsonElement> Rows { get; }

    /// <summary>Whether the provider reported that the feed was exhausted.</summary>
    internal bool Exhausted { get; }

    /// <summary>Whether reading stopped at the caller's row boundary.</summary>
    internal bool BoundaryStopped { get; }

    /// <summary>Request units aggregated across completed SDK responses.</summary>
    internal double RequestCharge { get; }

    /// <summary>HTTP status from the final completed SDK response, when one was read.</summary>
    internal HttpStatusCode? StatusCode { get; }

    /// <summary>Opaque deterministic non-sensitive feed evidence reference, when available.</summary>
    internal string? ProviderEvidenceReference { get; }
}
