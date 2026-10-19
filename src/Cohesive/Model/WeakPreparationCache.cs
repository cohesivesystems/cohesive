using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// A key-owned preparation slot publishes successes only. Failed factories leave the same slot
// retryable, so an older failure can never remove a newer successful preparation.
internal sealed class WeakPreparationCache<TKey, TValue> where TKey : class
{
    readonly ConditionalWeakTable<TKey, PreparationSlot<TValue>> slots = new();

    internal bool TryGet(TKey key, out TValue value)
    {
        if (slots.TryGetValue(key, out var slot) && slot.TryGet(out value)) return true;
        value = default!;
        return false;
    }

    // Callers may prepare recursively without holding a slot lock, then publish a success.
    internal TValue Publish(TKey key, TValue value) =>
        slots.GetValue(key, static _ => new()).Get(value, static prepared => prepared);

    internal TValue Get(TKey key, Func<TKey, TValue> factory) =>
        slots.GetValue(key, static _ => new()).Get(key, factory);
}
