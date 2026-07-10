using System.Text.Json;
using Cohesive.Model;
using Cohesive.Model.Serialization;
using Json.Schema;
using static Cohesive.Adapters.Json.JsonSchemaBuilderDsl;

namespace Cohesive.Adapters.Json;

/// <summary>
/// JSON Schema provider for portable <see cref="ShapeGraph"/> documents <see cref="ShapeGraphDocument"/>.
/// </summary>
public sealed class ShapeGraphDocumentJsonSchemaProvider : IJsonSchemaProvider
{
    /// <summary>
    /// Singleton shape graph document schema provider.
    /// </summary>
    public static ShapeGraphDocumentJsonSchemaProvider Instance { get; } = new();

    ShapeGraphDocumentJsonSchemaProvider()
    {
        Schema = BuildSchema();
    }

    /// <inheritdoc />
    public string SchemaId => ShapeGraphDocumentJsonSchema.SchemaId;

    /// <inheritdoc />
    public string FileName => ShapeGraphDocumentJsonSchema.FileName;

    /// <inheritdoc />
    public JsonSchema Schema { get; }

    static JsonSchema BuildSchema() =>
        new JsonSchemaBuilder()
            .Schema("https://json-schema.org/draft/2020-12/schema")
            .Id(ShapeGraphDocumentJsonSchema.SchemaId)
            .Title("ShapeGraphDocument")
            .Type(SchemaValueType.Object)
            .Required("schemaVersion", "graph")
            .AdditionalProperties(false)
            .Properties(
                ("schemaVersion", ConstValue(ShapeGraphDocument.CurrentSchemaVersion)),
                ("metadata", Ref("#/$defs/metadata")),
                ("graph", Ref("#/$defs/shapeGraph")))
            .Defs(
                ("metadata", Metadata()),
                ("shapeGraph", ShapeGraph()),
                ("shape", Shape()),
                ("field", Field()),
                ("structuralField", new JsonSchemaBuilder().AllOf(Ref("#/$defs/field"))),
                ("typeDefinition", TypeDefinition()),
                ("typeRef", TypeRef()),
                ("constraint", Constraint()),
                ("graphDiagnostic", GraphDiagnostic()),
                ("enumValue", EnumValue()),
                ("nonEmptyString", StringSchema(minLength: 1)));

    static JsonSchemaBuilder Metadata() =>
        ObjectSchema(
            additionalProperties: true,
            ("origin", new JsonSchemaBuilder().Type(SchemaValueType.String, SchemaValueType.Integer)),
            ("name", StringOrNull()),
            ("description", StringOrNull()),
            ("sourceUri", StringOrNull()),
            ("createdAtUtc", DateTimeStringOrNull()),
            ("updatedAtUtc", DateTimeStringOrNull()),
            ("annotations", AnyObject()));

    static JsonSchemaBuilder ShapeGraph() =>
        ObjectSchema(
            additionalProperties: true,
            required: ["id", "shapes"],
            ("id", Ref("#/$defs/nonEmptyString")),
            ("shapes", ArrayOf(Ref("#/$defs/shape"))),
            ("namedTypes", ArrayOf(Ref("#/$defs/typeDefinition"))),
            ("diagnostics", ArrayOf(Ref("#/$defs/graphDiagnostic"))),
            ("annotations", AnyObject()));

    static JsonSchemaBuilder Shape() =>
        ObjectSchema(
            additionalProperties: true,
            required: ["id", "fields"],
            ("id", Ref("#/$defs/nonEmptyString")),
            ("fields", ArrayOf(Ref("#/$defs/field"))),
            ("constraints", ArrayOf(Ref("#/$defs/constraint"))),
            ("annotations", AnyObject()),
            ("role", StringOrNull()));

