using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Model.Serialization;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Relations.Realization;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationSourceContractsTests
{
    const long MaximumPageBytes = 1_000_000;
    const int MaximumProfileItems = 100;

    static readonly DateTimeOffset Epoch = DateTimeOffset.UnixEpoch;
    static readonly RelationQuerySourceInstanceId Source = new("tests/source");
    static readonly MaterializationSourcePartitionId Partition = new("tenant-a");
    static readonly RelationQueryInputId Input = new("source/items");
    static readonly MaterializationOrderingScopeId OrderingScope = new("tenant-a/feed-0");
    static readonly QualifiedShapeId Shape = new(new("tests"), new("Item"));
    static readonly RelationQueryPhysicalPlanFingerprint PhysicalPlan =
        new("sha256", "tests/canonicalization/v1", "0123456789abcdef");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        new("placement/source"),
        Input,
        new("node/source"),
        new("binding/source"),
        Shape,
        Source,
        RelationQuerySourcePlacementBindingKind.SourceSet,
        RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        RelationQuerySourcePlacementOrigin.Explicit,
        new RelationQuerySourceIdentityBinding(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(PhysicalPlan, Placement, Partition, OrderingScope);

    [Fact]
    public void SourceScope_RejectsDefaultValueIdentitiesAsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new MaterializationSourceScope(
            PhysicalPlan,
            Placement,
            default,
            OrderingScope));
        Assert.Throws<ArgumentException>(() => new MaterializationSourceScope(
            PhysicalPlan,
            Placement,
            Partition,
            default));
    }

    [Fact]
    public void ChangeOnlySourceDescriptor_DoesNotInventRelationsReaderCapability()
    {
        var profile = CreateSource().Source.Descriptor.CapabilityProfile;
        MaterializationSourceDescriptor descriptor = new(
            source: Source,
            executionDomain: new("tests/domain"),
            capabilityProfile: profile);
        IMaterializationChangeSource source = new ChangeOnlySource(descriptor);

        Assert.Same(descriptor, source.Descriptor);
        Assert.IsNotType<MaterializationQuerySourceDescriptor>(source.Descriptor);
        Assert.Equal(Source, source.Descriptor.Source);
        Assert.Equal(new RelationQueryExecutionDomainId("tests/domain"), source.Descriptor.ExecutionDomain);
    }

    [Fact]
    public async Task PagedRead_ScopesOpaqueContinuationToSourceAndPartition()
    {
        var fixture = CreateSource();
        var request = new MaterializationSourcePageRequest(
            ReadRequest(Source),
            Scope,
            continuation: null,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes);

        var first = await fixture.Source.ReadPageAsync(OperationContext.Create(), request);

        Assert.Equal(RelationQuerySourceReadState.Partial, first.Read.State);
        Assert.Equal(MaterializationSourcePageState.MoreAvailable, first.State);
        Assert.Equal(["a", "b"], first.Read.Observations.Select(static item => item.Identity));
        var continuation = Assert.IsType<MaterializationSourceContinuation>(first.Continuation);
        Assert.Equal(Scope, continuation.Scope);
        Assert.Equal(MaterializationSourceReadFingerprinter.Compute(request.Read), continuation.ReadFingerprint);
        Assert.Equal(1, continuation.FormatVersion);

        Assert.Throws<ArgumentException>(() => new MaterializationSourcePageRequest(
            request.Read,
            new MaterializationSourceScope(PhysicalPlan, Placement, new("tenant-b"), new("tenant-b/feed-0")),
            continuation,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes));
        RelationQuerySourcePlacementBinding wrongInputPlacement = new(
            new("placement/other-input"),
            new("source/other-items"),
            new("node/other-source"),
            new("binding/other-source"),
            Shape,
            Source,
            RelationQuerySourcePlacementBindingKind.SourceSet,
            RelationQuerySourceAcquisitionKind.BoundedEnumeration,
            RelationQuerySourcePlacementOrigin.Explicit,
            new RelationQuerySourceIdentityBinding(Shape, "id"));
        Assert.Throws<ArgumentException>(() => new MaterializationSourcePageRequest(
            request.Read,
            new MaterializationSourceScope(PhysicalPlan, wrongInputPlacement, Partition, OrderingScope),
            continuation: null,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes));
        Assert.Throws<ArgumentException>(() => new MaterializationSourcePageRequest(
            ReadRequest(new RelationQuerySourceInstanceId("tests/other-source")),
            Scope,
            continuation,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes));
        Assert.Throws<ArgumentException>(() => new MaterializationSourcePageRequest(
            ReadRequest(Source, stage: "read/other"),
            Scope,
            continuation,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes));

        var second = await fixture.Source.ReadPageAsync(
            OperationContext.Create(),
            new MaterializationSourcePageRequest(
                request.Read,
                Scope,
                continuation,
                maximumItems: 2,
                maximumBytes: MaximumPageBytes));

        Assert.Equal(RelationQuerySourceReadState.Complete, second.Read.State);
        Assert.Equal(MaterializationSourcePageState.Exhausted, second.State);
        Assert.Equal(["c"], second.Read.Observations.Select(static item => item.Identity));
        Assert.Null(second.Continuation);
        var completion = MaterializationSourceReadCompletion.FromPage(second);
        Assert.Equal(Scope, completion.Scope);
        Assert.Equal(continuation.ReadFingerprint, completion.ReadFingerprint);
        Assert.Equal(2, fixture.Reader.ReadCount);
    }

    [Fact]
    public async Task SourceRead_PagesByCanonicalEncodedBytes()
    {
        var fixture = CreateSource();
        RelationQuerySourceReadObservation firstObservation = new("a", Shape, []);

        var first = await fixture.Source.ReadPageAsync(
            OperationContext.Create(),
            new MaterializationSourcePageRequest(
                ReadRequest(Source),
                Scope,
                continuation: null,
                maximumItems: 3,
                maximumBytes: CanonicalByteCount(firstObservation)));

        Assert.Equal("a", Assert.Single(first.Read.Observations).Identity);
        Assert.Equal(MaterializationSourcePageState.MoreAvailable, first.State);
        Assert.NotNull(first.Continuation);
    }

    [Fact]
    public void AcquisitionCatalog_ClassifiesCanonicalRelationsReadConstraints()
    {
        Assert.Equal(
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationSourceAcquisitionCatalog.GetReadCapability(new RelationQueryBoundedEnumeration(1)));
        Assert.Equal(
            MaterializationCapabilityKind.SourceBatchedPointRead,
            MaterializationSourceAcquisitionCatalog.GetReadCapability(new RelationQueryIdentityBatchLookup(["item-1"])));
        Assert.Equal(
            MaterializationCapabilityKind.SourceParameterizedPredicateQuery,
            MaterializationSourceAcquisitionCatalog.GetReadCapability(new RelationQueryRelationshipKeyBatchLookup(
                FieldPath.FromField("ownerId"),
                "owner_id",
                ["owner-1"])));
    }

    [Fact]
    public void CapabilityLimits_RequireKnownKindsAndPositiveBounds()
    {
        var profile = CreateSource().Source.Descriptor.CapabilityProfile;

        Assert.True(MaterializationCapabilityLimits.SupportsBounds(
            profile,
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationLimitKind.ReadItems,
            MaximumProfileItems,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes));
        Assert.False(MaterializationCapabilityLimits.SupportsBounds(
            profile,
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationLimitKind.ReadItems,
            MaximumProfileItems + 1,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterializationCapabilityLimits.SupportsBounds(
            profile,
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationLimitKind.ReadItems,
            requestedItems: 0,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterializationCapabilityLimits.SupportsBounds(
            profile,
            (MaterializationCapabilityKind)int.MaxValue,
            MaterializationLimitKind.ReadItems,
            MaximumProfileItems,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes));
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterializationCapabilityLimits.SupportsBounds(
            profile,
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            (MaterializationLimitKind)int.MaxValue,
            MaximumProfileItems,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes));
        Assert.Throws<ArgumentException>(() => MaterializationCapabilityLimits.RequireSupportedBounds(
            profile,
            MaterializationCapabilityKind.SourceBoundedEnumeration,
            MaterializationLimitKind.ReadItems,
            MaximumProfileItems,
            MaterializationLimitKind.ReadBytes,
            MaximumPageBytes,
            parameterName: " "));
    }

    [Fact]
    public void CapabilityLimits_RejectNonCanonicalOrCapabilityInapplicableDimensions()
    {
        MaterializationCapabilityProfile profile = new(
            new("tests/materialization-bounds/v1"),
            MaterializationEndpointRole.Source,
            Source.Value,
            [
                new MaterializationCapabilityEvidence(
                    new("bounded-enumeration"),
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    CapabilityRealizationKind.Native,
                    [],
                    [
                        new MaterializationOperatingLimit(MaterializationLimitKind.ReadItems, MaximumProfileItems),
                        new MaterializationOperatingLimit(MaterializationLimitKind.ReadBytes, MaximumPageBytes),
                        new MaterializationOperatingLimit(MaterializationLimitKind.Parallelism, 4)
                    ],
                    ["tests/bounds"])
            ]);

        var itemDimension = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationCapabilityLimits.SupportsBounds(
                profile,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationLimitKind.Parallelism,
                requestedItems: 1,
                MaterializationLimitKind.Parallelism,
                requestedBytes: 1));
        Assert.Equal("itemLimitKind", itemDimension.ParamName);

        var byteDimension = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationCapabilityLimits.SupportsBounds(
                profile,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationLimitKind.ReadItems,
                requestedItems: 1,
                MaterializationLimitKind.ReadItems,
                requestedBytes: 1));
        Assert.Equal("byteLimitKind", byteDimension.ParamName);

        var inapplicableItem = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationCapabilityLimits.SupportsBounds(
                profile,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationLimitKind.WriteItems,
                requestedItems: 1,
                MaterializationLimitKind.ReadBytes,
                requestedBytes: 1));
        Assert.Equal("itemLimitKind", inapplicableItem.ParamName);

        var inapplicableBytes = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MaterializationCapabilityLimits.SupportsBounds(
                profile,
                MaterializationCapabilityKind.SourceBoundedEnumeration,
                MaterializationLimitKind.ReadItems,
                requestedItems: 1,
                MaterializationLimitKind.WriteBytes,
                requestedBytes: 1));
        Assert.Equal("byteLimitKind", inapplicableBytes.ParamName);
    }

    [Fact]
    public async Task SourceRead_RejectsBoundsBeyondItsAttributableProfile()
    {
        var fixture = CreateSource();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Source.ReadPageAsync(
            OperationContext.Create(),
            new MaterializationSourcePageRequest(
                ReadRequest(Source),
                Scope,
                continuation: null,
                maximumItems: MaximumProfileItems + 1,
                maximumBytes: MaximumPageBytes)).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Source.ReadPageAsync(
            OperationContext.Create(),
            new MaterializationSourcePageRequest(
                ReadRequest(Source),
                Scope,
                continuation: null,
                maximumItems: 1,
                maximumBytes: MaximumPageBytes + 1)).AsTask());

        Assert.Equal(0, fixture.Reader.ReadCount);
    }

    [Fact]
    public async Task SourceRead_RejectsAnIndivisibleObservationBeyondTheByteBound()
    {
        var fixture = CreateSource();
        RelationQuerySourceReadObservation firstObservation = new("a", Shape, []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ReadPageAsync(
            OperationContext.Create(),
            new MaterializationSourcePageRequest(
                ReadRequest(Source),
                Scope,
                continuation: null,
                maximumItems: 1,
                maximumBytes: CanonicalByteCount(firstObservation) - 1)).AsTask());
    }

    [Fact]
    public void SourceScope_RetainsStructuralIdentityAcrossPersistence()
    {
        var options = MaterializationJsonSerializer.CreateOptions();
        var persisted = JsonSerializer.Serialize(Scope, options);
        var restored = Assert.IsType<MaterializationSourceScope>(
            JsonSerializer.Deserialize<MaterializationSourceScope>(persisted, options));
        var read = ReadRequest(Source);
        MaterializationSourceContinuation continuation = new(
            formatVersion: 1,
            MaterializationSourceReadFingerprinter.Compute(read),
            restored,
            "opaque/continuation");

        var request = new MaterializationSourcePageRequest(
            read,
            Scope,
            continuation,
            maximumItems: 2,
            maximumBytes: MaximumPageBytes);
        MaterializationProgressKey originalKey = new(
            new("tests/materialization"),
            new("sha256", "execution-definition/v1", "0123456789abcdef"),
            new("generation-1"),
            Scope);
        MaterializationProgressKey restoredKey = new(
            originalKey.Materialization,
            originalKey.DefinitionFingerprint,
            originalKey.Generation,
            restored);

        Assert.Equal(Scope, restored);
        Assert.Equal(originalKey, restoredKey);
        Assert.Equal(continuation, request.Continuation);
    }

    [Fact]
    public async Task SourceRead_DoesNotImplicitlyCreateApplicationProgress()
    {
        var fixture = CreateSource();
        IMaterializationProgressStore progress = new InMemoryMaterializationProgressStore();
        MaterializationProgressKey key = new(
            new("tests/materialization"),
            new("sha256", "execution-definition/v1", "0123456789abcdef"),
            new("generation-1"),
            Scope);
        var context = OperationContext.Create();

        Assert.Null(await progress.LoadAsync(context, key));
        await fixture.Source.ReadPageAsync(
            context,
            new MaterializationSourcePageRequest(
                ReadRequest(Source),
                Scope,
                continuation: null,
                maximumItems: 2,
                maximumBytes: MaximumPageBytes));

        Assert.Null(await progress.LoadAsync(context, key));
    }

    [Fact]
    public void Pages_RequireCoherentContinuationAndChangeBoundaries()
    {
        var readRequest = ReadRequest(Source);
        MaterializationSourceContinuation continuation = new(
            1,
            MaterializationSourceReadFingerprinter.Compute(readRequest),
            Scope,
            "continuation-1");

        Assert.Throws<ArgumentException>(() => new MaterializationSourcePage(
            Scope,
            continuation.ReadFingerprint,
            new RelationQuerySourceReadResult(RelationQuerySourceReadState.Partial, []),
            MaterializationSourcePageState.MoreAvailable));
        Assert.Throws<ArgumentException>(() => new MaterializationSourcePage(
            Scope,
            continuation.ReadFingerprint,
            new RelationQuerySourceReadResult(RelationQuerySourceReadState.Complete, []),
            MaterializationSourcePageState.Exhausted,
            continuation));
        var terminalPartial = new MaterializationSourcePage(
            Scope,
            continuation.ReadFingerprint,
            new RelationQuerySourceReadResult(RelationQuerySourceReadState.Partial, []),
            MaterializationSourcePageState.Exhausted);
        Assert.Throws<ArgumentException>(() => new MaterializationChangePage(
            [],
            new MaterializationSourcePosition(1, Scope, "boundary-1"),
            MaterializationChangePageState.MoreAvailable));
        var progressed = new MaterializationChangePage(
            [],
            new MaterializationSourcePosition(1, Scope, "boundary-2"),
            MaterializationChangePageState.Progressed);
        Assert.Throws<ArgumentException>(() => new MaterializationChangePage(
            [DeleteDelivery("delivery-1", "change-1", "item-1", "position-1", Epoch)],
            progressed.ThroughPosition,
            MaterializationChangePageState.Progressed));
        var otherShape = new QualifiedShapeId(new("tests"), new("OtherItem"));
        Assert.Throws<ArgumentException>(() => new MaterializationSourcePage(
            Scope,
            continuation.ReadFingerprint,
            new RelationQuerySourceReadResult(
                RelationQuerySourceReadState.Complete,
                [new RelationQuerySourceReadObservation("wrong-shape", otherShape, [])]),
            MaterializationSourcePageState.Exhausted));
        Assert.Throws<ArgumentException>(() => MaterializationSourceReadCompletion.FromPage(terminalPartial));
        Assert.Empty(progressed.Deliveries);
        Assert.Equal(MaterializationChangePageState.Progressed, progressed.State);
    }

    [Fact]
    public void SettlementResults_SeparateReceiptsFromRejectionDiagnostics()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        MaterializationSourceSettlement receipt = new(
            new("settlement-1"),
            new("checkpoint-1"),
            new MaterializationSourcePosition(1, Scope, "position-1"),
            timestamp);
        DocumentValidationDiagnostic diagnostic = new(
            "materialization.source.rejected",
            DiagnosticSeverity.Error,
            "The source rejected settlement.");

        Assert.Throws<ArgumentException>(() => new MaterializationSourceSettlementResult(
            MaterializationSourceSettlementDisposition.Acknowledged,
            receipt,
            [diagnostic]));
        Assert.Throws<ArgumentException>(() => new MaterializationSourceSettlementResult(
            MaterializationSourceSettlementDisposition.Rejected,
            receipt: null));
    }

    [Fact]
    public void DeleteChange_RetainsStableSubjectWithoutBeforeImage()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

        MaterializationChangeEnvelope change = new(
            new("change-1"),
            "item-1",
            Scope,
            Shape,
            new MaterializationSourcePosition(1, Scope, "position-1"),
            MaterializationChangeKind.Delete,
            before: null,
            after: null,
            timestamp,
            timestamp,
            "tests/delete-feed");

        Assert.Equal("item-1", change.SubjectIdentity);
        Assert.Null(change.Before);
        Assert.Null(change.After);
    }

    [Fact]
    public void ChangeEnvelope_RejectsShapeOutsideItsRelationsPlacementScope()
    {
        var otherShape = new QualifiedShapeId(new("tests"), new("OtherItem"));

        Assert.Throws<ArgumentException>(() => new MaterializationChangeEnvelope(
            new("change/wrong-shape"),
            "item-1",
            Scope,
            otherShape,
            new MaterializationSourcePosition(1, Scope, "position/1"),
            MaterializationChangeKind.Delete,
            before: null,
            after: null,
            Epoch,
            Epoch,
            "tests/change-feed"));
    }

    [Fact]
    public async Task CaptureCurrentPosition_ReturnsCurrentEndWithoutReadingOrSettlingChanges()
    {
        var fixture = CreateSource([
            DeleteDelivery("delivery-1", "change-1", "item-1", "position-1", Epoch),
            DeleteDelivery("delivery-2", "change-2", "item-2", "position-2", Epoch.AddSeconds(1))
        ]);
        var context = OperationContext.Create();

        var position = await fixture.Source.CaptureCurrentPositionAsync(context, Scope);
        var afterCut = await fixture.Source.ReadChangesAsync(
            context,
            new MaterializationChangeReadRequest(
                Scope,
                position,
                maximumDeliveries: 10,
                maximumBytes: MaximumPageBytes));

        Assert.Equal(Scope, position.Scope);
        Assert.Equal(MaterializationChangePageState.CaughtUp, afterCut.State);
        Assert.Empty(afterCut.Deliveries);
        Assert.Equal(position, afterCut.ThroughPosition);
        Assert.Equal(0, fixture.Reader.ReadCount);
    }

    [Fact]
    public void RetainedHistoryPort_IsExposedOnlyByTheCapabilitySpecificReferenceSource()
    {
        var fixture = CreateSource();
        var exception = Assert.Throws<ArgumentException>(() =>
            new InMemoryMaterializationSource(fixture.Source.Descriptor));

        Assert.IsAssignableFrom<IMaterializationRetainedChangeSource>(fixture.Source);
        Assert.Contains("retained-change-source interface", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangeRead_PreservesSourceOrderAndResumesAfterPageBoundary()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var first = DeleteDelivery("delivery-1", "change-1", "item-1", "position-1", timestamp);
        var second = DeleteDelivery("delivery-2", "change-2", "item-2", "position-2", timestamp.AddSeconds(1));
        var fixture = CreateSource([first, second]);
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(OperationContext.Create(), Scope);

        var initial = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 1,
                maximumBytes: MaximumPageBytes));
        var resumed = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                initial.ThroughPosition,
                maximumDeliveries: 10,
                maximumBytes: MaximumPageBytes));

        Assert.Equal(MaterializationChangePageState.MoreAvailable, initial.State);
        Assert.Equal("delivery-1", Assert.Single(initial.Deliveries).Id.Value);
        Assert.Equal(MaterializationChangePageState.CaughtUp, resumed.State);
        Assert.Equal("delivery-2", Assert.Single(resumed.Deliveries).Id.Value);
    }

    [Fact]
    public async Task ChangeRead_PagesByCanonicalEncodedBytes()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var firstDelivery = DeleteDelivery("delivery-1", "change-1", "item-1", "position-1", timestamp);
        var fixture = CreateSource([
            firstDelivery,
            DeleteDelivery("delivery-2", "change-2", "item-2", "position-2", timestamp.AddSeconds(1))
        ]);
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(OperationContext.Create(), Scope);

        var page = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 2,
                maximumBytes: CanonicalByteCount(firstDelivery)));

        Assert.Equal("delivery-1", Assert.Single(page.Deliveries).Id.Value);
        Assert.Equal(MaterializationChangePageState.MoreAvailable, page.State);
    }

    [Fact]
    public async Task ChangeRead_RejectsProfileOverflowAndIndivisibleDeliveries()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var firstDelivery = DeleteDelivery("delivery-1", "change-1", "item-1", "position-1", timestamp);
        var fixture = CreateSource([firstDelivery]);
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(OperationContext.Create(), Scope);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: MaximumProfileItems + 1,
                maximumBytes: MaximumPageBytes)).AsTask());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 1,
                maximumBytes: MaximumPageBytes + 1)).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 1,
                maximumBytes: CanonicalByteCount(firstDelivery) - 1)).AsTask());
    }

    [Fact]
    public async Task ChangeRead_PageBoundaryDoesNotLoseDeliveriesSharingAnObservedPosition()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var first = DeleteDelivery("delivery-1", "change-1", "item-1", "shared-position", timestamp);
        var second = DeleteDelivery("delivery-2", "change-2", "item-2", "shared-position", timestamp);
        var fixture = CreateSource([first, second]);
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(OperationContext.Create(), Scope);

        var initial = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 1,
                maximumBytes: MaximumPageBytes));
        var resumed = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                initial.ThroughPosition,
                maximumDeliveries: 1,
                maximumBytes: MaximumPageBytes));

        Assert.Equal("delivery-1", Assert.Single(initial.Deliveries).Id.Value);
        Assert.Equal("delivery-2", Assert.Single(resumed.Deliveries).Id.Value);
        Assert.NotEqual(first.Change.Position, initial.ThroughPosition);
    }

    [Fact]
    public async Task EmptyCaughtUpPage_ProvidesCheckpointableBoundary()
    {
        var fixture = CreateSource();
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(OperationContext.Create(), Scope);

        var page = await fixture.Source.ReadChangesAsync(
            OperationContext.Create(),
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 10,
                maximumBytes: MaximumPageBytes));
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("empty-cut"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: page.ThroughPosition,
            appliedDeliveries: [],
            committedAtUtc: new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero),
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(page.ThroughPosition));

        Assert.Equal(MaterializationChangePageState.CaughtUp, page.State);
        Assert.Empty(page.Deliveries);
        Assert.Equal(Scope, checkpoint.Position?.Scope);
    }

    [Fact]
    public void UpdateChange_AllowsAfterImageWithoutBeforeImage()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        RelationQuerySourceReadObservation after = new("item-1", Shape, []);

        MaterializationChangeEnvelope change = new(
            new("change-1"),
            "item-1",
            Scope,
            Shape,
            new MaterializationSourcePosition(1, Scope, "position-1"),
            MaterializationChangeKind.Update,
            before: null,
            after,
            timestamp,
            timestamp);

        Assert.Null(change.Before);
        Assert.Same(after, change.After);
    }

    [Fact]
    public async Task Settlement_AcknowledgesAfterCheckpointThenPersistsReceiptSeparately()
    {
        var timestamp = new DateTimeOffset(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var fixture = CreateSource([
            DeleteDelivery("delivery-1", "change-1", "item-1", "observed-position-1", timestamp)
        ]);
        IMaterializationProgressStore progress = new InMemoryMaterializationProgressStore();
        MaterializationProgressKey key = new(
            new("tests/materialization"),
            new("sha256", "execution-definition/v1", "0123456789abcdef"),
            new("generation-1"),
            Scope);
        var context = OperationContext.Create();
        var retainedStart = await fixture.Source.CaptureRetainedStartPositionAsync(context, Scope);
        var claim = Assert.IsType<MaterializationProgressSnapshot>((await progress.AcquireFenceAsync(
            context,
            key,
            new("claim-1"),
            expectedRevision: null,
            owner: "worker-a")).Snapshot);
        var changePage = await fixture.Source.ReadChangesAsync(
            context,
            new MaterializationChangeReadRequest(
                Scope,
                retainedStart,
                maximumDeliveries: 10,
                maximumBytes: MaximumPageBytes));
        var position = changePage.ThroughPosition;
        var checkpointTime = context.UtcNow.AddSeconds(-1);
        MaterializationApplicationCheckpoint checkpoint = new(
            id: new("checkpoint-1"),
            kind: MaterializationCheckpointKind.ChangeProgress,
            continuation: null,
            completion: null,
            position: position,
            appliedDeliveries: [new MaterializationDeliveryId("delivery-1")],
            committedAtUtc: checkpointTime,
            channelProgress: MaterializationChannelSemantics.CreatePositionedDurableProgress(position));
        var checkpointResult = await progress.SaveCheckpointAsync(
            context,
            key,
            new("checkpoint-mutation-1"),
            claim.Revision,
            "worker-a",
            claim.Fence,
            checkpoint);
        var checkpointSnapshot = Assert.IsType<MaterializationProgressSnapshot>(checkpointResult.Snapshot);
        MaterializationSourceSettlementRequest request = new(
            new("settlement-1"),
            checkpoint.Id,
            position,
            context.UtcNow);

        var acknowledged = await fixture.Source.SettleAsync(context, request);
        var replayed = await fixture.Source.SettleAsync(context, request);
        var identityConflict = await fixture.Source.SettleAsync(
            context,
            new MaterializationSourceSettlementRequest(
                request.Id,
                new MaterializationCheckpointId("checkpoint-2"),
                position,
                request.RequestedAtUtc));

        Assert.Equal(MaterializationSourceSettlementDisposition.Acknowledged, acknowledged.Disposition);
        Assert.Equal(MaterializationSourceSettlementDisposition.Replayed, replayed.Disposition);
        Assert.Equal(MaterializationSourceSettlementDisposition.IdentityConflict, identityConflict.Disposition);
        Assert.Equal(
            MaterializationSourceDiagnosticCodes.SettlementIdentityConflict,
            Assert.Single(identityConflict.Diagnostics).Code);
        AssertCompleteDiagnostic(Assert.Single(identityConflict.Diagnostics));
        var receipt = Assert.IsType<MaterializationSourceSettlement>(acknowledged.Receipt);
        Assert.Equal(receipt, replayed.Receipt);
        Assert.Null(checkpointSnapshot.LatestSettlement);

        var persisted = await progress.SaveSettlementAsync(
            context,
            key,
            new("settlement-mutation-1"),
            checkpointSnapshot.Revision,
            "worker-a",
            checkpointSnapshot.Fence,
            receipt);

        Assert.Equal(MaterializationProgressMutationDisposition.Applied, persisted.Disposition);
        Assert.Equal(
            receipt,
            Assert.IsType<MaterializationProgressSnapshot>(persisted.Snapshot).LatestSettlement);
    }

    static SourceFixture CreateSource(ImmutableArray<MaterializationChangeDelivery> changes = default)
    {
        RelationQueryTargetCapabilityProfile relationProfile = new(
            new("tests/relations-target"),
            new("tests/relations-target/v1"),
            ["relation-query/v1"],
            ["tests/compiler/v1"]);
        RelationQuerySourceReaderDescriptor readerDescriptor = new(
            Source,
            new("tests/domain"),
            relationProfile);
        RecordingReader reader = new(
            readerDescriptor,
            new RelationQuerySourceReadResult(
                RelationQuerySourceReadState.Complete,
                [
                    new RelationQuerySourceReadObservation("a", Shape, []),
                    new RelationQuerySourceReadObservation("b", Shape, []),
                    new RelationQuerySourceReadObservation("c", Shape, [])
                ],
                "tests/read"));
        MaterializationCapabilityProfile materializationProfile = new(
            new("tests/materialization-source/v1"),
            MaterializationEndpointRole.Source,
            Source.Value,
            [
                new MaterializationCapabilityEvidence(
                    new("bounded-enumeration"),
                    MaterializationCapabilityKind.SourceBoundedEnumeration,
                    CapabilityRealizationKind.Native,
                    [
                        MaterializationGuaranteeKind.StableOrdering,
                        MaterializationGuaranteeKind.RequestLocalCompleteness
                    ],
                    [
                        new MaterializationOperatingLimit(MaterializationLimitKind.ReadItems, MaximumProfileItems),
                        new MaterializationOperatingLimit(MaterializationLimitKind.ReadBytes, MaximumPageBytes)
                    ],
                    ["tests/in-memory-reader"]),
                new MaterializationCapabilityEvidence(
                    new("continuation"),
                    MaterializationCapabilityKind.SourceContinuation,
                    CapabilityRealizationKind.Native,
                    [],
                    [],
                    ["tests/in-memory-reader"]),
                new MaterializationCapabilityEvidence(
                    new("change-delivery"),
                    MaterializationCapabilityKind.SourceChangeDelivery,
                    CapabilityRealizationKind.Native,
                    [
                        MaterializationGuaranteeKind.StableOrdering,
                        MaterializationGuaranteeKind.AtLeastOnceDelivery,
                        MaterializationGuaranteeKind.BaselinePlusCatchUp,
                        MaterializationGuaranteeKind.RetainedHistoryStart,
                        MaterializationGuaranteeKind.CompleteMutationDelivery
                    ],
                    [
                        new MaterializationOperatingLimit(MaterializationLimitKind.ChangeItems, MaximumProfileItems),
                        new MaterializationOperatingLimit(MaterializationLimitKind.ReadBytes, MaximumPageBytes)
                    ],
                    ["tests/in-memory-reader"]),
                new MaterializationCapabilityEvidence(
                    new("settlement"),
                    MaterializationCapabilityKind.SourceSettlement,
                    CapabilityRealizationKind.Native,
                    [MaterializationGuaranteeKind.ExplicitSettlement],
                    [],
                    ["tests/in-memory-reader"])
            ]);
        return new(
            new InMemoryRetainedMaterializationSource(
                new MaterializationQuerySourceDescriptor(reader, materializationProfile),
                changes),
            reader);
    }

    static MaterializationChangeDelivery DeleteDelivery(
        string delivery,
        string change,
        string subject,
        string position,
        DateTimeOffset timestamp) => new(
        new(delivery),
            new MaterializationChangeEnvelope(
            new(change),
            subject,
            Scope,
            Shape,
            new MaterializationSourcePosition(1, Scope, position),
            MaterializationChangeKind.Delete,
            before: null,
            after: null,
            timestamp,
            timestamp,
            "tests/change-feed"),
        timestamp,
        "tests/change-delivery");

    static RelationQuerySourceReadRequest ReadRequest(
        RelationQuerySourceInstanceId source,
        string stage = "read/source") => new(
        PhysicalPlan,
        new(stage),
        Placement.Id,
        source,
        Shape,
        "id",
        [],
        new RelationQueryBoundedEnumeration(maximumRows: 100),
        maximumBufferedRows: 100);

    static long CanonicalByteCount<T>(T item) where T : class =>
        StrictDocumentJson.GetCanonicalBytes(item, MaterializationJsonSerializer.CreateOptions()).LongLength;

    static void AssertCompleteDiagnostic(DocumentValidationDiagnostic diagnostic)
    {
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Location));
        var evidence = Assert.IsType<DocumentDiagnosticEvidence>(diagnostic.Evidence);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Stage));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Subject));
        Assert.NotEmpty(evidence.SourceReferences);
        Assert.False(string.IsNullOrWhiteSpace(evidence.Expected));
        Assert.False(string.IsNullOrWhiteSpace(evidence.Observed));
    }

    sealed record SourceFixture(InMemoryRetainedMaterializationSource Source, RecordingReader Reader);

    sealed class ChangeOnlySource(MaterializationSourceDescriptor descriptor) : IMaterializationChangeSource
    {
        public MaterializationSourceDescriptor Descriptor { get; } = descriptor;
    }

    sealed class RecordingReader(
        RelationQuerySourceReaderDescriptor descriptor,
        RelationQuerySourceReadResult result) : IRelationQuerySourceReader
    {
        public RelationQuerySourceReaderDescriptor Descriptor { get; } = descriptor;

        public int ReadCount { get; private set; }

        public ValueTask<RelationQuerySourceReadResult> ReadAsync(
            RelationQuerySourceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            return ValueTask.FromResult(result);
        }
    }
}
