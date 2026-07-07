using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Runtime.CompilerServices;

namespace Cohesive.Prelude;

/// <summary>
/// Shared guard helpers that throw <see cref="ArgumentException"/> to enforce input invariants.
/// </summary>
public static class Guard
{
    /// <summary>
    /// Ensures a reference is non-null and returns it.
    /// </summary>
    /// <param name="value">The reference type argument to validate as non-null.</param>
    /// <param name="paramName">The name of the parameter with which the argument corresponds. If you omit this parameter, the name of the argument is used.</param>
    /// <exception cref="ArgumentNullException">argument is null.</exception>
    /// <remarks>Use <see cref="ArgumentNullException.ThrowIfNull(object?, string?)"/> if the return value is not required</remarks>
    /// <returns>The <paramref name="value"/> if validated.</returns>
    [return: NotNull]
    [StackTraceHidden]
    [Pure]
    public static T RequireNotNull<T>([NotNull] T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null) where T : class
    {
        ArgumentNullException.ThrowIfNull(value, paramName);
        return value;
    }

    /// <summary>
    /// Ensures text input is non-null, non-empty, and non-whitespace, and returns it.
    /// </summary>
    /// <param name="value">The string argument to validate.</param>
    /// <param name="paramName">The name of the parameter with which the argument corresponds. If you omit this parameter, the name of the argument is used.</param>
    /// <exception cref="ArgumentException">argument is empty or consists only of white-space characters.</exception>
    /// <exception cref="ArgumentNullException">argument is null.</exception>
    /// <remarks>Use <see cref="ArgumentNullException.ThrowIfNullOrWhiteSpace(string?, string?)"/> if the return value is not required</remarks>
    /// <returns>The <paramref name="value"/> if validated.</returns>
    [StackTraceHidden]
    [Pure]
    public static string RequireNotNullOrWhiteSpace([NotNull] string? value, [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    /// <summary>
    /// Ensures that the input satisfies the specified predicate.
    /// </summary>
    /// <param name="value"></param>
    /// <param name="predicate"></param>
    /// <param name="message"></param>
    /// <param name="paramName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    [StackTraceHidden]
    [Pure]
    public static T Require<T>(T value, Func<T, bool> predicate, string? message, [CallerArgumentExpression(nameof(predicate))] string? paramName = null) => 
        !predicate(value) ? throw new ArgumentException(message: message, paramName: paramName) : value;
}