using System.Text.Json;
using Json.Schema;

namespace Cohesive.Adapters.Json;

/// <summary>
/// Provides a named JSON Schema artifact.
/// </summary>
public interface IJsonSchemaProvider
{
    /// <summary>
    /// Stable schema id URI.
    /// </summary>
    string SchemaId { get; }

    /// <summary>
    /// File name to use when exporting the schema.
    /// </summary>
    string FileName { get; }

    /// <summary>
    /// Built JSON Schema.
    /// </summary>
    JsonSchema Schema { get; }
}

/// <summary>
/// Generated JSON Schema artifact.
/// </summary>
public sealed record JsonSchemaExport(string SchemaId, string FileName, string Json);

/// <summary>
/// Exports JSON Schema providers to stable JSON artifacts.
/// </summary>
public static class JsonSchemaExporter
{
    static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Formats a schema as indented JSON.
    /// </summary>
    public static string ToJson(JsonSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        return JsonSerializer.Serialize(schema, SerializerOptions) + Environment.NewLine;
    }

    /// <summary>
    /// Generates JSON artifacts for schema providers.
    /// </summary>
    public static IReadOnlyList<JsonSchemaExport> Generate(IEnumerable<IJsonSchemaProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return providers
            .Select(provider => new JsonSchemaExport(
                provider.SchemaId,
                provider.FileName,
                ToJson(provider.Schema)
                )
            )
            .ToArray();
    }

    /// <summary>
    /// Writes schema provider artifacts to a directory.
    /// </summary>
    public static IReadOnlyList<string> ExportToDirectory(
        IEnumerable<IJsonSchemaProvider> providers,
        string directory
        )
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        List<string> paths = [];
        foreach (var export in Generate(providers))
        {
            var path = Path.Combine(directory, export.FileName);
            File.WriteAllText(path, export.Json);
            paths.Add(path);
        }

        return paths;
    }
}
