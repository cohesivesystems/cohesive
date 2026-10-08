using Cohesive.Model.Serialization;
using Cohesive.Prelude;

namespace Cohesive.Storage.Processes;

/// <summary>Structured setup failure for a native process transition association.</summary>
/// <param name="validation">Capability and authority diagnostics produced by the binding.</param>
public sealed class ProcessTransitionBindingException(DocumentValidationResult validation)
    : PreparationException("binding", validation.Diagnostics[0].Code,
        string.Join("; ", validation.Diagnostics.Select(diagnostic => diagnostic.Message)))
{
    /// <summary>Original coded diagnostics and locations.</summary>
    public DocumentValidationResult Validation { get; } = validation;
}
