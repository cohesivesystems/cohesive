using Cohesive.Model.Serialization;

namespace Cohesive.Integrations;

/// <summary>Binding failure retaining the complete structured result from the failing validation stage.</summary>
/// <remarks>Later validation stages may require earlier ones to succeed. This exception preserves every
/// diagnostic reported by the reached stage; it does not claim to enumerate every possible defect.</remarks>
public sealed class IntegrationBindingException : ArgumentException
{
    internal IntegrationBindingException(DocumentValidationResult validation)
        : base(string.Join(Environment.NewLine, validation.Diagnostics.Select(d => $"{d.Code}: {d.Message}")), "compilation") => Validation = validation;

    /// <summary>Complete reported diagnostics, including severity, location and evidence.</summary>
    public DocumentValidationResult Validation { get; }
}
