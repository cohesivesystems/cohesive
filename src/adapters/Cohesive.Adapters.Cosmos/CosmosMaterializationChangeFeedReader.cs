using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Cohesive.Model;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Adapters.Cosmos;

/// <summary>Kind of provider boundary used to create one pull change-feed iterator.</summary>
internal enum CosmosMaterializationChangeFeedStartKind
{
    /// <summary>Starts at a caller-captured current boundary.</summary>
    Now = 0,

    /// <summary>Resumes from an opaque Cosmos continuation.</summary>
    Continuation = 1
}

/// <summary>One validated pull change-feed start boundary.</summary>
internal readonly record struct CosmosMaterializationChangeFeedStart
{
    /// <summary>Creates a pull boundary.</summary>
    /// <param name="kind">Now or continuation.</param>
    /// <param name="continuationToken">Required only for a continuation boundary.</param>
    /// <exception cref="ArgumentException">Token presence conflicts with <paramref name="kind"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is unsupported.</exception>
    internal CosmosMaterializationChangeFeedStart(
        CosmosMaterializationChangeFeedStartKind kind,
        string? continuationToken = null)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported change-feed start kind.");
        continuationToken = continuationToken.TrimmedEmptyOrWhiteSpaceAs();
        if ((kind == CosmosMaterializationChangeFeedStartKind.Continuation) != (continuationToken is not null))
            throw new ArgumentException("Only a continuation start carries a Cosmos continuation token.", nameof(continuationToken));
        Kind = kind;
        ContinuationToken = continuationToken;
    }

    /// <summary>Now or continuation.</summary>
    internal CosmosMaterializationChangeFeedStartKind Kind { get; }

    /// <summary>Opaque Cosmos continuation for a continuation start.</summary>
    internal string? ContinuationToken { get; }
}

/// <summary>Provider-neutral immutable projection of one full-fidelity Cosmos change.</summary>
internal sealed record CosmosMaterializationProviderChange(
    CosmosObservationContainerDocument? Current,
    CosmosObservationContainerDocument? Previous,
    long Lsn,
    long PreviousLsn,
    CosmosMaterializationProviderChangeKind OperationType,
    DateTime ConflictResolutionTimestamp,
    bool IsTimeToLiveExpired,
    string? DeletedItemId);

/// <summary>Full-fidelity provider operation kind projected without leaking an SDK type.</summary>
internal enum CosmosMaterializationProviderChangeKind
{
    /// <summary>An item was created.</summary>
    Create = 0,

    /// <summary>An item was replaced.</summary>
    Replace = 1,

    /// <summary>An item was deleted.</summary>
    Delete = 2
}

/// <summary>One complete, untruncated Cosmos SDK change-feed response.</summary>
internal sealed record CosmosMaterializationProviderChangePage
{
    /// <summary>Creates a provider response projection.</summary>
    /// <param name="changes">Every item in the complete SDK response page.</param>
    /// <param name="continuationToken">Opaque boundary after the complete SDK response.</param>
    /// <param name="statusCode">Successful provider status, including not-modified.</param>
    /// <param name="requestCharge">Non-negative request-unit charge.</param>
    /// <param name="providerEvidenceReference">Non-sensitive provider evidence digest.</param>
    /// <exception cref="ArgumentException">A collection, token, charge, or evidence value is invalid.</exception>
    internal CosmosMaterializationProviderChangePage(
        ImmutableArray<CosmosMaterializationProviderChange> changes,
        string continuationToken,
        HttpStatusCode statusCode,
        double requestCharge,
        string providerEvidenceReference)
    {
        if (changes.IsDefault)
            throw new ArgumentException("A provider change page requires a materialized change collection.", nameof(changes));
        for (var index = 0; index < changes.Length; index++)
        {
            if (changes[index] is null)
            {
                throw new ArgumentException(
                    "A provider change page cannot contain a null change.",
                    nameof(changes));
            }
        }
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
            throw new ArgumentOutOfRangeException(nameof(requestCharge), requestCharge, "A provider request charge must be finite and non-negative.");
        Changes = changes;
        ContinuationToken = Guard.RequireNotNullOrWhiteSpace(continuationToken);
        StatusCode = statusCode;
        RequestCharge = requestCharge;
        ProviderEvidenceReference = Guard.RequireNotNullOrWhiteSpace(providerEvidenceReference);
    }

