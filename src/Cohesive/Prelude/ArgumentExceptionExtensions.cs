using System.Collections.Immutable;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cohesive.Prelude;

/// <summary>
/// Throw extensions to <see cref="ArgumentException"/>.
/// </summary>
public static class ArgumentExceptionExtensions
{
    extension(ArgumentException)
    {
        /// <summary>
        /// Throws an <see cref="ArgumentException"/> when an immutable array is default or empty.
        /// </summary>
        /// <typeparam name="T">Element type of the immutable array.</typeparam>
        /// <param name="value">Array argument to validate.</param>
        /// <param name="paramName">The name of the argument to include in the thrown exception.</param>
        /// <param name="message">Optional exception message.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> is default or contains zero elements.
        /// </exception>
        [StackTraceHidden]
        public static void ThrowIfDefaultOrEmpty<T>(ImmutableArray<T> value, [CallerArgumentExpression(nameof(value))] string? paramName = null, string? message = null)
        {
            if (value.IsDefaultOrEmpty)
                throw new ArgumentException(message: message, paramName: paramName);
        }
        
        /// <summary>
        /// Throws an <see cref="ArgumentException"/> when a read-only collection is null or empty.
        /// </summary>
        /// <typeparam name="T">Element type of the collection.</typeparam>
        /// <param name="value">Collection argument to validate.</param>
        /// <param name="paramName">The name of the argument to include in the thrown exception.</param>
        /// <param name="message">Optional exception message.</param>
        /// <exception cref="ArgumentException">
        /// <paramref name="value"/> is <see langword="null"/> or contains zero elements.
        /// </exception>
        [StackTraceHidden]
        public static void ThrowIfNullOrEmpty<T>(IReadOnlyCollection<T>? value, [CallerArgumentExpression(nameof(value))] string? paramName = null, string? message = null)
        {
            if (value is null || value.Count == 0)
                throw new ArgumentException(message: message, paramName: paramName);
        }
    }

    /// <summary>
    /// Throw extensions to <see cref="ArgumentOutOfRangeException"/>.
    /// </summary>
    extension(ArgumentOutOfRangeException)
    {
        /// <summary>
        /// Throws an <see cref="ArgumentOutOfRangeException"/> when a numeric value is not finite.
        /// </summary>
        /// <typeparam name="T">Numeric value type to validate.</typeparam>
        /// <param name="value">Numeric argument to validate.</param>
        /// <param name="paramName">The name of the argument to include in the thrown exception.</param>
        /// <param name="message">Optional exception message.</param>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="value"/> is <c>NaN</c> or positive/negative infinity.
        /// </exception>
        [StackTraceHidden]
        public static void ThrowIfNotFinite<T>(T value, [CallerArgumentExpression(nameof(value))] string? paramName = null, string? message = null)
            where T : INumberBase<T>
        {
            if (T.IsNaN(value) || T.IsInfinity(value))
                throw new ArgumentOutOfRangeException(paramName: paramName, message: message);
        }
    }
}
