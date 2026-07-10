using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cohesive.Prelude;

/// <summary>
/// Unit type representing an explicit "no value" result.
/// </summary>
[DebuggerDisplay(UnitString)]
[JsonConverter(typeof(UnitJsonConverter))]
[StructLayout(LayoutKind.Sequential, Size = 1)]
public readonly record struct Unit
{
    /// <summary>
    /// Canonical unit value.
    /// </summary>
    public static Unit Value => default;

    /// <summary>
    /// Returns the conventional unit representation.
    /// </summary>
    public override string ToString() => UnitString;
    
    internal const string UnitString = "()";
}

/// <summary>
/// JSON converter for <see cref="Unit"/> values.
/// </summary>
public sealed class UnitJsonConverter : JsonConverter<Unit>
{
    /// <inheritdoc />
    public override Unit Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return Unit.Value;

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException($"Expected JSON string '{Unit.UnitString}' for Unit.");

        var text = reader.GetString();
        return !string.Equals(text, Unit.UnitString, StringComparison.Ordinal) 
            ? throw new JsonException($"Expected JSON string '{Unit.UnitString}' for Unit.") 
            : Unit.Value;
    }
    
    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Unit value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value: Unit.UnitString);
    }
}