    /// <summary>Every item in the complete SDK response page.</summary>
    internal ImmutableArray<CosmosMaterializationProviderChange> Changes { get; }

    /// <summary>Opaque provider boundary after the complete SDK response.</summary>
    internal string ContinuationToken { get; }

    /// <summary>Successful provider status, including not-modified.</summary>
    internal HttpStatusCode StatusCode { get; }

    /// <summary>Request-unit charge.</summary>
    internal double RequestCharge { get; }

    /// <summary>Non-sensitive provider evidence digest.</summary>
    internal string ProviderEvidenceReference { get; }
}

/// <summary>Narrow testable port for one-page full-fidelity pull consumption.</summary>
internal interface ICosmosMaterializationChangeFeedReader
{
    /// <summary>Reads one complete SDK response without truncating a transactional provider page.</summary>
    /// <param name="start">Current or continuation boundary.</param>
    /// <param name="feedRange">Optional fixed logical-partition range for a current boundary.</param>
    /// <param name="pageSizeHint">Positive provider page-size hint.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    /// <returns>One complete provider response and its continuation.</returns>
    ValueTask<CosmosMaterializationProviderChangePage> ReadPageAsync(
        CosmosMaterializationChangeFeedStart start,
        FeedRange? feedRange,
        int pageSizeHint,
        CancellationToken cancellationToken);
}

/// <summary>Cosmos SDK implementation of one-page full-fidelity pull consumption.</summary>
internal sealed class CosmosMaterializationChangeFeedReader : ICosmosMaterializationChangeFeedReader
{
    const string EvidenceProfile = "cosmos-materialization-change-feed/v1";
    readonly Func<
        ChangeFeedStartFrom,
        ChangeFeedMode,
        ChangeFeedRequestOptions,
        FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>>> iteratorFactory;
    readonly string affinityEvidence;

    /// <summary>Creates a full-fidelity reader for one Cosmos container.</summary>
    /// <param name="container">Borrowed SDK container.</param>
    /// <exception cref="ArgumentNullException"><paramref name="container"/> is <see langword="null"/>.</exception>
    internal CosmosMaterializationChangeFeedReader(Container container)
    {
        ArgumentNullException.ThrowIfNull(container);
        var account = CosmosPhysicalAffinity.CanonicalAccountEndpointText(container.Database.Client.Endpoint);
        affinityEvidence = string.Concat(
            "account/", CosmosPhysicalAffinity.Fingerprint(account),
            "/database/", Uri.EscapeDataString(container.Database.Id),
            "/container/", Uri.EscapeDataString(container.Id));
        iteratorFactory = (start, mode, options) => container.GetChangeFeedIterator<
            ChangeFeedItem<CosmosObservationContainerDocument>>(
            start,
            mode,
            options);
    }

    /// <summary>Creates a full-fidelity reader over an explicit iterator factory.</summary>
    /// <param name="affinityEvidence">Stable non-sensitive physical affinity evidence.</param>
    /// <param name="iteratorFactory">Factory for full-fidelity SDK iterators.</param>
    /// <exception cref="ArgumentNullException"><paramref name="iteratorFactory"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="affinityEvidence"/> is empty or white space.</exception>
    internal CosmosMaterializationChangeFeedReader(
        string affinityEvidence,
        Func<
            ChangeFeedStartFrom,
            ChangeFeedMode,
            ChangeFeedRequestOptions,
            FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>>> iteratorFactory)
    {
        this.affinityEvidence = Guard.RequireNotNullOrWhiteSpace(affinityEvidence);
        this.iteratorFactory = Guard.RequireNotNull(iteratorFactory);
    }

