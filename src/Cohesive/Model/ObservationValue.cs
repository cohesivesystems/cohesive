using System.Collections;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Cohesive.Model.Serialization;

namespace Cohesive.Model;

/// <summary>
/// Compact scalar/object/array JSON-like value container used by observation and entity-state snapshots.
/// </summary>
/// <param name="kind">The logical value kind that selects the active payload.</param>
/// <param name="int64">The Int64 payload when <paramref name="kind"/> is <see cref="ObservationValueKind.Int64"/>.</param>
/// <param name="d">The binary floating-point payload when <paramref name="kind"/> is <see cref="ObservationValueKind.Double"/>.</param>
/// <param name="b">The Boolean payload when <paramref name="kind"/> is <see cref="ObservationValueKind.Bool"/>.</param>
/// <param name="s">The string or temporal text payload for string-backed kinds.</param>
/// <param name="fields">The object properties when <paramref name="kind"/> is <see cref="ObservationValueKind.Object"/>.</param>
/// <param name="array">The array items when <paramref name="kind"/> is <see cref="ObservationValueKind.Array"/>.</param>
/// <param name="bytes">The binary payload when <paramref name="kind"/> is <see cref="ObservationValueKind.Bytes"/>.</param>
/// <param name="dec">The exact base-10 payload when <paramref name="kind"/> is <see cref="ObservationValueKind.Decimal"/>.</param>
/// <remarks>
/// Caller-owned mutable object dictionaries, arrays, and byte buffers are snapshotted during construction.
/// Subsequent mutation of the supplied storage does not change the observation value. Immutable collection
/// storage may be retained directly because its ownership contract prevents subsequent mutation.
/// </remarks>
[JsonConverter(typeof(ObservationValueJsonConverter))]
public readonly struct ObservationValue(
    ObservationValueKind kind,
    long int64 = 0,
    double d = 0,
    bool b = false,
    string? s = null,
    IReadOnlyDictionary<string, ObservationValue>? fields = null,
    ObservationValue[]? array = null,
    ReadOnlyMemory<byte> bytes = default,
    decimal dec = 0m
    ) : IEquatable<ObservationValue>
{
    ObservationValue(ImmutableArray<ObservationValue> array)
        : this(ObservationValueKind.Array)
    {
        Array = array;
    }

    /// <summary>
    /// Value shape kind.
    /// </summary>
    public ObservationValueKind Kind { get; } = kind;

    /// <summary>
    /// 64-bit integer payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Int64"/>.
    /// </summary>
    public long Int64 { get; } = int64;

    /// <summary>
    /// Floating-point payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Double"/>.
    /// </summary>
    public double Double { get; } = d;

    /// <summary>
    /// Base-10 numeric payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Decimal"/>.
    /// </summary>
    public decimal Decimal { get; } = dec;

    /// <summary>
    /// Boolean payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Bool"/>.
    /// </summary>
    public bool Bool { get; } = b;

    /// <summary>
    /// String payload when <see cref="Kind"/> is <see cref="ObservationValueKind.String"/>
    /// or a temporal string-backed kind.
    /// </summary>
    public string? String { get; } = s;

    /// <summary>
    /// Object payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Object"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ObservationValue>? Fields { get; } = SnapshotFields(fields);

    /// <summary>
    /// Immutable array payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Array"/>.
    /// </summary>
    public ImmutableArray<ObservationValue> Array { get; } = SnapshotArray(array);

    /// <summary>
    /// Binary payload when <see cref="Kind"/> is <see cref="ObservationValueKind.Bytes"/>.
    /// </summary>
    public ReadOnlyMemory<byte> Bytes { get; } = SnapshotBytes(bytes);

    /// <summary>Attempts to resolve a direct or nested field from this object value.</summary>
    /// <param name="path">Canonical field-only path to resolve. A default path is treated as absent.</param>
    /// <param name="value">The resolved value when present; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when this value is an object containing the complete path; otherwise
    /// <see langword="false"/>. A default path is treated as absent.
    /// </returns>
    /// <exception cref="NotSupportedException"><paramref name="path"/> contains collection-element navigation.</exception>
    public bool TryGetField(FieldPath path, out ObservationValue value) =>
        TryGetFieldSegments(path.Segments.AsSpan(), out value);

    /// <summary>Returns an object value with one direct or nested field assigned immutably.</summary>
    /// <param name="path">Non-empty canonical field-only path to assign.</param>
    /// <param name="value">
    /// Value to assign. <see cref="Undefined"/> removes the terminal field and every empty object ancestor
    /// encountered along <paramref name="path"/>.
    /// </param>
    /// <returns>
    /// A new object value containing the assignment. Existing object properties are retained and ordered ordinally.
    /// </returns>
    /// <remarks>
    /// An <see cref="Undefined"/> or <see cref="Null"/> receiver is treated as an empty object. An existing object is
    /// copied; this value and its dictionaries are never mutated.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// An assignment traverses an existing scalar or array value.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="path"/> contains a non-field segment, including collection-element navigation.
    /// </exception>
    public ObservationValue WithField(FieldPath path, ObservationValue value)
    {
        if (path.Segments.IsDefaultOrEmpty)
            throw new ArgumentException("An object assignment requires a non-empty field path.", nameof(path));

        return WithFieldCore(this, path, segmentIndex: 0, value);
    }

    /// <summary>Returns an object value with one direct or nested field removed immutably.</summary>
    /// <param name="path">Non-empty canonical field-only path to remove.</param>
    /// <returns>
    /// A new object value without the terminal field. Every empty object ancestor encountered along
    /// <paramref name="path"/> is removed as well.
    /// </returns>
    /// <remarks>
    /// An <see cref="Undefined"/> or <see cref="Null"/> receiver is treated as an empty object, so removing from either
    /// produces an empty object value. An existing object is copied and is never mutated.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="path"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">
    /// The removal traverses an existing scalar or array value.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="path"/> contains a non-field segment, including collection-element navigation.
    /// </exception>
    public ObservationValue WithoutField(FieldPath path) => WithField(path, Undefined);

    /// <summary>
    /// Attempts to resolve a direct or nested field from this object value without materializing a
    /// <see cref="FieldPath"/>.
    /// </summary>
    /// <param name="path">Canonical field-only path segments to resolve. An empty span is treated as absent.</param>
    /// <param name="value">The resolved value when present; otherwise the default value.</param>
    /// <returns>
    /// <see langword="true"/> when this value is an object containing the complete path using ordinal field-name
    /// matching; otherwise <see langword="false"/>.
    /// </returns>
    /// <exception cref="NotSupportedException"><paramref name="path"/> contains collection-element navigation.</exception>
    public bool TryGetFieldSegments(ReadOnlySpan<FieldPathSegment> path, out ObservationValue value)
    {
        if (path.IsEmpty)
        {
            value = default;
            return false;
        }

        var current = this;
        foreach (var segment in path)
        {
            if (segment.Kind != SegmentKind.Field)
            {
                throw new NotSupportedException(
                    "Observation value field lookup does not support collection-element navigation.");
            }

            if (!current.TryGetProperty(segment.Segment!, out var next))
            {
                value = default;
                return false;
            }

            current = next;
        }

        value = current;
        return true;
    }

    static IReadOnlyDictionary<string, ObservationValue>? SnapshotFields(
        IReadOnlyDictionary<string, ObservationValue>? fields)
    {
        if (fields is null)
            return null;
        if (fields.Count == 0)
            return EmptyObjectValues;
        if (fields is ImmutableDictionary<string, ObservationValue> immutable
            && ReferenceEquals(immutable.KeyComparer, StringComparer.Ordinal))
        {
            return fields;
        }
        if (fields is ImmutableSortedDictionary<string, ObservationValue> sorted
            && ReferenceEquals(sorted.KeyComparer, StringComparer.Ordinal))
        {
            return fields;
        }

        Dictionary<string, ObservationValue> snapshot = new(fields.Count, StringComparer.Ordinal);
        foreach (var (name, value) in fields)
            snapshot.Add(name, value);
        return new ReadOnlyDictionary<string, ObservationValue>(snapshot);
    }

    static ImmutableArray<ObservationValue> SnapshotArray(ObservationValue[]? array) => array switch
    {
        null => [],
        { Length: 0 } => [],
        _ => ImmutableArray.CreateRange(array)
    };

    static ReadOnlyMemory<byte> SnapshotBytes(ReadOnlyMemory<byte> bytes) =>
        bytes.IsEmpty ? ReadOnlyMemory<byte>.Empty : bytes.ToArray();

    static ObservationValue WithFieldCore(
        ObservationValue current,
        FieldPath path,
        int segmentIndex,
        ObservationValue value)
    {
        var segment = path.Segments[segmentIndex];
        if (segment.Kind != SegmentKind.Field || string.IsNullOrWhiteSpace(segment.Segment))
        {
            throw new NotSupportedException(
                $"Observation value assignment does not support collection-element path '{path}'.");
        }

        var name = segment.Segment;
        var fields = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        if (current.Kind == ObservationValueKind.Object && current.Fields is not null)
        {
            foreach (var (key, existing) in current.Fields)
                fields[key] = existing;
        }
        else if (current.Kind is not ObservationValueKind.Undefined and not ObservationValueKind.Null)
        {
            throw new InvalidOperationException(
                $"Field path '{path}' cannot be assigned through value kind '{current.Kind}'.");
        }

        if (segmentIndex == path.Segments.Length - 1)
        {
            if (value.Kind == ObservationValueKind.Undefined)
                fields.Remove(name);
            else
                fields[name] = value;
        }
        else
        {
            var child = fields.GetValueOrDefault(name, EmptyObject);
            var updated = WithFieldCore(child, path, segmentIndex + 1, value);
            if (updated.Kind == ObservationValueKind.Object && updated.Fields?.Count == 0)
                fields.Remove(name);
            else
                fields[name] = updated;
        }

        return FromObject(fields.ToImmutable());
    }

    static readonly ObservationValue[] EmptyArrayValues = [];
    static readonly IReadOnlyDictionary<string, ObservationValue> EmptyObjectValues = new ReadOnlyDictionary<string, ObservationValue>(new Dictionary<string, ObservationValue>(capacity: 0, comparer: StringComparer.Ordinal));
    const int UndefinedHash = unchecked((int)0x5F9A43C1);
    const int NullHash = unchecked((int)0x4A0F1B77);
    const int FalseHash = unchecked((int)0x11F1C2A3);
    const int TrueHash = unchecked((int)0x22E4D5B1);
    const int NumericHashMarker = unchecked((int)0x4E554D31);
    const int StringHashMarker = unchecked((int)0x53545231);
    const int DateTimeOffsetHashMarker = unchecked((int)0x44544F31);
    const int DateOnlyHashMarker = unchecked((int)0x444F4E31);
    const int TimeOnlyHashMarker = unchecked((int)0x544F4E31);
    const int TimeSpanHashMarker = unchecked((int)0x54535031);
    const int BytesHashSeed = unchecked((int)0x42595445);
    const int ObjectHashSeed = unchecked((int)0x4F424A31);
    const int ArrayHashSeed = unchecked((int)0x41525231);

    /// <summary>
    /// Creates an undefined observation value.
    /// </summary>
    public static ObservationValue Undefined => new(ObservationValueKind.Undefined);

    /// <summary>
    /// Creates a null observation value.
    /// </summary>
    public static ObservationValue Null => new(ObservationValueKind.Null);

    /// <summary>Gets the shared immutable empty object value.</summary>
    public static ObservationValue EmptyObject => new(ObservationValueKind.Object, fields: EmptyObjectValues);

    /// <summary>
    /// Creates an Int64 observation value.
    /// </summary>
    public static ObservationValue FromInt64(long value) => new(ObservationValueKind.Int64, int64: value);

    /// <summary>
    /// Creates a Double observation value.
    /// </summary>
    public static ObservationValue FromDouble(double value) => new(ObservationValueKind.Double, d: value);

    /// <summary>
    /// Creates a Boolean observation value.
    /// </summary>
    public static ObservationValue FromBool(bool value) => new(ObservationValueKind.Bool, b: value);

    /// <summary>
    /// Creates a String observation value, or <see cref="Null"/> when <paramref name="value"/> is null.
    /// </summary>
    public static ObservationValue FromString(string? value) => value is null ? Null : new(ObservationValueKind.String, s: value);

    /// <summary>
    /// Creates a DateTimeOffset observation value, stored in round-trip string form.
    /// </summary>
    public static ObservationValue FromDateTimeOffset(DateTimeOffset value)
        => new(ObservationValueKind.DateTimeOffset, s: value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a DateOnly observation value, stored in round-trip string form.
    /// </summary>
    public static ObservationValue FromDateOnly(DateOnly value)
        => new(ObservationValueKind.DateOnly, s: value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a TimeOnly observation value, stored in round-trip string form.
    /// </summary>
    public static ObservationValue FromTimeOnly(TimeOnly value)
        => new(ObservationValueKind.TimeOnly, s: value.ToString("O", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a TimeSpan observation value, stored in constant string form.
    /// </summary>
    public static ObservationValue FromTimeSpan(TimeSpan value)
        => new(ObservationValueKind.TimeSpan, s: value.ToString("c", CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a bytes observation value and copies the source buffer.
    /// </summary>
    /// <param name="value">Caller-owned bytes to snapshot.</param>
    /// <returns>A bytes observation value backed by an owned snapshot of <paramref name="value"/>.</returns>
    public static ObservationValue FromBytes(ReadOnlyMemory<byte> value) =>
        new(ObservationValueKind.Bytes, bytes: value);

    /// <summary>
    /// Creates an object observation value, snapshotting mutable properties when necessary.
    /// </summary>
    /// <param name="values">
    /// Properties that may be retained when they use built-in immutable storage with ordinal key semantics;
    /// otherwise caller-owned properties to snapshot.
    /// </param>
    /// <returns>An object observation value backed by ordinal, immutable or read-only property storage.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public static ObservationValue FromObject(IReadOnlyDictionary<string, ObservationValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(ObservationValueKind.Object, fields: values);
    }

    /// <summary>
    /// Creates an array observation value and copies the provided items.
    /// </summary>
    /// <param name="values">Caller-owned array items to snapshot.</param>
    /// <returns>An array observation value backed by an owned snapshot of <paramref name="values"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public static ObservationValue FromArray(ObservationValue[] values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new(ObservationValueKind.Array, array: values);
    }

    /// <summary>
    /// Creates an array observation value backed directly by immutable item storage.
    /// </summary>
    /// <param name="values">Immutable array items whose backing storage is retained without copying.</param>
    /// <returns>An array observation value backed by <paramref name="values"/>.</returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> is the default immutable array.</exception>
    /// <remarks>
    /// Unlike <see cref="FromArray(ObservationValue[])"/>, this factory does not copy the supplied storage because
    /// <see cref="ImmutableArray{T}"/> carries an immutable ownership contract. Callers must not mutate its backing
    /// storage through unsafe or interop APIs after construction.
    /// </remarks>
    public static ObservationValue FromImmutableArray(ImmutableArray<ObservationValue> values)
    {
        if (values.IsDefault)
            throw new ArgumentException("Observation array items must be initialized.", nameof(values));

        return new(values);
    }

    /// <summary>
    /// Converts a <see cref="JsonElement"/> into an <see cref="ObservationValue"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Unsupported JsonValueKind.</exception>
    public static ObservationValue FromJsonElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Undefined => Undefined,
        JsonValueKind.Null => Null,
        JsonValueKind.True => FromBool(true),
        JsonValueKind.False => FromBool(false),
        JsonValueKind.String => FromString(element.GetString()),
        JsonValueKind.Number when element.TryGetInt64(out var int64) => FromInt64(int64),
        JsonValueKind.Number when TryParseExactJsonDecimal(element.GetRawText(), out var dec) => FromDecimal(dec),
        JsonValueKind.Number when element.TryGetDouble(out var dbl) => FromDouble(dbl),
        JsonValueKind.Number => FromDouble(double.Parse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture)),
        JsonValueKind.Object => FromObject(ReadObject(element)),
        JsonValueKind.Array => FromImmutableArray(ReadArray(element)),
        _ => throw new InvalidOperationException($"Unsupported JsonValueKind '{element.ValueKind}'.")
    };

    /// <summary>
    /// Converts a <see cref="JsonNode"/> into an <see cref="ObservationValue"/>.
    /// </summary>
    /// <param name="node"></param>
    /// <returns></returns>
    /// <exception cref="NotSupportedException">Unknown JsonNode type.</exception>
    public static ObservationValue FromJsonNode(JsonNode? node)
    {
        if (node is null)
            return Null;

        if (node is JsonValue value)
        {
            if (value.TryGetValue<JsonElement>(out var element))
                return FromJsonElement(element);

            if (value.TryGetValue<bool>(out var boolean))
                return FromBool(boolean);

            if (value.TryGetValue<long>(out var int64))
                return FromInt64(int64);

            if (value.TryGetValue<decimal>(out var dec))
                return FromDecimal(dec);

            if (value.TryGetValue<double>(out var dbl))
                return FromDouble(dbl);

            if (value.TryGetValue<string>(out var text))
                return FromString(text);
        }

        return node switch
        {
            JsonObject obj => FromObject(ReadJsonObject(obj)),
            JsonArray arr => FromImmutableArray(ReadJsonArray(arr)),
            _ => throw new NotSupportedException($"Unknown JsonNode type '{node.GetType().Name}'.")
        };
        
        static IImmutableDictionary<string, ObservationValue> ReadJsonObject(JsonObject obj)
        {
            var values = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (var (key, value) in obj)
                values[key] = FromJsonNode(value);
            return values.ToImmutable();
        }
        
        static ImmutableArray<ObservationValue> ReadJsonArray(JsonArray arr)
        {
            var values = ImmutableArray.CreateBuilder<ObservationValue>(arr.Count);
            foreach (var item in arr)
                values.Add(FromJsonNode(item));
            return values.MoveToImmutable();
        }
    }

    /// <summary>
    /// Converts an arbitrary CLR value into an <see cref="ObservationValue"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="float"/> values use their shortest round-trip single-precision decimal representation so their
    /// projection agrees with the portable Decimal contract inferred for the CLR type. <see cref="double"/> values
    /// retain the Double observation kind, while <see cref="decimal"/> values retain the Decimal kind.
    /// </remarks>
    /// <param name="value">CLR value to project, or <see langword="null"/> for an explicit null observation.</param>
    /// <returns>The recursively projected portable observation value.</returns>
    /// <exception cref="NotSupportedException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="JsonException"></exception>
    public static ObservationValue FromObject(object? value)
    {
        HashSet<object>? visited = null;
        return FromObjectCore(value, ref visited);
    }

    static ObservationValue FromObjectCore(object? value, ref HashSet<object>? visited)
    {
        switch (value)
        {
            case null: return Null;
            case ObservationValue observed: return observed;
            case JsonElement element: return FromJsonElement(element);
            case JsonDocument document: return FromJsonElement(document.RootElement);
            case JsonNode node: return FromJsonNode(node);
            case IReadOnlyDictionary<string, ObservationValue> objectValue: return FromObject(objectValue);
        }

        if (TryProjectJsonConvertedValue(value, out var jsonConverted))
            return jsonConverted;

        if (TryProjectKnownScalar(value, out var scalar))
            return scalar;
        
        return value switch
        {
            IDictionary dictionary => FromDictionary(dictionary, ref visited),
            IEnumerable enumerable and not string => FromEnumerable(enumerable, ref visited),
            _ => FromClrObjectProperties(value, ref visited)
        };
    }

    /// <summary>
    /// Projects a CLR input object into an object-shaped observation value keyed by CLR property name.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public static ObservationValue FromClrPropertyBag(object? value, JsonSerializerOptions? options = null)
    {
        HashSet<object>? visited = null;
        if (value is null)
            return FromObject(EmptyObjectValues);

        if (value is ObservationValue observationValue)
        {
            return observationValue.Kind switch
            {
                ObservationValueKind.Undefined or ObservationValueKind.Null => FromObject(EmptyObjectValues),
                ObservationValueKind.Object when observationValue.Fields is not null => FromObject(observationValue.Fields),
                ObservationValueKind.Object => FromObject(EmptyObjectValues),
                _ => throw new InvalidOperationException($"CLR input kind '{observationValue.Kind}' cannot be projected as an object property bag.")
            };
        }

        var observed = value is IDictionary dictionary
            ? FromDictionary(dictionary, ref visited)
            : FromClrObjectProperties(value, ref visited);
        
        if (observed.Kind == ObservationValueKind.Object && observed.Fields is not null)
            return observed;

        throw new InvalidOperationException($"CLR input type '{value.GetType().FullName}' cannot be projected as an object property bag.");
    }
    
    /// <summary>
    /// Projects a known non-primitive CLR object into field values keyed by canonical field names.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public static IReadOnlyDictionary<string, ObservationValue> ToFieldDictionary(object value, JsonSerializerOptions? options = null)
    {
        var observed = value is ObservationValue observationValue ? observationValue : FromClrPropertyBag(value, options);
        if (observed.Kind != ObservationValueKind.Object || observed.Fields is null)
            throw new InvalidOperationException($"Value of CLR type '{value.GetType().FullName}' did not project to an object-shaped observation value.");
        
        return observed.Fields;
    }

    static PropertyInfo[] GetReadableProperties(Type type) => ShapeTypeInspector.GetReadableProperties(type);

    static bool TryProjectKnownScalar(object value, out ObservationValue observed)
    {
        switch (value)
        {
            case byte[] bytes:
                observed = FromBytes(bytes);
                return true;
            case ReadOnlyMemory<byte> bytes:
                observed = FromBytes(bytes);
                return true;
            case Memory<byte> bytes:
                observed = FromBytes(bytes);
                return true;
            case ArraySegment<byte> bytes:
                observed = FromBytes(bytes.AsMemory());
                return true;
            case bool boolean:
                observed = FromBool(boolean);
                return true;
            case byte b:
                observed = FromInt64(b);
                return true;
            case sbyte b:
                observed = FromInt64(b);
                return true;
            case short i:
                observed = FromInt64(i);
                return true;
            case ushort i:
                observed = FromInt64(i);
                return true;
            case int i:
                observed = FromInt64(i);
                return true;
            case uint i:
                observed = FromInt64(i);
                return true;
            case long i:
                observed = FromInt64(i);
                return true;
            case ulong i when i <= long.MaxValue:
                observed = FromInt64((long)i);
                return true;
            case ulong i:
                observed = FromDouble(i);
                return true;
            case float f:
                observed = FromSingle(f);
                return true;
            case double d:
                observed = FromDouble(d);
                return true;
            case decimal d:
                observed = FromDecimal(d);
                return true;
            case char ch:
                observed = FromString(ch.ToString());
                return true;
            case string s:
                observed = FromString(s);
                return true;
            case Guid guid:
                observed = FromString(guid.ToString());
                return true;
            case DateTime dateTime:
                observed = FromString(dateTime.ToString("O", CultureInfo.InvariantCulture));
                return true;
            case DateTimeOffset dateTimeOffset:
                observed = FromDateTimeOffset(dateTimeOffset);
                return true;
            case DateOnly dateOnly:
                observed = FromDateOnly(dateOnly);
                return true;
            case TimeOnly timeOnly:
                observed = FromTimeOnly(timeOnly);
                return true;
            case TimeSpan timeSpan:
                observed = FromTimeSpan(timeSpan);
                return true;
            case Uri uri:
                observed = FromString(uri.ToString());
                return true;
            case Enum @enum:
                observed = FromString(@enum.ToString());
                return true;
        }

        observed = default;
        return false;
    }

    static ObservationValue FromSingle(float value)
    {
        // Preserve the float's own shortest round-trip value. Widening first would expose binary noise that can
        // contradict the Decimal contract authored for the same CLR type.
        if (float.IsFinite(value)
            && decimal.TryParse(
                value.ToString("R", CultureInfo.InvariantCulture),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var canonical))
        {
            return FromDecimal(canonical);
        }

        return FromDouble(value);
    }

    static bool TryProjectJsonConvertedValue(object value, out ObservationValue observed)
    {
        var type = value.GetType();
        if (type.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is null
            && !PortableJsonValueAttribute.TryGetKind(type, out _))
        {
            observed = default;
            return false;
        }

        observed = FromJsonNode(JsonSerializer.SerializeToNode(value, type));
        return true;
    }

    static ObservationValue FromDictionary(IDictionary dictionary, ref HashSet<object>? visited)
    {
        var entered = TryEnterReference(dictionary, ref visited);
        try
        {
            if (dictionary.Count == 0)
                return FromObject(EmptyObjectValues);

            var values = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
            {
                if (!TryProjectDictionaryKey(entry.Key, out var key))
                    throw new InvalidOperationException("CLR input dictionary keys must be strings.");
                values[key] = FromObjectCore(entry.Value, ref visited);
            }

            return FromObject(values.ToImmutable());
        }
        finally
        {
            ExitReference(dictionary, visited, entered);
        }
    }

    static bool TryProjectDictionaryKey(object? value, out string key)
    {
        switch (value)
        {
            case string text:
                key = text;
                return true;
            case null:
                key = "";
                return false;
        }

        var type = value.GetType();
        if (type.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is not null)
        {
            var node = JsonSerializer.SerializeToNode(value, type);
            if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var converted) && !string.IsNullOrWhiteSpace(converted))
            {
                key = converted;
                return true;
            }
        }

        key = "";
        return false;
    }

    static ObservationValue FromEnumerable(IEnumerable enumerable, ref HashSet<object>? visited)
    {
        var entered = TryEnterReference(enumerable, ref visited);
        try
        {
            var capacity = enumerable is ICollection collection ? collection.Count : 0;
            var values = ImmutableArray.CreateBuilder<ObservationValue>(capacity);
            foreach (var item in enumerable)
                values.Add(FromObjectCore(item, ref visited));

            var immutableValues = values.Count == values.Capacity
                ? values.MoveToImmutable()
                : values.ToImmutable();
            return FromImmutableArray(immutableValues);
        }
        finally
        {
            ExitReference(enumerable, visited, entered);
        }
    }

    static ObservationValue FromClrObjectProperties(object value, ref HashSet<object>? visited)
    {
        var entered = TryEnterReference(value, ref visited);
        try
        {
            var properties = GetReadableProperties(value.GetType());
            if (properties.Length == 0)
                return FromObject(EmptyObjectValues);

            var values = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                if (ShouldIgnoreProperty(property))
                    continue;

                var propertyName = ResolvePropertyName(property);
                values[propertyName] = FromPropertyValue(value, property, ref visited);
            }

            return values.Count == 0
                ? FromObject(EmptyObjectValues)
                : FromObject(values.ToImmutable());
        }
        finally
        {
            ExitReference(value, visited, entered);
        }
    }

    static ObservationValue FromPropertyValue(object source, PropertyInfo property, ref HashSet<object>? visited)
    {
        var value = property.GetValue(source);
        if (value is null)
            return Null;

        if (ShouldProjectWithDeclaredJsonType(property.PropertyType))
            return FromJsonNode(JsonSerializer.SerializeToNode(value, property.PropertyType));

        return FromObjectCore(value, ref visited);
    }

    static bool ShouldProjectWithDeclaredJsonType(Type type)
    {
        if (type.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is not null
            || type.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: true) is not null
            || PortableJsonValueAttribute.TryGetKind(type, out _))
        {
            return true;
        }

        return TryGetSequenceElementType(type, out var elementType)
               && (elementType.GetCustomAttribute<JsonConverterAttribute>(inherit: true) is not null
                   || elementType.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: true) is not null
                   || PortableJsonValueAttribute.TryGetKind(elementType, out _));
    }

    static bool TryGetSequenceElementType(Type type, out Type elementType)
    {
        if (type == typeof(string))
        {
            elementType = typeof(void);
            return false;
        }

        if (type.IsArray)
        {
            elementType = type.GetElementType() ?? typeof(void);
            return elementType != typeof(void);
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
        {
            elementType = type.GetGenericArguments()[0];
            return true;
        }

        var interfaces = type.GetInterfaces();
        for (var i = 0; i < interfaces.Length; i++)
        {
            var candidate = interfaces[i];
            if (!candidate.IsGenericType || candidate.GetGenericTypeDefinition() != typeof(IEnumerable<>))
                continue;

            elementType = candidate.GetGenericArguments()[0];
            return true;
        }

        elementType = typeof(void);
        return false;
    }

    static bool ShouldIgnoreProperty(PropertyInfo property)
        => property.GetCustomAttribute<JsonIgnoreAttribute>(inherit: true) is not null;

    static string ResolvePropertyName(PropertyInfo property)
        => property.GetCustomAttribute<JsonPropertyNameAttribute>(inherit: true)?.Name
           ?? property.Name;

    static bool TryEnterReference(object value, ref HashSet<object>? visited)
    {
        if (!value.GetType().IsClass)
            return false;

        visited ??= new(ReferenceEqualityComparer.Instance);
        if (!visited.Add(value))
            throw new InvalidOperationException("Cyclic CLR object graphs are not supported by ObservationValue projection.");
        return true;
    }

    static void ExitReference(object value, HashSet<object>? visited, bool entered)
    {
        if (entered)
            visited!.Remove(value);
    }

    /// <summary>
    /// Attempts to read the value as <see cref="long"/>.
    /// </summary>
    public bool TryGetInt64(out long value)
    {
        switch (Kind)
        {
            case ObservationValueKind.Int64:
                value = Int64;
                return true;
            case ObservationValueKind.Decimal when Decimal is >= long.MinValue and <= long.MaxValue:
            {
                var truncated = decimal.Truncate(Decimal);
                if (truncated == Decimal)
                {
                    value = (long)truncated;
                    return true;
                }

                break;
            }
            case ObservationValueKind.Double
                when Math.TryGetCanonicalDecimalFromDouble(Double, out var canonical)
                     && canonical is >= long.MinValue and <= long.MaxValue:
            {
                var truncated = decimal.Truncate(canonical);
                if (truncated == canonical)
                {
                    value = (long)truncated;
                    return true;
                }

                break;
            }
            case ObservationValueKind.String when long.TryParse(String, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="long"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public long GetInt64()
    {
        if (!TryGetInt64(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Int64.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="int"/>.
    /// </summary>
    public bool TryGetInt32(out int value)
    {
        if (TryGetInt64(out var int64) && int64 >= int.MinValue && int64 <= int.MaxValue)
        {
            value = (int)int64;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="int"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public int GetInt32()
    {
        if (!TryGetInt32(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Int32.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="double"/>.
    /// </summary>
    public bool TryGetDouble(out double value)
    {
        switch (Kind)
        {
            case ObservationValueKind.Int64:
                value = Int64;
                return true;
            case ObservationValueKind.Double:
                value = Double;
                return true;
            case ObservationValueKind.Decimal:
                value = (double)Decimal;
                return true;
            case ObservationValueKind.String when double.TryParse(String, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                value = parsed;
                return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="double"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public double GetDouble()
    {
        if (!TryGetDouble(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Double.");
        return value;
    }

    /// <summary>
    /// Attempts to recover the canonical base-10 value used to compare and persist numeric values.
    /// </summary>
    /// <param name="value">
    /// The exact canonical decimal when this value is an Int64, Decimal, or a Double whose shortest
    /// round-trip JSON representation is exactly representable by <see cref="decimal"/>; otherwise zero.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when this numeric value has an exact canonical decimal representation;
    /// otherwise <see langword="false"/>.
    /// </returns>
    public bool TryGetCanonicalNumericDecimal(out decimal value)
    {
        switch (Kind)
        {
            case ObservationValueKind.Int64:
                value = Int64;
                return true;
            case ObservationValueKind.Decimal:
                value = Decimal;
                return true;
            case ObservationValueKind.Double:
                return Math.TryGetCanonicalDecimalFromDouble(Double, out value);
            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// Attempts to read the value as <see cref="decimal"/>.
    /// </summary>
    /// <param name="value">
    /// The exact canonical numeric value, or an invariant-culture decimal parsed from a string value.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the value has an exact decimal representation or contains a valid
    /// invariant-culture decimal string; otherwise <see langword="false"/>.
    /// </returns>
    public bool TryGetDecimal(out decimal value)
    {
        if (TryGetCanonicalNumericDecimal(out value))
            return true;

        if (Kind == ObservationValueKind.String
            && decimal.TryParse(
                String,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        value = 0m;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="decimal"/>.
    /// </summary>
    /// <returns>The decimal value.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public decimal GetDecimal()
    {
        if (!TryGetDecimal(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Decimal.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="bool"/>.
    /// </summary>
    /// <param name="value">The parsed boolean value when conversion succeeds; otherwise false.</param>
    /// <returns><c>true</c> when conversion succeeds; otherwise <c>false</c>.</returns>
    public bool TryGetBoolean(out bool value)
    {
        switch (Kind)
        {
            case ObservationValueKind.Bool:
                value = Bool;
                return true;
            case ObservationValueKind.String when bool.TryParse(String, out var parsed):
                value = parsed;
                return true;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="bool"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public bool GetBoolean()
    {
        if (!TryGetBoolean(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Boolean.");
        return value;
    }

    /// <summary>
    /// Reads the value as <see cref="string"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value kind cannot be read as a string.</exception>
    public string? GetString()
    {
        return Kind switch
        {
            ObservationValueKind.String => String,
            ObservationValueKind.DateTimeOffset => String,
            ObservationValueKind.DateOnly => String,
            ObservationValueKind.TimeOnly => String,
            ObservationValueKind.TimeSpan => String,
            ObservationValueKind.Null => null,
            _ => throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as String.")
        };
    }

    /// <summary>
    /// Attempts to read the value as a string.
    /// </summary>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">The value kind cannot be read as a string, or the string is null.</exception>
    public string GetRequiredString() => GetString() ?? throw new InvalidOperationException("The string value is null.");
    
    /// <summary>
    /// Attempts to read the value as <see cref="DateTimeOffset"/>.
    /// </summary>
    public bool TryGetDateTimeOffset(out DateTimeOffset value)
    {
        if (Kind == ObservationValueKind.DateTimeOffset
            && TryParseDateTimeOffset(String, exact: true, out value))
        {
            return true;
        }

        if (Kind == ObservationValueKind.String
            && TryParseDateTimeOffset(String, exact: false, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Attempts to read the value as an absolute instant.
    /// </summary>
    /// <param name="value">The parsed instant, including its declared offset, when conversion succeeds; otherwise default.</param>
    /// <returns>
    /// <see langword="true"/> for a valid dedicated <see cref="ObservationValueKind.DateTimeOffset"/> value,
    /// or for a parseable string whose time is followed by an explicit <c>Z</c>, <c>z</c>, or numeric offset;
    /// otherwise <see langword="false"/>. In particular, offset-less civil date-time strings are not instants.
    /// </returns>
    public bool TryGetInstant(out DateTimeOffset value)
    {
        if (Kind == ObservationValueKind.DateTimeOffset)
            return TryParseDateTimeOffset(String, exact: true, out value);

        if (Kind == ObservationValueKind.String
            && HasExplicitDateTimeOffset(String)
            && TryParseDateTimeOffset(String, exact: false, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="DateTimeOffset"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public DateTimeOffset GetDateTimeOffset()
    {
        if (!TryGetDateTimeOffset(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as DateTimeOffset.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="DateOnly"/>.
    /// </summary>
    public bool TryGetDateOnly(out DateOnly value)
    {
        if (Kind == ObservationValueKind.DateOnly
            && TryParseDateOnly(String, exact: true, out value))
        {
            return true;
        }

        if (Kind == ObservationValueKind.String
            && TryParseDateOnly(String, exact: false, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="DateOnly"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public DateOnly GetDateOnly()
    {
        if (!TryGetDateOnly(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as DateOnly.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="TimeOnly"/>.
    /// </summary>
    public bool TryGetTimeOnly(out TimeOnly value)
    {
        if (Kind == ObservationValueKind.TimeOnly
            && TryParseTimeOnly(String, exact: true, out value))
        {
            return true;
        }

        if (Kind == ObservationValueKind.String
            && TryParseTimeOnly(String, exact: false, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="TimeOnly"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public TimeOnly GetTimeOnly()
    {
        if (!TryGetTimeOnly(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as TimeOnly.");
        return value;
    }

    /// <summary>
    /// Attempts to read the value as <see cref="TimeSpan"/>.
    /// </summary>
    public bool TryGetTimeSpan(out TimeSpan value)
    {
        if (Kind == ObservationValueKind.TimeSpan
            && TryParseTimeSpan(String, exact: true, out value))
        {
            return true;
        }

        if (Kind == ObservationValueKind.String
            && TryParseTimeSpan(String, exact: false, out value))
        {
            return true;
        }

        value = TimeSpan.Zero;
        return false;
    }

    /// <summary>
    /// Reads the value as <see cref="TimeSpan"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public TimeSpan GetTimeSpan()
    {
        if (!TryGetTimeSpan(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as TimeSpan.");
        return value;
    }

    /// <summary>
    /// Converts a scalar value to its string representation.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public string? ToScalarString(
        IFormatProvider? formatProvider = null,
        ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Throw
        )
    {
        formatProvider ??= CultureInfo.InvariantCulture;
        return Kind switch
        {
            ObservationValueKind.Undefined => null,
            ObservationValueKind.Null => null,
            ObservationValueKind.String => String,
            ObservationValueKind.DateTimeOffset => String,
            ObservationValueKind.DateOnly => String,
            ObservationValueKind.TimeOnly => String,
            ObservationValueKind.TimeSpan => String,
            ObservationValueKind.Int64 => Int64.ToString(formatProvider),
            ObservationValueKind.Double => Double.ToString(formatProvider),
            ObservationValueKind.Decimal => Decimal.ToString(formatProvider),
            ObservationValueKind.Bool => Bool ? "true" : "false",
            ObservationValueKind.Bytes => bytesEncoding switch
            {
                ObservationBytesJsonEncoding.Throw => throw new InvalidOperationException(
                    "ObservationValue bytes cannot be converted to scalar string with the current policy."),
                ObservationBytesJsonEncoding.Base64String => Convert.ToBase64String(Bytes.Span),
                _ => throw new InvalidOperationException($"Unknown bytes JSON encoding '{bytesEncoding}'.")
            },
            _ => throw new InvalidOperationException($"Value kind '{Kind}' cannot be interpreted as a scalar string.")
        };
    }

    /// <summary>
    /// Attempts to read the value as bytes.
    /// </summary>
    /// <param name="value">The bytes when the value kind is <see cref="ObservationValueKind.Bytes"/>; otherwise default.</param>
    /// <returns><c>true</c> when the value is bytes; otherwise <c>false</c>.</returns>
    public bool TryGetBytes(out ReadOnlyMemory<byte> value)
    {
        if (Kind == ObservationValueKind.Bytes)
        {
            value = Bytes;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads the value as bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public ReadOnlyMemory<byte> GetBytes()
    {
        if (!TryGetBytes(out var value))
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Bytes.");
        return value;
    }

    /// <summary>
    /// Attempts to get an object property value.
    /// </summary>
    /// <param name="propertyName">The property name to read.</param>
    /// <param name="value">The property value when found; otherwise default.</param>
    /// <returns>
    /// <c>true</c> when this value is an object containing the ordinally matched
    /// <paramref name="propertyName"/>; otherwise <c>false</c>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="propertyName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="propertyName"/> is empty or white space.</exception>
    public bool TryGetProperty(string propertyName, out ObservationValue value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        if (Kind != ObservationValueKind.Object || Fields is null)
        {
            value = default;
            return false;
        }

        return Fields.TryGetValue(propertyName, out value);
    }

    /// <summary>
    /// Gets an object property value.
    /// </summary>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="KeyNotFoundException"></exception>
    public ObservationValue GetProperty(string propertyName)
    {
        if (!TryGetProperty(propertyName, out var value))
            throw new KeyNotFoundException($"Object value does not contain property '{propertyName}'.");
        return value;
    }

    /// <summary>
    /// Gets the length of an array value.
    /// </summary>
    /// <returns>The number of retained array items.</returns>
    /// <exception cref="InvalidOperationException">
    /// This value is not an array or does not retain an array payload.
    /// </exception>
    public int GetArrayLength()
    {
        if (Kind != ObservationValueKind.Array || Array.IsDefault)
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Array.");

        return Array.Length;
    }

    /// <summary>
    /// Gets the immutable retained array items without copying.
    /// </summary>
    /// <returns>The immutable retained array payload.</returns>
    /// <exception cref="InvalidOperationException">
    /// This value is not an array or does not retain an array payload.
    /// </exception>
    public ImmutableArray<ObservationValue> EnumerateArray()
    {
        if (Kind != ObservationValueKind.Array || Array.IsDefault)
            throw new InvalidOperationException($"Value kind '{Kind}' cannot be read as Array.");

        return Array;
    }

    /// <summary>
    /// Deserializes this value into a CLR type using JSON conversion.
    /// </summary>
    /// <exception cref="JsonException"></exception>
    /// <exception cref="NotSupportedException"></exception>
    public T? Deserialize<T>(JsonSerializerOptions? options = null, ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Throw) => 
        JsonSerializer.Deserialize<T>(GetRawText(bytesEncoding), options);

    /// <summary>
    /// Writes this value as JSON using the provided writer.
    /// </summary>
    /// <param name="writer">Destination JSON writer.</param>
    /// <param name="bytesEncoding">How bytes values are encoded when encountered.</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    public void WriteTo(Utf8JsonWriter writer, ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Throw)
    {
        ArgumentNullException.ThrowIfNull(writer);
        WriteValue(writer, this, bytesEncoding);
        return;
        
        static void WriteValue(Utf8JsonWriter writer, ObservationValue value, ObservationBytesJsonEncoding bytesEncoding)
        {
            switch (value.Kind)
            {
                case ObservationValueKind.Undefined:
                case ObservationValueKind.Null:
                    writer.WriteNullValue();
                    return;

                case ObservationValueKind.Int64:
                    writer.WriteNumberValue(value.Int64);
                    return;

                case ObservationValueKind.Double:
                    writer.WriteNumberValue(value.Double);
                    return;

                case ObservationValueKind.Decimal:
                    writer.WriteNumberValue(value.Decimal);
                    return;

                case ObservationValueKind.Bool:
                    writer.WriteBooleanValue(value.Bool);
                    return;

                case ObservationValueKind.String:
                    writer.WriteStringValue(value.String);
                    return;

                case ObservationValueKind.DateTimeOffset:
                case ObservationValueKind.DateOnly:
                case ObservationValueKind.TimeOnly:
                case ObservationValueKind.TimeSpan:
                    writer.WriteStringValue(value.String);
                    return;

                case ObservationValueKind.Bytes:
                    switch (bytesEncoding)
                    {
                        case ObservationBytesJsonEncoding.Throw:
                            throw new InvalidOperationException("ObservationValue bytes cannot be encoded as JSON with the current policy.");
                        case ObservationBytesJsonEncoding.Base64String:
                            writer.WriteStringValue(Convert.ToBase64String(value.Bytes.Span));
                            return;
                        default:
                            throw new InvalidOperationException($"Unknown bytes JSON encoding '{bytesEncoding}'.");
                    }

                case ObservationValueKind.Object:
                    writer.WriteStartObject();
                    if (value.Fields is not null)
                    {
                        foreach (var (key, childValue) in value.Fields)
                        {
                            writer.WritePropertyName(key);
                            WriteValue(writer, childValue, bytesEncoding);
                        }
                    }

                    writer.WriteEndObject();
                    return;

                case ObservationValueKind.Array:
                    writer.WriteStartArray();
                    if (!value.Array.IsDefault)
                    {
                        foreach (var item in value.Array)
                            WriteValue(writer, item, bytesEncoding);
                    }

                    writer.WriteEndArray();
                    return;

                default:
                    throw new InvalidOperationException($"Unsupported observation value kind '{value.Kind}'.");
            }
        }
    }

    /// <summary>
    /// Serializes this value into raw JSON text.
    /// </summary>
    /// <param name="bytesEncoding">How bytes values are encoded when encountered.</param>
    /// <returns>The JSON text representation of this value.</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public string GetRawText(ObservationBytesJsonEncoding bytesEncoding = ObservationBytesJsonEncoding.Throw)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteTo(writer, bytesEncoding);
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Compares two values for deep semantic equality.
    /// </summary>
    public static bool DeepEquals(ObservationValue left, ObservationValue right)
    {
        if (IsNumericKind(left.Kind) && IsNumericKind(right.Kind))
        {
            var leftIsDecimal = left.TryGetCanonicalNumericDecimal(out var leftDecimal);
            var rightIsDecimal = right.TryGetCanonicalNumericDecimal(out var rightDecimal);
            if (leftIsDecimal || rightIsDecimal)
                return leftIsDecimal && rightIsDecimal && leftDecimal == rightDecimal;

            return left.Kind == ObservationValueKind.Double
                   && right.Kind == ObservationValueKind.Double
                   && left.Double.Equals(right.Double);
        }

        if (left.Kind == right.Kind)
        {
            return left.Kind switch
            {
                ObservationValueKind.Undefined => true,
                ObservationValueKind.Null => true,
                ObservationValueKind.Bool => left.Bool == right.Bool,
                ObservationValueKind.String => string.Equals(left.String, right.String, StringComparison.Ordinal),
                ObservationValueKind.DateTimeOffset => string.Equals(left.String, right.String, StringComparison.Ordinal),
                ObservationValueKind.DateOnly => string.Equals(left.String, right.String, StringComparison.Ordinal),
                ObservationValueKind.TimeOnly => string.Equals(left.String, right.String, StringComparison.Ordinal),
                ObservationValueKind.TimeSpan => string.Equals(left.String, right.String, StringComparison.Ordinal),
                ObservationValueKind.Bytes => left.Bytes.Span.SequenceEqual(right.Bytes.Span),
                ObservationValueKind.Object => AreObjectsEqual(left.Fields, right.Fields),
                ObservationValueKind.Array => AreArraysEqual(left.Array, right.Array),
                _ => false
            };
        }

        return false;

        static bool AreObjectsEqual(IReadOnlyDictionary<string, ObservationValue>? left, IReadOnlyDictionary<string, ObservationValue>? right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left is null || right is null)
                return false;
            if (left.Count != right.Count)
                return false;

            foreach (var (key, leftValue) in left)
            {
                if (!right.TryGetValue(key, out var rightValue))
                    return false;
                if (!DeepEquals(leftValue, rightValue))
                    return false;
            }

            return true;
        }

        static bool AreArraysEqual(
            ImmutableArray<ObservationValue> left,
            ImmutableArray<ObservationValue> right)
        {
            if (left.IsDefault || right.IsDefault)
                return left.IsDefault == right.IsDefault;
            if (left.Length != right.Length)
                return false;

            for (var i = 0; i < left.Length; i++)
            {
                if (!DeepEquals(left[i], right[i]))
                    return false;
            }

            return true;
        }
    }
    
    static IImmutableDictionary<string, ObservationValue> ReadObject(JsonElement element)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, ObservationValue>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            values[property.Name] = FromJsonElement(property.Value);
        return values.ToImmutable();
    }

    static ImmutableArray<ObservationValue> ReadArray(JsonElement element)
    {
        var values = ImmutableArray.CreateBuilder<ObservationValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
            values.Add(FromJsonElement(item));
        return values.MoveToImmutable();
    }

    static bool TryParseExactJsonDecimal(ReadOnlySpan<char> text, out decimal result)
    {
        var index = 0;
        var negative = false;
        if (index < text.Length && text[index] == '-')
        {
            negative = true;
            index++;
        }

        BigInteger coefficient = BigInteger.Zero;
        var fractionalDigits = 0;
        var inFraction = false;
        while (index < text.Length)
        {
            var character = text[index];
            if (character is >= '0' and <= '9')
            {
                coefficient = coefficient * 10 + (character - '0');
                if (inFraction)
                    fractionalDigits++;
                index++;
                continue;
            }

            if (character == '.' && !inFraction)
            {
                inFraction = true;
                index++;
                continue;
            }

            break;
        }

        var exponent = 0;
        if (index < text.Length && text[index] is 'e' or 'E')
        {
            index++;
            if (!int.TryParse(
                    text[index..],
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out exponent))
            {
                result = default;
                return false;
            }
            index = text.Length;
        }

        if (index != text.Length)
        {
            result = default;
            return false;
        }

        if (coefficient.IsZero)
        {
            result = decimal.Zero;
            return true;
        }

        var scale = (long)fractionalDigits - exponent;
        while (scale > 0 && coefficient % 10 == 0)
        {
            coefficient /= 10;
            scale--;
        }

        if (scale < 0)
        {
            var expansion = -scale;
            if (expansion > 28)
            {
                result = default;
                return false;
            }

            coefficient *= BigInteger.Pow(10, (int)expansion);
            scale = 0;
        }

        var maximumCoefficient = (BigInteger.One << 96) - BigInteger.One;
        if (scale > 28 || coefficient > maximumCoefficient)
        {
            result = default;
            return false;
        }

        const ulong WordMask = uint.MaxValue;
        var low = unchecked((int)(uint)(coefficient & WordMask));
        var mid = unchecked((int)(uint)((coefficient >> 32) & WordMask));
        var high = unchecked((int)(uint)((coefficient >> 64) & WordMask));
        result = new decimal(low, mid, high, negative, (byte)scale);
        return true;
    }

    /// <summary>
    /// Creates a numeric value from <see cref="decimal"/>, preserving integer shape when possible.
    /// </summary>
    /// <param name="value">The base-10 value to preserve.</param>
    /// <returns>
    /// An Int64 value for integral inputs in the Int64 range; otherwise an exact Decimal value.
    /// </returns>
    public static ObservationValue FromDecimal(decimal value)
    {
        if (value == Math.Truncate(value) && value is >= long.MinValue and <= long.MaxValue)
            return FromInt64((long)value);

        return new(ObservationValueKind.Decimal, dec: value);
    }

    /// <summary>
    /// Compares this value with another value using deep equality semantics.
    /// </summary>
    public bool Equals(ObservationValue other) => DeepEquals(this, other);

    /// <summary>
    /// Compares this value with another object using deep equality semantics.
    /// </summary>
    public override bool Equals(object? obj) => obj is ObservationValue other && Equals(other);

    /// <summary>
    /// Computes a hash code compatible with <see cref="DeepEquals"/>.
    /// </summary>
    public override int GetHashCode()
    {
        return Kind switch
        {
            ObservationValueKind.Undefined => UndefinedHash,
            ObservationValueKind.Null => NullHash,
            ObservationValueKind.Int64 => HashNumericDecimal(Int64),
            ObservationValueKind.Double => HashNumericDouble(Double),
            ObservationValueKind.Decimal => HashNumericDecimal(Decimal),
            ObservationValueKind.Bool => Bool ? TrueHash : FalseHash,
            ObservationValueKind.String => CombineHash(StringHashMarker, StringComparer.Ordinal.GetHashCode(String ?? string.Empty)),
            ObservationValueKind.DateTimeOffset => CombineHash(DateTimeOffsetHashMarker, StringComparer.Ordinal.GetHashCode(String ?? string.Empty)),
            ObservationValueKind.DateOnly => CombineHash(DateOnlyHashMarker, StringComparer.Ordinal.GetHashCode(String ?? string.Empty)),
            ObservationValueKind.TimeOnly => CombineHash(TimeOnlyHashMarker, StringComparer.Ordinal.GetHashCode(String ?? string.Empty)),
            ObservationValueKind.TimeSpan => CombineHash(TimeSpanHashMarker, StringComparer.Ordinal.GetHashCode(String ?? string.Empty)),
            ObservationValueKind.Bytes => HashBytes(Bytes.Span),
            ObservationValueKind.Object => HashObject(Fields),
            ObservationValueKind.Array => HashArray(Array),
            _ => 0
        };

        static int HashNumericDouble(double value)
        {
            if (Math.TryGetCanonicalDecimalFromDouble(value, out var exact))
                return HashNumericDecimal(exact);
            return CombineHash(NumericHashMarker, value.GetHashCode());
        }

        static int HashNumericDecimal(decimal value)
            => CombineHash(NumericHashMarker, value.GetHashCode());

        static int HashBytes(ReadOnlySpan<byte> bytes)
        {
            unchecked
            {
                var hash = BytesHashSeed;
                foreach (var b in bytes)
                    hash = CombineHash(hash, b);
                return hash;
            }
        }

        static int HashObject(IReadOnlyDictionary<string, ObservationValue>? values)
        {
            if (values is null || values.Count == 0)
                return ObjectHashSeed;

            unchecked
            {
                var xor = 0;
                var sum = 0;
                var product = 1;
                foreach (var (key, value) in values)
                {
                    var entryHash = CombineHash(StringComparer.Ordinal.GetHashCode(key), value.GetHashCode());
                    xor ^= entryHash;
                    sum += entryHash;
                    product *= (entryHash | 1);
                }

                var hash = ObjectHashSeed;
                hash = CombineHash(hash, values.Count);
                hash = CombineHash(hash, xor);
                hash = CombineHash(hash, sum);
                hash = CombineHash(hash, product);
                return hash;
            }
        }

        static int HashArray(ImmutableArray<ObservationValue> values)
        {
            if (values.IsDefaultOrEmpty)
                return ArrayHashSeed;

            unchecked
            {
                var hash = ArrayHashSeed;
                for (var i = 0; i < values.Length; i++)
                    hash = CombineHash(hash, values[i].GetHashCode());
                return CombineHash(hash, values.Length);
            }
        }

        static int CombineHash(int seed, int value)
        {
            unchecked
            {
                return (seed * 16777619) ^ value;
            }
        }
    }

    /// <summary>
    /// Returns a diagnostic string representation of this value.
    /// </summary>
    public override string ToString()
    {
        return Kind switch
        {
            ObservationValueKind.Null => "null",
            ObservationValueKind.String => String ?? string.Empty,
            ObservationValueKind.DateTimeOffset => String ?? string.Empty,
            ObservationValueKind.DateOnly => String ?? string.Empty,
            ObservationValueKind.TimeOnly => String ?? string.Empty,
            ObservationValueKind.TimeSpan => String ?? string.Empty,
            ObservationValueKind.Bytes => $"bytes:{Bytes.Length.ToString(CultureInfo.InvariantCulture)}",
            _ => GetRawText(ObservationBytesJsonEncoding.Base64String)
        };
    }

    static bool TryParseDateTimeOffset(string? text, bool exact, out DateTimeOffset value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        return exact
            ? DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value)
            : DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    static bool HasExplicitDateTimeOffset(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.AsSpan().Trim();
        var timeBoundary = span.IndexOfAny('T', 't');
        if (timeBoundary < 0)
            timeBoundary = span.IndexOf(':');
        if (timeBoundary < 0)
            return false;

        if (span[^1] is 'Z' or 'z')
            return true;

        return span.LastIndexOf('+') > timeBoundary
            || span.LastIndexOf('-') > timeBoundary;
    }

    static bool TryParseDateOnly(string? text, bool exact, out DateOnly value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        return exact
            ? DateOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            : DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    static bool TryParseTimeOnly(string? text, bool exact, out TimeOnly value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        return exact
            ? TimeOnly.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out value)
            : TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    static bool TryParseTimeSpan(string? text, bool exact, out TimeSpan value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = default;
            return false;
        }

        return exact
            ? TimeSpan.TryParseExact(text, "c", CultureInfo.InvariantCulture, out value)
            : TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out value);
    }

    static bool IsNumericKind(ObservationValueKind kind)
        => kind is ObservationValueKind.Int64
            or ObservationValueKind.Double
            or ObservationValueKind.Decimal;

    /// <summary>
    /// Equality operator based on deep semantic equality.
    /// </summary>
    public static bool operator ==(ObservationValue left, ObservationValue right) => left.Equals(right);

    /// <summary>
    /// Inequality operator based on deep semantic equality.
    /// </summary>
    public static bool operator !=(ObservationValue left, ObservationValue right) => !left.Equals(right);
}

/// <summary>
/// Logical observation-value kind.
/// </summary>
public enum ObservationValueKind
{
    /// <summary>Represents the undefined option.</summary>
    Undefined = 0,
    /// <summary>Represents the null option.</summary>
    Null = 1,
    /// <summary>Represents the int64 option.</summary>
    Int64 = 2,
    /// <summary>Represents the double option.</summary>
    Double = 3,
    /// <summary>Represents the bool option.</summary>
    Bool = 4,
    /// <summary>Represents the string option.</summary>
    String = 5,
    /// <summary>Represents the object option.</summary>
    Object = 6,
    /// <summary>Represents the array option.</summary>
    Array = 7,
    /// <summary>Represents the bytes option.</summary>
    Bytes = 8,
    /// <summary>Represents the date time offset option.</summary>
    DateTimeOffset = 9,
    /// <summary>Represents the date only option.</summary>
    DateOnly = 10,
    /// <summary>Represents the time only option.</summary>
    TimeOnly = 11,
    /// <summary>Represents the time span option.</summary>
    TimeSpan = 12,
    /// <summary>Represents the exact base-10 decimal option.</summary>
    Decimal = 13
}

/// <summary>
/// Policy for encoding binary observation values when writing JSON.
/// </summary>
public enum ObservationBytesJsonEncoding
{
    /// <summary>Represents the throw option.</summary>
    Throw = 0,
    /// <summary>Represents the base64 string option.</summary>
    Base64String = 1
}
