using System.Text.Json;
using System.Text.Json.Serialization;
using Cohesive.Relations.Mapping;

namespace Cohesive.Relations.Model;

/// <summary>
/// Shape observation payload keyed by canonical field names.
/// </summary>
public sealed record Observation
{
    readonly ObservationBuffer buffer;

    /// <summary>
    /// Creates an observation instance.
    /// </summary>
    [JsonConstructor]
    public Observation(
        ShapeId shapeId,
        string id,
        IReadOnlyDictionary<string, ObservationValue> fields,
        long version = 0,
        ObservationLineage? lineage = null
        )
    {
        ArgumentNullException.ThrowIfNull(fields);
        
        var layout = ObservationLayout.Create(shapeId, fields.Keys);
        Layout = layout;
        ShapeId = layout.Schema;
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        buffer = BuildBuffer(layout, fields);
        Version = version;
        Lineage = lineage ?? ObservationLineage.Empty;
    }

    /// <summary>
    /// Creates an observation backed by indexed field values.
    /// </summary>
    public Observation(
        ObservationLayout layout,
        string id,
        ObservationValue[] valuesByOrdinal,
        bool[] hasValueByOrdinal,
        long version = 0,
        ObservationLineage? lineage = null
        ) : this(
            layout: layout,
            id: id,
            buffer: ObservationBuffer.FromDense(valuesByOrdinal, hasValueByOrdinal),
            version: version,
            lineage: lineage
            )
    {
    }

    /// <summary>
    /// Creates an observation backed by ordinal-aligned array segments.
    /// </summary>
    public Observation(
        ObservationLayout layout,
        string id,
        ArraySegment<ObservationValue> valuesByOrdinal,
        ArraySegment<ulong> hasValueBitMask,
        long version = 0,
        ObservationLineage? lineage = null
        ) : this(
            layout: layout,
            id: id,
            buffer: new(
                valuesByOrdinal.AsMemory(),
                hasValueBitMask.AsMemory(),
                Guard.RequireNotNull(layout).Count
                ),
            version: version,
            lineage: lineage
            )
    {
    }

    /// <summary>
    /// Creates an observation backed by an <see cref="ObservationBuffer"/>.
    /// </summary>
    Observation(
        ObservationLayout layout,
        string id,
        ObservationBuffer buffer,
        long version = 0,
        ObservationLineage? lineage = null
        )
    {
        Layout = Guard.RequireNotNull(layout);
        ShapeId = layout.Schema;
        Id = Guard.RequireNotNullOrWhiteSpace(id);
        this.buffer = buffer;
        if (buffer.FieldCount != layout.Count)
            throw new ArgumentException($"Observation value buffers must match layout field count ({layout.Count}) for schema '{layout.Schema}'.");
        Version = version;
        Lineage = lineage ?? ObservationLineage.Empty;
    }
    
    /// <summary>
    /// Observation shape.
    /// </summary>
    public ShapeId ShapeId { get; init; }

    /// <summary>
    /// Stable observation identity.
    /// </summary>
    public string Id { get; init; }

    /// <summary>
    /// Source version used for incremental cache invalidation.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Indexed field layout.
    /// </summary>
    public ObservationLayout Layout { get; init; }

    /// <summary>
    /// Ordinal-aligned values as read-only memory.
    /// </summary>
    public ReadOnlyMemory<ObservationValue> ValuesByOrdinal => buffer.ValuesByOrdinal;

    /// <summary>
    /// Packed value-presence bit mask as read-only memory.
    /// </summary>
    public ReadOnlyMemory<ulong> HasValueBitMask => buffer.HasValueBitMask;

    /// <summary>
    /// Field values by canonical field name.
    /// </summary>
    public IReadOnlyDictionary<string, ObservationValue> Fields => field ??= BuildDictionary();

    /// <summary>
    /// Materializes this observation into a CLR shape using the shared shape mapper.
    /// </summary>
    public T Map<T>(ShapeMappingContext? mappingContext = null)
        => (mappingContext ?? ShapeMappingContext.Default).Map<T>(this);

    /// <summary>
    /// Materializes this observation into a CLR shape using an explicitly configured mapper.
    /// </summary>
    public T Map<T>(Action<ObservationObjectMapperBuilder<T>> configure, ShapeMappingContext? mappingContext = null)
    {
        ArgumentNullException.ThrowIfNull(configure);
        return (mappingContext ?? ShapeMappingContext.Default).Map(this, configure);
    }

