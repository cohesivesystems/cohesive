namespace Cohesive.Processes.Runtime;

/// <summary>
/// Marks an effect failure as transient for retry behavior.
/// </summary>
public class ProcessTransientEffectException(string message) : Exception(message);

/// <summary>
/// Concurrency conflict raised by storage commits.
/// </summary>
public sealed class ProcessConcurrencyConflictException(string message) : Exception(message);

/// <summary>
/// Raised when required capability is not available in current place.
/// </summary>
public sealed class ProcessCapabilityViolationException(string message) : Exception(message);

/// <summary>
/// Raised when conflict policy escalates execution to saga orchestration.
/// </summary>
public sealed class ProcessSagaEscalationException(string message) : Exception(message);