using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Cli;

/// <summary>
/// Output channels available to a CLI invocation.
/// </summary>
public sealed class CliOutput(
    TextWriter standardOutput, 
    TextWriter errorOutput, 
    JsonSerializerOptions? jsonOptions = null
    )
{
    readonly JsonSerializerOptions jsonOptions = jsonOptions ?? DefaultJsonOptions();

    static JsonSerializerOptions DefaultJsonOptions()
    {
        return new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
    }
    
    /// <summary>Gets the standard output.</summary>
    public TextWriter StandardOutput { get; } = Guard.RequireNotNull(standardOutput);

    /// <summary>Gets the error output.</summary>
    public TextWriter ErrorOutput { get; } = Guard.RequireNotNull(errorOutput);
    
    /// <summary>Writes line.</summary>
    public void WriteLine(string? value) => StandardOutput.WriteLine(value);

    /// <summary>Writes error line.</summary>
    public void WriteErrorLine(string? value) => ErrorOutput.WriteLine(value);

    /// <summary>
    /// Writes the specified object as JSON to the standard output.
    /// </summary>
    /// <param name="obj">The object to serialize into JSON and write to standard output.</param>
    /// <param name="jsonOptionsOverride"></param>
    public void WriteJson(object obj, JsonSerializerOptions? jsonOptionsOverride = null) => 
        WriteLine(JsonSerializer.Serialize(obj, jsonOptionsOverride ?? jsonOptions));
    
    /// <summary>Writes json error.</summary>
    public void WriteJsonError(object obj) => 
        WriteErrorLine(JsonSerializer.Serialize(obj, jsonOptions));

    /// <summary>
    /// Creates a new <see cref="CliOutput"/> instance that writes to the standard output and error streams.
    /// </summary>
    public static CliOutput Standard => new(Console.Out, Console.Error);
    
    /// <summary>
    /// Creates a new <see cref="CliOutput"/> instance that writes to the null streams.
    /// </summary>
    public static CliOutput Null => new(TextWriter.Null, TextWriter.Null);
    
    internal static CliOutput Create(ParseResult parseResult, CliInvocationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(parseResult);
        return new(
            standardOutput: options?.StandardOutput ?? parseResult.InvocationConfiguration.Output ?? Console.Out,
            errorOutput: options?.ErrorOutput ?? parseResult.InvocationConfiguration.Error ?? Console.Error,
            jsonOptions: options?.JsonSerializerOptions
        );
    }
}
