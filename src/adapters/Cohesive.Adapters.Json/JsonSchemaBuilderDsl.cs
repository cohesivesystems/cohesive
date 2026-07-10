using System.Text.Json.Nodes;
using Json.Schema;

namespace Cohesive.Adapters.Json;

/// <summary>
/// Small C# DSL for the JSON Schema shapes used by Cohesive document schemas.
/// </summary>
public static class JsonSchemaBuilderDsl
{
    /// <summary>Creates an object schema from named properties.</summary>
    public static JsonSchemaBuilder ObjectSchema(
        bool additionalProperties,
        IEnumerable<string> required,
        params (string Name, JsonSchemaBuilder Schema)[] properties
        )
    {
        var builder = new JsonSchemaBuilder()
            .Type(SchemaValueType.Object)
            .AdditionalProperties(additionalProperties)
            .Properties(properties);

        var requiredArray = required.ToArray();
        if (requiredArray.Length > 0)
            builder.Required(requiredArray);

        return builder;
    }

    /// <summary>Creates an object schema from a property collection.</summary>
    public static JsonSchemaBuilder ObjectSchema(
        bool additionalProperties,
        params (string Name, JsonSchemaBuilder Schema)[] properties
        ) => ObjectSchema(additionalProperties, [], properties);

    /// <summary>Creates an object schema from property tuples.</summary>
    public static JsonSchemaBuilder ObjectSchema(params (string Name, JsonSchemaBuilder Schema)[] properties) =>
        ObjectSchema(additionalProperties: true, properties);

    /// <summary>Creates an array schema for the supplied item schema.</summary>
    public static JsonSchemaBuilder ArrayOf(JsonSchemaBuilder itemSchema, uint? minItems = null)
    {
        var builder = new JsonSchemaBuilder()
            .Type(SchemaValueType.Array)
            .Items(itemSchema);

        if (minItems is not null)
            builder.MinItems(minItems.Value);

        return builder;
    }

    /// <summary>Creates a schema reference.</summary>
    public static JsonSchemaBuilder Ref(string reference) =>
        new JsonSchemaBuilder().Ref(reference);

    /// <summary>Creates a schema constrained to a constant string value.</summary>
    public static JsonSchemaBuilder ConstValue(string value) =>
        new JsonSchemaBuilder().Const(JsonValue.Create(value));

    /// <summary>Creates a string enumeration schema.</summary>
    public static JsonSchemaBuilder StringEnum(params string[] values) =>
        new JsonSchemaBuilder().Enum(values);

    /// <summary>Creates a string schema.</summary>
    public static JsonSchemaBuilder StringSchema(uint? minLength = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.String);
        if (minLength is not null)
            builder.MinLength(minLength.Value);
        return builder;
    }

    /// <summary>Creates a schema accepting a string or null.</summary>
    public static JsonSchemaBuilder StringOrNull() =>
        new JsonSchemaBuilder().Type(SchemaValueType.String, SchemaValueType.Null);

    /// <summary>Creates a bounded schema accepting a number or null.</summary>
    public static JsonSchemaBuilder NumberOrNull(decimal? minimum = null, decimal? maximum = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.Number, SchemaValueType.Null);
        if (minimum is not null)
            builder.Minimum(minimum.Value);
        if (maximum is not null)
            builder.Maximum(maximum.Value);
        return builder;
    }

    /// <summary>Creates an integer schema.</summary>
    public static JsonSchemaBuilder IntegerSchema(decimal? minimum = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.Integer);
        if (minimum is not null)
            builder.Minimum(minimum.Value);
        return builder;
    }

    /// <summary>Creates a schema for a semantic enumeration value.</summary>
    public static JsonSchemaBuilder EnumValue() =>
        new JsonSchemaBuilder().Type(SchemaValueType.Integer, SchemaValueType.String);

    /// <summary>Creates a schema accepting any object.</summary>
    public static JsonSchemaBuilder AnyObject() =>
        new JsonSchemaBuilder().Type(SchemaValueType.Object);

    /// <summary>Creates a schema accepting a date-time string or null.</summary>
    public static JsonSchemaBuilder DateTimeStringOrNull() =>
        StringOrNull().Format("date-time");
}
