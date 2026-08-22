using System.Collections.Immutable;
using System.Text.Json;
using Cohesive.Relations.Acquisition;
using Cohesive.Relations.Compilation;
using Cohesive.Relations.Physical;
using Cohesive.Storage.Materialization;

namespace Cohesive.Tests.Storage;

public sealed class MaterializationCurrentStateEnrichmentTests
{
    const long MaximumItems = 8;
    const long MaximumBytes = 64_000;

    static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch;
    static readonly RelationQueryInputId Input = new("orders");
    static readonly QualifiedShapeId Shape = new(new("tests/current-state"), new("Order"));
    static readonly RelationQuerySourceInstanceId Source = new("tests/current-state/source");
    static readonly RelationQuerySourcePlacementBinding Placement = new(
        id: new("placement/current-state/orders"),
        input: Input,
        node: new("node/current-state/orders"),
        binding: new("binding/current-state/orders"),
        shape: Shape,
        source: Source,
        kind: RelationQuerySourcePlacementBindingKind.SourceSet,
        acquisition: RelationQuerySourceAcquisitionKind.BoundedEnumeration,
        origin: RelationQuerySourcePlacementOrigin.Explicit,
        identity: new(Shape, "id"));
    static readonly MaterializationSourceScope Scope = new(
        physicalPlan: new("sha256", "tests/current-state/v1", "current-state-plan"),
        placement: Placement,
        logicalPartition: new("tenant-a"),
        partition: new("tenant-a"),
        orderingScope: new("tenant-a/orders"));

    [Fact]
    public void Compiler_SelectsDeliveredChangeImageWhenItAlreadyProvesCompleteCurrentObservation()
    {
        var profile = Profile(completeChangeImage: true, includePointRead: false);

        var result = Compile(profile);

        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(result.Plan);
        Assert.True(result.IsSuccessful);
        Assert.Same(profile, result.Profile);
        Assert.Equal(MaterializationCurrentStateEnrichmentStrategyKind.DeliveredChangeImage, plan.Strategy);
        Assert.Equal(MaterializationCurrentStateConsistencyKind.ChangePositioned, plan.Consistency);
        Assert.Null(plan.CurrentStateReadEvidence);
        Assert.Equal(plan.SignalEvidence, plan.EffectiveChangeEvidence);
        MaterializationCurrentStateEnrichmentCompiler.Link(plan, profile);
    }

    [Fact]
    public void Compiler_FailsClosedWhenPartialChangeImageHasNoCompletePointRead()
    {
        var result = Compile(Profile(completeChangeImage: false, includePointRead: false));

        Assert.False(result.IsSuccessful);
        Assert.Null(result.Plan);
        Assert.Null(result.Profile);
        Assert.Contains(
            result.Diagnostics,
            static diagnostic => diagnostic.Code
                == MaterializationCurrentStateEnrichmentDiagnosticCodes.CurrentStateReadUnavailable);
    }

    [Fact]
    public void Compiler_ComposesPartialChangeSignalAndBoundedPointReadWithExplicitConsistency()
    {
        var raw = Profile(completeChangeImage: false, includePointRead: true);

        var result = Compile(raw);

        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(result.Plan);
        var profile = Assert.IsType<MaterializationCapabilityProfile>(result.Profile);
        Assert.Equal(MaterializationCurrentStateEnrichmentStrategyKind.BatchedIdentityRead, plan.Strategy);
        Assert.Equal(MaterializationCurrentStateConsistencyKind.ReconciledLatest, plan.Consistency);
        Assert.NotNull(plan.CurrentStateReadEvidence);
        Assert.NotEqual(plan.SignalEvidence, plan.EffectiveChangeEvidence);
        Assert.Contains(profile.Evidence, evidence => evidence.Id == plan.SignalEvidence);
        Assert.Contains(
            profile.Evidence,
            evidence => evidence.Id == plan.EffectiveChangeEvidence
                && evidence.Realization == CapabilityRealizationKind.Composed
                && evidence.Guarantees.Contains(MaterializationGuaranteeKind.CompleteCurrentObservation));
        MaterializationCurrentStateEnrichmentCompiler.Link(plan, profile);

        var json = JsonSerializer.Serialize(plan, MaterializationJsonSerializer.CreateOptions());
        var restored = JsonSerializer.Deserialize<MaterializationCurrentStateEnrichmentPlan>(
            json,
            MaterializationJsonSerializer.CreateOptions());
        Assert.Equal(plan, restored);
    }

