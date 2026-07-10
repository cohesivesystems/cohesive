using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Stable identifier for a shape graph build.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct GraphId
{
    /// <summary>
    /// Creates a graph id value.
    /// </summary>
    [JsonConstructor]
    public GraphId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw graph id value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a unique graph id.
    /// </summary>
    public static GraphId New() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable identifier for a graph diagnostic.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct DiagnosticId
{
    /// <summary>
    /// Creates a diagnostic id value.
    /// </summary>
    [JsonConstructor]
    public DiagnosticId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw diagnostic id value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// Stable human-readable field name.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct FieldName
{
    /// <summary>
    /// Creates a field name value.
    /// </summary>
    [JsonConstructor]
    public FieldName(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw field name text.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
    
    /// <summary>Converts a field name to its string value.</summary>
    public static implicit operator string(FieldName fieldName) => fieldName.Value;
}

/// <summary>
/// Stable identifier for a named type definition.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct TypeId
{
    /// <summary>
    /// Creates a type id value.
    /// </summary>
    [JsonConstructor]
    public TypeId(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value);
    }

    /// <summary>
    /// Raw type id value.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;
    
    /// <summary>Converts a type identifier to its string value.</summary>
    public static implicit operator string(TypeId typeId) => typeId.Value;
}
