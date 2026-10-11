using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// A key-owned preparation slot publishes successes only. Failed factories leave the same slot
// retryable, so an older failure can never remove a newer successful preparation.
internal sealed class WeakPreparationCache<TKey, TValue> where TKey : class
{
    readonly ConditionalWeakTable<TKey, Slot> slots = new();

    internal TValue Get(TKey key, Func<TKey, TValue> factory) =>
        slots.GetValue(key, static _ => new()).Get(key, factory);

    sealed class Slot
    {
        TValue value = default!;
        int prepared;

        internal TValue Get(TKey key, Func<TKey, TValue> factory)
        {
            if (Volatile.Read(ref prepared) != 0) return value;
            lock (this)
            {
                if (prepared == 0)
                {
                    value = factory(key);
                    Volatile.Write(ref prepared, 1);
                }
                return value;
            }
        }
    }
}
