namespace Cohesive.Assurance;

/// <summary>Shared structured rejection for observation production, transport, or monitor ingestion.</summary>
/// <remarks>Hosts may catch this type once and use Diagnostic for disposition. The exception does not imply
/// that a monitor ran. Cancellation remains OperationCanceledException and is not a rejection diagnostic.</remarks>
public class TraceObservationException : InvalidOperationException
{
    /// <summary>Creates an observation rejection for core validation or a specialized adapter.</summary>
    /// <param name="diagnostic">Stable diagnostic identifying the failed boundary.</param>
    /// <exception cref="ArgumentNullException">Diagnostic is null.</exception>
    protected internal TraceObservationException(TraceEvaluationDiagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message) => Diagnostic = diagnostic;

    /// <summary>Stable diagnostic describing the rejection; codes may be shared across boundary-specific subtypes.</summary>
    public TraceEvaluationDiagnostic Diagnostic { get; }
}
