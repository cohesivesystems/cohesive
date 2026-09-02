using System.Collections.Immutable;
using Cohesive.Relations.Compilation;

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

    /// <summary>
    /// Selects fields whose uniqueness and deterministic ordering were established by the compiled execution slice.
    /// </summary>
    /// <param name="value">Object value from which demanded fields are selected.</param>
    /// <param name="fields">Canonical compiled field references.</param>
    /// <returns>An object containing the demanded fields that are present in <paramref name="value"/>.</returns>
    public static ObservationValue SelectCanonical(
        ObservationValue value,
        ImmutableArray<RelationQueryFieldReference> fields)
    {
        if (fields.IsDefaultOrEmpty)
            return value;

        var topLevelOnly = true;
        foreach (var field in fields)
        {
            if (!field.Path.TryGetDirectFieldName(out _))
            {
                topLevelOnly = false;
                break;
            }
        }

        if (topLevelOnly)
        {
            var selectedFields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(
                StringComparer.Ordinal);
            foreach (var field in fields)
            {
                _ = field.Path.TryGetDirectFieldName(out var name);
                if (value.TryGetProperty(name, out var selected))
                    selectedFields.Add(name, selected);
            }
            return ObservationValue.FromObject(selectedFields.ToImmutable());
        }

        var result = Empty;
        foreach (var field in fields)
        {
            if (TryGet(value, field.Path, out var selected))
                result = Set(result, field.Path, selected);
        }
        return result;
    }

}
