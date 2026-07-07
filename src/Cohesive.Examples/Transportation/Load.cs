namespace Cohesive.Examples.Transportation;

/// <summary>
/// Example lifecycle states for a load.
/// </summary>
public enum LoadStatus
{
    Draft = 0,
    Assigned = 1,
    InTransit = 2,
    Completed = 3
}

/// <summary>
/// Stop type in a load route.
/// </summary>
public enum StopType
{
    Pickup = 0,
    Delivery = 1,
    Waypoint = 2
}

/// <summary>
/// Domain stop definition for load planning.
/// </summary>
/// <param name="Code">Stop location code</param>
/// <param name="WindowStartUtc">Stop window start date</param>
/// <param name="WindowEndUtc">Stop window end date</param>
/// <param name="Type">Stop type</param>
public sealed record Stop(
    string Code, 
    DateTimeOffset WindowStartUtc, 
    DateTimeOffset WindowEndUtc, 
    StopType Type
    );

/// <summary>
/// Example load entity.
/// </summary>
public sealed class Load : Entity<Load>
{
    public const string CalculateDistanceRequestName = "CalculateDistance";

    public sealed record AddStopRequest(string Code, DateTimeOffset WindowStartUtc, DateTimeOffset WindowEndUtc, StopType Type);

    public sealed record CalculateDistanceRequest(
        string LoadId,
        IReadOnlyList<Stop> Stops
        ) : IEffectRequest<DistanceCalculatedResult>
    {
        public static string RequestName => CalculateDistanceRequestName;
    }

    public sealed record DistanceCalculatedResult(Distance TotalDistance);
    
    /// <summary>
    /// A request to add a stop to a load.
    /// </summary>
    /// <param name="Stop">Stop data.</param>
    /// <param name="Position">The position</param>
    /// <param name="IsWindowValid"></param>
    public sealed record AddStopInput(AddStopRequest Stop, int Position, bool IsWindowValid)
    {
        public Stop MappedStop => new(
            Code: Stop.Code,
            WindowStartUtc: Stop.WindowStartUtc,
            WindowEndUtc: Stop.WindowEndUtc,
            Type: Stop.Type
            );

        public string StopCode => Stop.Code;
    }
    
    public sealed record AssignCarrierInput(string CarrierId);
    
    public sealed record StartTransitInput;

    static readonly TypeRef StopObjectType = DomainTypes.Object(
        new(nameof(Stop.Code), DomainTypes.String()),
        new(nameof(Stop.WindowStartUtc), DomainTypes.DateTime()),
        new(nameof(Stop.WindowEndUtc), DomainTypes.DateTime()),
        new(nameof(Stop.Type), DomainTypes.Enum(nameof(StopType), Enum.GetNames<StopType>()))
        );

    static readonly FieldDefinition StatusDef = FieldDefinition.Create(
        name: new(nameof(Status)),
        type: DomainTypes.Enum(nameof(LoadStatus), Enum.GetNames<LoadStatus>())
        );

    static readonly FieldDefinition StopsDef = FieldDefinition.Create(
        name: new(value: nameof(Stops)),
        type: StopObjectType,
        cardinality: FieldCardinality.Many
        );

    static readonly FieldDefinition CarrierIdDef = FieldDefinition.Create(
        name: new(nameof(CarrierId)),
        type: DomainTypes.String(),
        presence: FieldPresence.Optional
        );

    static readonly FieldDefinition PlannedDistanceDef = FieldDefinition.Create(
        name: new(nameof(PlannedDistance)),
        type: DomainTypes.Quantity(nameof(Distance), ScalarTypeKind.Decimal)
        );

