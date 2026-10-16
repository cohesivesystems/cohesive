using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Cohesive.Model;

// A traversal owns its temporary table. It retains parser references only until disposal and never
// interns tenant/user names globally. Direct mapping bounds memory independently of payload size;
// collisions merely decode a name again. Every returned string is independently owned.
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
    }

    // Keep the abandoned-table branch inline; unique keys should resume the ordinary parser path.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal string Get(JsonProperty property) => misses == MissLimit ? property.Name : GetPrepared(property);

    string GetPrepared(JsonProperty property)
    {
        var raw = JsonMarshal.GetRawUtf8PropertyName(property);
        HashCode hash = new();
        hash.AddBytes(raw);
        var index = hash.ToHashCode() & (Capacity - 1);
        var table = entries ??= ArrayPool<Entry>.Shared.Rent(Capacity);
        ref var entry = ref table[index];
        if (entry.Name is { } name && raw.SequenceEqual(JsonMarshal.GetRawUtf8PropertyName(entry.Property)))
        {
            misses = -1;
            return name;
        }
        name = property.Name; // Preserve the parser's UTF-8 and escaped-surrogate failure policy.
        entry = new() { Property = property, Name = name };
        // A stream of unique keys should not pay hashing for the rest of its traversal.
        if (misses >= 0 && ++misses == MissLimit) Dispose();
        return name;
    }

    public void Dispose()
    {
        if (entries is not { } table) return;
        entries = null;
        ArrayPool<Entry>.Shared.Return(table, clearArray: true);
    }
}
