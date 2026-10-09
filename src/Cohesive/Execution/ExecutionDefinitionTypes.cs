using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Text;
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
        // Discover child-first references without retaining an intermediate body document.
        using (var writer = new Utf8JsonWriter(Stream.Null))
            JsonSerializer.Serialize(writer, definition, options);
        if (pool.Entries.Count != 0)
            pool.Order();
        return JsonSerializer.SerializeToElement(definition, codec.RootInfo(typeof(T), definition!.GetType()));
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
        return (T?)definition.Deserialize(codec.RootInfo(typeof(T), definition))
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
        readonly Dictionary<(Type Root, Type Concrete), JsonTypeInfo> roots = new();
        readonly Dictionary<Type, RootDispatch> dispatches = new();

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
                        else if (Tags.TryGetValue(info.Type, out var tag))
                        {
                            AddMetadataProperty(info, typeof(string), "$type", _ => tag);
                        }
                    } }
                }
            };
            Options.Converters.Insert(0, Converter);
            Options.MakeReadOnly();
        }

        RootDispatch Dispatch(Type root)
        {
            if (!dispatches.TryGetValue(root, out var dispatch))
            {
                dispatch = new(root, Options.GetTypeInfo(root).PolymorphismOptions);
                dispatches.Add(root, dispatch);
            }
            return dispatch;
        }

        internal JsonTypeInfo RootInfo(Type root, JsonElement definition) =>
            RootInfo(root, Dispatch(root).Resolve(definition));

        internal JsonTypeInfo RootInfo(Type root, Type concrete)
        {
            var dispatch = Dispatch(root);
            if (dispatch.DiscriminatorName is null)
                concrete = root; // Preserve the declared contract for nonpolymorphic base types.
            if (roots.TryGetValue((root, concrete), out var cached))
                return cached;
            var registered = dispatch.Tags.TryGetValue(concrete, out var tag);
            if (root != concrete && !registered)
                throw new JsonException($"Unsupported execution definition type '{concrete.FullName}'.");
            var info = Options.TypeInfoResolver!.GetTypeInfo(concrete, Options)
                ?? throw new JsonException($"No serializer metadata for '{concrete.FullName}'.");
            // Built-in polymorphic decoding reserves '$' properties. Dispatch uses that same
            // declared registry and concrete metadata, with metadata fields projected directly.
            info.PolymorphismOptions = null;
            if (tag is not null)
                AddMetadataProperty(info, tag.GetType(), dispatch.DiscriminatorName!, _ => tag);
            if (info.Kind != JsonTypeInfoKind.Object)
                throw new JsonException("An execution definition must be an object.");
            AddMetadataProperty(info, typeof(TypePool), TableProperty, _ => Converter.Pool, new TypeTableConverter());
            info.MakeReadOnly();
            roots.Add((root, concrete), info);
            return info;
        }

        static void AddMetadataProperty(JsonTypeInfo info, Type type, string name,
            Func<object, object?> get, JsonConverter? converter = null)
        {
            foreach (var existing in info.Properties)
                if (existing.Name == name)
                    throw new JsonException($"'{name}' is reserved for execution metadata.");
            var property = info.CreateJsonPropertyInfo(type, name);
            property.Get = get;
            property.Set = static (_, _) => { };
            property.CustomConverter = converter;
            info.Properties.Add(property);
        }

        // Only CLR dispatch metadata is retained. Successful resolution compares existing UTF-8
        // tokens; it neither decodes a tag string nor boxes integer tags nor creates a predicate.
        sealed class RootDispatch
        {
            readonly Type root;
            internal string? DiscriminatorName { get; }
            internal Dictionary<Type, object?> Tags { get; } = new();

            internal RootDispatch(Type root, JsonPolymorphismOptions? polymorphism)
            {
                this.root = root;
                DiscriminatorName = polymorphism?.TypeDiscriminatorPropertyName;
                if (polymorphism is not null)
                    foreach (var derived in polymorphism.DerivedTypes)
                        Tags.Add(derived.DerivedType, derived.TypeDiscriminator);
            }

            internal Type Resolve(JsonElement definition)
            {
                if (DiscriminatorName is null)
                    return root;
                if (!definition.TryGetProperty(DiscriminatorName, out var discriminator))
                {
                    if (root.IsAbstract)
                        throw new JsonException($"Execution definition requires '{DiscriminatorName}'.");
                    return root;
                }
                foreach (var entry in Tags)
                    if (entry.Value is string text && discriminator.ValueKind == JsonValueKind.String && discriminator.ValueEquals(text)
                        || entry.Value is int integer && discriminator.ValueKind == JsonValueKind.Number
                            && discriminator.TryGetInt32(out var number) && number == integer)
                        return entry.Key;
                throw new JsonException($"Unknown execution definition discriminator '{discriminator}'.");
            }
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

    // The table is validated by TypePool before root projection. Consume its tokens directly;
    // serializing exposes the existing entries without a second body/tree representation.
    sealed class TypeTableConverter : JsonConverter<TypePool>
    {
        public override TypePool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            reader.Skip();
            return null;
        }

        public override void Write(Utf8JsonWriter writer, TypePool value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var entry in value.Entries)
                entry.WriteTo(writer);
            writer.WriteEndArray();
        }
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

        public override void Write(Utf8JsonWriter writer, TypeRef value, JsonSerializerOptions options)
        {
            var index = Pool!.Reference(value, options);
            writer.WriteNumberValue(index);
            Pool.RecordReference(writer, index);
        }
    }

    // Hashing selects a bucket only. Exact canonical byte equality establishes identity,
    // including annotations and normalized numbers; borrowed candidates are never retained.
    sealed class CanonicalBytesComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        internal static readonly CanonicalBytesComparer Instance = new();
        public bool Equals(byte[]? left, byte[]? right) =>
            ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);
        public bool Equals(ReadOnlySpan<byte> left, byte[] right) => left.SequenceEqual(right);
        public int GetHashCode(byte[] value) => GetHashCode(value.AsSpan());
        public int GetHashCode(ReadOnlySpan<byte> value)
        {
            HashCode hash = new();
            hash.AddBytes(value);
            return hash.ToHashCode();
        }
        public byte[] Create(ReadOnlySpan<byte> value) => value.ToArray();
    }

    sealed class TypePool
    {
        readonly Dictionary<TypeRef, int> byIdentity = new(ReferenceEqualityComparer.Instance);
        readonly Dictionary<byte[], int> byContent = new(CanonicalBytesComparer.Instance);
        readonly Dictionary<ScalarTypeRef, int> scalars = new();
        readonly List<string?> keys = [];
        readonly List<byte[]?> payloads = [];
        readonly List<List<ReferenceToken>?> references = [];
        readonly Stack<List<ReferenceToken>?> capturing = new();
        readonly record struct ReferenceToken(int Offset, int Length, int Index);
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

        internal void Order()
        {
            orderedIndices = new int[Entries.Count];
            var ordered = new List<JsonElement>(Entries.Count);
            foreach (var level in Enumerable.Range(0, Entries.Count).GroupBy(index => depths[index]).OrderBy(group => group.Key))
            {
                var entries = level.Select(index =>
                {
                    // Leaf bytes are final. Replay the original serializer payload for parents,
                    // changing only number tokens emitted by the TypeRef converter.
                    var entry = depths[index] == 0 ? Entries[index] : Remap(index);
                    return (Index: index, Entry: entry, Key: depths[index] == 0 ? keys[index] : entry.GetRawText());
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
            // Scalar value equality covers its complete sealed serializer contract (kind/format).
            // Keep this memo document-local; nested types still use canonical content and cycle checks.
            if (value is ScalarTypeRef scalar && scalars.TryGetValue(scalar, out index))
            {
                byIdentity.Add(value, index);
                return index;
            }
            byIdentity.Add(value, -1);
            if (!Tags.ContainsKey(value.GetType()))
                throw new JsonException($"Unsupported portable type '{value.GetType().FullName}'.");
            // Child references are interned first. The canonical entry contains only local child indices,
            // so structural deduplication never repeatedly serializes a complete nested type tree.
            childDepths.Push(0);
            capturing.Push(null);
            var payload = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), options);
            var tokens = capturing.Pop();
            var depth = childDepths.Pop();
            using var parsed = JsonDocument.Parse(payload);
            var fields = parsed.RootElement;
            // Retain original parent bytes only when unique. Canonical bytes deduplicate content;
            // converter-recorded token locations permit final numbering without another CLR walk.
            using PooledByteBufferWriter canonical = new();
            ExecutionDefinitionFingerprinter.WriteCanonicalDefinition(canonical, fields);
            var contents = byContent.GetAlternateLookup<ReadOnlySpan<byte>>();
            if (!contents.TryGetValue(canonical.WrittenSpan, out index))
            {
                index = Entries.Count;
                // Only unique leaf entries need an owned canonical document and ordering text.
                // Parents are normalized after reference renumbering; provisional bytes suffice here.
                var reader = new Utf8JsonReader(canonical.WrittenSpan);
                Entries.Add(depth == 0 ? JsonElement.ParseValue(ref reader) : default);
                keys.Add(depth == 0 ? Encoding.UTF8.GetString(canonical.WrittenSpan) : null);
                payloads.Add(depth == 0 ? null : payload);
                references.Add(tokens);
                depths.Add(depth);
                contents.TryAdd(canonical.WrittenSpan, index);
            }
            byIdentity[value] = index;
            if (value is ScalarTypeRef scalarValue)
                scalars.TryAdd(scalarValue, index);
            return index;
        }

        internal void RecordReference(Utf8JsonWriter writer, int index)
        {
            if (capturing.Count == 0)
                return;
            // The converter has just written a nonnegative Int32. Its last bytes are the digits;
            // committed + pending remains valid across writer buffer flushes and excludes delimiters.
            var length = 1;
            for (var remaining = index; remaining >= 10; remaining /= 10) length++;
            var tokens = capturing.Pop() ?? [];
            tokens.Add(new(checked((int)(writer.BytesCommitted + writer.BytesPending)) - length, length, index));
            capturing.Push(tokens);
        }

        JsonElement Remap(int index)
        {
            var payload = payloads[index]!;
            using PooledByteBufferWriter buffer = new();
            var previous = 0;
            // Nonnegative Int32 references need at most ten ASCII digits. Annotation values never
            // enter this list, even when their property names resemble the type wire contract.
            Span<byte> number = stackalloc byte[10];
            foreach (var token in references[index]!)
            {
                buffer.Write(payload.AsSpan(previous, token.Offset - previous));
                Utf8Formatter.TryFormat(orderedIndices![token.Index], number, out var length);
                buffer.Write(number[..length]);
                previous = token.Offset + token.Length;
            }
            buffer.Write(payload.AsSpan(previous));
            using var parsed = JsonDocument.Parse(buffer.WrittenMemory);
            var entry = ExecutionDefinitionFingerprinter.NormalizeDefinition(parsed.RootElement);
            payloads[index] = null;
            references[index] = null;
            return entry;
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
                var value = entry.Deserialize(type, options) as TypeRef
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
