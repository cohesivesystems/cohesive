using System.Collections.Immutable;
using System.Globalization;
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

    readonly Func<QueryDefinition, QueryRequestOptions, FeedIterator<JsonElement>> iteratorFactory;

    /// <summary>Creates a JSON feed reader for one Cosmos container.</summary>
    /// <param name="container">Container that creates each SDK query iterator.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is <see langword="null"/>.</exception>
    internal CosmosJsonQueryFeedReader(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        AccountEndpoint = CosmosPhysicalAffinity.NormalizeAccountEndpoint(container.Database.Client.Endpoint);
        DatabaseName = Guard.RequireNotNullOrWhiteSpace(container.Database.Id);
        ContainerName = Guard.RequireNotNullOrWhiteSpace(container.Id);
        iteratorFactory = (query, requestOptions) => container.GetItemQueryIterator<JsonElement>(
            query,
            continuationToken: null,
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

    /// <summary>Reads cloned JSON rows from one validated request until exhaustion or a row boundary.</summary>
    /// <param name="request">Previously validated bound Cosmos SDK request.</param>
    /// <param name="maximumRows">Positive maximum number of JSON rows retained in memory.</param>
    /// <param name="cancellationToken">Token observed before iterator creation and throughout page enumeration.</param>
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
        CancellationToken cancellationToken)
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
        using var iterator = iteratorFactory(request.Query, request.Options)
            ?? throw new InvalidOperationException("The Cosmos query iterator factory returned null.");

        var initialCapacity = (int)Math.Min(maximumRows, 256L);
        ImmutableArray<JsonElement>.Builder rows = ImmutableArray.CreateBuilder<JsonElement>(initialCapacity);
        var boundaryStopped = false;

        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Append(evidence, page.ActivityId);
            Append(evidence, page.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var row in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (rows.Count >= maximumRows)
                {
                    boundaryStopped = true;
                    break;
                }

                rows.Add(row.Clone());
            }

            if (boundaryStopped)
                break;
        }

        var exhausted = !boundaryStopped && !iterator.HasMoreResults;
        if (!exhausted && !boundaryStopped)
            boundaryStopped = true;

        var materializedRows = rows.ToImmutable();
        return new(
            materializedRows,
            exhausted,
            boundaryStopped,
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
        return hash;
    }

    static string CompleteEvidenceReference(IncrementalHash hash, int rowCount, bool exhausted)
    {
        Append(hash, rowCount.ToString(CultureInfo.InvariantCulture));
        Append(hash, exhausted ? "exhausted" : "boundary-stopped");
        return $"{EvidenceProfile}/sha256/{Convert.ToHexStringLower(hash.GetHashAndReset())}";
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

/// <summary>Immutable physical result of one bounded Cosmos SDK JSON feed read.</summary>
internal sealed record CosmosJsonQueryFeedReadResult
{
    /// <summary>Creates a bounded physical feed result.</summary>
    /// <param name="rows">Cloned JSON rows in provider order.</param>
    /// <param name="exhausted">Whether the provider reported that the feed was exhausted.</param>
    /// <param name="boundaryStopped">Whether reading stopped at the caller's row boundary.</param>
    /// <param name="providerEvidenceReference">Opaque non-sensitive feed evidence reference, when available.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="rows"/> is default, or the termination facts are equal rather than mutually exclusive.
    /// </exception>
    internal CosmosJsonQueryFeedReadResult(
        ImmutableArray<JsonElement> rows,
        bool exhausted,
        bool boundaryStopped,
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
        if (providerEvidenceReference is not null && string.IsNullOrWhiteSpace(providerEvidenceReference))
        {
            throw new ArgumentException(
                "A Cosmos JSON feed evidence reference cannot be empty.",
                nameof(providerEvidenceReference));
        }

        Rows = rows;
        Exhausted = exhausted;
        BoundaryStopped = boundaryStopped;
        ProviderEvidenceReference = providerEvidenceReference;
    }

    /// <summary>Cloned JSON rows in provider order.</summary>
    internal ImmutableArray<JsonElement> Rows { get; }

    /// <summary>Whether the provider reported that the feed was exhausted.</summary>
    internal bool Exhausted { get; }

    /// <summary>Whether reading stopped at the caller's row boundary.</summary>
    internal bool BoundaryStopped { get; }

    /// <summary>Opaque deterministic non-sensitive feed evidence reference, when available.</summary>
    internal string? ProviderEvidenceReference { get; }
}