    /// <inheritdoc />
    public async ValueTask<CosmosMaterializationProviderChangePage> ReadPageAsync(
        CosmosMaterializationChangeFeedStart start,
        FeedRange? feedRange,
        int pageSizeHint,
        CancellationToken cancellationToken)
    {
        if (pageSizeHint <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSizeHint), pageSizeHint, "A change-feed page hint must be positive.");
        if (start.Kind == CosmosMaterializationChangeFeedStartKind.Continuation && feedRange is not null)
        {
            throw new ArgumentException(
                "A Cosmos continuation already identifies its feed range and cannot be combined with another range.",
                nameof(feedRange));
        }
        cancellationToken.ThrowIfCancellationRequested();
        ChangeFeedStartFrom providerStart = start.Kind switch
        {
            CosmosMaterializationChangeFeedStartKind.Now when feedRange is null => ChangeFeedStartFrom.Now(),
            CosmosMaterializationChangeFeedStartKind.Now => ChangeFeedStartFrom.Now(feedRange),
            CosmosMaterializationChangeFeedStartKind.Continuation =>
                ChangeFeedStartFrom.ContinuationToken(start.ContinuationToken!),
            _ => throw new ArgumentOutOfRangeException(nameof(start), start.Kind, "Unsupported change-feed start kind.")
        };
        ChangeFeedRequestOptions options = new() { PageSizeHint = pageSizeHint };
        using var iterator = iteratorFactory(providerStart, ChangeFeedMode.AllVersionsAndDeletes, options)
            ?? throw Protocol(
                "change-feed-iterator-null",
                "The Cosmos change-feed iterator factory returned null.");
        if (!ReadHasMoreResults(iterator, cancellationToken))
        {
            throw Protocol(
                "change-feed-iterator-without-page",
                "The newly created Cosmos change-feed iterator exposed no response page.");
        }