    /// <summary>
    /// Explainability metadata for emitted fields.
    /// </summary>
    public ObservationLineage Lineage { get; init; }

    /// <summary>
    /// Returns value by field name.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Unable to find the given field.</exception>
    public ObservationValue GetField(string fieldName)
    {
        if (!TryGetField(fieldName: fieldName, out var value))
            throw new KeyNotFoundException($"Observation '{ShapeId}:{Id}' does not contain field '{fieldName}'.");
        return value;
    }

    /// <summary>
    /// Returns value by field definition using its canonical name.
    /// </summary>
    /// <exception cref="KeyNotFoundException"></exception>
    public ObservationValue GetField(FieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return TryGetField(field, out var value) ? value : throw new KeyNotFoundException($"Observation '{ShapeId}:{Id}' does not contain field '{field.Name.Value}'.");
    }

    /// <summary>
    /// Returns value by field ordinal.
    /// </summary>
    /// <exception cref="KeyNotFoundException"></exception>
    public ObservationValue GetField(int ordinal)
    {
        if (!TryGetField(ordinal, out var value))
            throw new KeyNotFoundException($"Observation '{ShapeId}:{Id}' does not contain ordinal '{ordinal}'.");
        return value;
    }

    /// <summary>
    /// Attempts to read a field value by field definition.
    /// </summary>
    public bool TryGetField(FieldDefinition field, out ObservationValue value)
    {
        ArgumentNullException.ThrowIfNull(field);
        return TryGetField(field.Name.Value, out value);
    }

    /// <summary>
    /// Attempts to read a field value by field name/token.
    /// </summary>
    public bool TryGetField(string fieldName, out ObservationValue value)
    {
        if (!Layout.TryGetOrdinal(fieldName, out var ordinal))
        {
            value = default;
            return false;
        }

        return TryGetField(ordinal, out value);
    }

    /// <summary>
    /// Attempts to read a direct or nested object field by its canonical semantic path.
    /// </summary>
    /// <param name="path">Path containing only field-navigation segments. A default path is treated as absent.</param>
    /// <param name="value">The resolved value when the complete path is present; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when every field in <paramref name="path"/> is present using ordinal field-name
    /// matching; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="NotSupportedException"><paramref name="path"/> contains collection-element navigation.</exception>
    public bool TryGetField(FieldPath path, out ObservationValue value)
    {
        if (path.Segments.IsDefaultOrEmpty)
        {
            value = default;
            return false;
        }

        var segments = path.Segments.AsSpan();
        var first = segments[0];
        if (first.Kind != SegmentKind.Field)
        {
            throw new NotSupportedException(
                $"Observation field lookup does not support collection-element path '{path}'.");
        }

        if (!TryGetField(first.Segment!, out value))
            return false;
        if (segments.Length == 1)
            return true;

        var nested = value;
        return nested.TryGetFieldSegments(segments[1..], out value);
    }

    /// <summary>
    /// Attempts to read a field value by ordinal.
    /// </summary>
    public bool TryGetField(int ordinal, out ObservationValue value)
    {
        if ((uint)ordinal >= (uint)Layout.Count || !buffer.HasValue(ordinal))
        {
            value = default;
            return false;
        }

        value = buffer.GetValue(ordinal);
        return true;
    }

    /// <summary>
    /// Ensures this observation adheres to the supplied shape semantics.
    /// </summary>
    /// <param name="shape">Expected semantic shape.</param>
    /// <param name="graph">Optional shape graph used to resolve named type references.</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public void EnsureAdheresToShape(Shape shape, ShapeGraph? graph = null)
    {
        if (ObservationShapeValidator.TryValidateAgainstShape(this, shape, out var validationError, graph))
            return;

        throw new InvalidOperationException($"Observation '{ShapeId.Value}:{Id}' does not adhere to shape '{shape.Id.Value}': {validationError}");
    }

    /// <summary>
    /// Creates an observation from a JSON document with a nested state object payload.
    /// </summary>
    public static Observation FromJsonDocument(
        ObservationLayout layout,
        JsonDocument document,
        string id,
        long version = 0,
        string statePropertyName = "state",
        ObservationLineage? lineage = null
        )
    {
        var options = new JsonObservationReadOptions
        {
            FlattenedState = false,
            StatePropertyName = statePropertyName,
            IdOverride = id,
            VersionOverride = version
        };
        return FromJsonDocument(
            layout: layout,
            document: document,
            options: options,
            lineage: lineage
            );
    }

