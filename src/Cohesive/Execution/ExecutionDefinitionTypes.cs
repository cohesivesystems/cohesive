using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Cohesive.Model;
using Cohesive.Model.Serialization;

namespace Cohesive.Execution;

// A document-local wire table, not another semantic type system. Entries use the existing TypeRef
// contract; integer uses resolve to shared immutable TypeRef instances during strict projection.
internal static class ExecutionDefinitionTypes
{
    static readonly ConcurrentQueue<Codec> Codecs = new();
    static int retainedCodecs;
    const int MaxRetainedCodecs = 8;

    const string TableProperty = "$types";
    static readonly IReadOnlyDictionary<Type, string> Tags = ExecutionDefinitionJsonSerializer.GetReadOnlyOptions()
        .GetTypeInfo(typeof(TypeRef)).PolymorphismOptions!.DerivedTypes
        .ToDictionary(static entry => entry.DerivedType, static entry => (string)entry.TypeDiscriminator!);
    static readonly IReadOnlyDictionary<string, Type> Types = Tags.ToDictionary(static entry => entry.Value, static entry => entry.Key, StringComparer.Ordinal);

    internal static JsonElement Serialize<T>(T definition)
    {
        var pool = new TypePool();
        using var codec = Rent(pool);
        var options = codec.Options;
        var body = JsonSerializer.SerializeToElement(definition, options);
        if (pool.Entries.Count != 0)
        {
            pool.Order(options);
            body = JsonSerializer.SerializeToElement(definition, options);
        }
        if (body.ValueKind != JsonValueKind.Object)
            throw new JsonException("An execution definition must be an object.");
        return WriteObject(body, static name => name != TableProperty, writer =>
        {
            if (body.TryGetProperty(TableProperty, out _))
                throw new JsonException($"'{TableProperty}' is reserved for document-local type definitions.");
            writer.WritePropertyName(TableProperty);
            writer.WriteStartArray();
            foreach (var entry in pool.Entries)
                entry.WriteTo(writer);
            writer.WriteEndArray();
        });
    }

    internal static T Deserialize<T>(JsonElement definition)
    {
        if (!definition.TryGetProperty(TableProperty, out var table) || table.ValueKind != JsonValueKind.Array)
            throw new JsonException($"An execution definition requires its '{TableProperty}' array.");
        var pool = new TypePool(table);
        using var codec = Rent(pool);
        var options = codec.Options;
        // Validate every entry, including unreferenced entries. Cycles and invalid indices cannot hide
        // behind an unused declaration; the round-trip gate rejects unused or noncanonical tables.
        for (var index = 0; index < table.GetArrayLength(); index++)
            pool.Resolve(index, options);
        var body = WriteObject(definition, static name => name != TableProperty);
        return body.Deserialize<T>(options)
            ?? throw new JsonException($"Execution definition projected to null for '{typeof(T).FullName}'.");
    }

    static Codec Rent(TypePool pool)
    {
        if (Codecs.TryDequeue(out var codec))
            Interlocked.Decrement(ref retainedCodecs);
        else
            codec = new Codec();
        codec.Converter.Pool = pool;
        return codec;
    }

    // Exclusive leases reuse frozen CLR serializer metadata without retaining documents or sharing
    // invocation state. Excess concurrent codecs are discarded; the idle cache is strictly bounded.
    sealed class Codec : IDisposable
    {
        internal TypeReferenceConverter Converter { get; } = new();
        internal JsonSerializerOptions Options { get; }

        internal Codec()
        {
            Options = new JsonSerializerOptions(ExecutionDefinitionJsonSerializer.GetReadOnlyOptions())
            {
                TypeInfoResolver = new DefaultJsonTypeInfoResolver
                {
                    Modifiers = { static info =>
                    {
                        if (info.Type == typeof(TypeRef))
                            info.PolymorphismOptions = null;
                    } }
                }
            };
            Options.Converters.Insert(0, Converter);
            Options.MakeReadOnly();
        }

        public void Dispose()
        {
            Converter.Pool = null;
            if (Interlocked.Increment(ref retainedCodecs) <= MaxRetainedCodecs)
                Codecs.Enqueue(this);
            else
                Interlocked.Decrement(ref retainedCodecs);
        }
    }

