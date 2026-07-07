namespace Cohesive.Prelude;

/// <summary>
/// Extension methods for dictionaries.
/// </summary>
public static class DictionaryExtensions
{
    /// <param name="dictionary"></param>
    extension(IReadOnlyDictionary<string, object?> dictionary)
    {
        /// <summary>
        /// Gets the value associated with the specified key and casts it to the specified type.
        /// </summary>
        /// <param name="key">The key to locate.</param>
        /// <typeparam name="T">The type to cast the value to.</typeparam>
        /// <returns>The value of the key if found and is of the given type; default otherwise.</returns>
        /// <exception cref="ArgumentNullException">key is null</exception>
        public T? TryGetValue<T>(string key) =>
            dictionary.TryGetValue(key, out var value) ? value is T t ? t : default : default;

        /// <summary>
        /// Gets the value associated with the specified key and casts it to the specified type.
        /// </summary>
        /// <param name="key">The key to locate.</param>
        /// <param name="value">When this method returns, the value associated with the specified key, if the key is found; otherwise, the default value for the type of the value parameter. This parameter is passed uninitialized.</param>
        /// <typeparam name="T">The type to cast the value to.</typeparam>
        /// <returns><c>true</c> if the object that implements the dictionary interface contains an element that has the specified key; otherwise, <c>false</c>.</returns>
        /// <exception cref="ArgumentNullException">key is null</exception>
        /// <remarks>If the key is found but the value is null or not of the given type, the value is set to null and the method returns true.</remarks>
        public bool TryGetValue<T>(string key, out T? value)
        {
            if (!dictionary.TryGetValue(key, out var raw))
            {
                value = default;
                return false;
            }

            if (raw is null)
            {
                value = default;
                return true;
            }

            if (raw is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        /// <summary>
        /// Tries to get the first value associated with the specified keys.
        /// </summary>
        /// <param name="keys">The keys to search for, in the given order.</param>
        /// <param name="value">The value of the first key found that is of the given type; default otherwise.</param>
        /// <typeparam name="T">The type of the value to retrieve.</typeparam>
        /// <returns><c>true</c> if the object that implements the dictionary interface contains an element that has the specified key; otherwise, <c>false</c>.</returns>
        public bool TryGetFirstValue<T>(ReadOnlySpan<string> keys, out T? value)
        {
            foreach (var key in keys)
            {
                if (dictionary.TryGetValue(key, out value))
                    return true;
            }
            value = default;
            return false;
        }
    }
}