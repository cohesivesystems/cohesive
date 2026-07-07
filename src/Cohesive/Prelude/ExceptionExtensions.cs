namespace Cohesive.Prelude;

/// <summary>
/// Extension methods for <see cref="Exception"/>.
/// </summary>
public static class ExceptionExtensions
{
    /// <summary>
    /// Combines the exception with another exception.
    /// If the first exception is null, the second exception is returned.
    /// Otherwise, a new <see cref="AggregateException"/> is created.
    /// </summary>
    /// <param name="ex">The possibly null exception to aggregate another exception with.</param>
    /// <param name="other">The other exception to aggregate with the first.</param>
    /// <returns>An <see cref="AggregateException"/> containing both exceptions if the first one is not null, and just the second exception otherwise.</returns>
    public static Exception CombineWith(this Exception? ex, Exception other) => (ex, other) switch
    {
        (null, _) => other,
        (AggregateException aex1, AggregateException aex2) => new AggregateException(aex1.InnerExceptions.Concat(aex2.InnerExceptions)),
        (AggregateException aex, _) => new AggregateException(aex.InnerExceptions.Concat([other])),
        ({ } _, _) => new AggregateException(ex, other) 
    };

    extension(Exception ex)
    {
        /// <summary>
        /// Tries to find an inner <see cref="Exception"/> of the specified type, starting from the provided exception.
        /// </summary>
        /// <typeparam name="TException">The exception type to find.</typeparam>
        /// <returns>The exception of the specified type, or null if not found.</returns>
        public TException? TryFindInnerException<TException>() where TException : Exception
        {
            for (var current = ex; current is not null; current = current.InnerException)
            {
                if (current is TException innerException)
                {
                    return innerException;
                }
            }
            
            return null;
        }
    }
}