using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Logical entity type name.
/// </summary>
[JsonConverter(typeof(SingleValueWrapperJsonConverter))]
public readonly record struct EntityTypeName
{
    /// <summary>
    /// Creates an entity type name value.
    /// </summary>
    [JsonConstructor]
    public EntityTypeName(string value)
    {
        Value = Guard.RequireNotNullOrWhiteSpace(value: value);
    }

    /// <summary>
    /// Raw type name text.
    /// </summary>
    public string Value { get; }

    public override string ToString() => Value;
    
    public static implicit operator string(EntityTypeName entityTypeName) => entityTypeName.Value;

    /// <summary>
    /// Creates an entity type name from a CLR type.
    /// </summary>
    public static EntityTypeName From(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var typeName = type.Name;
        var genericTick = typeName.IndexOf('`');
        if (genericTick >= 0)
            typeName = typeName[..genericTick];

        return new(typeName);
    }

    /// <summary>
    /// Creates an entity type name from a CLR type parameter.
    /// </summary>
    public static EntityTypeName From<TEntity>() => From(typeof(TEntity));
}
