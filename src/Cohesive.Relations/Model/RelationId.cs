using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Relations.Model;

/// <summary>
/// Stable relation identifier.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public sealed record RelationId
{
    /// <summary>
    /// Creates a relation identifier.
    /// </summary>
    /// <param name="value">Nonempty stable identifier text.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is empty or consists only of whitespace.</exception>
    public RelationId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value: value);
    }

    /// <summary>
    /// Raw relation identifier text.
    /// </summary>
    public string Value { get; init; }

    /// <inheritdoc />
    public override string ToString() => Value;
}
