using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Base semantic constraint metadata.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$constraint")]
[JsonDerivedType(typeof(RequiredConstraint), "required")]
[JsonDerivedType(typeof(MinLengthConstraint), "minLength")]
[JsonDerivedType(typeof(MaxLengthConstraint), "maxLength")]
[JsonDerivedType(typeof(RangeConstraint), "range")]
[JsonDerivedType(typeof(RegexConstraint), "regex")]
[JsonDerivedType(typeof(AllowedValuesConstraint), "allowedValues")]
[JsonDerivedType(typeof(OccurrenceConstraint), "occurrence")]
public abstract record ShapeConstraint;

/// <summary>
/// Required field/value constraint.
/// </summary>
public sealed record RequiredConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates a required constraint.
    /// </summary>
    [JsonConstructor]
    public RequiredConstraint(FieldPath? field = null, string? message = null)
    {
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Minimum string/collection length constraint.
/// </summary>
public sealed record MinLengthConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates a min-length constraint.
    /// </summary>
    [JsonConstructor]
    public MinLengthConstraint(int value, FieldPath? field = null, string? message = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName: nameof(value), message: "Min length must be >= 0.");

        Value = value;
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Length minimum.
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Maximum string/collection length constraint.
/// </summary>
public sealed record MaxLengthConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates a max-length constraint.
    /// </summary>
    [JsonConstructor]
    public MaxLengthConstraint(int value, FieldPath? field = null, string? message = null)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(paramName: nameof(value), message: "Max length must be >= 0.");

        Value = value;
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Length limit.
    /// </summary>
    public int Value { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Numeric range constraint.
/// </summary>
public sealed record RangeConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates a range constraint.
    /// </summary>
    [JsonConstructor]
    public RangeConstraint(decimal? minimum = null, decimal? maximum = null, FieldPath? field = null, string? message = null)
    {
        if (minimum is null && maximum is null)
            throw new ArgumentException("Range requires a minimum and/or maximum bound.");

        if (minimum is not null && maximum is not null && minimum > maximum)
            throw new ArgumentException("Range minimum cannot be greater than maximum.");

        Minimum = minimum;
        Maximum = maximum;
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Inclusive minimum bound.
    /// </summary>
    public decimal? Minimum { get; init; }

    /// <summary>
    /// Inclusive maximum bound.
    /// </summary>
    public decimal? Maximum { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Regex pattern constraint.
/// </summary>
public sealed record RegexConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates a regex constraint.
    /// </summary>
    [JsonConstructor]
    public RegexConstraint(string pattern, FieldPath? field = null, string? message = null)
    {
        Pattern = Guard.RequireNotNullOrWhiteSpace(pattern);
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Regex pattern text.
    /// </summary>
    public string Pattern { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Allowed literal value constraint for enum-like scalar fields.
/// </summary>
public sealed record AllowedValuesConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates an allowed-values constraint.
    /// </summary>
    [JsonConstructor]
    public AllowedValuesConstraint(IEnumerable<string> values, FieldPath? field = null, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(values);

        Values =
        [
            .. values
                .WhereNotNullOrWhiteSpace()
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
        ];

        if (Values.IsDefaultOrEmpty)
            throw new ArgumentException("Allowed-values constraint requires at least one value.", nameof(values));

        Field = field;
        Message = message;
    }

    /// <summary>
    /// Allowed string literal values.
    /// </summary>
    public ImmutableArray<string> Values { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Compares allowed-values constraints using value semantics for the value set.
    /// </summary>
    public bool Equals(AllowedValuesConstraint? other)
    {
        if (ReferenceEquals(this, other))
            return true;
        if (other is null)
            return false;

        return Values.SequenceEqual(other.Values)
               && Field == other.Field
               && Message == other.Message;
    }

    /// <summary>
    /// Computes a hash code aligned with <see cref="Equals(AllowedValuesConstraint?)"/>.
    /// </summary>
    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var value in Values)
            hash.Add(value, StringComparer.Ordinal);
        hash.Add(Field);
        hash.Add(Message, StringComparer.Ordinal);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Occurrence/count constraint for repeated fields such as EDI segments and loops.
/// </summary>
public sealed record OccurrenceConstraint : ShapeConstraint
{
    /// <summary>
    /// Creates an occurrence constraint.
    /// </summary>
    [JsonConstructor]
    public OccurrenceConstraint(int? minimum = null, int? maximum = null, FieldPath? field = null, string? message = null)
    {
        if (minimum is null && maximum is null)
            throw new ArgumentException("Occurrence requires a minimum and/or maximum bound.");
        if (minimum < 0)
            throw new ArgumentOutOfRangeException(nameof(minimum), "Occurrence minimum must be >= 0.");
        if (maximum < 0)
            throw new ArgumentOutOfRangeException(nameof(maximum), "Occurrence maximum must be >= 0.");
        if (minimum is not null && maximum is not null && minimum > maximum)
            throw new ArgumentException("Occurrence minimum cannot be greater than maximum.");

        Minimum = minimum;
        Maximum = maximum;
        Field = field;
        Message = message;
    }

    /// <summary>
    /// Inclusive minimum occurrence count.
    /// </summary>
    public int? Minimum { get; init; }

    /// <summary>
    /// Inclusive maximum occurrence count.
    /// </summary>
    public int? Maximum { get; init; }

    /// <summary>
    /// Optional field path target.
    /// </summary>
    public FieldPath? Field { get; init; }

    /// <summary>
    /// Optional explanatory message.
    /// </summary>
    public string? Message { get; init; }
}
