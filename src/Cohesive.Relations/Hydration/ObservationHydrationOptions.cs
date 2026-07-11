namespace Cohesive.Relations.Hydration;

/// <summary>
/// Field-selective observation query for hydration backends.
/// </summary>
/// <param name="Schema">Schema of the observations to hydrate.</param>
/// <param name="Fields">Canonical field names to include in each hydrated observation.</param>
/// <param name="Keys">Optional observation keys that constrain which observations are hydrated.</param>
public sealed record ObservationHydrationOptions(
    ShapeId Schema,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string>? Keys = null
    );
