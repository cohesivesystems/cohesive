using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Model;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosRelationQuerySourceReaderTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static readonly QualifiedShapeId Shape = new(new("tests/cosmos-source/v1"), new("Load"));
    static readonly FieldPath NamePath = FieldPath.FromField("Name");
    static readonly FieldPath CustomerIdsPath = FieldPath.FromField("CustomerIds");

    [Fact]
    public void Registration_UsesEntityEnvelopeConventionsAndDeterministicPhysicalAffinity()
    {
        using CosmosClient client = new(
            "https://localhost:8081/",
            EmulatorMasterKey,
            new CosmosClientOptions { ConnectionMode = ConnectionMode.Gateway });
        var container = client.GetContainer("operations", "entities");
        var policy = FixedPolicy();

        var first = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            policy);
        var second = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            policy);
        var reader = Assert.IsType<CosmosRelationQuerySourceReader>(first.Reader);
        ServiceCollection services = new();
        services.RegisterEntityRelationQuerySource(first);
        using var provider = services.BuildServiceProvider();
        var registered = Assert.Single(provider.GetEntityRelationQuerySourceCatalog().Sources);

        Assert.Equal(first.Source.Id, second.Source.Id);
        Assert.Equal(first.Source.ExecutionDomain, second.Source.ExecutionDomain);
        Assert.Equal("observationId", first.IdentitySourceSelector);
        Assert.Equal("observation.Name", first.FieldSourceSelector(NamePath));
        Assert.Equal("observation.CustomerIds", first.RelationshipKeySourceSelector(CustomerIdsPath));
        Assert.Same(policy, reader.Policy);
        Assert.Equal("entity", reader.EntityDocumentKind);
        Assert.Equal("https://localhost:8081", reader.AccountEndpoint);
        Assert.Equal("operations", reader.DatabaseId);
        Assert.Equal("entities", reader.ContainerId);
        Assert.Same(first, registered);
        Assert.Throws<ArgumentException>(() => CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "other-container",
            policy));
        Assert.Throws<ArgumentException>(() => CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                CosmosRelationQueryCrossPartitionPolicy.Prohibit)));
        Assert.Throws<ArgumentException>(() => new CosmosRelationQuerySourceReader(
            Shape,
            first.Source,
            container,
            "operations",
            "entities",
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                CosmosRelationQueryCrossPartitionPolicy.Prohibit)));
        var otherPolicy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-b"));
        var otherScope = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            otherPolicy);
        Assert.NotEqual(first.Source.Id, otherScope.Source.Id);
        Assert.Throws<ArgumentException>(() => CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            policy,
            fieldSourceSelector: static path => path.ToString()));

        var policyConstrained = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                fixedPartitionKey: new("tenant-a"),
                maximumEnumerationRows: 40,
                maximumKeysPerQuery: 2,
                maximumQueryChunks: 3),
            limits: new RelationQuerySourcePlacementLimits(
                maximumBatchSize: 10,
                maximumBufferedRows: 100,
                maximumFanOut: 10,
                maximumConcurrency: 1));
        Assert.Equal(6, policyConstrained.Source.Limits.MaximumBatchSize);
        Assert.Equal(40, policyConstrained.Source.Limits.MaximumBufferedRows);
    }

    [Fact]
    public async Task Reader_ValidatesAffinityAndBatchPolicyBeforeIo()
    {
        RecordingFeedFactory chunkFeed = new();
        var chunked = CreateFixture(
            chunkFeed,
            new(
                partitionSourceSelector: "partitionKey",
                fixedPartitionKey: new("tenant-a"),
                maximumKeysPerQuery: 1,
                maximumQueryChunks: 1));
        var request = Request(
            chunked,
            [SemanticField(chunked, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10));
        var mismatchResult = await chunked.Reader.ReadAsync(new(
            request.PhysicalPlan,
            request.Stage,
            request.PlacementBinding,
            new("foreign-source"),
            request.Shape,
            request.IdentitySelector,
            request.Fields,
            request.Constraint,
            request.MaximumBufferedRows));
        var chunkResult = await chunked.Reader.ReadAsync(Request(
            chunked,
            [SemanticField(chunked, NamePath)],
            new RelationQueryIdentityBatchLookup(["load-a", "load-b"])));

        Assert.Equal(RelationQuerySourceReadState.Failed, mismatchResult.State);
        Assert.Equal(RelationQuerySourceReadState.Inconclusive, chunkResult.State);
        Assert.Contains("batch-boundary-exceeded", chunkResult.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(chunkFeed.Queries);
    }

    [Fact]
    public async Task Enumeration_ProjectsExactFieldsAndPreservesMissingNullAndPartialEvidence()
    {
        RecordingFeedFactory feed = new();
        feed.Enqueue(
            Json("""{"_identity":"load-a","_field0":"Alpha"}"""),
            Json("""{"_identity":"load-b","_field0":null}"""),
            Json("""{"_identity":"load-c"}"""));
        var fixture = CreateFixture(feed, FixedPolicy());

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 2)));

        Assert.Equal(RelationQuerySourceReadState.Partial, result.State);
        Assert.Equal(["load-a", "load-b"], result.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQuerySourceReadFieldState.Value, result.Observations[0].Fields.Single().State);
        Assert.Equal("Alpha", result.Observations[0].Fields.Single().Value!.Value.String);
        Assert.Equal(RelationQuerySourceReadFieldState.Null, result.Observations[1].Fields.Single().State);
        Assert.Contains(
            "provider",
            result.Observations[0].Fields.Single().EvidenceReference,
            StringComparison.Ordinal);
        var query = Assert.Single(feed.Queries);
        Assert.Contains("c[\"observationId\"]", query.Query.QueryText, StringComparison.Ordinal);
        Assert.Contains("c[\"observation\"][\"Name\"]", query.Query.QueryText, StringComparison.Ordinal);
        Assert.Contains("c[\"documentKind\"]", query.Query.QueryText, StringComparison.Ordinal);
        Assert.Contains("c[\"observationType\"]", query.Query.QueryText, StringComparison.Ordinal);
        Assert.Contains("IS_OBJECT", query.Query.QueryText, StringComparison.Ordinal);
        Assert.DoesNotContain("Other", query.Query.QueryText, StringComparison.Ordinal);
        Assert.Equal(3, query.Options.MaxItemCount);
        Assert.Equal(3, query.Options.MaxBufferedItemCount);
        Assert.NotNull(query.Options.PartitionKey);
        Assert.Contains("/account/sha256/", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Contains("physical-plan", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Contains("placement-binding", result.EvidenceReference, StringComparison.Ordinal);

        RecordingFeedFactory completeFeed = new();
        completeFeed.Enqueue(
            Json("""{"_identity":"load-b","_field0":null}"""),
            Json("""{"_identity":"load-a","_field0":"Alpha"}"""),
            Json("""{"_identity":"load-c"}"""));
        var completeFixture = CreateFixture(completeFeed, FixedPolicy());
        var complete = await completeFixture.Reader.ReadAsync(Request(
            completeFixture,
            [SemanticField(completeFixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));
        Assert.True(
            complete.State == RelationQuerySourceReadState.Complete,
            complete.EvidenceReference);
        Assert.Equal(["load-a", "load-b", "load-c"], complete.Observations.Select(static row => row.Identity));
        Assert.Equal(RelationQuerySourceReadFieldState.Missing, complete.Observations[2].Fields.Single().State);
    }

    [Fact]
    public async Task BatchedLookups_ChunkDeterministicallyAndDeduplicateRelationshipRows()
    {
        var policy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-a"),
            maximumKeysPerQuery: 1,
            maximumQueryChunks: 4);
        RecordingFeedFactory identityFeed = new();
        identityFeed.Enqueue(Json("""{"_identity":"load-a","_field0":"Alpha"}"""));
        identityFeed.Enqueue(Json("""{"_identity":"load-b","_field0":"Beta"}"""));
        var identityFixture = CreateFixture(identityFeed, policy);

        var identity = await identityFixture.Reader.ReadAsync(Request(
            identityFixture,
            [SemanticField(identityFixture, NamePath)],
            new RelationQueryIdentityBatchLookup(["load-b", "load-a"])));

        Assert.True(
            identity.State == RelationQuerySourceReadState.Complete,
            identity.EvidenceReference);
        Assert.Equal(["load-a", "load-b"], identity.Observations.Select(static row => row.Identity));
        Assert.Equal(2, identityFeed.Queries.Count);
        Assert.All(identityFeed.Queries, query =>
            Assert.Contains("ARRAY_CONTAINS", query.Query.QueryText, StringComparison.Ordinal));

        RecordingFeedFactory relationshipFeed = new();
        var joined = Json("""{"_identity":"load-a","_field0":["customer-a","customer-b"]}""");
        relationshipFeed.Enqueue(joined);
        relationshipFeed.Enqueue(joined);
        var relationshipFixture = CreateFixture(relationshipFeed, policy);
        var correlation = CorrelationField(relationshipFixture, CustomerIdsPath);
        var relationship = await relationshipFixture.Reader.ReadAsync(Request(
            relationshipFixture,
            [correlation],
            new RelationQueryRelationshipKeyBatchLookup(
                CustomerIdsPath,
                correlation.SourceSelector,
                ["customer-b", "customer-a"])));

        Assert.Equal(RelationQuerySourceReadState.Complete, relationship.State);
        Assert.Equal("load-a", Assert.Single(relationship.Observations).Identity);
        Assert.Equal(2, relationshipFeed.Queries.Count);
        Assert.Contains("cosmos-source-feed-chain", relationship.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationshipLookup_RepeatedRowsDoNotConsumeTheUniqueOutputProbe()
    {
        var policy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-a"),
            maximumKeysPerQuery: 1,
            maximumQueryChunks: 2);
        var first = Json("""{"_identity":"load-a","_field0":["customer-a","customer-b"]}""");
        var second = Json("""{"_identity":"load-b","_field0":["customer-a","customer-b"]}""");
        RecordingFeedFactory feed = new();
        feed.Enqueue(first, second);
        feed.Enqueue(first, second);
        var fixture = CreateFixture(feed, policy);
        var correlation = CorrelationField(fixture, CustomerIdsPath);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [correlation],
            new RelationQueryRelationshipKeyBatchLookup(
                CustomerIdsPath,
                correlation.SourceSelector,
                ["customer-a", "customer-b"]),
            maximumBufferedRows: 2));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal(["load-a", "load-b"], result.Observations.Select(static row => row.Identity));
        Assert.Equal(2, feed.Queries.Count);
        Assert.Equal(3, feed.Queries[1].Options.MaxItemCount);
    }

    [Fact]
    public async Task EmptyCompleteIdentityLookup_ReturnsAuthoritativeNotFound()
    {
        RecordingFeedFactory feed = new();
        feed.Enqueue();
        var fixture = CreateFixture(feed, FixedPolicy());

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryIdentityBatchLookup(["missing-load"])));

        Assert.Equal(RelationQuerySourceReadState.NotFound, result.State);
        Assert.Equal(RelationQueryEvidenceCompleteness.Complete, result.Completeness);
        Assert.Empty(result.Observations);
        Assert.Single(feed.Queries);
    }

    [Fact]
    public async Task IdentityLookup_AtExactBufferCapacity_ProbesRemainingChunksBeforeReportingComplete()
    {
        var policy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-a"),
            maximumKeysPerQuery: 1,
            maximumQueryChunks: 2);
        RecordingFeedFactory feed = new();
        feed.Enqueue(Json("""{"_identity":"load-a","_field0":"Alpha"}"""));
        feed.Enqueue();
        var fixture = CreateFixture(feed, policy);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryIdentityBatchLookup(["load-a", "missing-load"]),
            maximumBufferedRows: 1));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal("load-a", Assert.Single(result.Observations).Identity);
        Assert.Equal(2, feed.Queries.Count);
        Assert.Equal(1, feed.Queries[1].Options.MaxItemCount);
    }

    [Fact]
    public async Task CosmosThrottling_ReturnsStableStatusAndSubstatusEvidence()
    {
        RecordingFeedFactory feed = new()
        {
            ReadException = new CosmosException(
                "provider-secret",
                HttpStatusCode.TooManyRequests,
                subStatusCode: 3200,
                activityId: "sensitive-activity",
                requestCharge: 1)
        };
        var fixture = CreateFixture(feed, FixedPolicy());

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("status/429/substatus/3200", result.EvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-secret", result.EvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-activity", result.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderCancellationWithoutInvocationCancellation_ReturnsFailedEvidence()
    {
        RecordingFeedFactory feed = new()
        {
            ReadException = new OperationCanceledException("provider read canceled")
        };
        var fixture = CreateFixture(feed, FixedPolicy());

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("OperationCanceledException", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Single(feed.Queries);
    }

    [Fact]
    public async Task SelectorCancellationWithoutInvocationCancellation_ReturnsFailedEvidenceBeforeIo()
    {
        RecordingFeedFactory feed = new();
        var fixture = CreateFixture(
            feed,
            FixedPolicy(),
            fieldSourceSelector: static _ => throw new OperationCanceledException("selector canceled"));
        RelationQuerySourceReadField requested = new(
            new("field/name"),
            NamePath,
            "observation.Name",
            RelationQuerySourceReadFieldPurpose.SemanticInput);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [requested],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Failed, result.State);
        Assert.Contains("selector-policy-failed", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(feed.Queries);
    }

    [Fact]
    public void Reader_RejectsBufferLimitThatCannotRetainTheBoundaryProbe()
    {
        RecordingFeedFactory feed = new();

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            feed,
            FixedPolicy(),
            new RelationQuerySourcePlacementLimits(
                maximumBatchSize: 1,
                maximumBufferedRows: Array.MaxLength,
                maximumFanOut: 1,
                maximumConcurrency: 1),
            constrainLimits: false));

        Assert.Contains((Array.MaxLength - 1).ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_RejectsBatchLimitNotAlreadyConstrainedByPolicy()
    {
        RecordingFeedFactory feed = new();
        var policy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-a"),
            maximumKeysPerQuery: 1,
            maximumQueryChunks: 1);

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            feed,
            policy,
            new RelationQuerySourcePlacementLimits(
                maximumBatchSize: 2,
                maximumBufferedRows: 1,
                maximumFanOut: 1,
                maximumConcurrency: 1),
            constrainLimits: false));

        Assert.Contains("already constrained", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestSizeBoundary_ReturnsStableInconclusiveEvidenceBeforeIo()
    {
        RecordingFeedFactory feed = new();
        var fixture = CreateFixture(
            feed,
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                fixedPartitionKey: new("tenant-a"),
                requestSizeLimits: new CosmosQueryRequestSizeLimits(
                    maximumSqlQueryUtf8Bytes: 64,
                    maximumRequestUtf8Bytes: 8_192)));

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(RelationQuerySourceReadState.Inconclusive, result.State);
        Assert.Contains("sql-query-text-boundary-exceeded", result.EvidenceReference, StringComparison.Ordinal);
        Assert.Empty(feed.Queries);
    }

    [Fact]
    public void RequestSizeLimits_RejectsAnImpossibleCompleteRequestBoundary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CosmosQueryRequestSizeLimits(maximumSqlQueryUtf8Bytes: 0));
        Assert.Throws<ArgumentException>(() => new CosmosQueryRequestSizeLimits(
            maximumSqlQueryUtf8Bytes: 1_024,
            maximumRequestUtf8Bytes: 1_023));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            fixedPartitionKey: new("tenant-a"),
            maximumKeysPerQuery: CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery + 1));
    }

    [Fact]
    public void RequestSizeBoundary_CountsJsonEscapingInCompleteRequest()
    {
        RecordingFeedFactory feed = new();
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        QueryDefinition query = new(new string('\\', 400));

        var exception = Assert.Throws<CosmosQueryRequestSizeLimitException>(() => reader.Prepare(
            query,
            new QueryRequestOptions(),
            new CosmosQueryRequestSizeLimits(
                maximumSqlQueryUtf8Bytes: 4_500,
                maximumRequestUtf8Bytes: 4_500)));

        Assert.Equal("query-request-boundary-exceeded", exception.Reason);
        Assert.Empty(feed.Queries);
    }

    [Fact]
    public async Task MaximumSafeRelationshipWidth_RendersWithBoundedExpressionDepth()
    {
        RecordingFeedFactory feed = new();
        feed.Enqueue();
        var fixture = CreateFixture(
            feed,
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                fixedPartitionKey: new("tenant-a"),
                maximumKeysPerQuery: CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery),
            new RelationQuerySourcePlacementLimits(
                maximumBatchSize: CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery,
                maximumBufferedRows: 100,
                maximumFanOut: CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery,
                maximumConcurrency: 1));
        var keys = Enumerable.Range(0, CosmosRelationQuerySourcePolicy.MaximumSupportedKeysPerQuery)
            .Select(static index => $"customer-{index}")
            .ToImmutableArray();

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [CorrelationField(fixture, CustomerIdsPath)],
            new RelationQueryRelationshipKeyBatchLookup(
                CustomerIdsPath,
                fixture.Reader.RelationshipKeySourceSelector(CustomerIdsPath),
                keys)));

        Assert.Equal(RelationQuerySourceReadState.NotFound, result.State);
        Assert.Single(feed.Queries);
    }

    [Fact]
    public async Task CrossPartitionDuplicateIdentityAndDuplicateAliasFailClosedAndCancellationPropagates()
    {
        RecordingFeedFactory duplicateIdentityFeed = new();
        duplicateIdentityFeed.Enqueue(
            Json("""{"_identity":"load-a","_field0":"Alpha","_partition":"tenant-a"}"""),
            Json("""{"_identity":"load-a","_field0":"Beta","_partition":"tenant-b"}"""));
        var crossPartition = CreateFixture(
            duplicateIdentityFeed,
            new(
                partitionSourceSelector: "partitionKey",
                CosmosRelationQueryCrossPartitionPolicy.AllowBoundedQueries));
        var duplicateIdentity = await crossPartition.Reader.ReadAsync(Request(
            crossPartition,
            [SemanticField(crossPartition, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        RecordingFeedFactory duplicateAliasFeed = new();
        duplicateAliasFeed.Enqueue(Json("""{"_identity":"load-a","_identity":"load-b","_field0":"Alpha"}"""));
        var duplicateAliasFixture = CreateFixture(duplicateAliasFeed, FixedPolicy());
        var duplicateAlias = await duplicateAliasFixture.Reader.ReadAsync(Request(
            duplicateAliasFixture,
            [SemanticField(duplicateAliasFixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();

        Assert.Equal(RelationQuerySourceReadState.Failed, duplicateIdentity.State);
        Assert.Contains(
            "duplicate-observation-identity",
            duplicateIdentity.EvidenceReference ?? duplicateIdentity.State.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(RelationQuerySourceReadState.Failed, duplicateAlias.State);
        Assert.Contains("projected-row-duplicate-alias", duplicateAlias.EvidenceReference, StringComparison.Ordinal);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await duplicateAliasFixture.Reader.ReadAsync(Request(
                duplicateAliasFixture,
                [SemanticField(duplicateAliasFixture, NamePath)],
                new RelationQueryBoundedEnumeration(maximumRows: 10)), canceled.Token));
    }

    static CosmosRelationQuerySourcePolicy FixedPolicy() => new(
        partitionSourceSelector: "partitionKey",
        fixedPartitionKey: new("tenant-a"));

    static ReaderFixture CreateFixture(
        RecordingFeedFactory feed,
        CosmosRelationQuerySourcePolicy policy,
        RelationQuerySourcePlacementLimits? limits = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        bool constrainLimits = true)
    {
        var configuredLimits = limits ?? CosmosRelationQuerySourceReader.DefaultLimits;
        var effectiveLimits = constrainLimits
            ? policy.GetEffectivePlacementLimits(configuredLimits)
            : configuredLimits;
        RelationQuerySourceInstance source = new(
            new("source/tests/cosmos"),
            new("domain/tests/cosmos"),
            CosmosRelationQuerySourceReader.TargetProfile,
            effectiveLimits);
        CosmosJsonQueryFeedReader feedReader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        CosmosRelationQuerySourceReader reader = new(
            Shape,
            source,
            feedReader,
            "https://tests.invalid",
            "operations",
            "entities",
            policy,
            fieldSourceSelector: fieldSourceSelector);
        return new(source, reader);
    }

    static RelationQuerySourceReadField SemanticField(ReaderFixture fixture, FieldPath path) => new(
        new RelationQueryInputId($"field/{Uri.EscapeDataString(path.ToString())}"),
        path,
        fixture.Reader.FieldSourceSelector(path),
        RelationQuerySourceReadFieldPurpose.SemanticInput);

    static RelationQuerySourceReadField CorrelationField(ReaderFixture fixture, FieldPath path) => new(
        input: null,
        path,
        fixture.Reader.RelationshipKeySourceSelector(path),
        RelationQuerySourceReadFieldPurpose.Correlation);

    static RelationQuerySourceReadRequest Request(
        ReaderFixture fixture,
        ImmutableArray<RelationQuerySourceReadField> fields,
        RelationQuerySourceReadConstraint constraint,
        long maximumBufferedRows = 100) => new(
        new("sha256", "tests/canonicalization-v1", "0123456789abcdef"),
        new("read/source"),
        new("placement/source"),
        fixture.Source.Id,
        Shape,
        fixture.Reader.IdentitySourceSelector,
        fields,
        constraint,
        maximumBufferedRows);

    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    sealed record ReaderFixture(
        RelationQuerySourceInstance Source,
        CosmosRelationQuerySourceReader Reader);

    sealed class RecordingFeedFactory
    {
        readonly Queue<ImmutableArray<JsonElement>> responses = new();

        public List<CapturedQuery> Queries { get; } = [];

        public Exception? ReadException { get; init; }

        public void Enqueue(params JsonElement[] rows) => responses.Enqueue([.. rows]);

        public FeedIterator<JsonElement> Create(QueryDefinition query, QueryRequestOptions options)
        {
            Queries.Add(new(query, options));
            return new JsonFeedIterator(
                responses.Count == 0 ? [] : responses.Dequeue(),
                ReadException);
        }
    }

    sealed record CapturedQuery(QueryDefinition Query, QueryRequestOptions Options);

    sealed class JsonFeedIterator(
        ImmutableArray<JsonElement> rows,
        Exception? readException) : FeedIterator<JsonElement>
    {
        bool read;

        public override bool HasMoreResults => !read;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
                throw new InvalidOperationException("The test feed was already exhausted.");
            read = true;
            if (readException is not null)
                return Task.FromException<FeedResponse<JsonElement>>(readException);
            return Task.FromResult<FeedResponse<JsonElement>>(new JsonFeedResponse(rows));
        }
    }

    sealed class JsonFeedResponse(ImmutableArray<JsonElement> rows) : FeedResponse<JsonElement>
    {
        public override string ContinuationToken => string.Empty;

        public override int Count => rows.Length;

        public override string IndexMetrics => string.Empty;

        public override string QueryAdvice => string.Empty;

        public override Headers Headers { get; } = new();

        public override IEnumerable<JsonElement> Resource => rows;

        public override HttpStatusCode StatusCode => HttpStatusCode.OK;

        public override CosmosDiagnostics Diagnostics => null!;

        public override double RequestCharge => 1;

        public override string ActivityId => "test-activity";

        public override string ETag => string.Empty;

        public override IEnumerator<JsonElement> GetEnumerator() => ((IEnumerable<JsonElement>)rows).GetEnumerator();
    }
}
