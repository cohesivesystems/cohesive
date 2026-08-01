using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Cohesive.Adapters.Cosmos;
using Cohesive.Control;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;
using Microsoft.Azure.Cosmos;

namespace Cohesive.Tests.Cosmos;

public sealed class CosmosMaterializationSourceTests
{
    const long GenerousPageBytes = 1_000_000;
    static readonly QualifiedShapeId Shape = new(new("tests/cosmos-materialization/v1"), new("Load"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/canonicalization/v1", "0123456789abcdef");
    static readonly DateTimeOffset ObservedAt = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTime ProviderOccurredAt = new(2026, 7, 30, 11, 59, 0, DateTimeKind.Utc);

    public static TheoryData<string, string?> MalformedRequiredImageProperties => new()
    {
        { nameof(CosmosObservationContainerDocument.Id), null },
        { nameof(CosmosObservationContainerDocument.Id), "   " },
        { nameof(CosmosObservationContainerDocument.PartitionKey), null },
        { nameof(CosmosObservationContainerDocument.PartitionKey), "   " },
        { nameof(CosmosObservationContainerDocument.ObservationId), null },
        { nameof(CosmosObservationContainerDocument.ObservationId), "   " }
    };

    [Fact]
    public async Task BaselineResume_RetainsProviderPageTailAcrossItemAndByteBounds()
    {
        var itemBound = await DrainBaselineAsync(
            maximumItems: 2,
            maximumBytes: GenerousPageBytes);

        var oneObservationBytes = StrictDocumentJson.GetCanonicalBytes(
            new RelationQuerySourceReadObservation("a", Shape, []),
            MaterializationJsonSerializer.CreateOptions()).LongLength;
        var byteBound = await DrainBaselineAsync(
            maximumItems: 10,
            maximumBytes: oneObservationBytes);

        Assert.Equal(new[] { "a", "b", "c", "d" }, itemBound.Identities);
        Assert.Equal(new string?[] { null, null, "provider/next" }, itemBound.ProviderContinuations);
        Assert.Equal(new[] { "a", "b", "c", "d" }, byteBound.Identities);
        Assert.Equal(new string?[] { null, null, null, "provider/next" }, byteBound.ProviderContinuations);
    }

    [Fact]
    public async Task BaselineContinuation_RejectsTamperingBeforeProviderIo()
    {
        var baseline = StandardBaseline();
        var fixture = CreateFixture(baseline);
        var first = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 1, GenerousPageBytes));
        var continuation = Assert.IsType<MaterializationSourceContinuation>(first.Continuation);
        MaterializationSourceContinuation tampered = new(
            continuation.FormatVersion,
            continuation.ReadFingerprint,
            continuation.Scope,
            Tamper(continuation.Value));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, tampered, maximumItems: 1, GenerousPageBytes)).AsTask());

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(baseline.Calls);
    }

    [Fact]
    public async Task BaselineReplay_IgnoresVolatileProviderEvidenceInSemanticPrefix()
    {
        var namePath = FieldPath.FromField("name");
        RelationQuerySourceFieldBinding name = new(
            new("field/name"),
            namePath,
            CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(namePath));
        BaselineFeedFactory baseline = new(
            new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
            {
                [BaselineFeedFactory.Initial] = new(
                    [
                        JsonDocument.Parse("""{"_identity":"a","_field0":"Alpha"}""").RootElement.Clone(),
                        JsonDocument.Parse("""{"_identity":"b","_field0":"Beta"}""").RootElement.Clone()
                    ],
                    ContinuationToken: null,
                    RequestCharge: 1)
            });
        var fixture = CreateFixture(baseline, placementFields: [name]);

        var first = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 1, GenerousPageBytes));
        var second = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(
                fixture,
                Assert.IsType<MaterializationSourceContinuation>(first.Continuation),
                maximumItems: 1,
                GenerousPageBytes));

        Assert.Equal("a", Assert.Single(first.Read.Observations).Identity);
        Assert.Equal("b", Assert.Single(second.Read.Observations).Identity);
        Assert.NotEqual(baseline.Calls[0].ActivityId, baseline.Calls[1].ActivityId);
        Assert.All(baseline.Calls, static call =>
            Assert.Equal(ConsistencyLevel.Strong, call.ConsistencyLevel));
    }

    [Fact]
    public async Task Baseline_ClampsToTheCanonicalRelationsBoundary()
    {
        var fixture = CreateFixture(StandardBaseline(), maximumRows: 2);

        var page = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 10, GenerousPageBytes));

        Assert.Equal(["a", "b"], page.Read.Observations.Select(static item => item.Identity));
        Assert.Equal(RelationQuerySourceReadState.Partial, page.Read.State);
        Assert.Equal(MaterializationSourcePageState.Exhausted, page.State);
        Assert.Null(page.Continuation);
        Assert.Contains(page.Diagnostics, static diagnostic =>
            diagnostic.Code == CosmosMaterializationSource.ReadBoundaryReachedDiagnosticCode);
    }

    [Fact]
    public async Task Baseline_ResumesAcrossAnEmptyProviderPage()
    {
        BaselineFeedFactory baseline = new(
            new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
            {
                [BaselineFeedFactory.Initial] = new([], "provider/next", 1),
                ["provider/next"] = new([Row("a")], ContinuationToken: null, 1)
            });
        var fixture = CreateFixture(baseline);

        var empty = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 10, GenerousPageBytes));
        var final = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, Assert.IsType<MaterializationSourceContinuation>(empty.Continuation), 10, GenerousPageBytes));

        Assert.Empty(empty.Read.Observations);
        Assert.Equal(MaterializationSourcePageState.MoreAvailable, empty.State);
        Assert.Equal("a", Assert.Single(final.Read.Observations).Identity);
        Assert.Equal(new string?[] { null, "provider/next" }, baseline.Calls.Select(static call => call.ContinuationToken));
    }

    [Fact]
    public async Task BatchedPointRead_UsesTheBoundedRelationsTraversalPath()
    {
        BaselineFeedFactory baseline = new(
            new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
            {
                [BaselineFeedFactory.Initial] = new(
                    [Row("fixture-identity")],
                    ContinuationToken: null,
                    RequestCharge: 2.25)
            });
        var fixture = CreateFixture(
            baseline,
            placementKind: RelationQuerySourcePlacementBindingKind.RelationshipTraversal);

        var page = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 10, GenerousPageBytes));

        Assert.Equal("fixture-identity", Assert.Single(page.Read.Observations).Identity);
        Assert.Equal(MaterializationSourcePageState.Exhausted, page.State);
        var pointRead = Assert.Single(
            fixture.Source.Descriptor.CapabilityProfile.Evidence,
            static evidence => evidence.Capability == MaterializationCapabilityKind.SourceBatchedPointRead);
        Assert.Equal(CapabilityRealizationKind.Composed, pointRead.Realization);
        Assert.All(baseline.Calls, static call =>
            Assert.Equal(ConsistencyLevel.Strong, call.ConsistencyLevel));
    }

    [Fact]
    public async Task Baseline_RejectsDuplicateIdentityAcrossProviderPages()
    {
        BaselineFeedFactory baseline = new(
            new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
            {
                [BaselineFeedFactory.Initial] = new([Row("a")], "provider/next", 1),
                ["provider/next"] = new([Row("a")], ContinuationToken: null, 1)
            });
        var fixture = CreateFixture(baseline);
        var first = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 10, GenerousPageBytes));

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadPageAsync(
                Context(),
                BaselineRequest(fixture, Assert.IsType<MaterializationSourceContinuation>(first.Continuation), 10, GenerousPageBytes)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ReplayConflict, exception.FailureKind);
    }

    [Fact]
    public async Task CaptureAndResume_UsesOpaqueCapturedProviderBoundary()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/current", HttpStatusCode.NotModified);
        changes.Pages["cut/current"] = ChangePage(
            [ProviderChange(CosmosMaterializationProviderChangeKind.Create, current: Document("load-a"), lsn: 10)],
            "cut/after-a",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);

        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);
        var page = await fixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(fixture, captured));

        Assert.StartsWith("cosmos-materialization-change/v2/", captured.Value, StringComparison.Ordinal);
        Assert.NotEqual(captured, page.ThroughPosition);
        Assert.Equal("load-a", Assert.Single(page.Deliveries).Change.SubjectIdentity);
        Assert.Equal(
            [CosmosMaterializationChangeFeedStartKind.Now, CosmosMaterializationChangeFeedStartKind.Continuation],
            changes.Calls.Select(static call => call.Start.Kind));
        Assert.Equal("cut/current", changes.Calls[1].Start.ContinuationToken);
    }

    [Fact]
    public void ChangeRead_RequiresAnExplicitCapturedCut_AndDoesNotAdvertiseRetainedHistory()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/current", HttpStatusCode.NotModified);
        var fixture = CreateFixture(StandardBaseline(), changes);

        Assert.Throws<ArgumentNullException>(() => new MaterializationChangeReadRequest(
            fixture.Source.Scope,
            afterPosition: null!,
            maximumDeliveries: 10,
            maximumBytes: GenerousPageBytes));

        Assert.False((object)fixture.Source is IMaterializationRetainedChangeSource);
        var change = Assert.Single(fixture.Source.Descriptor.CapabilityProfile.Evidence, static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.DoesNotContain(MaterializationGuaranteeKind.RetainedHistoryStart, change.Guarantees);
        Assert.Empty(changes.Calls);
    }

    [Fact]
    public async Task ProviderContinuationEvolution_RemainsOpaqueAcrossPartitionSplit()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/parent-range", HttpStatusCode.NotModified);
        changes.Pages["cut/parent-range"] = ChangePage(
            [ProviderChange(CosmosMaterializationProviderChangeKind.Create, current: Document("load-a"), lsn: 30)],
            "cut/child-ranges",
            HttpStatusCode.OK);
        changes.Pages["cut/child-ranges"] = ChangePage([], "cut/children-current", HttpStatusCode.NotModified);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var splitPage = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));
        var caughtUp = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, splitPage.ThroughPosition));

        Assert.Equal(MaterializationChangePageState.MoreAvailable, splitPage.State);
        Assert.Equal(MaterializationChangePageState.CaughtUp, caughtUp.State);
        Assert.Equal(
            ["cut/parent-range", "cut/child-ranges"],
            changes.Calls.Skip(1).Select(static call => call.Start.ContinuationToken));
    }

    [Fact]
    public async Task ReplayingPosition_ProducesStableChangeAndDeliveryIdentities()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/replay", HttpStatusCode.NotModified);
        changes.Pages["cut/replay"] = ChangePage(
            [ProviderChange(CosmosMaterializationProviderChangeKind.Create, current: Document("load-a"), lsn: 41)],
            "cut/replay-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var first = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));
        var replay = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));

        var firstDelivery = Assert.Single(first.Deliveries);
        var replayDelivery = Assert.Single(replay.Deliveries);
        Assert.Equal(firstDelivery.Id, replayDelivery.Id);
        Assert.Equal(firstDelivery.Change.Id, replayDelivery.Change.Id);
        Assert.Equal(firstDelivery.Change.Position, replayDelivery.Change.Position);
    }

    [Fact]
    public async Task EquivalentTransactionalProviderOrders_UseOneCanonicalDeliveryOrder()
    {
        var firstProviderOrder = new FakeChangeFeedReader
        {
            Current = ChangePage([], "cut/canonical-order", HttpStatusCode.NotModified)
        };
        firstProviderOrder.Pages["cut/canonical-order"] = ChangePage(
            [
                ProviderChange(CosmosMaterializationProviderChangeKind.Create, Document("load-b"), lsn: 50),
                ProviderChange(CosmosMaterializationProviderChangeKind.Create, Document("load-a"), lsn: 50)
            ],
            "cut/canonical-next",
            HttpStatusCode.OK);
        var secondProviderOrder = new FakeChangeFeedReader
        {
            Current = ChangePage([], "cut/canonical-order", HttpStatusCode.NotModified)
        };
        secondProviderOrder.Pages["cut/canonical-order"] = ChangePage(
            [
                ProviderChange(CosmosMaterializationProviderChangeKind.Create, Document("load-a"), lsn: 50),
                ProviderChange(CosmosMaterializationProviderChangeKind.Create, Document("load-b"), lsn: 50)
            ],
            "cut/canonical-next",
            HttpStatusCode.OK);
        var first = CreateFixture(StandardBaseline(), firstProviderOrder);
        var second = CreateFixture(StandardBaseline(), secondProviderOrder);
        var firstCut = await first.Source.CaptureCurrentPositionAsync(Context(), first.Source.Scope);
        var secondCut = await second.Source.CaptureCurrentPositionAsync(Context(), second.Source.Scope);

        var firstPage = await first.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(first, firstCut, maximumDeliveries: 1));
        var secondPage = await second.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(second, secondCut, maximumDeliveries: 1));

        Assert.Equal("load-a", Assert.Single(firstPage.Deliveries).Change.SubjectIdentity);
        Assert.Equal(
            firstPage.Deliveries.Select(static delivery => delivery.Id),
            secondPage.Deliveries.Select(static delivery => delivery.Id));
        Assert.Equal(
            firstPage.Deliveries.Select(static delivery => delivery.Change.Id),
            secondPage.Deliveries.Select(static delivery => delivery.Change.Id));
        Assert.Equal(firstPage.ThroughPosition, secondPage.ThroughPosition);
    }

    [Fact]
    public async Task ChangeIdentity_IsIndependentOfCapabilityProfilePolicy()
    {
        static FakeChangeFeedReader Changes()
        {
            FakeChangeFeedReader changes = new()
            {
                Current = ChangePage([], "cut/profile-independent", HttpStatusCode.NotModified)
            };
            changes.Pages["cut/profile-independent"] = ChangePage(
                [ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a"),
                    lsn: 55)],
                "cut/profile-independent-next",
                HttpStatusCode.OK);
            return changes;
        }

        var first = CreateFixture(
            baseline: StandardBaseline(),
            changes: Changes(),
            maximumContainerParallelism: 1);
        var second = CreateFixture(
            baseline: StandardBaseline(),
            changes: Changes(),
            maximumContainerParallelism: 2);

        Assert.Equal(first.Source.Scope, second.Source.Scope);
        Assert.NotEqual(
            first.Source.Descriptor.CapabilityProfile.Id,
            second.Source.Descriptor.CapabilityProfile.Id);

        var firstCut = await first.Source.CaptureCurrentPositionAsync(Context(), first.Source.Scope);
        var secondCut = await second.Source.CaptureCurrentPositionAsync(Context(), second.Source.Scope);
        var firstDelivery = Assert.Single((await first.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(first, firstCut))).Deliveries);
        var secondDelivery = Assert.Single((await second.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(second, secondCut))).Deliveries);

        Assert.Equal(firstDelivery.Change.Id, secondDelivery.Change.Id);
        Assert.Equal(firstDelivery.Id, secondDelivery.Id);
    }

    [Fact]
    public async Task SemanticDocumentBinding_IsPartOfSourceScopeAndStableIdentity()
    {
        const string AlternateDocumentKind = "cohesive.tests.cosmos/alternate-entity/v1";

        static FakeChangeFeedReader Changes(string documentKind)
        {
            FakeChangeFeedReader changes = new()
            {
                Current = ChangePage([], "cut/semantic-binding", HttpStatusCode.NotModified)
            };
            changes.Pages["cut/semantic-binding"] = ChangePage(
                [ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a", documentKind: documentKind),
                    lsn: 56)],
                "cut/semantic-binding-next",
                HttpStatusCode.OK);
            return changes;
        }

        var first = CreateFixture(
            baseline: StandardBaseline(),
            changes: Changes(CosmosRelationQuerySourceReader.DefaultEntityDocumentKind));
        var second = CreateFixture(
            baseline: StandardBaseline(),
            changes: Changes(AlternateDocumentKind),
            entityDocumentKind: AlternateDocumentKind);

        Assert.NotEqual(first.Source.Scope, second.Source.Scope);
        Assert.NotEqual(
            first.Source.Descriptor.CapabilityProfile.Id,
            second.Source.Descriptor.CapabilityProfile.Id);

        var firstCut = await first.Source.CaptureCurrentPositionAsync(Context(), first.Source.Scope);
        var secondCut = await second.Source.CaptureCurrentPositionAsync(Context(), second.Source.Scope);
        var firstDelivery = Assert.Single((await first.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(first, firstCut))).Deliveries);
        var secondDelivery = Assert.Single((await second.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(second, secondCut))).Deliveries);

        Assert.NotEqual(firstDelivery.Change.Id, secondDelivery.Change.Id);
        Assert.NotEqual(firstDelivery.Id, secondDelivery.Id);
    }

    [Fact]
    public async Task DistinctSameSubjectChangesInOneTransactionalBatch_HaveDistinctDeliveryIdentities()
    {
        var statePath = FieldPath.FromField("state");
        RelationQuerySourceFieldBinding state = new(
            new("field/state"),
            statePath,
            CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(statePath));
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/same-subject", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/same-subject"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: VersionedDocument("load-a", version: 3),
                    previous: VersionedDocument("load-a", version: 2),
                    lsn: 51),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: VersionedDocument("load-a", version: 2),
                    previous: VersionedDocument("load-a", version: 1),
                    lsn: 51)
            ],
            "cut/same-subject-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes, placementFields: [state]);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut));

        Assert.Equal(2, page.Deliveries.Length);
        Assert.Equal(2, page.Deliveries.Select(static delivery => delivery.Id).Distinct().Count());
        Assert.All(page.Deliveries, static delivery =>
            Assert.Equal(MaterializationChangeKind.Update, delivery.Change.Kind));
        Assert.Equal(
            ["v2", "v3"],
            page.Deliveries.Select(delivery => Assert.Single(
                Assert.IsType<RelationQuerySourceReadObservation>(delivery.Change.After).Fields).Value!.Value.String));

        static CosmosObservationContainerDocument VersionedDocument(string identity, long version) => Document(
            identity,
            version: version,
            observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["state"] = ObservationValue.FromString($"v{version}")
            });
    }

    [Fact]
    public async Task AmbiguousSameItemTransactionOrder_FailsClosed()
    {
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/ambiguous-order", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/ambiguous-order"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: Document("load-a", version: 2),
                    previous: Document("load-a", version: 1),
                    lsn: 52),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: Document("load-a", version: 2),
                    previous: Document("load-a", version: 1),
                    lsn: 52)
            ],
            "cut/ambiguous-order-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
        Assert.Contains("unique transition order", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteThenRecreateWithResetVersion_UsesPreviousCurrentImageChain()
    {
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/delete-recreate", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/delete-recreate"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a", version: 1),
                    lsn: 53),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Delete,
                    previous: Document("load-a", version: 5),
                    lsn: 53)
            ],
            "cut/delete-recreate-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut));

        Assert.Equal(
            [MaterializationChangeKind.Delete, MaterializationChangeKind.Create],
            page.Deliveries.Select(static delivery => delivery.Change.Kind));
    }

    [Fact]
    public async Task DistinctPhysicalItemsForOneTransactionalSubject_FailClosed()
    {
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/cross-item-subject", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/cross-item-subject"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a", version: 1) with { Id = "entity/a" },
                    lsn: 54),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Delete,
                    previous: Document("load-a", version: 5) with { Id = "entity/z" },
                    lsn: 54)
            ],
            "cut/cross-item-subject-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
        Assert.Contains("same semantic observation identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ObservationEnvelopeBoundary_MapsReplaceToDeleteAndCreateLikeBaselineMembership()
    {
        var valid = Document("load-a");
        var outside = valid with { Observation = null };
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/envelope-boundary", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/envelope-boundary"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: outside with { ObservationVersion = 2 },
                    previous: valid,
                    lsn: 52),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: valid with { ObservationVersion = 3 },
                    previous: outside with { ObservationVersion = 2 },
                    lsn: 53)
            ],
            "cut/envelope-boundary-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut));

        Assert.Equal(
            [MaterializationChangeKind.Delete, MaterializationChangeKind.Create],
            page.Deliveries.Select(static delivery => delivery.Change.Kind));
    }

    [Fact]
    public async Task UnrelatedDocumentWithoutCohesiveDiscriminators_DoesNotPoisonTheFeed()
    {
        var unrelated = Document("unrelated") with
        {
            DocumentKind = null!,
            ObservationType = null!,
            ObservationId = null!,
            Observation = null
        };
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/unrelated", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/unrelated"] = ChangePage(
            [ProviderChange(CosmosMaterializationProviderChangeKind.Create, unrelated, lsn: 54)],
            "cut/unrelated-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut));

        Assert.Empty(page.Deliveries);
        Assert.Equal(MaterializationChangePageState.Progressed, page.State);
    }

    [Fact]
    public async Task ChangeResume_RetainsFilteredProviderPageTailAcrossItemAndByteBounds()
    {
        var itemChanges = BoundedChangeFeed();
        var itemFixture = CreateFixture(StandardBaseline(), itemChanges);
        var itemCut = await itemFixture.Source.CaptureCurrentPositionAsync(
            Context(),
            itemFixture.Source.Scope);

        var itemDrain = await DrainChangesAsync(
            itemFixture,
            itemCut,
            maximumDeliveries: 1,
            maximumBytes: GenerousPageBytes);

        Assert.Equal(new[] { "load-a", "load-b", "load-c" }, itemDrain.Identities);
        Assert.Equal(new[] { 1, 1, 1, 0 }, itemDrain.PageDeliveryCounts);
        Assert.Equal(
            new[] { "cut/bounded-page", "cut/bounded-page", "cut/bounded-page", "cut/bounded-next" },
            itemChanges.Calls.Skip(1).Select(static call => call.Start.ContinuationToken));
        Assert.All(itemChanges.Calls.Skip(1), static call => Assert.Equal(1, call.PageSizeHint));

        var byteChanges = BoundedChangeFeed();
        var byteFixture = CreateFixture(StandardBaseline(), byteChanges);
        var byteCut = await byteFixture.Source.CaptureCurrentPositionAsync(
            Context(),
            byteFixture.Source.Scope);
        var unbounded = await byteFixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(byteFixture, byteCut));
        var oneDeliveryBytes = StrictDocumentJson.GetCanonicalBytes(
            unbounded.Deliveries[0],
            MaterializationJsonSerializer.CreateOptions()).LongLength;

        var byteDrain = await DrainChangesAsync(
            byteFixture,
            byteCut,
            maximumDeliveries: 10,
            maximumBytes: oneDeliveryBytes);

        Assert.Equal(new[] { "load-a", "load-b", "load-c" }, byteDrain.Identities);
        Assert.Equal(new[] { 1, 1, 1, 0 }, byteDrain.PageDeliveryCounts);
    }

    [Fact]
    public async Task ChangePosition_RejectsTamperingBeforeProviderIo()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/tamper", HttpStatusCode.NotModified);
        changes.Pages["cut/tamper"] = ChangePage([], "cut/tamper-next", HttpStatusCode.NotModified);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);
        MaterializationSourcePosition tampered = new(
            captured.FormatVersion,
            captured.Scope,
            Tamper(captured.Value));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(fixture, tampered)).AsTask());

        Assert.Contains("authentication", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(changes.Calls);
    }

    [Fact]
    public async Task LegacyV1ChangePosition_IsRejectedBeforeProviderIo()
    {
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/legacy-position", HttpStatusCode.NotModified)
        };
        var fixture = CreateFixture(
            baseline: StandardBaseline(),
            changes: changes);
        MaterializationSourcePosition legacy = new(
            formatVersion: 1,
            scope: fixture.Source.Scope,
            value: "cosmos-materialization-change/v1/obsolete.invalid");

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => fixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(fixture, legacy)).AsTask());

        Assert.Contains("format", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(changes.Calls);
    }

    [Fact]
    public async Task IntraPageProviderResegmentation_FailsClosedAndRequiresNewGenerationRecovery()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/resegmented", HttpStatusCode.NotModified);
        changes.Pages["cut/resegmented"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("filtered", observationType: "Other"),
                    lsn: 91),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a"),
                    lsn: 92),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-b"),
                    lsn: 93)
            ],
            "cut/child-ranges",
            HttpStatusCode.OK);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);
        var partial = await fixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(fixture, captured, maximumDeliveries: 1));

        changes.Pages["cut/resegmented"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("different-filtered", observationType: "Other"),
                    lsn: 94),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a"),
                    lsn: 92),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-b"),
                    lsn: 93)
            ],
            "cut/child-ranges",
            HttpStatusCode.OK);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(
                Context(),
                ChangeRequest(fixture, partial.ThroughPosition, maximumDeliveries: 1)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ReplayConflict, exception.FailureKind);
        Assert.Equal(3.5, exception.Observation.RequestCharge);
        Assert.Equal(HttpStatusCode.OK, exception.Observation.StatusCode);
        Assert.Contains("failed closed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            new[] { "cut/resegmented", "cut/resegmented" },
            changes.Calls.Skip(1).Select(static call => call.Start.ContinuationToken));
        Assert.Contains(observer.Observations, static observation =>
            observation.Operation == CosmosMaterializationSourceOperationKind.ChangeRead
            && observation.Disposition == CosmosMaterializationSourceDisposition.TerminalFailure);
        // A changed consumed prefix has no trustworthy resume. Orchestration must abandon the candidate and start a
        // new generation from a freshly captured cut.
    }

    [Fact]
    public async Task SharedAdmission_QueuesSameScopeAndQueuedCancellationEmitsTypedEvidence()
    {
        using CosmosMaterializationAdmissionIndex admissionIndex = new();
        FakeChangeFeedReader firstChanges = new();
        firstChanges.Current = ChangePage([], "cut/held", HttpStatusCode.NotModified);
        firstChanges.Pages["cut/held"] = ChangePage([], "cut/held-next", HttpStatusCode.NotModified);
        FakeChangeFeedReader secondChanges = new();
        secondChanges.Current = ChangePage([], "cut/queued", HttpStatusCode.NotModified);
        secondChanges.Pages["cut/queued"] = ChangePage([], "cut/queued-next", HttpStatusCode.NotModified);
        RecordingObserver secondObserver = new();
        var firstFixture = CreateFixture(
            StandardBaseline(),
            firstChanges,
            admissionIndex: admissionIndex);
        var secondFixture = CreateFixture(
            StandardBaseline(),
            secondChanges,
            secondObserver,
            admissionIndex: admissionIndex);
        var firstCut = await firstFixture.Source.CaptureCurrentPositionAsync(
            Context(),
            firstFixture.Source.Scope);
        var secondCut = await secondFixture.Source.CaptureCurrentPositionAsync(
            Context(),
            secondFixture.Source.Scope);
        TaskCompletionSource<bool> firstEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        firstChanges.Handler = async (start, _, _, token) =>
        {
            firstEntered.TrySetResult(true);
            await releaseFirst.Task.WaitAsync(token);
            return firstChanges.Pages[start.ContinuationToken!];
        };

        var firstRead = firstFixture.Source.ReadChangesAsync(
            Context(),
            ChangeRequest(firstFixture, firstCut)).AsTask();
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using CancellationTokenSource cancellation = new();
        var queuedRead = secondFixture.Source.ReadChangesAsync(
            Context(cancellation.Token),
            ChangeRequest(secondFixture, secondCut)).AsTask();
        await Task.Yield();
        Assert.False(queuedRead.IsCompleted);

        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queuedRead);
        Assert.Single(secondChanges.Calls);
        Assert.Contains(secondObserver.Observations, static observation =>
            observation.Operation == CosmosMaterializationSourceOperationKind.ChangeRead
            && observation.Disposition == CosmosMaterializationSourceDisposition.Canceled);

        releaseFirst.TrySetResult(true);
        var completed = await firstRead;
        Assert.Equal(MaterializationChangePageState.CaughtUp, completed.State);
    }

    [Fact]
    public async Task ProviderThrottle_IsTypedWithStatusRequestChargeAndRetryAfterEvidence()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/throttle", HttpStatusCode.NotModified);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);
        CosmosException providerFailure = new(
            "provider response must remain sanitized",
            HttpStatusCode.TooManyRequests,
            subStatusCode: 3200,
            activityId: "provider-activity-must-not-leak",
            requestCharge: 9.75);
        providerFailure.Headers.Add("x-ms-retry-after-ms", "250");
        changes.Handler = (_, _, _, _) =>
            ValueTask.FromException<CosmosMaterializationProviderChangePage>(providerFailure);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.Throttled, exception.FailureKind);
        Assert.Equal(CosmosMaterializationSourceDisposition.Throttled, exception.Observation.Disposition);
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.Observation.StatusCode);
        Assert.Equal(3200, exception.Observation.SubStatusCode);
        Assert.Equal(9.75, exception.Observation.RequestCharge);
        Assert.Equal(TimeSpan.FromMilliseconds(250), exception.Observation.RetryAfter);
        Assert.DoesNotContain("provider response", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider-activity", exception.Observation.EvidenceReference, StringComparison.Ordinal);
        Assert.Same(exception.Observation, observer.Observations[^1]);
    }

    [Fact]
    public async Task NonThrowingInvalidBaselineStatus_BecomesTypedProviderProtocolEvidence()
    {
        BaselineFeedFactory baseline = new(
            new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
            {
                [BaselineFeedFactory.Initial] = new(
                    [],
                    ContinuationToken: null,
                    RequestCharge: 4.25,
                    StatusCode: HttpStatusCode.InternalServerError)
            });
        RecordingObserver observer = new();
        var fixture = CreateFixture(baseline, observer: observer);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadPageAsync(
                Context(),
                BaselineRequest(fixture, continuation: null, maximumItems: 10, GenerousPageBytes)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.Transient, exception.FailureKind);
        Assert.Equal(CosmosMaterializationSourceDisposition.RetryableFailure, exception.Observation.Disposition);
        Assert.Equal(HttpStatusCode.InternalServerError, exception.Observation.StatusCode);
        Assert.Equal(4.25, exception.Observation.RequestCharge);
        Assert.Contains("provider-protocol", exception.Observation.EvidenceReference, StringComparison.Ordinal);
        Assert.Same(exception.Observation, observer.Observations[^1]);
    }

    [Fact]
    public async Task CancellationAfterCompletedChangeResponse_RetainsProviderEvidence()
    {
        using CancellationTokenSource cancellation = new();
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/post-response-cancel", HttpStatusCode.NotModified);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);
        changes.Handler = (_, _, _, _) =>
        {
            cancellation.Cancel();
            return ValueTask.FromResult(ChangePage(
                [ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a"),
                    lsn: 70)],
                "cut/post-response-cancel-next",
                HttpStatusCode.OK));
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.Source.ReadChangesAsync(
                Context(cancellation.Token),
                ChangeRequest(fixture, cut)).AsTask());

        var observed = observer.Observations[^1];
        Assert.Equal(CosmosMaterializationSourceDisposition.Canceled, observed.Disposition);
        Assert.Equal(HttpStatusCode.OK, observed.StatusCode);
        Assert.Equal(3.5, observed.RequestCharge);
        Assert.Contains("tests/cosmos-provider-evidence", observed.EvidenceReference, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilteredProviderPage_AdvancesAsProgressedWithoutInventingDelivery()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/filter", HttpStatusCode.NotModified);
        changes.Pages["cut/filter"] = ChangePage(
            [ProviderChange(
                CosmosMaterializationProviderChangeKind.Create,
                current: Document("other", observationType: "Other"),
                lsn: 52)],
            "cut/filter-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));

        Assert.Equal(MaterializationChangePageState.Progressed, page.State);
        Assert.Empty(page.Deliveries);
        Assert.NotEqual(captured, page.ThroughPosition);
    }

    [Fact]
    public async Task NotModifiedProviderPage_IsCaughtUpAtReturnedPosition()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/caught-up", HttpStatusCode.NotModified);
        changes.Pages["cut/caught-up"] = ChangePage([], "cut/still-caught-up", HttpStatusCode.NotModified);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));

        Assert.Equal(MaterializationChangePageState.CaughtUp, page.State);
        Assert.Empty(page.Deliveries);
        Assert.NotEqual(captured, page.ThroughPosition);
    }

    [Fact]
    public async Task FullFidelityImages_MapCreateUpdateAndDeleteSemantics()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/changes", HttpStatusCode.NotModified);
        changes.Pages["cut/changes"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("created"),
                    lsn: 61),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: Document("updated", version: 2),
                    previous: Document("updated", version: 1),
                    lsn: 62),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Delete,
                    previous: Document("deleted"),
                    lsn: 63)
            ],
            "cut/changes-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));

        Assert.Equal(
            [MaterializationChangeKind.Create, MaterializationChangeKind.Update, MaterializationChangeKind.Delete],
            page.Deliveries.Select(static delivery => delivery.Change.Kind));
        Assert.Null(page.Deliveries[0].Change.Before);
        Assert.NotNull(page.Deliveries[0].Change.After);
        Assert.NotNull(page.Deliveries[1].Change.Before);
        Assert.NotNull(page.Deliveries[1].Change.After);
        Assert.NotNull(page.Deliveries[2].Change.Before);
        Assert.Null(page.Deliveries[2].Change.After);
        Assert.Equal(["created", "updated", "deleted"],
            page.Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
    }

    [Fact]
    public async Task FullFidelityImages_ProjectCanonicalSelectedFieldsLikeBaselineReads()
    {
        var namePath = FieldPath.FromField("name");
        var cityPath = new FieldPath(
            [FieldPathSegment.ForField("address"), FieldPathSegment.ForField("city")]);
        var nullablePath = FieldPath.FromField("note");
        var missingPath = FieldPath.FromField("missing");
        ImmutableArray<RelationQuerySourceFieldBinding> fields =
        [
            Field("field/name", namePath),
            Field("field/city", cityPath),
            Field("field/note", nullablePath),
            Field("field/missing", missingPath)
        ];
        Dictionary<string, ObservationValue> observation = new(StringComparer.Ordinal)
        {
            ["name"] = ObservationValue.FromString("Alpha"),
            ["address"] = ObservationValue.FromObject(new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
            {
                ["city"] = ObservationValue.FromString("Seattle")
            }),
            ["note"] = ObservationValue.Null
        };
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/project-fields", HttpStatusCode.NotModified);
        changes.Pages["cut/project-fields"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("created", observation: observation),
                    lsn: 64),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Replace,
                    current: Document("updated", version: 2, observation: observation),
                    previous: Document("updated", version: 1, observation: observation),
                    lsn: 65)
            ],
            "cut/project-fields-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes, placementFields: fields);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured));

        foreach (var delivery in page.Deliveries)
        {
            var after = Assert.IsType<RelationQuerySourceReadObservation>(delivery.Change.After);
            Assert.Equal(fields.Length, after.Fields.Length);
            AssertField(after, namePath, RelationQuerySourceReadFieldState.Value, ObservationValue.FromString("Alpha"));
            AssertField(after, cityPath, RelationQuerySourceReadFieldState.Value, ObservationValue.FromString("Seattle"));
            AssertField(after, nullablePath, RelationQuerySourceReadFieldState.Null);
            AssertField(after, missingPath, RelationQuerySourceReadFieldState.Missing);
        }

        static RelationQuerySourceFieldBinding Field(string input, FieldPath path) => new(
            new(input),
            path,
            CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(path));

        static void AssertField(
            RelationQuerySourceReadObservation observation,
            FieldPath path,
            RelationQuerySourceReadFieldState state,
            ObservationValue? value = null)
        {
            var field = Assert.Single(observation.Fields, result => result.Field.SemanticPath == path);
            Assert.Equal(state, field.State);
            Assert.Equal(value, field.Value);
        }
    }

    [Theory]
    [InlineData((int)CosmosMaterializationProviderChangeKind.Replace)]
    [InlineData((int)CosmosMaterializationProviderChangeKind.Delete)]
    public async Task MissingPreviousImage_IsTypedTerminalEvidenceFailure(int operationValue)
    {
        var operation = (CosmosMaterializationProviderChangeKind)operationValue;
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/missing-previous", HttpStatusCode.NotModified);
        changes.Pages["cut/missing-previous"] = ChangePage(
            [ProviderChange(operation, current: operation == CosmosMaterializationProviderChangeKind.Replace
                ? Document("load-a")
                : null, lsn: 71)],
            "cut/missing-previous-next",
            HttpStatusCode.OK);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
        Assert.Equal(CosmosMaterializationSourceDisposition.TerminalFailure, exception.Observation.Disposition);
        Assert.Equal(3.5, exception.Observation.RequestCharge);
        Assert.Equal(HttpStatusCode.OK, exception.Observation.StatusCode);
        Assert.Contains(observer.Observations, static observation =>
            observation.Operation == CosmosMaterializationSourceOperationKind.ChangeRead
            && observation.Disposition == CosmosMaterializationSourceDisposition.TerminalFailure);
    }

    [Theory]
    [InlineData((int)CosmosMaterializationProviderChangeKind.Create)]
    [InlineData((int)CosmosMaterializationProviderChangeKind.Replace)]
    public async Task MissingCurrentImage_IsTypedTerminalEvidenceFailure(int operationValue)
    {
        var operation = (CosmosMaterializationProviderChangeKind)operationValue;
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/missing-current", HttpStatusCode.NotModified);
        changes.Pages["cut/missing-current"] = ChangePage(
            [ProviderChange(
                operation,
                current: null,
                previous: operation == CosmosMaterializationProviderChangeKind.Replace ? Document("load-a") : null,
                lsn: 72)],
            "cut/missing-current-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(StandardBaseline(), changes);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
    }

    [Theory]
    [MemberData(nameof(MalformedRequiredImageProperties))]
    public async Task MalformedRequiredCurrentAndPreviousImageProperties_AreTypedEvidenceFailures(
        string property,
        string? value)
    {
        CosmosMaterializationProviderChangeKind[] operations =
        [
            CosmosMaterializationProviderChangeKind.Create,
            CosmosMaterializationProviderChangeKind.Delete
        ];
        foreach (var operation in operations)
        {
            var malformed = MalformedDocument(property, value);
            FakeChangeFeedReader changes = new();
            changes.Current = ChangePage([], $"cut/malformed-{operation}", HttpStatusCode.NotModified);
            changes.Pages[$"cut/malformed-{operation}"] = ChangePage(
                [ProviderChange(
                    operation,
                    current: operation == CosmosMaterializationProviderChangeKind.Create ? malformed : null,
                    previous: operation == CosmosMaterializationProviderChangeKind.Delete ? malformed : null,
                    lsn: 73)],
                $"cut/malformed-{operation}-next",
                HttpStatusCode.OK);
            RecordingObserver observer = new();
            var fixture = CreateFixture(StandardBaseline(), changes, observer);
            var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

            var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
                fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

            Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
            Assert.Equal(CosmosMaterializationSourceDisposition.TerminalFailure, exception.Observation.Disposition);
            Assert.Same(exception.Observation, observer.Observations[^1]);
        }
    }

    [Theory]
    [InlineData(nameof(CosmosObservationContainerDocument.Id))]
    [InlineData(nameof(CosmosObservationContainerDocument.PartitionKey))]
    public async Task DiscriminatorBoundaryReplace_WithPhysicalIdentityMismatch_IsTypedEvidenceFailure(
        string physicalProperty)
    {
        var current = Document("load-a", observationType: "Other");
        current = physicalProperty switch
        {
            nameof(CosmosObservationContainerDocument.Id) => current with { Id = "entity/other" },
            nameof(CosmosObservationContainerDocument.PartitionKey) => current with { PartitionKey = "tenant-b" },
            _ => throw new ArgumentOutOfRangeException(nameof(physicalProperty), physicalProperty, null)
        };
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/boundary-physical-mismatch", HttpStatusCode.NotModified);
        changes.Pages["cut/boundary-physical-mismatch"] = ChangePage(
            [ProviderChange(
                CosmosMaterializationProviderChangeKind.Replace,
                current,
                previous: Document("load-a"),
                lsn: 74)],
            "cut/boundary-physical-mismatch-next",
            HttpStatusCode.OK);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
        Assert.Equal(CosmosMaterializationSourceDisposition.TerminalFailure, exception.Observation.Disposition);
        Assert.Same(exception.Observation, observer.Observations[^1]);
    }

    [Fact]
    public async Task InScopeReplace_WithSemanticIdentityMismatch_IsTypedEvidenceFailure()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/semantic-mismatch", HttpStatusCode.NotModified);
        changes.Pages["cut/semantic-mismatch"] = ChangePage(
            [ProviderChange(
                CosmosMaterializationProviderChangeKind.Replace,
                current: Document("load-a") with { ObservationId = "load-b" },
                previous: Document("load-a"),
                lsn: 75)],
            "cut/semantic-mismatch-next",
            HttpStatusCode.OK);
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var exception = await Assert.ThrowsAsync<CosmosMaterializationSourceException>(() =>
            fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, captured)).AsTask());

        Assert.Equal(CosmosMaterializationFailureKind.ChangeEvidenceUnavailable, exception.FailureKind);
        Assert.Equal(CosmosMaterializationSourceDisposition.TerminalFailure, exception.Observation.Disposition);
        Assert.Same(exception.Observation, observer.Observations[^1]);
    }

    [Fact]
    public async Task Cancellation_IsObservedWithoutReturningPartialProgress()
    {
        using CancellationTokenSource cancellation = new();
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/cancel", HttpStatusCode.NotModified);
        changes.Handler = (start, _, _, token) =>
        {
            if (start.Kind == CosmosMaterializationChangeFeedStartKind.Continuation)
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(changes.Current);
        };
        RecordingObserver observer = new();
        var fixture = CreateFixture(StandardBaseline(), changes, observer);
        var captured = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Source.ReadChangesAsync(
            Context(cancellation.Token),
            ChangeRequest(fixture, captured)).AsTask());

        Assert.Contains(observer.Observations, static observation =>
            observation.Operation == CosmosMaterializationSourceOperationKind.ChangeRead
            && observation.Disposition == CosmosMaterializationSourceDisposition.Canceled);
    }

    [Fact]
    public async Task CapabilityProfileOmitsUnsupportedGuarantees_AndPartialReadEmitsTypedControlEvidence()
    {
        var baseline = StandardBaseline(requestCharge: 7.25);
        RecordingObserver observer = new();
        var fixture = CreateFixture(baseline, observer: observer);
        var profile = fixture.Source.Descriptor.CapabilityProfile;

        Assert.DoesNotContain(profile.Evidence, static evidence =>
            evidence.Capability == MaterializationCapabilityKind.SourceSettlement);
        var guarantees = profile.Evidence.SelectMany(static evidence => evidence.Guarantees).ToArray();
        Assert.DoesNotContain(MaterializationGuaranteeKind.CoordinatedSnapshot, guarantees);
        Assert.DoesNotContain(MaterializationGuaranteeKind.ExplicitSettlement, guarantees);
        Assert.Contains(MaterializationGuaranteeKind.BeforeImage, guarantees);

        var page = await fixture.Source.ReadPageAsync(
            Context(),
            BaselineRequest(fixture, continuation: null, maximumItems: 1, GenerousPageBytes));

        Assert.Equal(MaterializationSourcePageState.MoreAvailable, page.State);
        var observation = Assert.Single(observer.Observations);
        Assert.Equal(CosmosMaterializationSourceOperationKind.BaselineRead, observation.Operation);
        Assert.Equal(CosmosMaterializationSourceDisposition.Partial, observation.Disposition);
        Assert.Equal(7.25, observation.RequestCharge);
        Assert.Equal(HttpStatusCode.OK, observation.StatusCode);
        Assert.Contains(observation.Measurements, static measurement =>
            measurement.Metric == ControlMetricKind.Latency
            && measurement.Statistic == ControlStatisticKind.Last
            && measurement.Availability == ControlMeasurementAvailability.Available);
        Assert.Contains(observation.Measurements, static measurement =>
            measurement.Metric == ControlMetricKind.RejectionRatio
            && measurement.Value is { Value: 0, Unit: ControlUnit.BasisPoints });
    }

    [Fact]
    public void WholeContainerSource_IsRejectedUntilCompositePerRangePositionsExist()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            CreateFixture(StandardBaseline(), fixedPartition: false));

        Assert.Contains("fixed logical partition", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomSemanticIdentitySelector_IsRejectedUntilChangeImagesCanProjectIt()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            identitySourceSelector: "id"));

        Assert.Contains("observationId identity selector", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomPartitionSelector_IsRejectedUntilChangeImagesCanProjectIt()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            partitionSourceSelector: "tenant.partition"));

        Assert.Contains("partitionKey selector", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData((int)ConsistencyLevel.Eventual)]
    [InlineData((int)ConsistencyLevel.BoundedStaleness)]
    [InlineData((int)ConsistencyLevel.Session)]
    [InlineData((int)ConsistencyLevel.ConsistentPrefix)]
    public void BaselineWithoutStrongReads_IsRejected(int? consistencyValue)
    {
        var consistency = consistencyValue is null
            ? null
            : (ConsistencyLevel?)consistencyValue.Value;

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            readConsistencyLevel: consistency));

        Assert.Contains("Strong", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacementWithoutIdentityAttribution_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            includePlacementIdentity: false));

        Assert.Contains("identity selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlacementIdentitySelectorMismatch_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            placementIdentitySourceSelector: "id"));

        Assert.Contains("identity selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlacementPartitionSelectorMismatch_IsRejected()
    {
        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            placementPartitionSourceSelector: "otherPartition"));

        Assert.Contains("partition selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlacementFieldSelectorMismatch_IsRejected()
    {
        RelationQuerySourceFieldBinding mismatched = new(
            new("field/name"),
            FieldPath.FromField("name"),
            "wrong.selector");

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            placementFields: [mismatched]));

        Assert.Contains("field selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MatchingCustomFieldSelectorOutsideObservationEnvelope_IsRejected()
    {
        var path = FieldPath.FromField("name");
        RelationQuerySourceFieldBinding custom = new(new("field/name"), path, "payload.name");

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            placementFields: [custom],
            fieldSourceSelector: static semanticPath => $"payload.{semanticPath}"));

        Assert.Contains("canonical observation envelope", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlacementRelationshipKeySelectorMismatch_IsRejected()
    {
        RelationQueryRelationshipKeyBinding mismatched = new(
            new("relationship/customer"),
            FieldPath.FromField("customerId"),
            "wrong.selector");

        var exception = Assert.Throws<ArgumentException>(() => CreateFixture(
            StandardBaseline(),
            placementRelationshipKeys: [mismatched]));

        Assert.Contains("relationship-key selector", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RelationshipTraversalCatchUp_ProjectsBeforeAndAfterCorrelationKeys()
    {
        var path = FieldPath.FromField("customerId");
        RelationQueryRelationshipKeyBinding relationship = new(
            new("relationship/customer"),
            path,
            CosmosRelationQuerySourceReader.GetObservationFieldSourceSelector(path));
        FakeChangeFeedReader changes = new()
        {
            Current = ChangePage([], "cut/relationship", HttpStatusCode.NotModified)
        };
        changes.Pages["cut/relationship"] = ChangePage(
            [ProviderChange(
                CosmosMaterializationProviderChangeKind.Replace,
                current: Document(
                    "load-a",
                    version: 2,
                    observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        ["customerId"] = ObservationValue.FromString("customer-b")
                    }),
                previous: Document(
                    "load-a",
                    version: 1,
                    observation: new Dictionary<string, ObservationValue>(StringComparer.Ordinal)
                    {
                        ["customerId"] = ObservationValue.FromString("customer-a")
                    }),
                lsn: 90)],
            "cut/relationship-next",
            HttpStatusCode.OK);
        var fixture = CreateFixture(
            StandardBaseline(),
            changes,
            placementRelationshipKeys: [relationship],
            placementKind: RelationQuerySourcePlacementBindingKind.RelationshipTraversal);
        var cut = await fixture.Source.CaptureCurrentPositionAsync(Context(), fixture.Source.Scope);

        var page = await fixture.Source.ReadChangesAsync(Context(), ChangeRequest(fixture, cut));

        var change = Assert.Single(page.Deliveries).Change;
        var before = Assert.Single(Assert.IsType<RelationQuerySourceReadObservation>(change.Before).Fields);
        var after = Assert.Single(Assert.IsType<RelationQuerySourceReadObservation>(change.After).Fields);
        Assert.Equal(RelationQuerySourceReadFieldPurpose.Correlation, before.Field.Purpose);
        Assert.Equal("customer-a", before.Value!.Value.String);
        Assert.Equal(RelationQuerySourceReadFieldPurpose.Correlation, after.Field.Purpose);
        Assert.Equal("customer-b", after.Value!.Value.String);
        Assert.Contains(
            fixture.Source.Descriptor.CapabilityProfile.Evidence,
            static evidence => evidence.Capability == MaterializationCapabilityKind.SourceBatchedPointRead);
        var changeEvidence = Assert.Single(
            fixture.Source.Descriptor.CapabilityProfile.Evidence,
            static evidence => evidence.Capability == MaterializationCapabilityKind.SourceChangeDelivery);
        Assert.Contains(MaterializationGuaranteeKind.BeforeImage, changeEvidence.Guarantees);
    }

    static async Task<BaselineDrain> DrainBaselineAsync(int maximumItems, long maximumBytes)
    {
        var baseline = StandardBaseline();
        var fixture = CreateFixture(baseline);
        MaterializationSourceContinuation? continuation = null;
        List<string> identities = [];
        do
        {
            var page = await fixture.Source.ReadPageAsync(
                Context(),
                BaselineRequest(fixture, continuation, maximumItems, maximumBytes));
            identities.AddRange(page.Read.Observations.Select(static observation => observation.Identity));
            continuation = page.Continuation;
        }
        while (continuation is not null);

        return new(
            [.. identities],
            [.. baseline.Calls.Select(static call => call.ContinuationToken)]);
    }

    static async Task<ChangeDrain> DrainChangesAsync(
        SourceFixture fixture,
        MaterializationSourcePosition position,
        int maximumDeliveries,
        long maximumBytes)
    {
        List<string> identities = [];
        List<int> counts = [];
        for (var pageNumber = 0; pageNumber < 10; pageNumber++)
        {
            var page = await fixture.Source.ReadChangesAsync(
                Context(),
                ChangeRequest(fixture, position, maximumDeliveries, maximumBytes));
            identities.AddRange(page.Deliveries.Select(static delivery => delivery.Change.SubjectIdentity));
            counts.Add(page.Deliveries.Length);
            if (page.State == MaterializationChangePageState.CaughtUp)
            {
                return new([.. identities], [.. counts]);
            }

            position = page.ThroughPosition;
        }

        throw new InvalidOperationException("The deterministic change drain did not reach its provider boundary.");
    }

    static SourceFixture CreateFixture(
        BaselineFeedFactory baseline,
        FakeChangeFeedReader? changes = null,
        RecordingObserver? observer = null,
        long maximumRows = 100,
        bool fixedPartition = true,
        string? identitySourceSelector = null,
        CosmosMaterializationAdmissionIndex? admissionIndex = null,
        bool includePlacementIdentity = true,
        string? placementIdentitySourceSelector = null,
        string? placementPartitionSourceSelector = null,
        ImmutableArray<RelationQuerySourceFieldBinding> placementFields = default,
        ImmutableArray<RelationQueryRelationshipKeyBinding> placementRelationshipKeys = default,
        RelationQueryPlacementFieldSelector? fieldSourceSelector = null,
        ConsistencyLevel? readConsistencyLevel = ConsistencyLevel.Strong,
        RelationQuerySourcePlacementBindingKind placementKind = RelationQuerySourcePlacementBindingKind.SourceSet,
        string partitionSourceSelector = CosmosRelationQuerySourceReader.ObservationPartitionSourceSelector,
        int maximumContainerParallelism = CosmosMaterializationSourcePolicy.DefaultMaximumContainerParallelism,
        string? entityDocumentKind = null)
    {
        CosmosRelationQuerySourcePolicy queryPolicy = new(
            partitionSourceSelector,
            crossPartitionPolicy: fixedPartition
                ? CosmosRelationQueryCrossPartitionPolicy.Prohibit
                : CosmosRelationQueryCrossPartitionPolicy.AllowBoundedQueries,
            fixedPartitionKey: fixedPartition ? new("tenant-a") : null,
            maximumEnumerationRows: 100,
            maximumSdkPageSize: 4,
            readConsistencyLevel: readConsistencyLevel);
        var limits = queryPolicy.GetEffectivePlacementLimits(
            CosmosRelationQuerySourceReader.DefaultLimits);
        RelationQuerySourceInstance relationSource = new(
            new("source/tests/cosmos-materialization"),
            new("domain/tests/cosmos-materialization"),
            CosmosRelationQuerySourceReader.TargetProfile,
            limits);
        CosmosJsonQueryFeedReader queryFeed = new(
            new Uri("https://tests.invalid"),
            "operations",
            "entities",
            baseline.Create);
        CosmosRelationQuerySourceReader reader = new(
            Shape,
            relationSource,
            queryFeed,
            "https://tests.invalid",
            "operations",
            "entities",
            queryPolicy,
            identitySourceSelector: identitySourceSelector,
            fieldSourceSelector: fieldSourceSelector,
            entityDocumentKind: entityDocumentKind);
        RelationQuerySourcePlacementBinding placement = new(
            new("placement/source"),
            new("source/items"),
            new("node/source"),
            new("binding/source"),
            Shape,
            relationSource.Id,
            placementKind,
            placementKind == RelationQuerySourcePlacementBindingKind.SourceSet
                ? RelationQuerySourceAcquisitionKind.BoundedEnumeration
                : RelationQuerySourceAcquisitionKind.BoundedLookup,
            RelationQuerySourcePlacementOrigin.Explicit,
            includePlacementIdentity
                ? new RelationQuerySourceIdentityBinding(
                    Shape,
                    placementIdentitySourceSelector ?? reader.IdentitySourceSelector)
                : null,
            fields: placementFields,
            relationshipKeys: placementRelationshipKeys,
            partition: placementPartitionSourceSelector is null
                ? null
                : new RelationQueryPartitionBinding(placementPartitionSourceSelector));
        CosmosMaterializationSourcePolicy sourcePolicy = new(
            TimeSpan.FromHours(12),
            "tests/deployment/continuous-backup-enabled",
            "tests/deployment/previous-images-enabled",
            "tests/deployment/strong-consistency",
            maximumScanPageItems: 10,
            maximumScanPageBytes: GenerousPageBytes,
            maximumChangePageItems: 10,
            maximumChangePageBytes: GenerousPageBytes,
            maximumProviderPageItems: 4,
            maximumContainerParallelism: maximumContainerParallelism);
        changes ??= new FakeChangeFeedReader
        {
            Current = ChangePage([], "cut/default", HttpStatusCode.NotModified)
        };
        CosmosMaterializationSource source = new(
            reader,
            PhysicalPlan,
            placement,
            sourcePolicy,
            admissionIndex ?? new CosmosMaterializationAdmissionIndex(),
            changes,
            Enumerable.Range(1, 32).Select(static value => checked((byte)value)).ToArray(),
            observer);
        RelationQuerySourceReadRequest read = new(
            PhysicalPlan,
            new("read/source"),
            placement.Id,
            relationSource.Id,
            Shape,
            reader.IdentitySourceSelector,
            fields: placement.Fields.Select(static field => new RelationQuerySourceReadField(
                field.Input,
                field.SemanticPath,
                field.SourceSelector,
                RelationQuerySourceReadFieldPurpose.SemanticInput)).ToImmutableArray(),
            placementKind == RelationQuerySourcePlacementBindingKind.SourceSet
                ? new RelationQueryBoundedEnumeration(maximumRows)
                : new RelationQueryIdentityBatchLookup(["fixture-identity"]),
            maximumBufferedRows: maximumRows);
        return new(source, read);
    }

    static MaterializationSourcePageRequest BaselineRequest(
        SourceFixture fixture,
        MaterializationSourceContinuation? continuation,
        int maximumItems,
        long maximumBytes) => new(
        fixture.Read,
        fixture.Source.Scope,
        continuation,
        maximumItems,
        maximumBytes);

    static MaterializationChangeReadRequest ChangeRequest(
        SourceFixture fixture,
        MaterializationSourcePosition position,
        int maximumDeliveries = 10,
        long maximumBytes = GenerousPageBytes) => new(
        fixture.Source.Scope,
        position,
        maximumDeliveries,
        maximumBytes);

    static FakeChangeFeedReader BoundedChangeFeed()
    {
        FakeChangeFeedReader changes = new();
        changes.Current = ChangePage([], "cut/bounded-page", HttpStatusCode.NotModified);
        changes.Pages["cut/bounded-page"] = ChangePage(
            [
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("filtered", observationType: "Other"),
                    lsn: 80),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-a"),
                    lsn: 81),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-b"),
                    lsn: 82),
                ProviderChange(
                    CosmosMaterializationProviderChangeKind.Create,
                    current: Document("load-c"),
                    lsn: 83)
            ],
            "cut/bounded-next",
            HttpStatusCode.OK);
        changes.Pages["cut/bounded-next"] = ChangePage(
            [],
            "cut/bounded-current",
            HttpStatusCode.NotModified);
        return changes;
    }

    static BaselineFeedFactory StandardBaseline(double requestCharge = 2.5) => new(
        new Dictionary<string, BaselineProviderPage>(StringComparer.Ordinal)
        {
            [BaselineFeedFactory.Initial] = new(
                [Row("a"), Row("b"), Row("c")],
                "provider/next",
                requestCharge),
            ["provider/next"] = new([Row("d")], ContinuationToken: null, requestCharge)
        });

    static CosmosMaterializationProviderChangePage ChangePage(
        ImmutableArray<CosmosMaterializationProviderChange> changes,
        string continuation,
        HttpStatusCode statusCode) => new(
        changes,
        continuation,
        statusCode,
        requestCharge: 3.5,
        "tests/cosmos-provider-evidence");

    static CosmosMaterializationProviderChange ProviderChange(
        CosmosMaterializationProviderChangeKind operation,
        CosmosObservationContainerDocument? current = null,
        CosmosObservationContainerDocument? previous = null,
        long lsn = 1) => new(
        current,
        previous,
        lsn,
        PreviousLsn: Math.Max(0, lsn - 1),
        operation,
        ProviderOccurredAt,
        IsTimeToLiveExpired: false,
        DeletedItemId: previous?.Id);

    static CosmosObservationContainerDocument Document(
        string identity,
        string? observationType = null,
        long version = 1,
        Dictionary<string, ObservationValue>? observation = null,
        string? documentKind = null) => new(
        $"entity/{identity}",
        "tenant-a",
        documentKind ?? CosmosRelationQuerySourceReader.DefaultEntityDocumentKind,
        observationType ?? Shape.ShapeId.Value,
        identity,
        version,
        observation ?? []);

    static CosmosObservationContainerDocument MalformedDocument(string property, string? value)
    {
        var document = Document("load-a");
        return property switch
        {
            nameof(CosmosObservationContainerDocument.Id) => document with { Id = value! },
            nameof(CosmosObservationContainerDocument.PartitionKey) => document with { PartitionKey = value! },
            nameof(CosmosObservationContainerDocument.ObservationId) => document with { ObservationId = value! },
            nameof(CosmosObservationContainerDocument.DocumentKind) => document with { DocumentKind = value! },
            nameof(CosmosObservationContainerDocument.ObservationType) => document with { ObservationType = value! },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, null)
        };
    }

    static JsonElement Row(string identity) =>
        JsonDocument.Parse($"{{\"_identity\":\"{identity}\"}}").RootElement.Clone();

    static OperationContext Context(CancellationToken cancellationToken = default) =>
        OperationContext.Create(new FixedTimeProvider(ObservedAt), cancellationToken: cancellationToken);

    static string Tamper(string value)
    {
        var tagStart = value.LastIndexOf('.') + 1;
        var replacement = value[tagStart] == 'A' ? 'B' : 'A';
        return string.Concat(value[..tagStart], replacement, value[(tagStart + 1)..]);
    }

    sealed record SourceFixture(
        CosmosMaterializationSource Source,
        RelationQuerySourceReadRequest Read);

    sealed record BaselineDrain(
        ImmutableArray<string> Identities,
        ImmutableArray<string?> ProviderContinuations);

    sealed record ChangeDrain(
        ImmutableArray<string> Identities,
        ImmutableArray<int> PageDeliveryCounts);

    sealed record BaselineProviderPage(
        ImmutableArray<JsonElement> Rows,
        string? ContinuationToken,
        double RequestCharge,
        HttpStatusCode StatusCode = HttpStatusCode.OK);

    sealed record BaselineCall(
        string? ContinuationToken,
        string ActivityId,
        ConsistencyLevel? ConsistencyLevel);

    sealed class BaselineFeedFactory(
        IReadOnlyDictionary<string, BaselineProviderPage> pages)
    {
        internal const string Initial = "<initial>";

        internal List<BaselineCall> Calls { get; } = [];

        internal FeedIterator<JsonElement> Create(
            FeedRange? feedRange,
            QueryDefinition query,
            string? continuationToken,
            QueryRequestOptions options)
        {
            Assert.Null(feedRange);
            Assert.NotNull(query);
            Assert.NotNull(options.PartitionKey);
            var activityId = $"tests/baseline-provider-activity/{Calls.Count + 1}";
            Calls.Add(new(continuationToken, activityId, options.ConsistencyLevel));
            var key = continuationToken ?? Initial;
            return new BaselineFeedIterator(pages[key], activityId);
        }
    }

    sealed class BaselineFeedIterator(BaselineProviderPage page, string activityId) : FeedIterator<JsonElement>
    {
        bool read;

        public override bool HasMoreResults => !read || page.ContinuationToken is not null;

        public override Task<FeedResponse<JsonElement>> ReadNextAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (read)
            {
                throw new InvalidOperationException("The test provider page was already read.");
            }

            read = true;
            return Task.FromResult<FeedResponse<JsonElement>>(new BaselineFeedResponse(page, activityId));
        }
    }

    sealed class BaselineFeedResponse(BaselineProviderPage page, string activityId) : FeedResponse<JsonElement>
    {
        public override string ContinuationToken => page.ContinuationToken!;

        public override int Count => page.Rows.Length;

        public override string IndexMetrics => string.Empty;

        public override string QueryAdvice => string.Empty;

        public override Headers Headers { get; } = new();

        public override IEnumerable<JsonElement> Resource => page.Rows;

        public override HttpStatusCode StatusCode => page.StatusCode;

        public override CosmosDiagnostics Diagnostics => null!;

        public override double RequestCharge => page.RequestCharge;

        public override string ActivityId => activityId;

        public override string ETag => string.Empty;

        public override IEnumerator<JsonElement> GetEnumerator() =>
            ((IEnumerable<JsonElement>)page.Rows).GetEnumerator();
    }

    sealed record ChangeFeedCall(
        CosmosMaterializationChangeFeedStart Start,
        FeedRange? FeedRange,
        int PageSizeHint);

    sealed class FakeChangeFeedReader : ICosmosMaterializationChangeFeedReader
    {
        internal CosmosMaterializationProviderChangePage Current { get; set; } = null!;

        internal Dictionary<string, CosmosMaterializationProviderChangePage> Pages { get; } =
            new(StringComparer.Ordinal);

        internal List<ChangeFeedCall> Calls { get; } = [];

        internal Func<
            CosmosMaterializationChangeFeedStart,
            FeedRange?,
            int,
            CancellationToken,
            ValueTask<CosmosMaterializationProviderChangePage>>? Handler
        { get; set; }

        public ValueTask<CosmosMaterializationProviderChangePage> ReadPageAsync(
            CosmosMaterializationChangeFeedStart start,
            FeedRange? feedRange,
            int pageSizeHint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(new(start, feedRange, pageSizeHint));
            if (Handler is not null)
            {
                return Handler(start, feedRange, pageSizeHint, cancellationToken);
            }

            return ValueTask.FromResult(start.Kind == CosmosMaterializationChangeFeedStartKind.Now
                ? Current
                : Pages[start.ContinuationToken!]);
        }
    }

    sealed class RecordingObserver : ICosmosMaterializationSourceObserver
    {
        internal List<CosmosMaterializationSourceObservation> Observations { get; } = [];

        public void Observe(CosmosMaterializationSourceObservation observation) =>
            Observations.Add(observation);
    }

    sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