    static JsonSchemaBuilder Field() =>
        ObjectSchema(
            additionalProperties: true,
            required: ["name", "type"],
            ("name", Ref("#/$defs/nonEmptyString")),
            ("type", Ref("#/$defs/typeRef")),
            ("cardinality", Ref("#/$defs/enumValue")),
            ("presence", Ref("#/$defs/enumValue")),
            ("nullability", Ref("#/$defs/enumValue")),
            ("role", Ref("#/$defs/enumValue")),
            ("mutability", Ref("#/$defs/enumValue")),
            ("compute", new JsonSchemaBuilder().Type(SchemaValueType.Object, SchemaValueType.Null)),
            ("constraints", ArrayOf(Ref("#/$defs/constraint"))),
            ("annotations", AnyObject()));

    static JsonSchemaBuilder TypeDefinition() =>
        new JsonSchemaBuilder().OneOf(
            ObjectSchema(
                additionalProperties: true,
                required: ["$typeDef", "id", "fields"],
                ("$typeDef", ConstValue("structural")),
                ("id", Ref("#/$defs/nonEmptyString")),
                ("name", Ref("#/$defs/nonEmptyString")),
                ("fields", ArrayOf(Ref("#/$defs/structuralField"))),
                ("constraints", ArrayOf(Ref("#/$defs/constraint"))),
                ("annotations", AnyObject())),
            ObjectSchema(
                additionalProperties: true,
                required: ["$typeDef", "id", "underlying", "values"],
                ("$typeDef", ConstValue("enum")),
                ("id", Ref("#/$defs/nonEmptyString")),
                ("name", Ref("#/$defs/nonEmptyString")),
                ("underlying", Ref("#/$defs/enumValue")),
                ("values", ArrayOf(
                    ObjectSchema(
                        additionalProperties: true,
                        required: ["name"],
                        ("name", Ref("#/$defs/nonEmptyString")),
                        ("value", StringOrNull()),
                        ("label", StringOrNull()),
                        ("description", StringOrNull())),
                    minItems: 1)),
                ("annotations", AnyObject())),
            ObjectSchema(
                additionalProperties: true,
                required: ["$typeDef", "id", "discriminator", "cases"],
                ("$typeDef", ConstValue("union")),
                ("id", Ref("#/$defs/nonEmptyString")),
                ("name", Ref("#/$defs/nonEmptyString")),
                ("discriminator", ObjectSchema(
                    additionalProperties: true,
                    required: ["fieldName"],
                    ("fieldName", Ref("#/$defs/nonEmptyString")),
                    ("type", Ref("#/$defs/enumValue")))),
                ("cases", ArrayOf(
                    ObjectSchema(
                        additionalProperties: true,
                        required: ["name", "type"],
                        ("name", Ref("#/$defs/nonEmptyString")),
                        ("type", Ref("#/$defs/typeRef")),
                        ("discriminatorValue", Ref("#/$defs/nonEmptyString"))),
                    minItems: 1)),
                ("annotations", AnyObject())));

