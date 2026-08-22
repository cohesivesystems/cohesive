using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

/// <summary>Shared allocation-aware operations for canonical immutable document collections.</summary>
public static class CanonicalDocumentCollections
{
    /// <summary>Retains canonical storage or returns an ordinally sorted immutable copy.</summary>
    /// <typeparam name="T">Collection item type.</typeparam>
    /// <param name="values">Initialized immutable values to normalize.</param>
    /// <param name="comparison">Canonical ordering comparison.</param>
    /// <returns>
    /// <paramref name="values"/> when already ordered; otherwise a sorted immutable copy.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> is the default immutable array.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="comparison"/> is <see langword="null"/>.</exception>
    public static ImmutableArray<T> SortIfNeeded<T>(
        ImmutableArray<T> values,
        Comparison<T> comparison)
    {
        if (values.IsDefault)
            throw new ArgumentException("Canonical document values must be initialized.", nameof(values));
        ArgumentNullException.ThrowIfNull(comparison);

        for (var index = 1; index < values.Length; index++)
        {
            if (comparison(values[index - 1], values[index]) <= 0)
                continue;

            var sorted = ImmutableArray.CreateBuilder<T>(values.Length);
            sorted.AddRange(values);
            sorted.Sort(comparison);
            return sorted.MoveToImmutable();
        }

        return values;
    }

    /// <summary>Finds a key in an immutable collection ordered by the supplied comparison.</summary>
    /// <typeparam name="T">Collection item type.</typeparam>
    /// <typeparam name="TKey">Lookup-key type.</typeparam>
    /// <param name="values">Initialized immutable values in canonical order.</param>
    /// <param name="key">Key to find.</param>
    /// <param name="comparison">
    /// Comparison of a collection item with <paramref name="key"/> that defines the collection order.
    /// </param>
    /// <returns>The zero-based index of the matching item, or <c>-1</c> when the key is absent.</returns>
    /// <exception cref="ArgumentException"><paramref name="values"/> is the default immutable array.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="comparison"/> is <see langword="null"/>.</exception>
    public static int BinarySearchIndex<T, TKey>(
        ImmutableArray<T> values,
        TKey key,
        Func<T, TKey, int> comparison)
    {
        if (values.IsDefault)
            throw new ArgumentException("Canonical document values must be initialized.", nameof(values));
        ArgumentNullException.ThrowIfNull(comparison);

        var low = 0;
        var high = values.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var order = comparison(values[middle], key);
            if (order == 0)
                return middle;
            if (order < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return -1;
    }
}
