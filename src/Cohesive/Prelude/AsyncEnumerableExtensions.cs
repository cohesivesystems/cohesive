namespace Cohesive.Prelude;

/// <summary>
/// Extensions for <see cref="IAsyncEnumerable{T}"/>.
/// </summary>
public static class AsyncEnumerableExtensions
{
    /// <param name="source">The source sequence.</param>
    /// <typeparam name="T">The type of elements of the source.</typeparam>
    extension<T>(IAsyncEnumerable<T> source)
    {
        /// <summary>
        /// Projects each element of a sequence into a new form and filters out null results.
        /// </summary>
        /// <param name="selector">The selector function.</param>
        /// <typeparam name="TResult">The type of projected elements.</typeparam>
        /// <returns>An async sequence of non-null projected elements.</returns>
        public async IAsyncEnumerable<TResult> SelectNotNull<TResult>(Func<T, TResult?> selector) where TResult : class
        {
            await foreach (var item in source)
            {
                var result = selector(item);
                if (result is not null)
                    yield return result;
            }
        }

        /// <summary>
        /// Projects each element of a sequence into a new form and filters out null results.
        /// </summary>
        /// <param name="selector">The selector function.</param>
        /// <typeparam name="TResult">The type of projected elements.</typeparam>
        /// <returns>An async sequence of non-null projected elements.</returns>
        public async IAsyncEnumerable<TResult> SelectNotNull<TResult>(Func<T, ValueTask<TResult?>> selector) where TResult : class
        {
            await foreach (var item in source)
            {
                var result = await selector(item);
                if (result is not null)
                    yield return result;
            }
        }

        /// <summary>
        /// Projects each element of a sequence into a new form and filters out null results.
        /// </summary>
        /// <param name="selector">The selector function.</param>
        /// <typeparam name="TResult">The type of projected elements.</typeparam>
        /// <returns>An async sequence of non-null projected elements.</returns>
        public async IAsyncEnumerable<TResult> SelectNotNull<TResult>(Func<T, TResult?> selector) where TResult : struct
        {
            await foreach (var item in source)
            {
                var result = selector(item);
                if (result.HasValue)
                    yield return result.Value;
            }
        }
    }
}