        var response = await ReadNextResponseAsync(iterator, cancellationToken).ConfigureAwait(false);
        HttpStatusCode? completedStatusCode = null;
        double? completedRequestCharge = null;
        string? completedActivityId = null;
        try
        {
            var statusCode = response.StatusCode;
            completedStatusCode = statusCode;
            var requestCharge = response.RequestCharge;
            RequireValidRequestCharge(requestCharge, statusCode, activityId: null);
            completedRequestCharge = requestCharge;
            completedActivityId = response.ActivityId;
            RequireSupportedStatus(statusCode, requestCharge, completedActivityId);
            ThrowIfCanceledAfterResponse(statusCode, requestCharge, completedActivityId, cancellationToken);

            var providerCount = response.Count;
            if (providerCount < 0)
            {
                throw Protocol(
                    "change-feed-response-count-invalid",
                    "The Cosmos change-feed provider returned a negative response count.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            if (statusCode == HttpStatusCode.NotModified && providerCount != 0)
            {
                throw Protocol(
                    "change-feed-not-modified-with-changes",
                    "A not-modified Cosmos change-feed response reported changes.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }

            var continuation = response.ContinuationToken.TrimmedEmptyOrWhiteSpaceAs()
                ?? throw Protocol(
                    "change-feed-continuation-missing",
                    "A Cosmos change-feed response omitted its durable continuation.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            if (!iterator.HasMoreResults)
            {
                throw Protocol(
                    "change-feed-progress-inconsistent",
                    "The Cosmos change-feed iterator became exhausted despite returning a durable continuation.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            var resource = response.Resource
                ?? throw Protocol(
                    "change-feed-response-resource-null",
                    "The Cosmos change-feed provider returned a null response resource.",
                    statusCode,
                    requestCharge,
                    completedActivityId);

            var changes = ImmutableArray.CreateBuilder<CosmosMaterializationProviderChange>(providerCount);
            foreach (var item in resource)
            {
                ThrowIfCanceledAfterResponse(statusCode, requestCharge, completedActivityId, cancellationToken);
                if (item is null)
                {
                    throw Protocol(
                        "change-feed-item-null",
                        "A full-fidelity Cosmos response contained a null change item.",
                        statusCode,
                        requestCharge,
                        completedActivityId);
                }
                if (item.Metadata is null)
                {
                    throw Protocol(
                        "change-feed-metadata-missing",
                        "A full-fidelity Cosmos change omitted required metadata.",
                        statusCode,
                        requestCharge,
                        completedActivityId);
                }
                changes.Add(new(
                    Clone(item.Current),
                    Clone(item.Previous),
                    item.Metadata.Lsn,
                    item.Metadata.PreviousLsn,
                    ProjectOperation(
                        item.Metadata.OperationType,
                        statusCode,
                        requestCharge,
                        completedActivityId),
                    item.Metadata.ConflictResolutionTimestamp,
                    item.Metadata.IsTimeToLiveExpired,
                    item.Metadata.Id));
            }
            if (changes.Count != providerCount)
            {
                throw Protocol(
                    "change-feed-response-count-mismatch",
                    "The Cosmos change-feed response count did not match its resource.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            if (statusCode == HttpStatusCode.NotModified && changes.Count != 0)
            {
                throw Protocol(
                    "change-feed-not-modified-with-changes",
                    "A not-modified Cosmos change-feed response contained changes.",
                    statusCode,
                    requestCharge,
                    completedActivityId);
            }
            var immutable = changes.Count == changes.Capacity
                ? changes.MoveToImmutable()
                : changes.ToImmutable();
            ThrowIfCanceledAfterResponse(statusCode, requestCharge, completedActivityId, cancellationToken);
            return new(
                immutable,
                continuation,
                statusCode,
                requestCharge,
                Evidence(completedActivityId, statusCode, requestCharge, immutable.Length));
        }
        catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
            exception,
            cancellationToken))
        {
            throw Protocol(
                "change-feed-response-projection-failed",
                "The Cosmos change-feed provider response could not be projected safely.",
                completedStatusCode,
                completedRequestCharge,
                completedActivityId);
        }
    }

    static CosmosObservationContainerDocument? Clone(CosmosObservationContainerDocument? document)
    {
        if (document is null)
            return null;
        return document with
        {
            Observation = document.Observation is null
                ? null
                : new Dictionary<string, ObservationValue>(document.Observation, StringComparer.Ordinal)
        };
    }

    static CosmosMaterializationProviderChangeKind ProjectOperation(
        ChangeFeedOperationType operation,
        HttpStatusCode statusCode,
        double requestCharge,
        string? activityId) => operation switch
        {
            ChangeFeedOperationType.Create => CosmosMaterializationProviderChangeKind.Create,
            ChangeFeedOperationType.Replace => CosmosMaterializationProviderChangeKind.Replace,
            ChangeFeedOperationType.Delete => CosmosMaterializationProviderChangeKind.Delete,
            _ => throw Protocol(
                "change-feed-operation-unsupported",
                "A full-fidelity Cosmos change used an unsupported operation type.",
                statusCode,
                requestCharge,
                activityId)
        };

    static bool ReadHasMoreResults(
        FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>> iterator,
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
                "change-feed-iterator-progress-read-failed",
                "The Cosmos change-feed iterator progress could not be read safely.");
        }
    }

    static async ValueTask<FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>>> ReadNextResponseAsync(
        FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>> iterator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await iterator.ReadNextAsync(cancellationToken).ConfigureAwait(false)
                ?? throw Protocol(
                    "change-feed-response-null",
                    "The Cosmos change-feed provider returned a null response page.");
        }
        catch (Exception exception) when (CosmosProviderExceptionBoundary.ShouldNormalize(
            exception,
            cancellationToken))
        {
            throw Protocol(
                "change-feed-response-read-failed",
                "The Cosmos change-feed provider failed before returning a response page.");
        }
    }

    static void RequireValidRequestCharge(
        double requestCharge,
        HttpStatusCode statusCode,
        string? activityId)
    {
        if (!double.IsFinite(requestCharge) || requestCharge < 0)
        {
            throw Protocol(
                "change-feed-response-charge-invalid",
                "The Cosmos change-feed provider returned a non-finite or negative request charge.",
                statusCode,
                requestCharge: null,
                activityId);
        }
    }

    static void RequireSupportedStatus(
        HttpStatusCode statusCode,
        double requestCharge,
        string? activityId)
    {
        if (statusCode is not HttpStatusCode.OK and not HttpStatusCode.NotModified)
        {
            throw Protocol(
                "change-feed-response-status-invalid",
                "The Cosmos change-feed provider returned a status other than OK or not-modified.",
                statusCode,
                requestCharge,
                activityId);
        }
    }

    static void ThrowIfCanceledAfterResponse(
        HttpStatusCode statusCode,
        double requestCharge,
        string? activityId,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new CosmosProviderResponseCanceledException(
                statusCode,
                requestCharge,
                activityId,
                cancellationToken);
        }
    }

    static CosmosProviderProtocolException Protocol(
        string reason,
        string message,
        HttpStatusCode? statusCode = null,
        double? requestCharge = null,
        string? activityId = null) => new(
            reason,
            message,
            statusCode,
            requestCharge,
            activityId);

    string Evidence(string? activityId, HttpStatusCode statusCode, double requestCharge, int count)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, EvidenceProfile);
        Append(hash, affinityEvidence);
        Append(hash, activityId);
        Append(hash, ((int)statusCode).ToString(CultureInfo.InvariantCulture));
        Append(hash, requestCharge.ToString("R", CultureInfo.InvariantCulture));
        Append(hash, count.ToString(CultureInfo.InvariantCulture));
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
