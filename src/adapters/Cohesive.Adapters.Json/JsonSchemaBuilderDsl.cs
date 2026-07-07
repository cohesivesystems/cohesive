using System.Text.Json.Nodes;
using Json.Schema;

namespace Cohesive.Adapters.Json;

/// <summary>
/// Small C# DSL for the JSON Schema shapes used by Cohesive document schemas.
/// </summary>
public static class JsonSchemaBuilderDsl
{
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

    public static JsonSchemaBuilder ObjectSchema(
        bool additionalProperties,
        params (string Name, JsonSchemaBuilder Schema)[] properties
        ) => ObjectSchema(additionalProperties, [], properties);

    public static JsonSchemaBuilder ObjectSchema(params (string Name, JsonSchemaBuilder Schema)[] properties) =>
        ObjectSchema(additionalProperties: true, properties);

    public static JsonSchemaBuilder ArrayOf(JsonSchemaBuilder itemSchema, uint? minItems = null)
    {
        var builder = new JsonSchemaBuilder()
            .Type(SchemaValueType.Array)
            .Items(itemSchema);

        if (minItems is not null)
            builder.MinItems(minItems.Value);

        return builder;
    }

    public static JsonSchemaBuilder Ref(string reference) =>
        new JsonSchemaBuilder().Ref(reference);

    public static JsonSchemaBuilder ConstValue(string value) =>
        new JsonSchemaBuilder().Const(JsonValue.Create(value));

    public static JsonSchemaBuilder StringEnum(params string[] values) =>
        new JsonSchemaBuilder().Enum(values);

    public static JsonSchemaBuilder StringSchema(uint? minLength = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.String);
        if (minLength is not null)
            builder.MinLength(minLength.Value);
        return builder;
    }

    public static JsonSchemaBuilder StringOrNull() =>
        new JsonSchemaBuilder().Type(SchemaValueType.String, SchemaValueType.Null);

    public static JsonSchemaBuilder NumberOrNull(decimal? minimum = null, decimal? maximum = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.Number, SchemaValueType.Null);
        if (minimum is not null)
            builder.Minimum(minimum.Value);
        if (maximum is not null)
            builder.Maximum(maximum.Value);
        return builder;
    }

    public static JsonSchemaBuilder IntegerSchema(decimal? minimum = null)
    {
        var builder = new JsonSchemaBuilder().Type(SchemaValueType.Integer);
        if (minimum is not null)
            builder.Minimum(minimum.Value);
        return builder;
    }

    public static JsonSchemaBuilder EnumValue() =>
        new JsonSchemaBuilder().Type(SchemaValueType.Integer, SchemaValueType.String);

    public static JsonSchemaBuilder AnyObject() =>
        new JsonSchemaBuilder().Type(SchemaValueType.Object);

    public static JsonSchemaBuilder DateTimeStringOrNull() =>
        StringOrNull().Format("date-time");
}
