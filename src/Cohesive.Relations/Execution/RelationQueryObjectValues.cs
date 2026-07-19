namespace Cohesive.Relations.Execution;

/// <summary>Deterministic object-path operations shared by runtime evidence reconstruction and output shaping.</summary>
static class RelationQueryObjectValues
{
    public static ObservationValue Empty { get; } =
        ObservationValue.EmptyObject;

    public static ObservationValue Set(ObservationValue value, FieldPath path, ObservationValue fieldValue) =>
        value.WithField(path, fieldValue);

    public static ObservationValue Remove(ObservationValue value, FieldPath path) =>
        value.WithoutField(path);

    public static bool TryGet(
        ObservationValue value,
        FieldPath path,
        out ObservationValue fieldValue) =>
        value.TryGetField(path, out fieldValue);

    public static ObservationValue Select(
        ObservationValue value,
        IEnumerable<FieldPath> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        var result = Empty;
        foreach (var field in fields
                     .Distinct()
                     .OrderBy(static field => field.ToString(), StringComparer.Ordinal))
        {
            if (TryGet(value, field, out var selected))
                result = Set(result, field, selected);
        }

        return result;
    }
}
