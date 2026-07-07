namespace Cohesive.Relations.Hydration;

/// <summary>
/// Field-selective observation query for hydration backends.
/// </summary>
public sealed record ObservationHydrationOptions(
    ShapeId Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string>? Keys = null
    );
