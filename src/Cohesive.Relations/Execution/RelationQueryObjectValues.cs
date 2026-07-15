using System.Collections.ObjectModel;

namespace Cohesive.Relations.Execution;

/// <summary>Deterministic object-path operations shared by runtime evidence reconstruction and output shaping.</summary>
static class RelationQueryObjectValues
{
    public static ObservationValue Empty { get; } =
        ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(
            new Dictionary<string, ObservationValue>(capacity: 0, StringComparer.Ordinal)));

    public static ObservationValue Set(ObservationValue value, FieldPath path, ObservationValue fieldValue)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("An object assignment requires a non-empty field path.", nameof(path));

        return SetCore(value, path, segmentIndex: 0, fieldValue);
    }

    public static ObservationValue Remove(ObservationValue value, FieldPath path) =>
        Set(value, path, ObservationValue.Undefined);

    public static bool TryGet(
        ObservationValue value,
        FieldPath path,
        out ObservationValue fieldValue)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            fieldValue = default;
            return false;
        }

        var current = value;
        foreach (var segment in path.Segments)
        {
            var name = RequireField(segment, path);
            if (current.Kind != ObservationValueKind.Object
                || current.Fields is null
                || !TryGetProperty(current.Fields, name, out current))
            {
                fieldValue = default;
                return false;
            }
        }

        fieldValue = current;
        return true;
    }

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

    static ObservationValue SetCore(
        ObservationValue current,
        FieldPath path,
        int segmentIndex,
        ObservationValue fieldValue)
    {
        var name = RequireField(path.Segments[segmentIndex], path);
        Dictionary<string, ObservationValue> fields = new(StringComparer.Ordinal);
        if (current.Kind == ObservationValueKind.Object && current.Fields is not null)
        {
            foreach (var (key, existing) in current.Fields)
                fields[key] = existing;
        }
        else if (current.Kind is not ObservationValueKind.Undefined and not ObservationValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Field path '{path}' cannot be assigned through value kind '{current.Kind}'.");
        }

        if (segmentIndex == path.Segments.Length - 1)
        {
            if (fieldValue.Kind == ObservationValueKind.Undefined)
                fields.Remove(name);
            else
                fields[name] = fieldValue;
        }
        else
        {
            var child = fields.GetValueOrDefault(name, Empty);
            var updated = SetCore(child, path, segmentIndex + 1, fieldValue);
            if (updated.Kind == ObservationValueKind.Object && updated.Fields?.Count == 0)
                fields.Remove(name);
            else
                fields[name] = updated;
        }

        var ordered = fields
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        return ObservationValue.FromObject(new ReadOnlyDictionary<string, ObservationValue>(ordered));
    }

    static string RequireField(FieldPathSegment segment, FieldPath path)
    {
        if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
        {
            throw new NotSupportedException(
                $"Runtime object path '{path}' contains an unsupported non-field segment.");
        }

        return segment.Segment;
    }

    static bool TryGetProperty(
        IReadOnlyDictionary<string, ObservationValue> fields,
        string name,
        out ObservationValue value)
    {
        foreach (var (key, candidate) in fields)
        {
            if (!string.Equals(key, name, StringComparison.Ordinal))
                continue;
            value = candidate;
            return true;
        }

        value = default;
        return false;
    }
}
