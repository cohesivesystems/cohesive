namespace Cohesive.Storage;

/// <summary>
/// Optimistic concurrency conflict raised by observation repositories.
/// </summary>
public sealed class ObservationConcurrencyConflictException(string message, Exception? innerException = null) 
    : Exception(message, innerException);