    /// <summary>
    /// Creates the load entity definition.
    /// </summary>
    public Load()
    {
        Status = Field(StatusDef, LoadStatus.Draft);
        Stops = Field<IReadOnlyList<Stop>>(StopsDef, []);
        CarrierId = Field<string?>(CarrierIdDef, initialValue: null);
        PlannedDistance = Field(PlannedDistanceDef, Distance.AdditiveIdentity);

        Invariant("StatusTransitionsAreValid",
            e => e.Status == LoadStatus.Draft || e.Status == LoadStatus.Assigned || e.Status == LoadStatus.InTransit || e.Status == LoadStatus.Completed
            );

        Invariant("AssignedRequiresCarrier",
            e => e.Status != LoadStatus.Assigned || (e.CarrierId != null && e.CarrierId != "")
            );

        ApplyDistanceCalculation = Transition<DistanceCalculatedResult>(
            nameof(ApplyDistanceCalculation),
            t => t
                .Set(e => e.PlannedDistance, (_, p) => p.TotalDistance)
                .EmitSnapshot("MileageApplied", (snapshot, _) => new { loadId = snapshot.EntityId.Value })
            );

        AddStop = Transition<AddStopInput>(
            name: nameof(AddStop),
            t => t
                .Requires(
                    "CanAddStop",
                    (e, req) => e.Status == LoadStatus.Draft
                                && req.IsWindowValid
                                && req.Position >= 0
                                && req.Position <= e.Stops.Count
                    )
                .Insert(field: e => e.Stops, index: (_, req) => req.Position, value: (_, req) => req.MappedStop)
                .EmitSnapshot("StopAdded", (snapshot, req) => new { loadId = snapshot.EntityId.Value, stopCode = req.StopCode })
                .RequestSnapshot<CalculateDistanceRequest, DistanceCalculatedResult>((snapshot, _) => new(LoadId: snapshot.EntityId.Value, Stops: snapshot.Get(e => e.Stops)))
                .Then(ApplyDistanceCalculation)
            );

        AssignCarrier = Transition<AssignCarrierInput>(
            nameof(AssignCarrier),
            t => t
                .Requires("CanAssignCarrier", (e, p) => e.Status == LoadStatus.Draft && p.CarrierId != "")
                .Set(e => e.CarrierId, (_, p) => p.CarrierId)
                .Set(e => e.Status, (_, _) => LoadStatus.Assigned)
                .EmitSnapshot("CarrierAssigned", (snapshot, p) => new { loadId = snapshot.EntityId.Value, carrierId = p.CarrierId })
            );

        StartTransit = Transition<StartTransitInput>(
            nameof(StartTransit),
            t => t
                .Requires("CanStartTransit", (load, _) => load.Status == LoadStatus.Assigned)
                .Set(load => load.Status, (_, _) => LoadStatus.InTransit)
                .EmitSnapshot("LoadInTransit", (snapshot, _) => new { loadId = snapshot.EntityId.Value })
            );

    }

    /// <summary>
    /// Current lifecycle status.
    /// </summary>
    public Field<LoadStatus> Status { get; }
    
    /// <summary>
    /// Planned stops.
    /// </summary>
    public Field<IReadOnlyList<Stop>> Stops { get; }
    
    /// <summary>
    /// Assigned carrier id when available.
    /// </summary>
    public Field<string?> CarrierId { get; }

    /// <summary>
    /// Planned route distance for the current stop sequence.
    /// </summary>
    public Field<Distance> PlannedDistance { get; }

    /// <summary>
    /// Transition that adds a stop while in the draft state.
    /// </summary>
    public Transition<Load, AddStopInput> AddStop { get; }

    /// <summary>
    /// Transition that assigns a carrier and moves the load to the assigned state.
    /// </summary>
    public Transition<Load, AssignCarrierInput> AssignCarrier { get; }

    /// <summary>
    /// Transition that moves an assigned load into the in-transit state.
    /// </summary>
    public Transition<Load, StartTransitInput> StartTransit { get; }

    /// <summary>
    /// Continuation transition that applies externally calculated route mileage.
    /// </summary>
    public Transition<Load, DistanceCalculatedResult> ApplyDistanceCalculation { get; }
}
