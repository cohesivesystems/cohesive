using System.Collections.Immutable;

namespace Cohesive.Adapters.Sql;

// Shared first-use ordering and one-slot-per-runtime-binding rule. Adapters own slot payloads and markers.
internal sealed class SqlParameterSlots<TSlot>
{
    readonly ImmutableArray<TSlot>.Builder slots = ImmutableArray.CreateBuilder<TSlot>();
    readonly Dictionary<string, int> runtimePositions = new(StringComparer.Ordinal);
    internal ImmutableArray<TSlot> Snapshot() => slots.ToImmutable();
    internal int AddConstant(Func<int, TSlot> createSlot)
    {
        var position = slots.Count;
        slots.Add(createSlot(position));
        return position;
    }
    internal int GetOrAddRuntime(string binding, Func<int, TSlot> createSlot)
    {
        if (runtimePositions.TryGetValue(binding, out var existing)) return existing;
        var position = slots.Count;
        var slot = createSlot(position);
        runtimePositions.Add(binding, position);
        slots.Add(slot);
        return position;
    }
}
