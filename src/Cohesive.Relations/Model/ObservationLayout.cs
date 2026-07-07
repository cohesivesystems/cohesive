namespace Cohesive.Relations.Model;

/// <summary>
/// Stable observation field layout for indexed value access by canonical field name.
/// </summary>
public sealed class ObservationLayout
{
    readonly Dictionary<string, int> ordinalByFieldName;
    readonly IReadOnlyList<string> fieldNames;

    /// <summary>
    /// Creates a field layout.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    public ObservationLayout(ShapeId schema, IReadOnlyList<string> fieldNames)
    {
        Schema = schema;
        this.fieldNames = Guard.RequireNotNull(fieldNames);
        if (this.fieldNames.Count == 0)
            throw new ArgumentException("Observation layout must define at least one field.", nameof(fieldNames));

        ordinalByFieldName = new(this.fieldNames.Count, StringComparer.Ordinal);
        for (var i = 0; i < this.fieldNames.Count; i++)
        {
            var fieldName = this.fieldNames[i];
            if (!ordinalByFieldName.TryAdd(fieldName, i))
                throw new ArgumentException($"Layout for schema '{schema}' contains duplicate field name token '{fieldName}'.", nameof(fieldNames));
        }
    }

    /// <summary>
    /// Schema this layout applies to.
    /// </summary>
    public ShapeId Schema { get; }

    /// <summary>
    /// Ordered field names by ordinal.
    /// </summary>
    public IReadOnlyList<string> FieldNames => fieldNames;

    /// <summary>
    /// Number of fields in this layout.
    /// </summary>
    public int Count => fieldNames.Count;

    /// <summary>
    /// Gets ordinal by field name.
    /// </summary>
    /// <exception cref="KeyNotFoundException"></exception>
    public int GetOrdinal(string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return ordinalByFieldName.TryGetValue(fieldName, out var ordinal)
            ? ordinal
            : throw new KeyNotFoundException($"Field '{fieldName}' is not part of layout '{Schema}'.");
    }

    /// <summary>
    /// Attempts to resolve ordinal by field name.
    /// </summary>
    public bool TryGetOrdinal(string fieldName, out int ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        return ordinalByFieldName.TryGetValue(fieldName, out ordinal);
    }

    /// <summary>
    /// Creates a layout from fields.
    /// </summary>
    public static ObservationLayout Create(ShapeId schema, IEnumerable<string> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new(schema, [.. fields.Distinct(StringComparer.Ordinal)]);
    }

    public override string ToString() => $"{Schema} [{string.Join(", ", fieldNames)}]";
}