    static JsonElement WriteObject(JsonElement source, Func<string, bool> include, Action<Utf8JsonWriter>? append = null)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in source.EnumerateObject())
            {
                if (include(property.Name))
                    property.WriteTo(writer);
            }
            append?.Invoke(writer);
            writer.WriteEndObject();
        }
        using var parsed = JsonDocument.Parse(buffer.WrittenMemory);
        return parsed.RootElement.Clone();
    }

    sealed class TypeReferenceConverter : JsonConverter<TypeRef>
    {
        internal TypePool? Pool { get; set; }
        public override TypeRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var index))
                throw new JsonException("A document-local type reference must be an integer index.");
            return Pool!.Resolve(index, options);
        }

        public override void Write(Utf8JsonWriter writer, TypeRef value, JsonSerializerOptions options) =>
            writer.WriteNumberValue(Pool!.Reference(value, options));
    }

    sealed class TypePool
    {
        readonly Dictionary<TypeRef, int> byIdentity = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<string, int> byContent = new(StringComparer.Ordinal);
        readonly List<TypeRef> values = [];
        readonly List<int> depths = [];
        readonly Stack<int> childDepths = new();
        int[]? orderedIndices;
        readonly JsonElement table;
        readonly TypeRef?[]? decoded;
        readonly bool[]? decoding;
        internal List<JsonElement> Entries { get; } = [];

        internal TypePool() { }
        internal TypePool(JsonElement table)
        {
            this.table = table;
            decoded = new TypeRef?[table.GetArrayLength()];
            decoding = new bool[decoded.Length];
        }

        internal int Reference(TypeRef value, JsonSerializerOptions options)
        {
            var index = Intern(value, options);
            if (childDepths.Count != 0)
            {
                var depth = childDepths.Pop();
                childDepths.Push(Math.Max(depth, depths[index] + 1));
            }
            return orderedIndices is null ? index : orderedIndices[index];
        }

        internal void Order(JsonSerializerOptions options)
        {
            orderedIndices = new int[Entries.Count];
            var ordered = new List<JsonElement>(Entries.Count);
            foreach (var level in Enumerable.Range(0, Entries.Count).GroupBy(index => depths[index]).OrderBy(group => group.Key))
            {
                var entries = level.Select(index =>
                {
                    var fields = JsonSerializer.SerializeToElement(values[index], values[index].GetType(), options);
                    var entry = ExecutionDefinitionFingerprinter.NormalizeDefinition(
                        WriteObject(fields, static _ => true, writer => writer.WriteString("$type", Tags[values[index].GetType()])));
                    return (Index: index, Entry: entry, Key: entry.GetRawText());
                }).OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
                foreach (var entry in entries)
                {
                    orderedIndices[entry.Index] = ordered.Count;
                    ordered.Add(entry.Entry);
                }
            }
            Entries.Clear();
            Entries.AddRange(ordered);
        }

        int Intern(TypeRef value, JsonSerializerOptions options)
        {
            if (byIdentity.TryGetValue(value, out var index))
                return index >= 0 ? index : throw new JsonException("A portable structural type cannot contain a cycle.");
            byIdentity.Add(value, -1);
            if (!Tags.TryGetValue(value.GetType(), out var tag))
                throw new JsonException($"Unsupported portable type '{value.GetType().FullName}'.");
            // Child references are interned first. The canonical entry contains only local child indices,
            // so structural deduplication never repeatedly serializes a complete nested type tree.
            childDepths.Push(0);
            var fields = JsonSerializer.SerializeToElement(value, value.GetType(), options);
            var depth = childDepths.Pop();
            var entry = WriteObject(fields, static _ => true, writer => writer.WriteString("$type", tag));
            entry = ExecutionDefinitionFingerprinter.NormalizeDefinition(entry);
            var key = entry.GetRawText();
            if (!byContent.TryGetValue(key, out index))
            {
                index = Entries.Count;
                Entries.Add(entry);
                values.Add(value);
                depths.Add(depth);
                byContent.Add(key, index);
            }
            byIdentity[value] = index;
            return index;
        }

        internal TypeRef Resolve(int index, JsonSerializerOptions options)
        {
            if (decoded is null || (uint)index >= (uint)decoded.Length)
                throw new JsonException($"Document-local type index '{index}' is out of range.");
            if (decoded[index] is { } cached)
                return cached;
            if (decoding![index])
                throw new JsonException($"Document-local type table contains a cycle at index '{index}'.");
            decoding[index] = true;
            try
            {
                var entry = table[index];
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("$type", out var discriminator)
                    || discriminator.ValueKind != JsonValueKind.String || !Types.TryGetValue(discriminator.GetString()!, out var type))
                    throw new JsonException($"Document-local type entry '{index}' has an unknown type discriminator.");
                var fields = WriteObject(entry, static name => name != "$type");
                var value = fields.Deserialize(type, options) as TypeRef
                    ?? throw new JsonException($"Document-local type entry '{index}' projected to null.");
                decoded[index] = value;
                return value;
            }
            finally
            {
                decoding[index] = false;
            }
        }
    }
}
