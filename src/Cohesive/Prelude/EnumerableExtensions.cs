using System.Diagnostics.Contracts;

namespace Cohesive.Prelude;

/// <summary>
/// Extensions to <see cref="IEnumerable{T}"/>.
/// </summary>
public static class EnumerableExtensions
{
    /// <summary>
    /// Tries to find a duplicate by the given key in the collection.
    /// </summary>
    /// <param name="items">The items to check.</param>
    /// <param name="key">The key to check for duplicates by.</param>
    /// <param name="comparer">The key comparer to use.</param>
    /// <typeparam name="T">The item type.</typeparam>
    /// <typeparam name="TKey">The key type.</typeparam>
    /// <returns>The duplicate key if found and default otherwise.</returns>
    [Pure]
    public static T? TryGetDuplicateByKey<T, TKey>(this IEnumerable<T> items, Func<T, TKey> key, IEqualityComparer<TKey>? comparer = null)
    {
        var set = new HashSet<TKey>(comparer);
        foreach (var item in items)
        {
            if (!set.Add(key(item)))
                return item;
        }
        return default;
    }
    
    /// <summary>
    /// Returns only non-null items from <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> source is treated as an empty sequence. Enumeration is deferred and
    /// the source is streamed once without allocating an intermediate collection.
    /// </remarks>
    [Pure]
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?>? source)
    {
        if (source is null)
            yield break;

        foreach (var item in source)
        {
            if (item is not null)
                yield return item;
        }
    }

    /// <summary>
    /// Returns only populated nullable value types from <paramref name="source"/>.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> source is treated as an empty sequence. Enumeration is deferred and
    /// the source is streamed once without allocating an intermediate collection.
    /// </remarks>
    [Pure]
    public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?>? source) where T : struct
    {
        if (source is null)
            yield break;

        foreach (var item in source)
        {
            if (item.HasValue)
                yield return item.Value;
        }
    }

    /// <summary>
    /// Returns the given list of tuples as a list of key-value pairs.
    /// </summary>
    /// <param name="items"></param>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <returns></returns>
    [Pure]
    public static IEnumerable<KeyValuePair<TKey, TValue>> AsKeyValuePairs<TKey, TValue>(this IEnumerable<(TKey Key, TValue Value)> items) =>
        items.Select(x => KeyValuePair.Create(x.Key, x.Value));
    
    /// <summary>
    /// Returns only non-null and non-empty strings from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source collection to filter.</param>
    /// <returns>A non-null collection of non-null and non-empty strings.</returns>
    [Pure]
    public static IEnumerable<string> WhereNotNullOrEmpty(this IEnumerable<string?>? source)
    {
        if (source is null)
            yield break;

        foreach (var item in source)
        {
            if (!string.IsNullOrEmpty(item))
                yield return item;
        }
    }
    
    /// <summary>
    /// Returns only non-null and non-whitespace strings from <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The source collection to filter.</param>
    /// <returns>A non-null collection of non-null and non-whitespace strings.</returns>
    [Pure]
    public static IEnumerable<string> WhereNotNullOrWhiteSpace(this IEnumerable<string?>? source)
    {
        if (source is null)
            yield break;

        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item))
                yield return item;
        }
    }
}
