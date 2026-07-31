using System.Collections.Immutable;
using System.Net;
using Cohesive.Adapters.Cosmos;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosMaterializationChangeFeedReaderTests
{
    static readonly DateTime ProviderTimestamp = new(2026, 7, 30, 12, 34, 56, DateTimeKind.Utc);

    [Fact]
    public async Task ReadPage_UsesFullFidelityAndProjectsTheCompleteProviderPage()
    {
        Dictionary<string, ObservationValue> currentObservation = new(StringComparer.Ordinal)
        {
            ["Name"] = ObservationValue.FromString("current")
        };
        Dictionary<string, ObservationValue> previousObservation = new(StringComparer.Ordinal)
        {
            ["Name"] = ObservationValue.FromString("previous")
        };
        var current = Document("load-a", version: 2, currentObservation);
        var previous = Document("load-a", version: 1, previousObservation);
        var deleted = Document("load-b", version: 3, new(StringComparer.Ordinal));
        RecordingChangeFeedFactory feed = new(
            [
                Change(current, null, lsn: 10, previousLsn: 0, ChangeFeedOperationType.Create),
                Change(current, previous, lsn: 11, previousLsn: 10, ChangeFeedOperationType.Replace),
                Change(null, deleted, lsn: 12, previousLsn: 11, ChangeFeedOperationType.Delete, true, "load-b")
            ],
            continuationToken: "provider/after",
            requestCharge: 8.5,
            statusCode: HttpStatusCode.OK,
            activityId: "provider-activity-must-not-leak");
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);
        var feedRange = FeedRange.FromPartitionKey(new PartitionKey("tenant-a"));

        var result = await reader.ReadPageAsync(
            new(CosmosMaterializationChangeFeedStartKind.Now),
            feedRange,
            pageSizeHint: 1,
            CancellationToken.None);

        var call = Assert.Single(feed.Calls);
        Assert.Equal(ChangeFeedStartFrom.Now(feedRange).GetType(), call.Start.GetType());
        Assert.Same(ChangeFeedMode.AllVersionsAndDeletes, call.Mode);
        Assert.Equal(1, call.Options.PageSizeHint);
        Assert.Equal(3, result.Changes.Length);
        Assert.Equal(
            [
                CosmosMaterializationProviderChangeKind.Create,
                CosmosMaterializationProviderChangeKind.Replace,
                CosmosMaterializationProviderChangeKind.Delete
            ],
            [.. result.Changes.Select(static change => change.OperationType)]);
        Assert.Equal([10, 11, 12], [.. result.Changes.Select(static change => change.Lsn)]);
        Assert.Equal("load-b", result.Changes[2].DeletedItemId);
        Assert.True(result.Changes[2].IsTimeToLiveExpired);
        Assert.Equal(ProviderTimestamp, result.Changes[1].ConflictResolutionTimestamp);
        Assert.Equal("provider/after", result.ContinuationToken);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(8.5, result.RequestCharge);
        Assert.StartsWith("cosmos-materialization-change-feed/v1/sha256/", result.ProviderEvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", result.ProviderEvidenceReference, StringComparison.Ordinal);

        Assert.NotSame(current, result.Changes[0].Current);
        Assert.NotSame(currentObservation, result.Changes[0].Current!.Observation);
        currentObservation["Name"] = ObservationValue.FromString("mutated-after-read");
        var projectedCurrent = Assert.IsType<CosmosObservationContainerDocument>(result.Changes[0].Current);
        Assert.Equal(ObservationValue.FromString("current"), projectedCurrent.Observation!["Name"]);
    }

    [Fact]
    public async Task ReadPage_ContinuationReturnsAnEmptyDurableNotModifiedBoundary()
    {
        RecordingChangeFeedFactory feed = new(
            [],
            continuationToken: "provider/next-cut",
            requestCharge: 1.25,
            statusCode: HttpStatusCode.NotModified,
            activityId: "not-modified/activity");
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);

        var result = await reader.ReadPageAsync(
            new(CosmosMaterializationChangeFeedStartKind.Continuation, "provider/prior-cut"),
            feedRange: null,
            pageSizeHint: 64,
            CancellationToken.None);

        var call = Assert.Single(feed.Calls);
        Assert.Equal(
            ChangeFeedStartFrom.ContinuationToken("tests/type-probe").GetType(),
            call.Start.GetType());
        Assert.Same(ChangeFeedMode.AllVersionsAndDeletes, call.Mode);
        Assert.Empty(result.Changes);
        Assert.Equal("provider/next-cut", result.ContinuationToken);
        Assert.Equal(HttpStatusCode.NotModified, result.StatusCode);
        Assert.Equal(1.25, result.RequestCharge);
    }

    [Fact]
    public async Task ReadPage_RejectsUnsupportedStartsBoundsAndCancellationBeforeProviderIo()
    {
        Assert.Equal(
            [CosmosMaterializationChangeFeedStartKind.Now, CosmosMaterializationChangeFeedStartKind.Continuation],
            Enum.GetValues<CosmosMaterializationChangeFeedStartKind>());
        Assert.Throws<ArgumentException>(() =>
            new CosmosMaterializationChangeFeedStart(CosmosMaterializationChangeFeedStartKind.Now, "provider/token"));
        Assert.Throws<ArgumentException>(() =>
            new CosmosMaterializationChangeFeedStart(CosmosMaterializationChangeFeedStartKind.Continuation));
        Assert.Throws<ArgumentException>(() =>
            new CosmosMaterializationChangeFeedStart(CosmosMaterializationChangeFeedStartKind.Continuation, " \t"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CosmosMaterializationChangeFeedStart((CosmosMaterializationChangeFeedStartKind)42));

        RecordingChangeFeedFactory feed = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 0,
            statusCode: HttpStatusCode.NotModified,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);
        var now = new CosmosMaterializationChangeFeedStart(CosmosMaterializationChangeFeedStartKind.Now);
        var continuation = new CosmosMaterializationChangeFeedStart(
            CosmosMaterializationChangeFeedStartKind.Continuation,
            "provider/prior");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            reader.ReadPageAsync(now, null, 0, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadPageAsync(
                continuation,
                FeedRange.FromPartitionKey(new PartitionKey("tenant-a")),
                1,
                CancellationToken.None).AsTask());

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadPageAsync(now, null, 1, cancellation.Token).AsTask());

        Assert.Empty(feed.Calls);
    }

    [Fact]
    public async Task ReadPage_RejectsMissingMetadataAndMissingDurableContinuation()
    {
        var itemWithoutMetadata = new ChangeFeedItem<CosmosObservationContainerDocument>
        {
            Current = Document("load-a", version: 1, new(StringComparer.Ordinal)),
            Metadata = null!
        };
        RecordingChangeFeedFactory missingMetadata = new(
            [itemWithoutMetadata],
            continuationToken: "provider/next",
            requestCharge: 1,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader metadataReader = new("tests/affinity", missingMetadata.Create);

        var metadataException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            metadataReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-metadata-missing", metadataException.Reason);
        Assert.Equal(HttpStatusCode.OK, metadataException.StatusCode);
        Assert.Equal(1, metadataException.RequestCharge);

        RecordingChangeFeedFactory missingContinuation = new(
            [],
            continuationToken: " ",
            requestCharge: 1,
            statusCode: HttpStatusCode.NotModified,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader continuationReader = new("tests/affinity", missingContinuation.Create);

        var continuationException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            continuationReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-continuation-missing", continuationException.Reason);
        Assert.Equal(HttpStatusCode.NotModified, continuationException.StatusCode);
        Assert.Equal(1, continuationException.RequestCharge);
    }

    [Fact]
    public async Task ReadPage_RejectsInvalidStatusAndChargeWithSanitizedCompletedResponseEvidence()
    {
        RecordingChangeFeedFactory rejected = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 2.75,
            statusCode: HttpStatusCode.BadRequest,
            activityId: "provider-activity-must-not-leak");
        CosmosMaterializationChangeFeedReader rejectedReader = new("tests/affinity", rejected.Create);

        var statusException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            rejectedReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());

        Assert.Equal("change-feed-response-status-invalid", statusException.Reason);
        Assert.Equal(HttpStatusCode.BadRequest, statusException.StatusCode);
        Assert.Equal(2.75, statusException.RequestCharge);
        Assert.False(statusException.ResponseChargeAccounted);
        Assert.StartsWith("cosmos-provider-protocol/v1/sha256/", statusException.ProviderEvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", statusException.ProviderEvidenceReference, StringComparison.Ordinal);

        RecordingChangeFeedFactory invalidCharge = new(
            [],
            continuationToken: "provider/next",
            requestCharge: double.NaN,
            statusCode: HttpStatusCode.OK,
            activityId: "invalid-charge/activity");
        CosmosMaterializationChangeFeedReader invalidChargeReader = new("tests/affinity", invalidCharge.Create);

        var chargeException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            invalidChargeReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());

        Assert.Equal("change-feed-response-charge-invalid", chargeException.Reason);
        Assert.Equal(HttpStatusCode.OK, chargeException.StatusCode);
        Assert.Null(chargeException.RequestCharge);
    }

    [Fact]
    public async Task ReadPage_RejectsNotModifiedChangesNullItemsAndUnsupportedOperations()
    {
        var valid = Change(
            Document("load-a", version: 1, new(StringComparer.Ordinal)),
            null,
            lsn: 1,
            previousLsn: 0,
            ChangeFeedOperationType.Create);
        RecordingChangeFeedFactory notModifiedWithChange = new(
            [valid],
            continuationToken: "provider/next",
            requestCharge: 1,
            statusCode: HttpStatusCode.NotModified,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader notModifiedReader = new(
            "tests/affinity",
            notModifiedWithChange.Create);

        var notModifiedException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            notModifiedReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-not-modified-with-changes", notModifiedException.Reason);

        RecordingChangeFeedFactory nullItem = new(
            [null!],
            continuationToken: "provider/next",
            requestCharge: 1,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader nullItemReader = new("tests/affinity", nullItem.Create);

        var nullItemException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            nullItemReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-item-null", nullItemException.Reason);

        var unsupported = new ChangeFeedItem<CosmosObservationContainerDocument>
        {
            Current = Document("load-b", version: 1, new(StringComparer.Ordinal)),
            Metadata = UnsupportedOperationMetadata(42)
        };
        RecordingChangeFeedFactory unsupportedOperation = new(
            [unsupported],
            continuationToken: "provider/next",
            requestCharge: 1,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        CosmosMaterializationChangeFeedReader unsupportedReader = new("tests/affinity", unsupportedOperation.Create);

        var operationException = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            unsupportedReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-operation-unsupported", operationException.Reason);
    }

    [Fact]
    public async Task ReadPage_RejectsNullIteratorAndNullResponse()
    {
        CosmosMaterializationChangeFeedReader nullIteratorReader = new(
            "tests/affinity",
            static (_, _, _) => null!);
        var nullIterator = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            nullIteratorReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-iterator-null", nullIterator.Reason);

        CosmosMaterializationChangeFeedReader nullResponseReader = new(
            "tests/affinity",
            static (_, _, _) => new NullResponseChangeFeedIterator());
        var nullResponse = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            nullResponseReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-response-null", nullResponse.Reason);

        RecordingChangeFeedFactory exhaustedAfterResponse = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 1,
            statusCode: HttpStatusCode.NotModified,
            activityId: "tests/activity",
            hasMoreResultsAfterRead: false);
        CosmosMaterializationChangeFeedReader exhaustedReader = new(
            "tests/affinity",
            exhaustedAfterResponse.Create);
        var progress = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            exhaustedReader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());
        Assert.Equal("change-feed-progress-inconsistent", progress.Reason);
    }

    [Fact]
    public async Task ReadPage_NormalizesNonCosmosProjectionFailuresWithoutLeakingProviderDetails()
    {
        var change = Change(
            Document("load-a", version: 1, new(StringComparer.Ordinal)),
            null,
            lsn: 1,
            previousLsn: 0,
            ChangeFeedOperationType.Create);
        RecordingChangeFeedFactory feed = new(
            [change],
            continuationToken: "provider/next",
            requestCharge: 4.75,
            statusCode: HttpStatusCode.OK,
            activityId: "provider-activity-must-not-leak",
            resourceException: new InvalidOperationException("provider-projection-secret"));
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);

        var exception = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            reader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());

        Assert.Equal("change-feed-response-projection-failed", exception.Reason);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(4.75, exception.RequestCharge);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("provider-projection-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPage_RetainsFiniteChargeWhenActivityEvidenceCannotBeRead()
    {
        RecordingChangeFeedFactory feed = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 5.25,
            statusCode: HttpStatusCode.NotModified,
            activityId: "must-not-be-observed",
            activityIdException: new InvalidOperationException("provider-activity-secret"));
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);

        var exception = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            reader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                CancellationToken.None).AsTask());

        Assert.Equal("change-feed-response-projection-failed", exception.Reason);
        Assert.Equal(HttpStatusCode.NotModified, exception.StatusCode);
        Assert.Equal(5.25, exception.RequestCharge);
        Assert.False(exception.ResponseChargeAccounted);
        Assert.DoesNotContain("provider-activity-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-secret", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadPage_PropagatesCancellationObservedDuringProviderRead()
    {
        using CancellationTokenSource cancellation = new();
        RecordingChangeFeedFactory feed = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 0,
            statusCode: HttpStatusCode.NotModified,
            activityId: "tests/activity",
            beforeRead: cancellation.Cancel);
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                cancellation.Token).AsTask());

        Assert.Single(feed.Calls);
    }

    [Fact]
    public async Task ReadPage_CancellationAfterCompletedResponseCarriesStatusChargeAndSanitizedEvidence()
    {
        using CancellationTokenSource cancellation = new();
        RecordingChangeFeedFactory feed = new(
            [],
            continuationToken: "provider/next",
            requestCharge: 3.5,
            statusCode: HttpStatusCode.NotModified,
            activityId: "provider-activity-must-not-leak",
            afterRead: cancellation.Cancel);
        CosmosMaterializationChangeFeedReader reader = new("tests/affinity", feed.Create);

        var exception = await Assert.ThrowsAsync<CosmosProviderResponseCanceledException>(() =>
            reader.ReadPageAsync(
                new(CosmosMaterializationChangeFeedStartKind.Now),
                null,
                1,
                cancellation.Token).AsTask());

        Assert.Equal(HttpStatusCode.NotModified, exception.StatusCode);
        Assert.Equal(3.5, exception.RequestCharge);
        Assert.False(exception.ResponseChargeAccounted);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain("provider-activity-must-not-leak", exception.ProviderEvidenceReference, StringComparison.Ordinal);
        Assert.Single(feed.Calls);
    }

    static ChangeFeedItem<CosmosObservationContainerDocument> Change(
        CosmosObservationContainerDocument? current,
        CosmosObservationContainerDocument? previous,
        long lsn,
        long previousLsn,
        ChangeFeedOperationType operationType,
        bool isTimeToLiveExpired = false,
        string? deletedItemId = null) => new()
        {
            Current = current!,
            Previous = previous!,
            Metadata = Metadata(lsn, previousLsn, operationType, isTimeToLiveExpired, deletedItemId)
        };

    static ChangeFeedMetadata Metadata(
        long lsn,
        long previousLsn,
        ChangeFeedOperationType operationType,
        bool isTimeToLiveExpired,
        string? deletedItemId)
    {
        var metadata = JsonConvert.DeserializeObject<ChangeFeedMetadata>(
            $$"""
            {
              "lsn": {{lsn}},
              "previousLsn": {{previousLsn}},
              "operationType": "{{operationType.ToString().ToLowerInvariant()}}",
              "crts": {{new DateTimeOffset(ProviderTimestamp).ToUnixTimeSeconds()}},
              "timeToLiveExpired": {{isTimeToLiveExpired.ToString().ToLowerInvariant()}},
              "id": {{(deletedItemId is null ? "null" : JsonConvert.ToString(deletedItemId))}}
            }
            """);
        return Assert.IsType<ChangeFeedMetadata>(metadata);
    }

    static ChangeFeedMetadata UnsupportedOperationMetadata(int operation)
    {
        var metadata = JsonConvert.DeserializeObject<ChangeFeedMetadata>(
            $$"""
            {
              "lsn": 1,
              "previousLsn": 0,
              "operationType": {{operation}},
              "crts": {{new DateTimeOffset(ProviderTimestamp).ToUnixTimeSeconds()}},
              "timeToLiveExpired": false,
              "id": null
            }
            """);
        return Assert.IsType<ChangeFeedMetadata>(metadata);
    }

    static CosmosObservationContainerDocument Document(
        string id,
        long version,
        Dictionary<string, ObservationValue> observation) => new(
            id,
            "tenant-a",
            "observation",
            "tests/load",
            id,
            version,
            observation);

    sealed record CapturedChangeFeedRead(
        ChangeFeedStartFrom Start,
        ChangeFeedMode Mode,
        ChangeFeedRequestOptions Options);

    sealed class RecordingChangeFeedFactory(
        ImmutableArray<ChangeFeedItem<CosmosObservationContainerDocument>> changes,
        string continuationToken,
        double requestCharge,
        HttpStatusCode statusCode,
        string activityId,
        Action? beforeRead = null,
        Action? afterRead = null,
        bool hasMoreResultsAfterRead = true,
        Exception? resourceException = null,
        Exception? activityIdException = null)
    {
        public List<CapturedChangeFeedRead> Calls { get; } = [];

        public FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>> Create(
            ChangeFeedStartFrom start,
            ChangeFeedMode mode,
            ChangeFeedRequestOptions options)
        {
            Calls.Add(new(start, mode, options));
            return new ChangeFeedIterator(
                new ChangeFeedResponse(
                    changes,
                    continuationToken,
                    requestCharge,
                    statusCode,
                    activityId,
                    resourceException,
                    activityIdException),
                beforeRead,
                afterRead,
                hasMoreResultsAfterRead);
        }
    }

    sealed class ChangeFeedIterator(
        FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>> response,
        Action? beforeRead,
        Action? afterRead,
        bool hasMoreResultsAfterRead)
        : FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>>
    {
        bool read;

        public override bool HasMoreResults => !read || hasMoreResultsAfterRead;

        public override Task<FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>>> ReadNextAsync(
            CancellationToken cancellationToken = default)
        {
            beforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
                throw new InvalidOperationException("The deterministic change-feed response was already read.");
            read = true;
            afterRead?.Invoke();
            return Task.FromResult(response);
        }
    }

    sealed class NullResponseChangeFeedIterator : FeedIterator<ChangeFeedItem<CosmosObservationContainerDocument>>
    {
        public override bool HasMoreResults => true;

        public override Task<FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>>> ReadNextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<
                FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>>>(null!);
    }

    sealed class ChangeFeedResponse(
        ImmutableArray<ChangeFeedItem<CosmosObservationContainerDocument>> changes,
        string continuationToken,
        double requestCharge,
        HttpStatusCode statusCode,
        string activityId,
        Exception? resourceException,
        Exception? activityIdException) : FeedResponse<ChangeFeedItem<CosmosObservationContainerDocument>>
    {
        public override string ContinuationToken => continuationToken;

        public override int Count => changes.Length;

        public override string IndexMetrics => string.Empty;

        public override string QueryAdvice => string.Empty;

        public override Headers Headers { get; } = new();

        public override IEnumerable<ChangeFeedItem<CosmosObservationContainerDocument>> Resource =>
            resourceException is null
                ? changes
                : new ThrowingEnumerable<ChangeFeedItem<CosmosObservationContainerDocument>>(resourceException);

        public override HttpStatusCode StatusCode => statusCode;

        public override CosmosDiagnostics Diagnostics => null!;

        public override double RequestCharge => requestCharge;

        public override string ActivityId => activityIdException is null
            ? activityId
            : throw activityIdException;

        public override string ETag => string.Empty;

        public override IEnumerator<ChangeFeedItem<CosmosObservationContainerDocument>> GetEnumerator() =>
            ((IEnumerable<ChangeFeedItem<CosmosObservationContainerDocument>>)changes).GetEnumerator();
    }

    sealed class ThrowingEnumerable<T>(Exception exception) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => throw exception;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
