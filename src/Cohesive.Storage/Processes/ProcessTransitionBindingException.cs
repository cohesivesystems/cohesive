using Cohesive.Model.Serialization;
using Cohesive.Prelude;

namespace Cohesive.Storage.Processes;

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
