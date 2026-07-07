namespace Cohesive.Prelude;

/// <summary>
/// Extension methods for <see cref="IComparable{T}"/> types.
/// </summary>
public static class ComparableExtensions
{
    /// <summary>
    /// Extension methods for selector-based comparisons over sequences.
    /// </summary>
    /// <param name="source">The source sequence.</param>
    /// <typeparam name="TSource">The source item type.</typeparam>
    extension<TSource>(IEnumerable<TSource> source)
    {
        /// <summary>
        /// Returns the item whose selected value is minimal, or <c>default</c> if the sequence is empty.
        /// </summary>
        /// <param name="selector">Selects the comparable value used for ordering.</param>
        /// <typeparam name="TValue">The type of the compared values.</typeparam>
        public TSource? MinByOrDefault<TValue>(Func<TSource, TValue> selector) where TValue : IComparable<TValue> 
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            using var enumerator = source.GetEnumerator();
            if (!enumerator.MoveNext())
                return default;

            var result = enumerator.Current;
            var selected = selector(result);

            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;
                var currentSelected = selector(current);
                if (currentSelected.CompareTo(selected) >= 0)
                    continue;

                result = current;
                selected = currentSelected;
            }

            return result;
        }

        /// <summary>
        /// Returns the item whose selected value is maximal, or <c>default</c> if the sequence is empty.
        /// </summary>
        /// <param name="selector">Selects the comparable value used for ordering.</param>
        /// <typeparam name="TValue">The type of the compared values.</typeparam>
        public TSource? MaxByOrDefault<TValue>(Func<TSource, TValue> selector) where TValue : IComparable<TValue>
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(selector);

            using var enumerator = source.GetEnumerator();
            if (!enumerator.MoveNext())
                return default;

            var result = enumerator.Current;
            var selected = selector(result);

            while (enumerator.MoveNext())
            {
                var current = enumerator.Current;
                var currentSelected = selector(current);
                if (currentSelected.CompareTo(selected) <= 0)
                    continue;

                result = current;
                selected = currentSelected;
            }

            return result;
        }
    }
    
    /// <summary>
    /// Extension methods for <see cref="IComparable{T}"/> types.
    /// </summary>
    /// <param name="value">The value being compared.</param>
    /// <typeparam name="T"></typeparam>
    extension<T>(T? value) where T : IComparable<T>
    {
        /// <summary>
        /// Returns the minimum of the two values.
        /// </summary>
        /// <param name="other">The second value being compared.</param>
        /// <returns>The minimum non-null value, or null if all values are null.</returns>
        public T? Min(T? other) => (value, other) switch
        {
            (null, null) => default,
            (null, _) => other,
            (_, null) => value,
            (_, _) => value.CompareTo(other) < 0 ? value : other
        };

        /// <summary>
        /// Returns the minimum of the three values.
        /// </summary>
        /// <param name="other1">The second value being compared.</param>
        /// <param name="other2">The third value being compared.</param>
        /// <returns>The minimum non-null value, or null if all values are null.</returns>
        public T? Min(T? other1, T? other2) => value.Min(other1.Min(other2));
        
        /// <summary>
        /// Returns the minimum of the four values.
        /// </summary>
        /// <param name="other1"></param>
        /// <param name="other2"></param>
        /// <param name="other3"></param>
        /// <returns>The minimum non-null value, or null if all values are null.</returns>
        public T? Min(T? other1, T? other2, T? other3) => value.Min(other1.Min(other2.Min(other3)));
        
        /// <summary>
        /// Returns the maximum of the three values.
        /// </summary>
        /// <param name="other"></param>
        /// <param name="other2"></param>
        /// <returns>The maximum non-null value, or null if all values are null.</returns>
        public T? Max(T? other, T? other2) => value.Max(other.Max(other2));
        
        /// <summary>
        /// Returns the maximum of the four values.
        /// </summary>
        /// <param name="other"></param>
        /// <param name="other2"></param>
        /// <param name="other3"></param>
        /// <returns>The maximum non-null value, or null if all values are null.</returns>
        public T? Max(T? other, T? other2, T? other3) => value.Max(other.Max(other2.Max(other3)));

        /// <summary>
        /// Returns the maximum of the two values.
        /// </summary>
        /// <param name="other"></param>
        /// <returns>The maximum non-null value, or null if all values are null.</returns>
        public T? Max(T? other) => (value, other) switch
        {
            (null, null) => default,
            (null, _) => other,
            (_, null) => value,
            (_, _) => value.CompareTo(other) > 0 ? value : other
        };
    }
}