    [Fact]
    public async Task Executor_DeduplicatesReadsAndPreservesDeliveryBeforeImageOrderingAndFences()
    {
        var compilation = Compile(Profile(completeChangeImage: false, includePointRead: true));
        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(compilation.Plan);
        var currentA = Observation("a");
        var requests = new List<MaterializationObservationReadRequest>();
        MaterializationObservationReader reader = (_, request) =>
        {
            requests.Add(request);
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Complete,
                observations: [currentA],
                evidenceReference: "tests/current-state/read/1"));
        };
        var page = Page(
            Change("delivery-a1", "change-a1", "a", "1", MaterializationChangeKind.Update),
            Change("delivery-a2", "change-a2", "a", "2", MaterializationChangeKind.Update),
            Change("delivery-b", "change-b", "b", "3", MaterializationChangeKind.Delete));
        var originalDeliveries = page.Deliveries;
        MaterializationChangeReadRequest request = new(
            scope: Scope,
            afterPosition: Position("0"),
            maximumDeliveries: 3,
            maximumBytes: MaximumBytes);
        MaterializationCurrentStateEnricher executor = new(plan, reader);

        var enriched = await executor.EnrichAsync(OperationContext.Create(), request, page);

        var read = Assert.Single(requests);
        Assert.Equal(MaterializationObservationReadKind.IdentityLookup, read.Kind);
        Assert.Equal(new[] { "a", "b" }, read.Keys.ToArray());
        Assert.Equal(2, read.MaximumRows);
        Assert.Equal(plan.MaximumReadBytes, read.MaximumBytes);
        Assert.Equal(originalDeliveries.Select(static delivery => delivery.Id), enriched.Deliveries.Select(static delivery => delivery.Id));
        Assert.Equal(originalDeliveries.Select(static delivery => delivery.Change.Id), enriched.Deliveries.Select(static delivery => delivery.Change.Id));
        Assert.Equal(originalDeliveries.Select(static delivery => delivery.Change.Position), enriched.Deliveries.Select(static delivery => delivery.Change.Position));
        Assert.Equal(originalDeliveries.Select(static delivery => delivery.Change.Before), enriched.Deliveries.Select(static delivery => delivery.Change.Before));
        Assert.Equal(page.ThroughPosition, enriched.ThroughPosition);
        Assert.Equal(page.State, enriched.State);
        Assert.All(enriched.Deliveries.Take(2), delivery =>
        {
            Assert.Equal(MaterializationChangeKind.Upsert, delivery.Change.Kind);
            Assert.Same(currentA, delivery.Change.After);
            Assert.Contains(plan.EvidenceReference, delivery.Change.EvidenceReference, StringComparison.Ordinal);
            Assert.Contains("tests/current-state/read/1", delivery.Change.EvidenceReference, StringComparison.Ordinal);
        });
        Assert.Equal(MaterializationChangeKind.Delete, enriched.Deliveries[2].Change.Kind);
        Assert.Null(enriched.Deliveries[2].Change.After);
    }

    [Fact]
    public async Task Executor_ReplayIsDeterministicAndConcurrentCallsRetainNoCrossPageState()
    {
        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(
            Compile(Profile(completeChangeImage: false, includePointRead: true)).Plan);
        var reads = 0;
        MaterializationObservationReader reader = (_, request) =>
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Complete,
                observations: [.. request.Keys.Select(Observation)],
                evidenceReference: "tests/current-state/replay"));
        };
        MaterializationCurrentStateEnricher executor = new(plan, reader);
        MaterializationChangeReadRequest request = new(
            scope: Scope,
            afterPosition: Position("0"),
            maximumDeliveries: 2,
            maximumBytes: MaximumBytes);
        var page = Page(
            Change("delivery-a", "change-a", "a", "1", MaterializationChangeKind.Update),
            Change("delivery-b", "change-b", "b", "2", MaterializationChangeKind.Update));

        var results = await Task.WhenAll(
            executor.EnrichAsync(OperationContext.Create(), request, page).AsTask(),
            executor.EnrichAsync(OperationContext.Create(), request, page).AsTask());

        Assert.Equal(2, Volatile.Read(ref reads));
        Assert.Equal(
            JsonSerializer.Serialize(results[0], MaterializationJsonSerializer.CreateOptions()),
            JsonSerializer.Serialize(results[1], MaterializationJsonSerializer.CreateOptions()));
    }

    [Fact]
    public async Task Executor_FailedReadCanReplayTheSameDeliveryIntoACompleteResult()
    {
        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(
            Compile(Profile(completeChangeImage: false, includePointRead: true)).Plan);
        var attempts = 0;
        MaterializationObservationReader reader = (_, request) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("simulated current-state read crash");
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Complete,
                observations: [.. request.Keys.Select(Observation)],
                evidenceReference: "tests/current-state/recovered"));
        };
        MaterializationCurrentStateEnricher executor = new(plan, reader);
        MaterializationChangeReadRequest request = new(
            scope: Scope,
            afterPosition: Position("0"),
            maximumDeliveries: 1,
            maximumBytes: MaximumBytes);
        var page = Page(Change("delivery-a", "change-a", "a", "1", MaterializationChangeKind.Update));

        await Assert.ThrowsAsync<IOException>(() =>
            executor.EnrichAsync(OperationContext.Create(), request, page).AsTask());
        var recovered = await executor.EnrichAsync(OperationContext.Create(), request, page);

        Assert.Equal(2, Volatile.Read(ref attempts));
        var delivery = Assert.Single(recovered.Deliveries);
        Assert.Equal(new MaterializationDeliveryId("delivery-a"), delivery.Id);
        Assert.Equal(new MaterializationChangeId("change-a"), delivery.Change.Id);
        Assert.Equal(MaterializationChangeKind.Upsert, delivery.Change.Kind);
    }

    [Fact]
    public async Task Executor_ChunksDistinctIdentitiesWithoutRereadingRepeatedRoots()
    {
        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(
            Compile(
                Profile(completeChangeImage: false, includePointRead: true),
                maximumIdentitiesPerRead: 2).Plan);
        List<ImmutableArray<string>> batches = [];
        MaterializationObservationReader reader = (_, request) =>
        {
            batches.Add(request.Keys);
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Complete,
                observations: [.. request.Keys.Select(Observation)],
                evidenceReference: $"tests/current-state/batch/{batches.Count}"));
        };
        MaterializationCurrentStateEnricher executor = new(plan, reader);
        MaterializationChangeReadRequest request = new(
            scope: Scope,
            afterPosition: Position("0"),
            maximumDeliveries: 4,
            maximumBytes: MaximumBytes);
        var page = Page(
            Change("delivery-a1", "change-a1", "a", "1", MaterializationChangeKind.Update),
            Change("delivery-b", "change-b", "b", "2", MaterializationChangeKind.Update),
            Change("delivery-a2", "change-a2", "a", "3", MaterializationChangeKind.Update),
            Change("delivery-c", "change-c", "c", "4", MaterializationChangeKind.Update));

        var enriched = await executor.EnrichAsync(OperationContext.Create(), request, page);

        Assert.Equal(2, batches.Count);
        Assert.Equal(new[] { "a", "b" }, batches[0].ToArray());
        Assert.Equal(new[] { "c" }, batches[1].ToArray());
        Assert.Equal(4, enriched.Deliveries.Length);
    }

    [Fact]
    public async Task Executor_RejectsPartialEvidenceAndHonorsCancellationBeforeReading()
    {
        var plan = Assert.IsType<MaterializationCurrentStateEnrichmentPlan>(
            Compile(Profile(completeChangeImage: false, includePointRead: true)).Plan);
        var reads = 0;
        MaterializationObservationReader partialReader = (_, _) =>
        {
            Interlocked.Increment(ref reads);
            return ValueTask.FromResult(new RelationQuerySourceReadResult(
                state: RelationQuerySourceReadState.Partial,
                observations: [Observation("a")],
                evidenceReference: "tests/current-state/partial"));
        };
        MaterializationCurrentStateEnricher executor = new(plan, partialReader);
        MaterializationChangeReadRequest request = new(
            scope: Scope,
            afterPosition: Position("0"),
            maximumDeliveries: 1,
            maximumBytes: MaximumBytes);
        var page = Page(Change("delivery-a", "change-a", "a", "1", MaterializationChangeKind.Update));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.EnrichAsync(OperationContext.Create(), request, page).AsTask());

        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.EnrichAsync(
                OperationContext.Create(cancellationToken: cancellation.Token),
                request,
                page).AsTask());
        Assert.Equal(1, Volatile.Read(ref reads));
    }

    static MaterializationCurrentStateEnrichmentCompilationResult Compile(
        MaterializationCapabilityProfile profile,
        long maximumIdentitiesPerRead = MaximumItems) =>
        MaterializationCurrentStateEnrichmentCompiler.Compile(
            input: Input,
            shape: Shape,
            source: Source,
            changeRequirement: ChangeRequirement(),
            profile: profile,
            policy: new(
                maximumIdentitiesPerRead: maximumIdentitiesPerRead,
                maximumReadBytes: MaximumBytes,
                evidenceReference: "tests/current-state/compiler/v1"));

    static MaterializationCapabilityRequirement ChangeRequirement() => new(
        id: new("orders/changes"),
        capability: MaterializationCapabilityKind.SourceChangeDelivery,
        guarantees:
        [
            MaterializationGuaranteeKind.StableOrdering,
            MaterializationGuaranteeKind.AtLeastOnceDelivery,
            MaterializationGuaranteeKind.BaselinePlusCatchUp,
            MaterializationGuaranteeKind.CompleteMutationDelivery,
            MaterializationGuaranteeKind.BeforeImage
        ],
        operatingLimits:
        [
            new(MaterializationLimitKind.ChangeItems, MaximumItems),
            new(MaterializationLimitKind.ReadBytes, MaximumBytes)
        ],
        modes: MaterializationSynchronizationMode.All);

    static MaterializationCapabilityProfile Profile(bool completeChangeImage, bool includePointRead)
    {
        var evidence = ImmutableArray.CreateBuilder<MaterializationCapabilityEvidence>(includePointRead ? 2 : 1);
        evidence.Add(new(
            id: new("source/change-signal"),
            capability: MaterializationCapabilityKind.SourceChangeDelivery,
            realization: CapabilityRealizationKind.Native,
            guarantees:
            [
                .. ChangeRequirement().Guarantees,
                .. completeChangeImage
                    ? [MaterializationGuaranteeKind.CompleteCurrentObservation]
                    : ImmutableArray<MaterializationGuaranteeKind>.Empty
            ],
            operatingLimits: ChangeRequirement().OperatingLimits,
            sourceReferences: ["tests/current-state/change-signal"]));
        if (includePointRead)
        {
            evidence.Add(new(
                id: new("source/current-state-read"),
                capability: MaterializationCapabilityKind.SourceBatchedPointRead,
                realization: CapabilityRealizationKind.Native,
                guarantees:
                [
                    MaterializationGuaranteeKind.StableOrdering,
                    MaterializationGuaranteeKind.RequestLocalCompleteness
                ],
                operatingLimits:
                [
                    new(MaterializationLimitKind.ReadItems, MaximumItems),
                    new(MaterializationLimitKind.ReadBytes, MaximumBytes)
                ],
                sourceReferences: ["tests/current-state/point-read"]));
        }
        return new(
            id: new("tests/current-state/profile/v1"),
            role: MaterializationEndpointRole.Source,
            subject: Source.Value,
            evidence: evidence.MoveToImmutable());
    }

    static MaterializationChangePage Page(params MaterializationChangeDelivery[] deliveries) => new(
        deliveries: [.. deliveries],
        throughPosition: Position(deliveries.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        state: MaterializationChangePageState.CaughtUp);

    static MaterializationChangeDelivery Change(
        string deliveryId,
        string changeId,
        string identity,
        string position,
        MaterializationChangeKind kind)
    {
        var before = kind == MaterializationChangeKind.Create ? null : Observation(identity);
        var after = kind == MaterializationChangeKind.Delete ? null : Observation(identity);
        return new(
            id: new(deliveryId),
            change: new(
                id: new(changeId),
                subjectIdentity: identity,
                scope: Scope,
                shape: Shape,
                position: Position(position),
                kind: kind,
                before: before,
                after: after,
                occurredAtUtc: Timestamp,
                observedAtUtc: Timestamp,
                evidenceReference: "tests/current-state/raw-change"),
            deliveredAtUtc: Timestamp,
            evidenceReference: "tests/current-state/raw-delivery");
    }

    static MaterializationSourcePosition Position(string value) => new(
        formatVersion: 1,
        scope: Scope,
        value: value);

    static RelationQuerySourceReadObservation Observation(string identity) => new(
        identity: identity,
        shape: Shape,
        fields: []);
}
