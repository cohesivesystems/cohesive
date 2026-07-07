using System.Collections.Immutable;

namespace Cohesive.Model.Serialization;

/// <summary>
/// Validation diagnostic for portable semantic documents.
/// </summary>
public sealed record DocumentValidationDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    string? Location = null,
    string? SchemaLocation = null
    );

/// <summary>
/// Validation result for portable semantic documents.
/// </summary>
public sealed record DocumentValidationResult
{
    /// <summary>
    /// Empty successful validation result.
    /// </summary>
    public static DocumentValidationResult Valid { get; } = new([]);

    /// <summary>
    /// Creates a validation result.
    /// </summary>
    public DocumentValidationResult(ImmutableArray<DocumentValidationDiagnostic> diagnostics)
    {
        Diagnostics = diagnostics.IsDefault ? [] : diagnostics;
    }

    /// <summary>
    /// Validation diagnostics.
    /// </summary>
    public ImmutableArray<DocumentValidationDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Returns true when there are no error diagnostics.
    /// </summary>
    public bool IsValid => !Diagnostics.Any(x => x.Severity == DiagnosticSeverity.Error);

    /// <summary>
    /// Creates a result from diagnostics.
    /// </summary>
    public static DocumentValidationResult FromDiagnostics(IEnumerable<DocumentValidationDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        var array = diagnostics.ToImmutableArray();
        return array.IsDefaultOrEmpty ? Valid : new(array);
    }

    /// <summary>
    /// Combines validation results.
    /// </summary>
    public static DocumentValidationResult Combine(params DocumentValidationResult[] results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return FromDiagnostics(results.SelectMany(x => x.Diagnostics));
    }
}
