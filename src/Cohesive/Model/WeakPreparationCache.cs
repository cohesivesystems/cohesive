using System.Runtime.CompilerServices;

namespace Cohesive.Model;

// A key-owned preparation slot publishes successes only. Failed factories leave the same slot
// retryable, so an older failure can never remove a newer successful preparation.
/// <summary>Owner-scoped weak-key preparation cache publishing successful factories once per key.</summary>
/// <typeparam name="TKey">Identity key, weakly retained.</typeparam><typeparam name="TValue">Prepared value.</typeparam>
/// <remarks>Keys use reference identity. Concurrent factories share one slot. Failures are not cached;
/// later calls retry without removing a slot that may already contain a successful value.</remarks>
public sealed class WeakPreparationCache<TKey, TValue> where TKey : class
{
    readonly ConditionalWeakTable<TKey, PreparationSlot<TValue>> slots = new();

    /// <summary>Reads an already prepared value without running a factory.</summary>
    /// <param name="key">Identity key.</param><param name="value">Prepared value when found.</param><returns>Whether preparation succeeded earlier.</returns>
    public bool TryGet(TKey key, out TValue value)
    {
        if (slots.TryGetValue(key, out var slot) && slot.TryGet(out value)) return true;
        value = default!;
        return false;
    }

    /// <summary>Prepares once under the key's shared slot and returns the published value.</summary>
    /// <param name="key">Weak identity key.</param><param name="factory">Factory; an exception leaves the slot retryable.</param><returns>Prepared value.</returns>
    public TValue Get(TKey key, Func<TKey, TValue> factory) =>
        slots.GetValue(key, static _ => new()).Get(key, factory);
}
