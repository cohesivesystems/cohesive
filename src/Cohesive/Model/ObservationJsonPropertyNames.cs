using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cohesive.Model;

// A traversal owns its temporary table. It retains parser references only until disposal and never
// interns tenant/user text globally. Direct mapping bounds memory independently of payload size;
// collisions merely decode the string again. Every returned string is independently owned.
internal sealed class ObservationJsonPropertyNames : IDisposable
{
    const int Capacity = 256;
    const int MissLimit = 32;
    Entry[]? entries;
    int misses; // -1 means reuse was observed; MissLimit disables an unproductive table.
    struct Entry
    {
        internal JsonProperty Property;
        internal string? Name;
        internal JsonElement StringValue;
        internal bool IsStringValue;
        internal bool IsToken;
    }

    // Keep the abandoned-table branch inline; unique keys should resume the ordinary parser path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal string Get(JsonProperty property) => misses == MissLimit ? property.Name : GetPrepared(property);

    internal string GetString(JsonElement value) => misses == MissLimit ? value.GetString()!
        : GetPrepared(default, value, isStringValue: true);

    string GetPrepared(JsonProperty property) => GetPrepared(property, default, isStringValue: false);

    string GetPrepared(JsonProperty property, JsonElement value, bool isStringValue)
    {
        var raw = isStringValue ? JsonMarshal.GetRawUtf8Value(value)[1..^1] : JsonMarshal.GetRawUtf8PropertyName(property);
        var index = Bucket(raw);
        var table = entries ??= ArrayPool<Entry>.Shared.Rent(Capacity);
        ref var entry = ref table[index];
        if (!entry.IsToken && entry.Name is { } name && raw.SequenceEqual(entry.IsStringValue
            ? JsonMarshal.GetRawUtf8Value(entry.StringValue)[1..^1]
            : JsonMarshal.GetRawUtf8PropertyName(entry.Property)))
        {
            misses = -1;
            return name;
        }
        name = isStringValue ? value.GetString()! : property.Name; // Preserve the parser's UTF-8 and escaped-surrogate failure policy.
        entry = new() { Property = property, Name = name, StringValue = value, IsStringValue = isStringValue };
        // A stream of unique strings should not pay hashing for the rest of its traversal.
        RecordMiss();
        return name;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    string GetToken(ref Utf8JsonReader reader) => misses == MissLimit ? reader.GetString()! : GetPreparedToken(ref reader);

    string GetPreparedToken(ref Utf8JsonReader reader)
    {
        var table = entries ??= ArrayPool<Entry>.Shared.Rent(Capacity);
        ref var entry = ref table[Bucket(reader.ValueSpan)];
        if (entry.IsToken && entry.Name is { } name && reader.ValueTextEquals(name))
        {
            misses = -1;
            return name;
        }
        name = reader.GetString()!;
        entry = new() { Name = name, IsToken = true };
        RecordMiss();
        return name;
    }

    internal static int Bucket(ReadOnlySpan<byte> raw)
    {
        HashCode hash = new();
        hash.AddBytes(raw);
        return hash.ToHashCode() & (Capacity - 1);
    }

    void RecordMiss()
    {
        if (misses >= 0 && ++misses == MissLimit) Dispose();
    }

    // Stack-owned token traversal lifetime. Small values avoid even allocating a cache owner;
    // long or segmented tokens retain the parser's ordinary decoding path. Token entries retain
    // only owned strings, never the caller's UTF-8 memory or a reader reference.
    internal struct TokenScope : IDisposable
    {
        const int ActivationCount = 64;
        const int MaximumTokenBytes = 256;
        ObservationJsonPropertyNames? names;
        int eligibleTokens;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal string Get(ref Utf8JsonReader reader)
        {
            if (reader.HasValueSequence || reader.ValueSpan.Length > MaximumTokenBytes)
                return reader.GetString()!;
            if (names is null && ++eligibleTokens <= ActivationCount)
                return reader.GetString()!;
            return (names ??= new()).GetToken(ref reader);
        }

        public void Dispose()
        {
            names?.Dispose();
            names = null;
        }
    }

    public void Dispose()
    {
        if (entries is not { } table) return;
        entries = null;
        ArrayPool<Entry>.Shared.Return(table, clearArray: true);
    }
}
