namespace Cohesive.Relations.Model;

/// <summary>
/// Compatibility validation surface for the indexed Relations observation representation.
/// </summary>
/// <remarks>
/// The core <see cref="Cohesive.Model.ObservationValidator"/> owns shape-value compatibility. This wrapper
/// preserves the current Relations API until the physical observation interpretation adopts the core model.
/// </remarks>
public static class ObservationShapeValidator
{
    /// <summary>Validates that an indexed observation adheres to the supplied shape semantics.</summary>
    /// <param name="observation">Indexed observation payload to validate.</param>
    /// <param name="shape">Expected semantic shape.</param>
    /// <param name="validationError">Validation failure reason when validation fails.</param>
    /// <param name="graph">Optional shape graph used to resolve named type references.</param>
    /// <returns><see langword="true"/> when the observation satisfies the shape; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="observation"/> or <paramref name="shape"/> is <see langword="null"/>.
    /// </exception>
    public static bool TryValidateAgainstShape(
        Observation observation,
        Shape shape,
        out string? validationError,
        ShapeGraph? graph = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(shape);

        if (observation.ShapeId != shape.Id)
        {
            validationError =
                $"Observation shape '{observation.ShapeId.Value}' does not match expected shape '{shape.Id.Value}'.";
            return false;
        }

        return Cohesive.Model.ObservationValidator.TryValidateAgainstShape(
            fields: observation.Fields,
            shape: shape,
            validationError: out validationError,
            graph: graph);
    }
}
