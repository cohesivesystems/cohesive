namespace Cohesive.Model;

/// <summary>Target-neutral selection of a complete shaped value or an explicit field subset.</summary>
public sealed record FieldSelection
{
    /// <summary>Creates a normalized field selection.</summary>
    /// <param name="fields">
    /// Selected field names, or <see langword="null"/> for the complete value. An explicitly empty collection is
    /// retained as an empty projection and is distinct from a full-value selection.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="fields"/> contains a null, empty, or whitespace name.
    /// </exception>
    public FieldSelection(IReadOnlyCollection<string>? fields = null) =>
        Fields = NormalizeFields(fields);

    /// <summary>Shared complete-value selection.</summary>
    public static FieldSelection Full { get; } = new();

    /// <summary>An explicit selected field subset, or <see langword="null"/> for the complete value.</summary>
    public IReadOnlySet<string>? Fields { get; }

    /// <summary>Whether the selection projects a field subset rather than the complete value.</summary>
    public bool HasFieldProjection => Fields is not null;

    /// <summary>Creates a normalized projected-field selection.</summary>
    /// <param name="fields">Field names to select.</param>
    /// <returns>A selection containing the distinct supplied names.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fields"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="fields"/> contains a null, empty, or whitespace name.
    /// </exception>
    public static FieldSelection ForFields(params string[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new(fields);
    }

    static IReadOnlySet<string>? NormalizeFields(IReadOnlyCollection<string>? fields)
    {
        if (fields is null)
            return null;

        HashSet<string> normalized = new(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (string.IsNullOrWhiteSpace(field))
            {
                throw new ArgumentException(
                    "Field-selection names must not be null, empty, or whitespace.",
                    nameof(fields));
            }

            normalized.Add(field);
        }

        return normalized;
    }
}
