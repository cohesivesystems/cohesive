using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Stable identifier for a value binding visible to semantic expressions.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct ValueBindingId
{
    /// <summary>
    /// Creates a value binding identifier.
    /// </summary>
    /// <param name="value">Raw value binding identifier.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of white-space characters.</exception>
    [JsonConstructor]
    public ValueBindingId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw binding identifier.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
