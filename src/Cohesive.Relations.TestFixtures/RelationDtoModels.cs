namespace Cohesive.Relations.TestFixtures;

/// <summary>A single-source DTO projected from a Load observation.</summary>
/// <param name="Id">Stable load identity.</param>
/// <param name="Status">Current load status.</param>
/// <param name="Amount">Load amount.</param>
public sealed record LoadSummaryDto(string Id, string Status, decimal Amount);

/// <summary>A joined DTO that flattens Load, Customer, and Equipment values.</summary>
/// <param name="Id">Stable load identity.</param>
/// <param name="CustomerId">Customer reference carried by the load.</param>
/// <param name="CustomerName">Name read from the related customer.</param>
/// <param name="CustomerType">Type read from the related customer.</param>
/// <param name="EquipmentId">Equipment reference carried by the load.</param>
/// <param name="EquipmentNumber">Number read from the related equipment.</param>
/// <param name="EquipmentType">Type read from the related equipment.</param>
/// <param name="Status">Current load status.</param>
/// <param name="Amount">Load amount.</param>
public sealed record LoadSearchDto(
    string Id,
    string CustomerId,
    string CustomerName,
    string CustomerType,
    string EquipmentId,
    string EquipmentNumber,
    string EquipmentType,
    string Status,
    decimal Amount);

/// <summary>
/// Diagnostic target with an intentionally incompatible CustomerName member used to measure conversion failures.
/// </summary>
/// <param name="Id">Stable load identity.</param>
/// <param name="CustomerId">Customer reference carried by the load.</param>
/// <param name="CustomerName">Integer target for a canonical string customer name.</param>
/// <param name="CustomerType">Type read from the related customer.</param>
/// <param name="EquipmentId">Equipment reference carried by the load.</param>
/// <param name="EquipmentNumber">Number read from the related equipment.</param>
/// <param name="EquipmentType">Type read from the related equipment.</param>
/// <param name="Status">Current load status.</param>
/// <param name="Amount">Load amount.</param>
public sealed record IncompatibleLoadSearchDto(
    string Id,
    string CustomerId,
    int CustomerName,
    string CustomerType,
    string EquipmentId,
    string EquipmentNumber,
    string EquipmentType,
    string Status,
    decimal Amount);
