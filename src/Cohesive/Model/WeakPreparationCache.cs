using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// A key-owned preparation slot publishes successes only. Failed factories leave the same slot
// retryable, so an older failure can never remove a newer successful preparation.
internal sealed class WeakPreparationCache<TKey, TValue> where TKey : class
{
    readonly ConditionalWeakTable<TKey, PreparationSlot<TValue>> slots = new();

    internal TValue Get(TKey key, Func<TKey, TValue> factory) =>
        slots.GetValue(key, static _ => new()).Get(key, factory);
}
