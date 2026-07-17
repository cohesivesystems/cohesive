using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Stable relation name.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public sealed record RelationName
{
    /// <summary>
    /// Creates a relation name.
    /// </summary>
    /// <param name="value">Nonempty human-readable relation name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of whitespace.</exception>
    public RelationName(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value: value);
    }

    /// <summary>
    /// Raw relation name text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
