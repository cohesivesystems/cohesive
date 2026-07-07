using Cohesive.Prelude;

namespace Cohesive.Examples.Transportation.Tests;

public sealed class LoadTests
{
    static readonly IReadOnlyDictionary<RouteDistanceResolver.RouteLeg, Distance> DefaultDistanceMatrix =
        new Dictionary<RouteDistanceResolver.RouteLeg, Distance>
        {
            [new("SFO", "RNO")] = Distance.FromMiles(218m),
            [new("SFO", "SAC")] = Distance.FromMiles(87m),
            [new("SAC", "RNO")] = Distance.FromMiles(132m)
        };

    [Fact]
    public void CreateState_InitializesDraftState()
    {
        var load = new Load();
        var state = load.CreateState("load-1");

        Assert.Equal(LoadStatus.Draft, load.Status.Get(state));
        Assert.Empty(load.Stops.Get(state));
        Assert.Null(load.CarrierId.Get(state));
        Assert.Equal(0m, load.PlannedDistance.Get(state).Miles);
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public void Snapshot_BindsTypedEntityToStateForFieldReads()
    {
        var load = Load.Instance;
        var initial = load.CreateState(entityId: "load-snapshot-1");
        var assigned = load.AssignCarrier.Apply(initial, new(CarrierId: "carrier-7")).NewState;
        var snapshot = load.Snapshot(assigned);

        Assert.Same(load, snapshot.Entity);
        Assert.Same(assigned, snapshot.State);
        Assert.Equal("load-snapshot-1", snapshot.EntityId.Value);
        Assert.Equal(1, snapshot.Version);
        Assert.Equal(LoadStatus.Assigned, snapshot.Get(e => e.Status));
        Assert.True(snapshot.TryGet(e => e.CarrierId, out var carrierId));
        Assert.Equal("carrier-7", carrierId);
        Assert.Empty(snapshot.Get(e => e.Stops));
    }

    [Fact]
    public void Snapshot_ProvidesPresenceFallbackAndRequireHelpers()
    {
        var load = Load.Instance;
        var initialSnapshot = load.Snapshot(load.CreateState(entityId: "load-snapshot-2"));
        var assignedSnapshot = load.Snapshot(load.AssignCarrier.Apply(initialSnapshot, new(CarrierId: "carrier-12")).NewState);

        Assert.False(initialSnapshot.Has(e => e.CarrierId));
        Assert.Equal("unassigned", initialSnapshot.GetOrDefault(e => e.CarrierId, "unassigned"));
        Assert.Throws<SemanticRuleViolationException>(() => initialSnapshot.Require(e => e.CarrierId));

        Assert.True(assignedSnapshot.Has(e => e.CarrierId));
        Assert.Equal("carrier-12", assignedSnapshot.Require(e => e.CarrierId));
        Assert.Equal(Distance.AdditiveIdentity, assignedSnapshot.GetOrDefault(e => e.PlannedDistance, Distance.FromMiles(999m)));
    }

    [Fact]
    public async Task AddStop_InDraftWithValidWindow_UpdatesStopsAndEmitsEffect()
    {
        var load = new Load();
        var state = load.CreateState("load-2");
        var addRequest = new Load.AddStopRequest(
            Code: "SEA",
            WindowStartUtc: DateTimeOffset.ParseIso8601Utc("2026-02-11T10:00:00"),
            WindowEndUtc: new(2026, 2, 11, 12, 0, 0, TimeSpan.Zero),
            Type: StopType.Pickup
            );

        var addResult = load.AddStop.Apply(state, new(Stop: addRequest, Position: 0, IsWindowValid: true));

        var storedStop = Assert.Single(load.Stops.Get(addResult.NewState));
        Assert.Equal(new(
                Code: addRequest.Code,
                WindowStartUtc: addRequest.WindowStartUtc,
                WindowEndUtc: addRequest.WindowEndUtc,
                Type: addRequest.Type
            ),
            storedStop
            );
        Assert.Equal(1, addResult.NewState.Version);
        Assert.Equal(0m, load.PlannedDistance.Get(addResult.NewState).Miles);
        Assert.Equal(nameof(Load.AddStop), addResult.TransitionName);
        Assert.Equal(2, addResult.Effects.Count);

        var stopAdded = Assert.Single(addResult.Effects, x => x.Name == "StopAdded");
        Assert.Equal("load-2", stopAdded.Payload.GetProperty("loadId").GetString());
        Assert.Equal("SEA", stopAdded.Payload.GetProperty("stopCode").GetString());

        var mileageRequest = Assert.Single(addResult.Effects, x => x.Name == Load.CalculateDistanceRequestName);
        Assert.NotNull(mileageRequest.Continuation);
        Assert.Equal(nameof(Load.ApplyDistanceCalculation), mileageRequest.Continuation!.TransitionName);
        Assert.True(mileageRequest.Continuation.HasDirectReference);
        Assert.NotNull(mileageRequest.Snapshot);
        Assert.Contains("Stops", mileageRequest.Snapshot!.FieldNames);
        
        var applyResult = await ExecuteDistanceContinuationAsync(
            transitionResult: addResult,
            resolver: CreateDistanceResolver());
        Assert.Equal(nameof(Load.ApplyDistanceCalculation), applyResult.TransitionName);
        Assert.Equal(2, applyResult.NewVersion);
        Assert.Equal(0m, load.PlannedDistance.Get(applyResult.NewState).Miles);
    }

    [Fact]
    public void AddStop_InvalidWindow_ThrowsAndDoesNotMutateState()
    {
        var load = new Load();
        var state = load.CreateState("load-3");
        var request = new Load.AddStopRequest(
            Code: "PDX",
            WindowStartUtc: new(2026, 2, 11, 14, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new(2026, 2, 11, 16, 0, 0, TimeSpan.Zero),
            Type: StopType.Delivery
            );

        Assert.Throws<TransitionPreconditionException>(
            () => load.AddStop.Apply(state, new(Stop: request, Position: 0, IsWindowValid: false))
            );

        Assert.Empty(load.Stops.Get(state));
        Assert.Equal(LoadStatus.Draft, load.Status.Get(state));
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public async Task AddStop_WaypointInBetweenPickupAndDelivery_PreservesStopOrder()
    {
        var load = new Load();
        var state = load.CreateState("load-8");
        var pickup = new Load.AddStopRequest(
            Code: "SFO",
            WindowStartUtc: new DateTimeOffset(2026, 2, 11, 8, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 2, 11, 9, 0, 0, TimeSpan.Zero),
            Type: StopType.Pickup);
        var delivery = new Load.AddStopRequest(
            Code: "RNO",
            WindowStartUtc: new DateTimeOffset(2026, 2, 11, 12, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 2, 11, 13, 0, 0, TimeSpan.Zero),
            Type: StopType.Delivery);
        var waypoint = new Load.AddStopRequest(
            Code: "SAC",
            WindowStartUtc: new DateTimeOffset(2026, 2, 11, 10, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 2, 11, 11, 0, 0, TimeSpan.Zero),
            Type: StopType.Waypoint);

        var resolver = CreateDistanceResolver();
        state = await AddStopAndApplyMileage(load, state, new(Stop: pickup, Position: 0, IsWindowValid: true), resolver);
        state = await AddStopAndApplyMileage(load, state, new(Stop: delivery, Position: 1, IsWindowValid: true), resolver);
        state = await AddStopAndApplyMileage(load, state, new(Stop: waypoint, Position: 1, IsWindowValid: true), resolver);

        var stops = load.Stops.Get(state);
        Assert.Equal(3, stops.Count);
        Assert.Equal("SFO", stops[0].Code);
        Assert.Equal(StopType.Pickup, stops[0].Type);
        Assert.Equal("SAC", stops[1].Code);
        Assert.Equal(StopType.Waypoint, stops[1].Type);
        Assert.Equal("RNO", stops[2].Code);
        Assert.Equal(StopType.Delivery, stops[2].Type);
        Assert.Equal(219m, load.PlannedDistance.Get(state).Miles);
    }

    [Fact]
    public async Task AddStop_WhenStaticMatrixMissesLeg_UsesExternalMileageFallback()
    {
        var externalCalls = 0;
        var resolver = new RouteDistanceResolver(
            staticDistanceByLeg: new Dictionary<RouteDistanceResolver.RouteLeg, Distance>(),
            externalDistanceProvider: (from, to, _) =>
            {
                externalCalls++;
                return Task.FromResult(from == "DEN" && to == "ABQ" ? Distance.FromMiles(449m) : Distance.AdditiveIdentity);
            });
        var load = new Load();
        var state = load.CreateState("load-8b");
        var den = new Load.AddStopRequest(
            Code: "DEN",
            WindowStartUtc: DateTimeOffset.ParseIso8601Utc("2026-02-11T08:00"),
            WindowEndUtc: DateTimeOffset.ParseIso8601Utc("2026-02-11T09:00"),
            Type: StopType.Pickup
            );
        var abq = new Load.AddStopRequest(
            Code: "ABQ",
            WindowStartUtc: new DateTimeOffset(2026, 2, 11, 12, 0, 0, TimeSpan.Zero),
            WindowEndUtc: new DateTimeOffset(2026, 2, 11, 13, 0, 0, TimeSpan.Zero),
            Type: StopType.Delivery
            );
        state = await AddStopAndApplyMileage(load, state, new Load.AddStopInput(Stop: den, Position: 0, IsWindowValid: true), resolver);
        state = await AddStopAndApplyMileage(load, state, new Load.AddStopInput(Stop: abq, Position: 1, IsWindowValid: true), resolver);
        Assert.Equal(1, externalCalls);
        Assert.Equal(449m, load.PlannedDistance.Get(state).Miles);
    }

    [Fact]
    public async Task AddStop_StaleMileageResult_IsRejectedBySnapshotToken()
    {
        var load = new Load();
        var state = load.CreateState("load-stale-1");
        
        var first = load.AddStop.Apply(state, new Load.AddStopInput(
            Stop: new(
                Code: "SFO",
                WindowStartUtc: new DateTimeOffset(2026, 2, 11, 8, 0, 0, TimeSpan.Zero),
                WindowEndUtc: new DateTimeOffset(2026, 2, 11, 9, 0, 0, TimeSpan.Zero),
                Type: StopType.Pickup
                ),
            Position: 0,
            IsWindowValid: true));

        _ = load.AddStop.Apply(first.NewState, new Load.AddStopInput(
            Stop: new(
                Code: "RNO",
                WindowStartUtc: new DateTimeOffset(2026, 2, 11, 12, 0, 0, TimeSpan.Zero),
                WindowEndUtc: new DateTimeOffset(2026, 2, 11, 13, 0, 0, TimeSpan.Zero),
                Type: StopType.Delivery
                ),
            Position: 1,
            IsWindowValid: true));

        var ex = await Assert.ThrowsAsync<SemanticRuleViolationException>(
            () => ExecuteDistanceContinuationAsync(
                transitionResult: first,
                resolver: CreateDistanceResolver()));
        Assert.Contains("snapshot token mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssignCarrier_InDraftWithCarrierId_TransitionsToAssigned()
    {
        var load = new Load();
        var state = load.CreateState("load-4");

        var result = load.AssignCarrier.Apply(state, new(CarrierId: "carrier-9"));

        Assert.Equal(LoadStatus.Assigned, load.Status.Get(result.NewState));
        Assert.Equal("carrier-9", load.CarrierId.Get(result.NewState));
        Assert.Equal(1, result.NewVersion);
        Assert.Equal(nameof(Load.AssignCarrier), result.TransitionName);

        var effect = Assert.Single(result.Effects);
        Assert.Equal("CarrierAssigned", effect.Name);
        Assert.Equal("load-4", effect.Payload.GetProperty("loadId").GetString());
        Assert.Equal("carrier-9", effect.Payload.GetProperty("carrierId").GetString());
    }

    [Fact]
    public void AssignCarrier_EmptyCarrierId_ThrowsAndRemainsDraft()
    {
        var load = new Load();
        var state = load.CreateState("load-5");

        Assert.Throws<TransitionPreconditionException>(
            () => load.AssignCarrier.Apply(state, new(CarrierId: "")));

        Assert.Equal(LoadStatus.Draft, load.Status.Get(state));
        Assert.Null(load.CarrierId.Get(state));
        Assert.Equal(0, state.Version);
    }

    [Fact]
    public void StartTransit_AfterAssignment_TransitionsToInTransit()
    {
        var load = new Load();
        var state = load.CreateState("load-6");
        var assigned = load.AssignCarrier.Apply(state, new(CarrierId: "carrier-42"));

        var result = load.StartTransit.Apply(assigned.NewState, new());

        Assert.Equal(LoadStatus.InTransit, load.Status.Get(result.NewState));
        Assert.Equal("carrier-42", load.CarrierId.Get(result.NewState));
        Assert.Equal(2, result.NewVersion);
        Assert.Equal(nameof(Load.StartTransit), result.TransitionName);

        var effect = Assert.Single(result.Effects);
        Assert.Equal("LoadInTransit", effect.Name);
        Assert.Equal("load-6", effect.Payload.GetProperty("loadId").GetString());
    }

    [Fact]
    public void StartTransit_FromDraft_Throws()
    {
        var load = new Load();
        var state = load.CreateState("load-7");

        Assert.Throws<TransitionPreconditionException>(() => load.StartTransit.Apply(state, new Load.StartTransitInput()));

        Assert.Equal(LoadStatus.Draft, load.Status.Get(state));
        Assert.Equal(0, state.Version);
    }

    static RouteDistanceResolver CreateDistanceResolver()
    {
        return new(
            staticDistanceByLeg: DefaultDistanceMatrix,
            externalDistanceProvider: (_, _, _) => Task.FromResult(Distance.AdditiveIdentity)
            );
    }

    static async Task<EntityState> AddStopAndApplyMileage(
        Load load,
        EntityState state,
        Load.AddStopInput input,
        RouteDistanceResolver resolver
        )
    {
        var addResult = load.AddStop.Apply(state, input);
        var applyResult = await ExecuteDistanceContinuationAsync(
            transitionResult: addResult,
            resolver: resolver);
        return applyResult.NewState;
    }

    static async Task<TransitionResult> ExecuteDistanceContinuationAsync(
        TransitionResult transitionResult,
        RouteDistanceResolver resolver,
        CancellationToken cancellationToken = default
        )
    {
        var request = Assert.Single(
            transitionResult.Effects,
            effect => effect.Name == Load.CalculateDistanceRequestName);

        var binding = EffectHandlerBinding<Load.CalculateDistanceRequest, Load.DistanceCalculatedResult>.FromJson(resolver);
        var handlerResult = await binding
            .HandleAsync(OperationContext.Create(cancellationToken: cancellationToken), request)
            .ConfigureAwait(false);

        var continuation = Assert.IsType<EffectContinuation>(request.Continuation);
        continuation.EnsureSnapshotMatches(request.Snapshot);
        return await continuation.RunAsync(handlerResult, cancellationToken).ConfigureAwait(false);
    }
}
