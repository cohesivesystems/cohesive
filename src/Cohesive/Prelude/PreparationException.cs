namespace Cohesive.Prelude;

/// <summary>Common boundary for failed preparation, with stable phase and diagnostic identity.</summary>
/// <param name="phase">Preparation phase that failed.</param>
/// <param name="code">Stable machine-readable failure code.</param>
/// <param name="message">Human-readable explanation; derived types retain original compiler evidence.</param>
/// <param name="innerException">Original failure retained in the standard exception chain.</param>
public abstract class PreparationException(string phase, string code, string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    /// <summary>Failed preparation phase.</summary>
    public string Phase { get; } = phase;
    /// <summary>Stable preparation failure code.</summary>
    public string Code { get; } = code;
}