    static JsonSchemaBuilder TypeRef() =>
        new JsonSchemaBuilder().OneOf(
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "typeId"],
                ("$type", ConstValue("named")),
                ("typeId", Ref("#/$defs/nonEmptyString"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "runtimeType"],
                ("$type", ConstValue("opaque")),
                ("runtimeType", Ref("#/$defs/nonEmptyString"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "kind"],
                ("$type", ConstValue("json")),
                ("kind", StringEnum("Any", "Object", "Array", "String", "Number", "Boolean"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "kind"],
                ("$type", ConstValue("scalar")),
                ("kind", Ref("#/$defs/enumValue")),
                ("format", Ref("#/$defs/enumValue"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "name", "members"],
                ("$type", ConstValue("enum")),
                ("name", Ref("#/$defs/nonEmptyString")),
                ("members", ArrayOf(Ref("#/$defs/nonEmptyString"), minItems: 1))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "entity"],
                ("$type", ConstValue("entityRef")),
                ("entity", Ref("#/$defs/nonEmptyString"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "elementType"],
                ("$type", ConstValue("array")),
                ("elementType", Ref("#/$defs/typeRef"))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "fields"],
                ("$type", ConstValue("object")),
                ("fields", ArrayOf(Ref("#/$defs/field"), minItems: 1))),
            ObjectSchema(
                additionalProperties: true,
                required: ["$type", "quantity"],
                ("$type", ConstValue("quantity")),
                ("quantity", Ref("#/$defs/nonEmptyString")),
                ("baseKind", Ref("#/$defs/enumValue"))));

    static JsonSchemaBuilder Constraint() =>
        ObjectSchema(
            additionalProperties: true,
            required: ["$constraint"],
            ("$constraint", StringEnum("required", "minLength", "maxLength", "range", "regex", "allowedValues", "occurrence")));

    static JsonSchemaBuilder GraphDiagnostic() =>
        ObjectSchema(
            additionalProperties: true,
            required: ["id", "severity", "message"],
            ("id", Ref("#/$defs/nonEmptyString")),
            ("severity", Ref("#/$defs/enumValue")),
            ("message", Ref("#/$defs/nonEmptyString")),
            ("shapeId", StringOrNull()),
            ("fieldIdentity", StringOrNull()),
            ("typeId", StringOrNull()));
}

/// <summary>
/// Shape graph document JSON Schema accessors.
/// </summary>
public static class ShapeGraphDocumentJsonSchema
{
    /// <summary>Identifies the schema id.</summary>
    public const string SchemaId = "https://cohesive.to/schemas/shape-graph.v1.schema.json";
    /// <summary>Identifies the file name.</summary>
    public const string FileName = "shape-graph.v1.schema.json";

    /// <summary>
    /// Built JSON Schema.
    /// </summary>
    public static JsonSchema Schema => ShapeGraphDocumentJsonSchemaProvider.Instance.Schema;

    /// <summary>
    /// Exported JSON Schema text.
    /// </summary>
    public static string SchemaText => JsonSchemaExporter.ToJson(Schema);
}

/// <summary>
/// Structural validator for portable shape graph documents.
/// </summary>
public static class ShapeGraphDocumentStructuralValidator
{
    /// <summary>
    /// Validates shape graph document JSON against its JSON Schema.
    /// </summary>
    public static DocumentValidationResult ValidateJson(string json) =>
        JsonSchemaDocumentValidator.ValidateJson(
            json,
            ShapeGraphDocumentJsonSchemaProvider.Instance,
            "shapeGraph.schema");
}

/// <summary>
/// Full validator for portable shape graph document JSON.
/// </summary>
public static class ShapeGraphDocumentValidator
{
    /// <summary>
    /// Runs structural JSON Schema validation, then semantic validation when the document can be deserialized.
    /// </summary>
    public static DocumentValidationResult ValidateJson(string json, JsonSerializerOptions? options = null)
    {
        var structural = ShapeGraphDocumentStructuralValidator.ValidateJson(json);
        if (!structural.IsValid)
            return structural;

        var deserialization = TryDeserialize(json, options, out var parsed);
        if (!deserialization.IsValid)
            return deserialization;

        return parsed is null
            ? DocumentValidationResult.FromDiagnostics([
                new(
                    "shapeGraph.deserialize.null",
                    DiagnosticSeverity.Error,
                    "JSON deserialized to a null shape graph document.",
                    "$")
            ])
            : ShapeGraphDocumentSemanticValidator.Validate(parsed);
    }

    static DocumentValidationResult TryDeserialize(
        string json,
        JsonSerializerOptions? options,
        out ShapeGraphDocument? document
        )
    {
        try
        {
            document = JsonSerializer.Deserialize<ShapeGraphDocument>(
                json,
                options ?? new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return DocumentValidationResult.Valid;
        }
        catch (JsonException ex)
        {
            document = null;
            return DeserializationError(ex);
        }
        catch (ArgumentException ex)
        {
            document = null;
            return DeserializationError(ex);
        }
        catch (InvalidOperationException ex)
        {
            document = null;
            return DeserializationError(ex);
        }
    }

    static DocumentValidationResult DeserializationError(Exception ex) =>
        DocumentValidationResult.FromDiagnostics([
            new(
                "shapeGraph.deserialize",
                DiagnosticSeverity.Error,
                ex.Message,
                "$")
        ]);
}
