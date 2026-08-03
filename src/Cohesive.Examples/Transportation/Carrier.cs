namespace Cohesive.Examples.Transportation;

/// <summary>
/// Example carrier entity.
/// </summary>
/// <remarks>
/// This example remains an ARI-218 compatibility fixture for the superseded continuation/effect authoring surface.
/// It is not an execution-kernel authority or a template for new integrations.
/// </remarks>
public sealed class Carrier : Entity
{
    public sealed record ReserveLoadInput(string LoadId, IReadOnlyList<string> NextReservedLoads);

    static readonly FieldDefinition TotalCapacityDef = FieldDefinition.Create(
        new(nameof(TotalCapacity)),
        DomainTypes.Int32(),
        mutability: FieldMutability.WriteOnce
    );

    static readonly FieldDefinition ReservedLoadsDef = FieldDefinition.Create(
        new(nameof(ReservedLoads)),
        DomainTypes.String(),
        cardinality: FieldCardinality.Many
    );

    /// <summary>
    /// Creates the carrier entity definition.
    /// </summary>
    public Carrier()
    {
        TotalCapacity = Field<int>(TotalCapacityDef, value => value >= 0);
        ReservedLoads = Field<IReadOnlyList<string>>(ReservedLoadsDef, []);
        Invariant<Carrier>(
            "ReservationsWithinCapacity", 
            e => e.ReservedLoads.Count <= e.TotalCapacity
            );
        ReserveLoad = Transition<Carrier, ReserveLoadInput>(
            nameof(ReserveLoad),
            t => t
                .Requires("ValidRequest", (carrier, req) => req.LoadId != "" && carrier.ReservedLoads.Count < carrier.TotalCapacity)
                .Set(e => e.ReservedLoads, (_, req) => req.NextReservedLoads)
                .EmitSnapshot("CarrierCapacityReserved", (snapshot, req) => new { carrierId = snapshot.EntityId.Value, loadId = req.LoadId })
            );
    }

    /// <summary>
    /// Total load capacity.
    /// </summary>
    public Field<int> TotalCapacity { get; }
    
    /// <summary>
    /// Set of load ids currently reserved.
    /// </summary>
    public Field<IReadOnlyList<string>> ReservedLoads { get; }

    /// <summary>
    /// Transition that reserves capacity for a load.
    /// </summary>
    public Transition<Carrier, ReserveLoadInput> ReserveLoad { get; }
}
