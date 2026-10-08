using Cohesive.Model.Serialization;
using Cohesive.Prelude;

namespace Cohesive.Storage.Processes;

/// <summary>Complete diagnostic catalog for native process transition binding admission.</summary>
public static class ProcessTransitionBindingDiagnosticCodes
{
    /// <summary>The repository cannot commit entity state and receipt atomically.</summary>
    public const string ReceiptCapabilityMissing = "storage.processes.binding.receiptCapabilityMissing";

    /// <summary>The transition observation differs from repository entity authority.</summary>
    public const string ObservationMismatch = "storage.processes.binding.observationMismatch";

}

/// <summary>Structured setup failure for a native process transition association.</summary>
/// <param name="validation">Capability and authority diagnostics produced by the binding, containing at least one error.</param>
/// <exception cref="ArgumentNullException">Validation is null.</exception>
/// <exception cref="ArgumentException">Validation contains no errors.</exception>
public sealed class ProcessTransitionBindingException(DocumentValidationResult validation)
    : PreparationException("binding", RequireInvalid(validation).Diagnostics.First(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Code,
        string.Join("; ", validation.Diagnostics.Select(diagnostic => diagnostic.Message)))
{
    static DocumentValidationResult RequireInvalid(DocumentValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        if (validation.IsValid)
            throw new ArgumentException("Binding failure requires at least one error diagnostic.", nameof(validation));
        return validation;
    }

    /// <summary>Original coded diagnostics and locations.</summary>
    public DocumentValidationResult Validation { get; } = validation;
}
