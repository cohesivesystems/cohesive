using Cohesive.Relations.Model;

namespace Cohesive.Relations.Mapping;

/// <summary>
/// Compiled observed-shape-to-object mapper.
/// </summary>
public sealed class ObservationObjectMapper<T> : IObservationObjectMapper<T>
{
    readonly Func<Observation, T> map;

    internal ObservationObjectMapper(ObservationLayout layout, Func<Observation, T> map)
    {
        Layout = Guard.RequireNotNull(layout);
        this.map = Guard.RequireNotNull(map);
    }
    
    public ObservationLayout Layout { get; }

    /// <inheritdoc />
    public T Map(Observation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Layout.Schema != Layout.Schema)
            throw new InvalidOperationException($"Mapper layout schema '{Layout.Schema}' does not match observation schema '{observation.Layout.Schema}'.");

        return map(observation);
    }
}

/// <summary>
/// Observation-to-object mapper factory.
/// </summary>
public static class ObservationObjectMapper
{
    /// <summary>
    /// Starts a builder for mapping observations to <typeparamref name="T"/>.
    /// </summary>
    public static ObservationObjectMapperBuilder<T> For<T>(
        ObservationLayout layout,
        ShapeMappingContext? context = null
        ) => new(layout, context);
}