    /// <summary>
    /// Creates an observation by reading metadata and state directly from a JSON document.
    /// Supports both nested <c>state</c> objects and flattened root documents.
    /// </summary>
    public static Observation FromJsonDocument(ObservationLayout layout, JsonDocument document, JsonObservationReadOptions? options = null, ObservationLineage? lineage = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new();
        var root = document.RootElement;
        const string SourceName = nameof(document);
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("JSON payload root must be an object.", SourceName);

        var id = options.IdOverride ?? ReadRequiredString(root, options.IdPropertyName);
        
        var version = options.VersionOverride
                      ?? ReadVersion(root, options.VersionPropertyName)
                      ?? 0;

        root = options.FlattenedState 
            ? root
            : root.TryGetProperty(options.StatePropertyName, out var nestedState)
                ? nestedState
                : throw new ArgumentException($"JSON payload does not contain '{options.StatePropertyName}'.", SourceName);
        
        var ignored = BuildIgnoredProperties(options);
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("State payload must be a JSON object.", SourceName);

        var values = new ObservationValue[layout.Count];
        var hasValueBitMask = new ulong[ObservationBuffer.RequiredWordCount(layout.Count)];
        foreach (var property in root.EnumerateObject())
        {
            if (ignored is not null && ignored.Contains(property.Name))
                continue;

            if (!layout.TryGetOrdinal(property.Name, out var ordinal))
                continue;

            values[ordinal] = ObservationValue.FromJsonElement(property.Value);
            ObservationBuffer.SetHasValue(hasValueBitMask, ordinal);
        }

        return new(
            layout: layout,
            id: id,
            buffer: new(values, hasValueBitMask, layout.Count),
            version: version,
            lineage: lineage
        );
    }

    Dictionary<string, ObservationValue> BuildDictionary()
    {
        Dictionary<string, ObservationValue> map = new(Layout.Count, StringComparer.Ordinal);
        for (var i = 0; i < Layout.Count; i++)
        {
            if (!buffer.HasValue(i))
                continue;

            map[Layout.FieldNames[i]] = buffer.GetValue(i);
        }
        return map;
    }

    static ObservationBuffer BuildBuffer(ObservationLayout layout, IReadOnlyDictionary<string, ObservationValue> fields)
    {
        var values = new ObservationValue[layout.Count];
        var hasValueBitMask = new ulong[ObservationBuffer.RequiredWordCount(layout.Count)];
        foreach (var (fieldName, value) in fields)
        {
            var ordinal = layout.GetOrdinal(fieldName);
            values[ordinal] = value;
            ObservationBuffer.SetHasValue(hasValueBitMask, ordinal);
        }

        return new(values, hasValueBitMask, layout.Count);
    }

    static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
            throw new ArgumentException("Property name must be non-empty.", nameof(propertyName));

        if (!root.TryGetProperty(propertyName, out var value))
            throw new ArgumentException($"JSON document does not contain required property '{propertyName}'.", nameof(root));

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException($"JSON document property '{propertyName}' must be a non-empty string.", nameof(root));
        
        return text;
    }

    static long? ReadVersion(JsonElement root, string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return null;

        if (!root.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.Null => null,
            _ => throw new ArgumentException($"JSON document property '{propertyName}' must be an integer.")
        };
    }

    static IReadOnlySet<string>? BuildIgnoredProperties(JsonObservationReadOptions options)
    {
        if (!options.FlattenedState && (options.IgnoredStateProperties is null || options.IgnoredStateProperties.Count == 0))
            return options.IgnoredStateProperties;

        if (!options.FlattenedState || !options.IgnoreMetadataInFlattenedState)
            return options.IgnoredStateProperties;

        HashSet<string> ignored = new(StringComparer.Ordinal);
        foreach (var name in new[]
                 {
                     options.IdPropertyName,
                     options.VersionPropertyName,
                     options.StatePropertyName
                 })
        {
            if (!string.IsNullOrWhiteSpace(name))
                ignored.Add(name);
        }

        if (options.IgnoredStateProperties is not null)
        {
            foreach (var property in options.IgnoredStateProperties)
                ignored.Add(property);
        }

        return ignored;
    }

}
