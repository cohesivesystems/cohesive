using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Diagnostics;
using Cohesive.Relations.Observability;
using Cohesive.Relations.Physical;
using Cohesive.Storage;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosRelationQuerySourceReaderTests
{
    const string EmulatorMasterKey = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    static readonly QualifiedShapeId Shape = new(new("tests/cosmos-source/v1"), new("Load"));
    static readonly FieldPath NamePath = FieldPath.FromField("Name");
    static readonly FieldPath CustomerIdsPath = FieldPath.FromField("CustomerIds");
    static readonly FieldPath StopsPath = FieldPath.FromField("Stops");
    static readonly FieldPath LocationIdPath = FieldPath.FromField("locationId");
    static readonly FieldPath VersionPath = FieldPath.FromField("SourceEntityVersion");

    [Fact]
    public async Task ObservationVersionProjection_SelectsExactEntityEnvelopeMetadata()
    {
        RecordingFeedFactory feed = new();
        feed.Enqueue(Json("""{"_identity":"load-a","_field0":7}"""));
        var fixture = CreateFixture(
            feed,
            FixedPolicy(),
            observationVersionSemanticPath: VersionPath);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, VersionPath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        Assert.Equal(VersionPath, fixture.Reader.ObservationVersionSemanticPath);
        Assert.Equal(
            CosmosRelationQuerySourceReader.ObservationVersionSourceSelector,
            fixture.Reader.FieldSourceSelector(VersionPath));
        Assert.Equal(7, result.Observations.Single().Fields.Single().Value!.Value.Int64);
        var query = Assert.Single(feed.Queries).Query.QueryText;
        Assert.Contains("c[\"observationVersion\"]", query, StringComparison.Ordinal);
        Assert.DoesNotContain("c[\"observation\"][\"SourceEntityVersion\"]", query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reader_EmitsBoundedNonSensitiveAcquisitionActivity()
    {
        var fixture = CreateFixture(new RecordingFeedFactory(), FixedPolicy());
        List<Activity> stopped = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = static source => source.Name == CosmosRelationQueryTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add
        };
        ActivitySource.AddActivityListener(listener);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryBoundedEnumeration(maximumRows: 10)));

        var activity = Assert.Single(stopped, item =>
            item.OperationName == CosmosRelationQueryTelemetry.SourceAcquisitionActivityName);
        Assert.Equal(ActivityKind.Client, activity.Kind);
        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal("complete", activity.GetTagItem(RelationQueryTelemetry.StatusTagName));
        Assert.Equal(
            "bounded_enumeration",
            activity.GetTagItem(RelationQueryTelemetry.ReadKindTagName));
        Assert.DoesNotContain(activity.TagObjects, tag =>
            tag.Value is string text
            && (text.Contains("tests.invalid", StringComparison.OrdinalIgnoreCase)
                || text.Contains("operations", StringComparison.OrdinalIgnoreCase)
                || text.Contains("entities", StringComparison.OrdinalIgnoreCase)));
    }

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
            policy,
            identitySemanticPath: FieldPath.FromField("id"),
            observationVersionSemanticPath: VersionPath);
        var second = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            policy,
            observationVersionSemanticPath: VersionPath);
        var payloadOnly = CosmosEntityRelationQuerySourceRegistration.Create(
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
        Assert.Equal(FieldPath.FromField("id"), first.IdentitySemanticPath);
        Assert.Equal(VersionPath, first.ObservationVersionSemanticPath);
        Assert.Equal("observationVersion", first.FieldSourceSelector(VersionPath));
        Assert.Equal("observation.Name", first.FieldSourceSelector(NamePath));
        Assert.Equal("observation.CustomerIds", first.RelationshipKeySourceSelector(CustomerIdsPath));
        Assert.Same(policy, reader.Policy);
        Assert.Equal("entity", reader.EntityDocumentKind);
        Assert.Equal("https://localhost:8081", reader.AccountEndpoint);
        Assert.Equal("operations", reader.DatabaseId);
        Assert.Equal("entities", reader.ContainerId);
        Assert.Same(first, registered);
        Assert.NotEqual(first.Source.Id, payloadOnly.Source.Id);
        Assert.Throws<ArgumentException>(() => CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            policy,
            identitySourceSelector: CosmosRelationQuerySourceReader.ObservationVersionSourceSelector,
            observationVersionSemanticPath: VersionPath));
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
                CosmosRelationQueryCrossPartitionPolicy.Prohibit)));
        Assert.Throws<ArgumentException>(() => new CosmosRelationQuerySourceReader(
            Shape,
            first.Source,
            container,
            "operations",
            "entities",
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
                CosmosRelationQueryCrossPartitionPolicy.Prohibit)));
        var otherPolicy = new CosmosRelationQuerySourcePolicy(
            partitionSourceSelector: "partitionKey",
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
            fixedPartitionKey: new("tenant-b"));
        var otherScope = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            otherPolicy);
        Assert.NotEqual(first.Source.Id, otherScope.Source.Id);
        var inheritedConsistencyScope = CosmosEntityRelationQuerySourceRegistration.Create(
            Shape,
            container,
            "operations",
            "entities",
            new CosmosRelationQuerySourcePolicy(
                partitionSourceSelector: "partitionKey",
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
                fixedPartitionKey: policy.FixedPartitionKey));
        Assert.NotEqual(first.Source.Id, inheritedConsistencyScope.Source.Id);
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
        Assert.Equal(ConsistencyLevel.Strong, query.Options.ConsistencyLevel);
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
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
    public async Task CollectionElementLookup_UsesSameElementExistsAndValidatesReturnedOwners()
    {
        RecordingFeedFactory feed = new();
        feed.Enqueue(Json(
            """{"_identity":"order-a","_field0":[{"locationId":"location-a"},{"locationId":"location-b"}]}"""));
        var fixture = CreateFixture(feed, FixedPolicy());
        var stops = SemanticField(fixture, StopsPath);

        var result = await fixture.Reader.ReadAsync(Request(
            fixture,
            [stops],
            new RelationQueryCollectionElementKeyBatchLookup(
                expansion: new("node:expand-stops"),
                collectionInput: stops.Input!.Value,
                collectionPath: StopsPath,
                elementReference: LocationIdPath,
                keys: ["location-a"])));

        Assert.Equal(RelationQuerySourceReadState.Complete, result.State);
        Assert.Equal("order-a", Assert.Single(result.Observations).Identity);
        var query = Assert.Single(feed.Queries).Query.QueryText;
        Assert.Contains("EXISTS (SELECT VALUE", query, StringComparison.Ordinal);
        Assert.Contains(" IN c[\"observation\"][\"Stops\"]", query, StringComparison.Ordinal);
        Assert.Contains("[\"locationId\"]", query, StringComparison.Ordinal);
        Assert.Contains("ARRAY_CONTAINS", query, StringComparison.Ordinal);
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
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
    public async Task BufferedMaterializationRead_PreservesProviderThrottlingForTypedSourceEvidence()
    {
        CosmosException throttled = new(
            "provider-secret",
            HttpStatusCode.TooManyRequests,
            subStatusCode: 3200,
            activityId: "sensitive-activity",
            requestCharge: 2.5);
        RecordingFeedFactory feed = new()
        {
            ReadException = throttled,
            ReturnResponseBeforeException = true
        };
        feed.Enqueue(Json("""{"_identity":"load-a","_field0":"Alpha"}"""));
        var fixture = CreateFixture(feed, FixedPolicy());
        var request = Request(
            fixture,
            [SemanticField(fixture, NamePath)],
            new RelationQueryIdentityBatchLookup(["load-a"]));

        var exception = await Assert.ThrowsAsync<CosmosRelationQueryMaterializationProviderException>(() => fixture.Reader
            .ReadMaterializationBufferedAsync(request, CancellationToken.None)
            .AsTask());

        Assert.Same(throttled, exception.ProviderException);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.ProviderException.StatusCode);
        Assert.Equal(2.5, exception.ProviderException.RequestCharge);
        Assert.Equal(1, exception.CompletedRequestCharge);
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
        Assert.Contains("CosmosProviderProtocolException", result.EvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider read canceled", result.EvidenceReference, StringComparison.Ordinal);
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
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
            logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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
                logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
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

    [Fact]
    public async Task FeedPage_PreservesTheCompleteSdkResponseAndProviderProgress()
    {
        JsonDocument document = JsonDocument.Parse(
            """[{"_identity":"load-a"},{"_identity":"load-b"},{"_identity":"load-c"}]""");
        CosmosJsonQueryFeedPageResult result;
        try
        {
            RecordingPageFeedFactory feed = new(
                [.. document.RootElement.EnumerateArray()],
                nextContinuationToken: "provider/next",
                hasMoreResultsAfterRead: true,
                requestCharge: 7.25,
                statusCode: HttpStatusCode.OK,
                activityId: "provider-activity-must-not-leak");
            CosmosJsonQueryFeedReader reader = new(
                new Uri("https://tests.invalid"),
                "operations",
                "entities",
                feed.Create);
            QueryDefinition query = new("SELECT * FROM c");
            QueryRequestOptions options = new() { MaxItemCount = 1 };
            var request = reader.Prepare(query, options, new());
            var feedRange = FeedRange.FromPartitionKey(new PartitionKey("tenant-a"));

            result = await reader.ReadPageAsync(
                request,
                feedRange,
                continuationToken: "provider/before",
                CancellationToken.None);

            var captured = Assert.Single(feed.Calls);
            Assert.Same(feedRange, captured.FeedRange);
            Assert.Same(query, captured.Query);
            Assert.Equal("provider/before", captured.ContinuationToken);
            Assert.Same(options, captured.Options);
        }
        finally
        {
            document.Dispose();
        }

        Assert.Equal(
            ["load-a", "load-b", "load-c"],
            [.. result.Rows.Select(row => row.GetProperty("_identity").GetString()!)]);
        Assert.Equal("provider/next", result.NextContinuationToken);
        Assert.True(result.HasMoreResults);
        Assert.Equal(7.25, result.RequestCharge);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.StartsWith("cosmos-json-query-page/v1/sha256/", result.ProviderEvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", result.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedPageEvidence_AttestsRequestConsistencyLevel()
    {
        var strong = await ReadEvidence(ConsistencyLevel.Strong);
        var eventual = await ReadEvidence(ConsistencyLevel.Eventual);
        var strongBuffered = await ReadBufferedEvidence(ConsistencyLevel.Strong);
        var eventualBuffered = await ReadBufferedEvidence(ConsistencyLevel.Eventual);

        Assert.NotEqual(strong, eventual);
        Assert.NotEqual(strongBuffered, eventualBuffered);

        static async Task<string?> ReadEvidence(ConsistencyLevel consistencyLevel)
        {
            RecordingPageFeedFactory feed = new(
                [Json("""{"_identity":"load-a"}""")],
                nextContinuationToken: null,
                hasMoreResultsAfterRead: false,
                requestCharge: 1,
                statusCode: HttpStatusCode.OK,
                activityId: "same-provider-activity");
            CosmosJsonQueryFeedReader reader = new(
                new Uri("https://tests.invalid"),
                "operations",
                "entities",
                feed.Create);
            var request = reader.Prepare(
                new("SELECT * FROM c"),
                new QueryRequestOptions { ConsistencyLevel = consistencyLevel },
                new());

            var result = await reader.ReadPageAsync(
                request,
                feedRange: null,
                continuationToken: null,
                CancellationToken.None);
            return result.ProviderEvidenceReference;
        }

        static async Task<string?> ReadBufferedEvidence(ConsistencyLevel consistencyLevel)
        {
            RecordingFeedFactory feed = new();
            feed.Enqueue(Json("""{"_identity":"load-a"}"""));
            CosmosJsonQueryFeedReader reader = new(
                new Uri("https://tests.invalid"),
                "operations",
                "entities",
                feed.Create);
            var request = reader.Prepare(
                new("SELECT * FROM c"),
                new QueryRequestOptions { ConsistencyLevel = consistencyLevel },
                new());

            var result = await reader.ReadAllAsync(
                request,
                maximumRows: 10,
                CancellationToken.None);
            return result.ProviderEvidenceReference;
        }
    }

    [Fact]
    public async Task FeedPage_RejectsInvalidProgressAndCancellationBeforeProviderIo()
    {
        RecordingPageFeedFactory feed = new(
            [],
            nextContinuationToken: "provider/next",
            hasMoreResultsAfterRead: false,
            requestCharge: 0,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            reader.ReadPageAsync(null!, null, null, CancellationToken.None).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            reader.ReadPageAsync(request, null, " \t", CancellationToken.None).AsTask());

        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            reader.ReadPageAsync(request, null, null, cancellation.Token).AsTask());

        Assert.Empty(feed.Calls);
    }

    [Fact]
    public async Task FeedPage_RejectsInvalidStatusChargeProgressAndItemsWithTypedEvidence()
    {
        RecordingPageFeedFactory rejected = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 2.5,
            statusCode: HttpStatusCode.Accepted,
            activityId: "provider-activity-must-not-leak");
        var statusException = await ReadProtocolFailure(rejected);

        Assert.Equal("query-response-status-invalid", statusException.Reason);
        Assert.Equal(HttpStatusCode.Accepted, statusException.StatusCode);
        Assert.Equal(2.5, statusException.RequestCharge);
        Assert.False(statusException.ResponseChargeAccounted);
        Assert.StartsWith("cosmos-provider-protocol/v1/sha256/", statusException.ProviderEvidenceReference, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", statusException.ProviderEvidenceReference, StringComparison.Ordinal);

        RecordingPageFeedFactory invalidCharge = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: double.PositiveInfinity,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        var chargeException = await ReadProtocolFailure(invalidCharge);
        Assert.Equal("query-response-charge-invalid", chargeException.Reason);
        Assert.Equal(HttpStatusCode.OK, chargeException.StatusCode);
        Assert.Null(chargeException.RequestCharge);

        RecordingPageFeedFactory inconsistentProgress = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: true,
            requestCharge: 1,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        var progressException = await ReadProtocolFailure(inconsistentProgress);
        Assert.Equal("query-continuation-missing", progressException.Reason);
        Assert.Equal(1, progressException.RequestCharge);

        RecordingPageFeedFactory undefinedItem = new(
            [default],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 1,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity");
        var itemException = await ReadProtocolFailure(undefinedItem);
        Assert.Equal("query-response-item-invalid", itemException.Reason);
    }

    [Fact]
    public async Task FeedPage_RejectsNullIteratorAndNullResponse()
    {
        CosmosJsonQueryFeedReader nullIteratorReader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            static (FeedRange? _, QueryDefinition _, string? _, QueryRequestOptions _) => null!);
        var request = nullIteratorReader.Prepare(new("SELECT * FROM c"), new(), new());
        var nullIterator = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            nullIteratorReader.ReadPageAsync(request, null, null, CancellationToken.None).AsTask());
        Assert.Equal("query-iterator-null", nullIterator.Reason);

        CosmosJsonQueryFeedReader nullResponseReader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            static (FeedRange? _, QueryDefinition _, string? _, QueryRequestOptions _) =>
                new NullJsonPageIterator());
        request = nullResponseReader.Prepare(new("SELECT * FROM c"), new(), new());
        var nullResponse = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            nullResponseReader.ReadPageAsync(request, null, null, CancellationToken.None).AsTask());
        Assert.Equal("query-response-null", nullResponse.Reason);
    }

    [Fact]
    public async Task FeedPage_NormalizesNonCosmosProjectionFailuresWithoutLeakingProviderDetails()
    {
        RecordingPageFeedFactory feed = new(
            [Json("""{"_identity":"load-a"}""")],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 3.25,
            statusCode: HttpStatusCode.OK,
            activityId: "provider-activity-must-not-leak",
            resourceException: new JsonException("malformed-provider-payload-secret"));

        var exception = await ReadProtocolFailure(feed);

        Assert.Equal("query-response-projection-failed", exception.Reason);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(3.25, exception.RequestCharge);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("malformed-provider-payload-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-must-not-leak", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FeedPage_RetainsFiniteChargeWhenActivityEvidenceCannotBeRead()
    {
        RecordingPageFeedFactory feed = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 3.75,
            statusCode: HttpStatusCode.OK,
            activityId: "must-not-be-observed",
            activityIdException: new InvalidOperationException("provider-activity-secret"));

        var exception = await ReadProtocolFailure(feed);

        Assert.Equal("query-response-projection-failed", exception.Reason);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(3.75, exception.RequestCharge);
        Assert.False(exception.ResponseChargeAccounted);
        Assert.DoesNotContain("provider-activity-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-secret", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedFeed_RetainsUnaccountedFiniteChargeWhenActivityEvidenceCannotBeRead()
    {
        RecordingPageFeedFactory feed = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 4.75,
            statusCode: HttpStatusCode.OK,
            activityId: "must-not-be-observed",
            activityIdException: new InvalidOperationException("provider-activity-secret"));
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());
        List<(double Charge, HttpStatusCode Status)> completed = [];

        var exception = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            reader.ReadAllAsync(
                request,
                maximumRows: 10,
                CancellationToken.None,
                (charge, status) => completed.Add((charge, status))).AsTask());

        Assert.Equal("query-response-evidence-invalid", exception.Reason);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(4.75, exception.RequestCharge);
        Assert.False(exception.ResponseChargeAccounted);
        Assert.Empty(completed);
        Assert.DoesNotContain("provider-activity-secret", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-activity-secret", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletedResponseCancellationCarriesEvidenceAndPrecedesProjection()
    {
        using CancellationTokenSource cancellation = new();
        RecordingPageFeedFactory feed = new(
            [Json("""{"_identity":"must-not-project"}""")],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 4.25,
            statusCode: HttpStatusCode.OK,
            activityId: "provider-activity-must-not-leak",
            afterRead: cancellation.Cancel);
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());

        var exception = await Assert.ThrowsAsync<CosmosProviderResponseCanceledException>(() =>
            reader.ReadPageAsync(request, null, null, cancellation.Token).AsTask());

        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(4.25, exception.RequestCharge);
        Assert.False(exception.ResponseChargeAccounted);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.DoesNotContain("provider-activity-must-not-leak", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundedFeed_AccountsCompletedResponseBeforeCancellation()
    {
        using CancellationTokenSource cancellation = new();
        RecordingPageFeedFactory feed = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 5.5,
            statusCode: HttpStatusCode.OK,
            activityId: "tests/activity",
            afterRead: cancellation.Cancel);
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());
        List<(double Charge, HttpStatusCode Status)> completed = [];

        var exception = await Assert.ThrowsAsync<CosmosProviderResponseCanceledException>(() =>
            reader.ReadAllAsync(
                request,
                maximumRows: 10,
                cancellation.Token,
                (charge, status) => completed.Add((charge, status))).AsTask());

        Assert.Equal(5.5, exception.RequestCharge);
        Assert.True(exception.ResponseChargeAccounted);
        Assert.Equal([(5.5, HttpStatusCode.OK)], completed);
    }

    [Fact]
    public async Task BoundedFeed_AccountsCompletedChargeBeforeRejectingStatus()
    {
        RecordingPageFeedFactory feed = new(
            [],
            nextContinuationToken: null,
            hasMoreResultsAfterRead: false,
            requestCharge: 6.5,
            statusCode: HttpStatusCode.BadRequest,
            activityId: "provider-activity-must-not-leak");
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());
        List<(double Charge, HttpStatusCode Status)> completed = [];

        var exception = await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            reader.ReadAllAsync(
                request,
                maximumRows: 10,
                CancellationToken.None,
                (charge, status) => completed.Add((charge, status))).AsTask());

        Assert.Equal("query-response-status-invalid", exception.Reason);
        Assert.Equal(6.5, exception.RequestCharge);
        Assert.True(exception.ResponseChargeAccounted);
        Assert.Equal([(6.5, HttpStatusCode.BadRequest)], completed);
        Assert.DoesNotContain("provider-activity-must-not-leak", exception.ProviderEvidenceReference, StringComparison.Ordinal);
    }

    static async Task<CosmosProviderProtocolException> ReadProtocolFailure(RecordingPageFeedFactory feed)
    {
        CosmosJsonQueryFeedReader reader = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            feed.Create);
        var request = reader.Prepare(new("SELECT * FROM c"), new(), new());
        return await Assert.ThrowsAsync<CosmosProviderProtocolException>(() =>
            reader.ReadPageAsync(request, null, null, CancellationToken.None).AsTask());
    }

    static CosmosRelationQuerySourcePolicy FixedPolicy() => new(
        partitionSourceSelector: "partitionKey",
        logicalPartition: RelationQueryLogicalPartitionIdentity.WholeSource,
        fixedPartitionKey: new("tenant-a"),
        readConsistencyLevel: ConsistencyLevel.Strong);

    static ReaderFixture CreateFixture(
        RecordingFeedFactory feed,
        CosmosRelationQuerySourcePolicy policy,
        RelationQuerySourcePlacementLimits? limits = null,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        bool constrainLimits = true,
        FieldPath? observationVersionSemanticPath = null)
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
            fieldSourceSelector: fieldSourceSelector,
            observationVersionSemanticPath: observationVersionSemanticPath);
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

        public bool ReturnResponseBeforeException { get; init; }

        public void Enqueue(params JsonElement[] rows) => responses.Enqueue([.. rows]);

        public FeedIterator<JsonElement> Create(QueryDefinition query, QueryRequestOptions options)
        {
            Queries.Add(new(query, options));
            return new JsonFeedIterator(
                responses.Count == 0 ? [] : responses.Dequeue(),
                ReadException,
                ReturnResponseBeforeException);
        }
    }

    sealed record CapturedQuery(QueryDefinition Query, QueryRequestOptions Options);

    sealed record CapturedPageQuery(
        FeedRange? FeedRange,
        QueryDefinition Query,
        string? ContinuationToken,
        QueryRequestOptions Options);

    sealed class RecordingPageFeedFactory(
        ImmutableArray<JsonElement> rows,
        string? nextContinuationToken,
        bool hasMoreResultsAfterRead,
        double requestCharge,
        HttpStatusCode statusCode,
        string activityId,
        Action? afterRead = null,
        Exception? resourceException = null,
        Exception? activityIdException = null)
    {
        public List<CapturedPageQuery> Calls { get; } = [];

        public FeedIterator<JsonElement> Create(
            FeedRange? feedRange,
            QueryDefinition query,
            string? continuationToken,
            QueryRequestOptions options)
        {
            Calls.Add(new(feedRange, query, continuationToken, options));
            return new JsonPageFeedIterator(
                new JsonPageFeedResponse(
                    rows,
                    nextContinuationToken,
                    requestCharge,
                    statusCode,
                    activityId,
                    resourceException,
                    activityIdException),
                hasMoreResultsAfterRead,
                afterRead);
        }
    }

    sealed class JsonFeedIterator(
        ImmutableArray<JsonElement> rows,
        Exception? readException,
        bool returnResponseBeforeException) : FeedIterator<JsonElement>
    {
        int reads;

        public override bool HasMoreResults => reads == 0
            || (returnResponseBeforeException && readException is not null && reads == 1);

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!HasMoreResults)
                throw new InvalidOperationException("The test feed was already exhausted.");
            reads++;
            if (readException is not null && (!returnResponseBeforeException || reads > 1))
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

    sealed class JsonPageFeedIterator(
        FeedResponse<JsonElement> response,
        bool hasMoreResultsAfterRead,
        Action? afterRead) : FeedIterator<JsonElement>
    {
        bool read;

        public override bool HasMoreResults => !read || hasMoreResultsAfterRead;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
                throw new InvalidOperationException("The test feed page was already read.");

            read = true;
            afterRead?.Invoke();
            return Task.FromResult(response);
        }
    }

    sealed class NullJsonPageIterator : FeedIterator<JsonElement>
    {
        public override bool HasMoreResults => true;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(
            CancellationToken cancellationToken = default) => Task.FromResult<FeedResponse<JsonElement>>(null!);
    }

    sealed class JsonPageFeedResponse(
        ImmutableArray<JsonElement> rows,
        string? continuationToken,
        double requestCharge,
        HttpStatusCode statusCode,
        string activityId,
        Exception? resourceException,
        Exception? activityIdException) : FeedResponse<JsonElement>
    {
        public override string ContinuationToken => continuationToken!;

        public override int Count => rows.Length;

        public override string IndexMetrics => string.Empty;

        public override string QueryAdvice => string.Empty;

        public override Headers Headers { get; } = new();

        public override IEnumerable<JsonElement> Resource => resourceException is null
            ? rows
            : new ThrowingEnumerable<JsonElement>(resourceException);

        public override HttpStatusCode StatusCode => statusCode;

        public override CosmosDiagnostics Diagnostics => null!;

        public override double RequestCharge => requestCharge;

        public override string ActivityId => activityIdException is null
            ? activityId
            : throw activityIdException;

        public override string ETag => string.Empty;

        public override IEnumerator<JsonElement> GetEnumerator() => ((IEnumerable<JsonElement>)rows).GetEnumerator();
    }

    sealed class ThrowingEnumerable<T>(Exception exception) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => throw exception;

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
