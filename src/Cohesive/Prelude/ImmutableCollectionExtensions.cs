using System.Collections.Immutable;

namespace Cohesive.Prelude;

/// <summary>
/// Extensions to <see cref="System.Collections.Immutable"/>.
/// </summary>
public static class ImmutableCollectionExtensions
{
    extension(ImmutableDictionary)
    {
        /// <summary>
        /// Creates a new immutable collection prefilled with the specified items. 
        /// </summary>
        /// <param name="keyComparer">The key comparer to use.</param>
        /// <param name="pairs">The key/value pairs to add.</param>
        /// <returns>A new immutable dictionary that contains the specified items and uses the specified comparer.</returns>
        /// <exception cref="ArgumentException">One of the given keys already exists in the dictionary but has a different value.</exception>
        public static ImmutableDictionary<TKey, TValue> CreateRange<TKey, TValue>(IEqualityComparer<TKey>? keyComparer, IEnumerable<(TKey, TValue)> pairs) where TKey : notnull =>
            ImmutableDictionary<TKey, TValue>.Empty.WithComparers(keyComparer).AddRange(pairs);
        
        /// <summary>
        /// Creates a new immutable collection prefilled with the specified items.
        /// </summary>
        /// <param name="pairs">The key/value pairs to add.</param>
        /// <returns>A new immutable dictionary that contains the specified items and uses the specified comparer.</returns>
        /// <exception cref="ArgumentException">One of the given keys already exists in the dictionary but has a different value.</exception>
        public static ImmutableDictionary<TKey, TValue> CreateRange<TKey, TValue>(IEnumerable<(TKey, TValue)> pairs) where TKey : notnull => 
            ImmutableDictionary<TKey, TValue>.Empty.AddRange(pairs);
    }

    extension<TKey, TValue>(ImmutableDictionary<TKey, TValue> dictionary) where TKey : notnull
    {
        /// <summary>
        /// Adds the specified key/value pairs to the immutable dictionary.
        /// </summary>
        /// <param name="pairs">The key/value pairs to add.</param>
        /// <returns>A new immutable dictionary that contains the specified items and uses the specified comparer.</returns>
        /// <exception cref="ArgumentException">One of the given keys already exists in the dictionary but has a different value.</exception>
        public ImmutableDictionary<TKey, TValue> AddRange(IEnumerable<(TKey, TValue)> pairs) => 
            dictionary.AddRange(pairs: pairs.AsKeyValuePairs());
    }
}